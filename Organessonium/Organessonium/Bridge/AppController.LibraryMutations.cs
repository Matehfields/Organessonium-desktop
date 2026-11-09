using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private LibraryMutationSession? _libraryMutations;

    private sealed record LibraryMutationSession(AppRegistry Registry, string Key, CancellationToken Token,
        List<LibraryMutation> Pending)
    {
        internal bool Running;
    }

    private LibraryMutationSession LibraryMutations()
    {
        var registry = Registry();
        if (_libraryMutations?.Registry == registry) return _libraryMutations;
        var settings = _store.Load();
        var key = $"libraryMutations:{settings.NamesChannelId}:{settings.FoldersForumId}:{settings.AppsForumId}:{settings.SerialChannelId}";
        return _libraryMutations = new(registry, key, RequireApi().PayloadLifetime, _store.LoadLibraryMutations(key));
    }

    private Task PublishPendingLibraryChangesAsync(LibraryMutationSession session) =>
        _bridge.CallAsync("_onPendingLibraryChanges", session.Pending.SelectMany(job => job.Changes).ToList());

    private void SaveLibraryMutations(LibraryMutationSession session)
    {
        session.Token.ThrowIfCancellationRequested();
        _store.SaveLibraryMutations(session.Key, session.Pending);
    }

    private async Task ResumeLibraryMutationsAsync()
    {
        var session = LibraryMutations();
        if (session.Pending.Count == 0) return;
        await PublishPendingLibraryChangesAsync(session);
        StartLibraryMutationWorker(session);
    }

    private Task DeleteItemsAsync(string idsJson, bool permanently) =>
        QueueLibraryMutationAsync(permanently ? LibraryMutationKind.Delete : LibraryMutationKind.Trash, idsJson);

    private LibraryMutation? PendingFolderCreation(string path) =>
        _libraryMutations?.Pending.FirstOrDefault(job => job.Kind == LibraryMutationKind.CreateFolder &&
            PathRules.ChildPath(job.Changes[0].Source).Equals(path, StringComparison.OrdinalIgnoreCase));

    private async Task AwaitFolderCreationAsync(string path)
    {
        if (PendingFolderCreation(PathRules.Normalize(path)) is not { } pending) return;
        await pending.Completion.Task.WaitAsync(_libraryMutations!.Token);
        if (FolderIdForPath(CurrentLocalEntries(), path) == null)
            throw new InvalidOperationException("A pasta de destino não está disponível.");
    }

    private async Task CreateFolderAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = _currentPath;
        var folder = AppEntry.Folder(PathRules.CleanFileName(name.Trim()), path);
        var session = LibraryMutations();
        if (PendingFolderCreation(PathRules.ChildPath(folder)) != null)
        {
            await _bridge.CallAsync("_onStatus", "Pasta aguardando sincronização.", false);
            return;
        }
        folder.ParentFolderId = path == "/" ? AppRegistry.RootFolderId :
            PendingFolderCreation(path)?.Changes[0].Source.FolderId ?? FolderIdForPath(CurrentLocalEntries(), path) ??
            await session.Registry.ResolveFolderIdByPathAsync(path, session.Token);
        if (string.IsNullOrWhiteSpace(folder.ParentFolderId))
            throw new InvalidOperationException("A pasta de destino não está disponível.");
        var job = new LibraryMutation(LibraryMutationKind.CreateFolder, [new(folder, folder, CreatingFolder: true)], TransferState.Now())
        {
            FolderCreation = new FolderCreationCheckpoint("index-" + NewId())
        };
        await EnqueueLibraryMutationAsync(session, job);
    }

    private async Task QueueLibraryMutationAsync(LibraryMutationKind kind, string? idsJson = null, string? path = null)
    {
        var session = LibraryMutations();
        var entries = LoadedEntriesForLookup().GroupBy(EntryCacheKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (idsJson == null)
        {
            var trash = await session.Registry.LoadCachedFolderAsync(AppRegistry.TrashFolderId, AppRegistry.TrashPath, session.Token);
            foreach (var entry in trash) entries.TryAdd(EntryCacheKey(entry), entry);
        }
        foreach (var change in session.Pending.SelectMany(job => job.Changes))
        {
            if (change.Destination == null) entries.Remove(EntryCacheKey(change.Source));
            else entries[EntryCacheKey(change.Source)] = change.Destination;
        }

        var ids = idsJson == null ? null :
            (JsonSerializer.Deserialize<string[]>(idsJson, JsonOptions.Web) ?? []).ToHashSet(StringComparer.Ordinal);
        var all = entries.Values.ToList();
        var selected = all.Where(entry => ids == null ? entry.ParentFolderId == AppRegistry.TrashFolderId :
            ids.Contains(entry.MetadataMessageId)).ToList();
        selected = selected.Where(entry => !selected.Any(parent => parent != entry && IsFolderEntry(parent) &&
            IsDescendantOfFolder(entry, parent, all))).ToList();
        if (selected.Count == 0)
        {
            if (ids != null) throw new InvalidOperationException("Nenhum item encontrado para esta ação.");
            return;
        }

        var timestamp = TransferState.Now();
        var targetPath = PathRules.Normalize(path);
        var targetFolderId = kind == LibraryMutationKind.Move
            ? FolderIdForPath(all, targetPath) ?? (targetPath == "/" ? AppRegistry.RootFolderId : null) : null;
        if (kind == LibraryMutationKind.Move && (targetFolderId == null ||
            targetPath == AppRegistry.TrashPath || targetPath.StartsWith(AppRegistry.TrashPath + "/", StringComparison.Ordinal)))
            throw new InvalidOperationException("A pasta de destino não está disponível.");

        var changes = new List<LibraryChange>();
        foreach (var entry in selected)
        {
            if (entry.FolderId is AppRegistry.RootFolderId or AppRegistry.TrashFolderId or AppRegistry.SynchronizedRootFolderId)
                throw new InvalidOperationException("Esta pasta técnica não pode ser movida ou apagada.");
            var source = entry.AtLocation(entry.ParentFolderId ?? AppRegistry.RootFolderId, entry.Path);
            var inTrash = IsInsideFolder(source, AppRegistry.TrashPath) || source.ParentFolderId == AppRegistry.TrashFolderId;
            if (kind is LibraryMutationKind.Move or LibraryMutationKind.Trash && inTrash ||
                kind == LibraryMutationKind.Restore && !inTrash)
                throw new InvalidOperationException("O item mudou de pasta. Atualize a seleção.");
            AppEntry? destination = null;
            if (kind == LibraryMutationKind.Move)
            {
                if (source.ParentFolderId == targetFolderId) continue;
                if (IsFolderEntry(source) && (source.FolderId == targetFolderId ||
                    targetPath.StartsWith(PathRules.ChildPath(source) + "/", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Uma pasta não pode ser movida para dentro dela.");
                destination = source.AtLocation(targetFolderId!, targetPath);
                destination.ModifiedAt = timestamp;
            }
            else if (kind == LibraryMutationKind.Trash)
            {
                destination = source.AtLocation(AppRegistry.TrashFolderId, AppRegistry.TrashPath);
                destination.Trash = new TrashMetadata { OriginalParentFolderId = source.ParentFolderId!, DeletedAt = timestamp };
            }
            else if (kind == LibraryMutationKind.Restore)
            {
                var parentId = source.Trash?.OriginalParentFolderId;
                if (parentId == AppRegistry.RootFolderId || source.ParentFolderId != AppRegistry.TrashFolderId)
                    destination = source.AtLocation(AppRegistry.RootFolderId, "/");
                else if (all.FirstOrDefault(candidate => IsFolderEntry(candidate) && candidate.FolderId == parentId) is { } parent &&
                    !IsInsideFolder(parent, AppRegistry.TrashPath))
                    destination = source.AtLocation(parent.FolderId, PathRules.ChildPath(parent));
                if (destination != null) destination.Trash = null;
            }
            changes.Add(new(source, destination));
        }
        if (changes.Count == 0) return;

        var job = new LibraryMutation(kind, changes, timestamp) { ReadWholeTrash = ids == null };
        await EnqueueLibraryMutationAsync(session, job);
    }

    private async Task EnqueueLibraryMutationAsync(LibraryMutationSession session, LibraryMutation job)
    {
        session.Pending.Add(job);
        try { SaveLibraryMutations(session); }
        catch { session.Pending.Remove(job); throw; }
        await PublishPendingLibraryChangesAsync(session);
        if (job.Kind == LibraryMutationKind.CreateFolder)
            await _bridge.CallAsync("_onStatus", "Pasta disponível localmente.", false);
        StartLibraryMutationWorker(session);
        await job.Completion.Task.WaitAsync(session.Token);
    }

    private void StartLibraryMutationWorker(LibraryMutationSession session)
    {
        if (session.Running) return;
        session.Running = true;
        _ = RunAsync(() => DrainLibraryMutationsAsync(session));
    }

    private async Task DrainLibraryMutationsAsync(LibraryMutationSession session)
    {
        var token = session.Token;
        var retry = 0;
        try
        {
            while (!_loggingOut && !token.IsCancellationRequested && session.Pending.FirstOrDefault() is { } job)
            {
                var registry = session.Registry;
                var completed = new List<AppEntry>();
                Exception? rejected = null;
                try
                {
                    using var network = NetworkRetryPolicy.TransferScope(token);
                    await using var priority = registry.SharedStorage.PrioritizeMutations();
                    if (job.ReadWholeTrash)
                    {
                        var remoteTrash = await registry.RefreshFolderAsync(AppRegistry.TrashFolderId, AppRegistry.TrashPath, token);
                        var known = job.Changes.Select(change => EntryCacheKey(change.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in remoteTrash.Where(entry => known.Add(EntryCacheKey(entry))))
                            job.Changes.Add(new(entry, null));
                        job.ReadWholeTrash = false;
                        SaveLibraryMutations(session);
                        await PublishPendingLibraryChangesAsync(session);
                    }
                    if (job.Kind == LibraryMutationKind.CreateFolder)
                    {
                        var folder = await registry.CreateQueuedFolderAsync(job, () => SaveLibraryMutations(session), token);
                        completed.Add(folder);
                        var latest = CurrentLocalEntries();
                        ReplaceLocalEntry(latest, folder);
                        await PublishLocalRegistryAsync(registry, latest, refreshStatistics: false, incremental: true, changedEntry: folder);
                    }
                    else if (job.Kind == LibraryMutationKind.Delete)
                    {
                        await registry.PublishPendingDeletionSerialsAsync(job, () => SaveLibraryMutations(session), token);
                        var failed = await DeleteLibraryEntriesAsync(registry, job.Changes.Select(change => change.Source).ToList(), token);
                        if (failed > 0)
                        {
                            foreach (var change in job.Changes.DistinctBy(change => change.Source.ParentFolderId))
                            {
                                var id = change.Source.ParentFolderId ?? AppRegistry.RootFolderId;
                                var entries = await registry.RefreshFolderAsync(id, change.Source.Path, token);
                                ReplaceLoadedFolder(id, entries);
                                await _bridge.CallAsync("_onFolderIndexReconciled", id, entries);
                            }
                            job.SerialPublished = false;
                            throw new InvalidOperationException($"{failed} item(ns) não puderam ser apagados.");
                        }
                    }
                    else
                    {
                        completed = await registry.ApplyLibraryRelocationsAsync(job, () => SaveLibraryMutations(session), token);
                        await PublishRelocatedEntriesAsync(job, completed);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    AppLogger.Error("Registry.LibraryMutation", error);
                    if (job.SerialPublished || job.FolderCreation is { ThreadId: not null } or { Published: not null } ||
                        job.Relocations?.Any(move => move.Destination.MetadataMessageId.Length > 0) == true ||
                        NetworkRetryPolicy.IsTransient(error) || error is DiscordApiException api && NetworkRetryPolicy.IsTransientStatus(api.StatusCode))
                    {
                        await NetworkRetryPolicy.DelayAsync(++retry, token);
                        continue;
                    }
                    rejected = error;
                }

                var byId = completed.ToDictionary(EntryCacheKey, StringComparer.OrdinalIgnoreCase);
                var affected = job.Changes.Select(change => EntryCacheKey(change.Source)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var requestedFolder = job.Kind == LibraryMutationKind.CreateFolder ? job.Changes[0].Source : null;
                var createdFolder = requestedFolder != null ? completed.SingleOrDefault() : null;
                foreach (var pending in session.Pending.Skip(1))
                {
                    for (var i = pending.Changes.Count - 1; i >= 0; i--)
                    {
                        var change = pending.Changes[i];
                        var id = EntryCacheKey(change.Source);
                        if (byId.TryGetValue(id, out var current)) pending.Changes[i] = change with { Source = current };
                        else if (affected.Contains(id)) pending.Changes.RemoveAt(i);
                        else if (requestedFolder != null)
                        {
                            var requestedPath = PathRules.ChildPath(requestedFolder);
                            var sourceDepends = change.Source.ParentFolderId == requestedFolder.FolderId;
                            var destinationDepends = change.Destination?.ParentFolderId == requestedFolder.FolderId;
                            if (createdFolder == null && (sourceDepends || destinationDepends ||
                                IsInsideFolder(change.Source, requestedPath) ||
                                (change.Destination != null && IsInsideFolder(change.Destination, requestedPath))))
                                pending.Changes.RemoveAt(i);
                            else if (createdFolder != null && (sourceDepends || destinationDepends))
                                pending.Changes[i] = change with
                                {
                                    Source = sourceDepends ? change.Source.AtLocation(createdFolder.FolderId, change.Source.Path) : change.Source,
                                    Destination = destinationDepends
                                        ? change.Destination!.AtLocation(createdFolder.FolderId, change.Destination.Path) : change.Destination
                                };
                        }
                    }
                }
                session.Pending.RemoveAt(0);
                session.Pending.RemoveAll(pending =>
                {
                    if (pending.Changes.Count != 0) return false;
                    pending.Completion.TrySetResult();
                    return true;
                });
                SaveLibraryMutations(session);
                await PublishPendingLibraryChangesAsync(session);
                if (rejected == null)
                {
                    var status = requestedFolder == null ? "OK Alteração sincronizada." :
                        requestedFolder.FolderId == createdFolder?.FolderId ? "OK Pasta criada." : "OK A pasta já existe.";
                    await _bridge.CallAsync("_onStatus", status, false);
                    if (createdFolder != null && requestedFolder!.FolderId != createdFolder.FolderId && _currentPath == PathRules.ChildPath(createdFolder))
                        _ = RunAsync(() => LoadFolderAtPathAsync(_currentPath, createdFolder.FolderId));
                    _ = RunAsync(() => PublishLibraryFileCountAsync(registry));
                }
                else if (!_loggingOut) await ErrorAsync(rejected.Message);
                job.Completion.TrySetResult();
                retry = 0;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { session.Running = false; }
    }

    private async Task PublishRelocatedEntriesAsync(LibraryMutation job, IReadOnlyList<AppEntry> completed)
    {
        var latest = CurrentLocalEntries();
        var changed = new List<AppEntry>();
        var byId = completed.ToDictionary(EntryCacheKey, StringComparer.OrdinalIgnoreCase);
        foreach (var change in job.Changes)
        {
            if (!byId.TryGetValue(EntryCacheKey(change.Source), out var moved)) continue;
            if (IsFolderEntry(moved))
            {
                var oldPath = PathRules.ChildPath(change.Source);
                var newPath = PathRules.ChildPath(moved);
                foreach (var child in latest.Where(child => IsInsideFolder(child, oldPath) &&
                    IsDescendantOfFolder(child, change.Source, latest)).ToList())
                {
                    var updated = child.AtLocation(child.ParentFolderId ?? AppRegistry.RootFolderId, MovedChildPath(oldPath, newPath, child.Path));
                    ReplaceLocalEntry(latest, updated);
                    changed.Add(updated);
                }
            }
            ReplaceLocalEntry(latest, moved);
            changed.Add(moved);
        }
        _loadedApps = latest;
        await _bridge.CallAsync("_onAppsBatch", changed);
        Interlocked.Increment(ref _favoriteLoadVersion);
        _favoriteApps = await Registry().LoadFavoritesAsync();
        await _bridge.CallAsync("_onSpecialItemsLoaded", "favorites", _favoriteApps);
        if (job.Changes.Any(change => change.Source.ParentFolderId == AppRegistry.SynchronizedRootFolderId ||
            change.Destination?.ParentFolderId == AppRegistry.SynchronizedRootFolderId))
        {
            Interlocked.Increment(ref _deviceLoadVersion);
            _deviceApps = await Registry().LoadSynchronizedDevicesAsync();
            await _bridge.CallAsync("_onSpecialItemsLoaded", "devices", _deviceApps);
        }
    }
}
