using System.Collections.Concurrent;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    private readonly ConcurrentDictionary<string, byte> _publishingLibraryFolders = new(StringComparer.OrdinalIgnoreCase);
    private long _libraryPublicationRevision;

    internal async Task<List<AppEntry>> ApplyLibraryRelocationsAsync(LibraryMutation job, Action checkpoint, CancellationToken token)
    {
        await using var priority = SharedStorage.PrioritizeMutations();
        await using var batch = BeginSerialBatch();
        var retry = job.Relocations != null;
        if (job.Relocations == null)
        {
            job.Relocations = await PlanLibraryRelocationsAsync(job, token);
            checkpoint();
        }
        var moves = job.Relocations;
        if (retry)
        {
            foreach (var move in moves.Where(move => move.FilePayload != null && move.Source.LogicalChunkCount == 0))
            {
                var source = _metadata.ToAppEntry(new MetadataEnvelope(move.Source.MetadataMessageId, move.FilePayload!))!;
                CopyFilePayload(source, move.Source);
                CopyFilePayload(source, move.Destination);
            }
        }
        var targets = new Dictionary<string, FolderIndexMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var folderId in moves.Select(move => NormalizeFolderId(move.Destination.ParentFolderId)).Distinct())
            targets[folderId] = await GetMappingAsync(folderId, token);
        var channels = targets.Values.Select(mapping => mapping.ThreadId)
            .Concat(moves.Select(move => move.SourceChannelId).Where(channel => channel != _settings.NamesChannelId));
        if (moves.Any(move => move.Source.Storage?.Shared == true || move.Source.Versions.Any(version => version.Payload.Storage?.Shared == true)))
            channels = channels.Append((await GetStorageJournalIndexAsync(token)).ThreadId);
        await using var writes = await SharedStorage.HoldWritesAsync(channels, token);
        var claims = new Stack<SharedStorageManager.MetadataMutationLease>();
        var affectedFolders = moves.SelectMany(move => new[] { NormalizeFolderId(move.Source.ParentFolderId), NormalizeFolderId(move.Destination.ParentFolderId) })
            .Concat(moves.Where(move => move.Destination.Type == "folder").Select(move => move.Destination.FolderId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var recovery = new Dictionary<string, List<AppEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var move in moves)
            {
                var claim = await SharedStorage.AcquireMetadataMutationAsync(move.Source, token, LibraryOperationClaim(job, move.Source));
                if (claim != null) claims.Push(claim);
                var destination = targets[move.Destination.ParentFolderId!];
                if (retry && move.Destination.MetadataMessageId.Length == 0)
                {
                    if (!recovery.TryGetValue(destination.FolderId, out var entries))
                        recovery[destination.FolderId] = entries = await ReadLibraryDestinationAsync(destination, move.Destination.Path, token);
                    var published = entries.FirstOrDefault(entry => SameEntryIdentity(entry, move.Destination) &&
                        entry.VersionId == move.Destination.VersionId && entry.Encryption?.Salt == move.Destination.Encryption?.Salt &&
                        entry.Name == move.Destination.Name && entry.Trash?.DeletedAt == move.Destination.Trash?.DeletedAt &&
                        entry.ModifiedAt == move.Destination.ModifiedAt);
                    if (published != null) move.Destination.MetadataMessageId = published.MetadataMessageId;
                }
                if (claim != null || job.RemoteMessageId.Length > 0)
                {
                    var live = await FindLibrarySourceAsync(move.Source, token);
                    if (live == null ? move.Destination.MetadataMessageId.Length == 0 :
                        live.Value.Entry.ModifiedAt != move.Source.ModifiedAt || live.Value.Entry.VersionId != move.Source.VersionId)
                        throw new InvalidOperationException("O item foi alterado no servidor durante a movimentação.");
                }
                if (move.Destination.MetadataMessageId.Length == 0)
                    await SendFolderEntryAsync(destination, move.Destination, token, nonce: job.RemoteMessageId.Length == 0 ? null :
                        LibraryOperationNonce(job.OperationId, move.Source.MetadataMessageId));
            }
            checkpoint();

            if (!job.SerialPublished)
            {
                await WriteLibrarySerialsAsync(moves.Select(move => new LibraryChange(move.Source, move.Destination)).ToList(), token);
                job.SerialPublished = true;
                checkpoint();
            }

            // Both indices point to readable metadata before the previous messages are removed.
            foreach (var move in moves)
            {
                try { await _metadata.DeleteEntryAsync(_discord, move.SourceChannelId, move.Source, token); }
                catch (DiscordApiException error) when ((int)error.StatusCode == 404) { }
            }

            var previousFavorites = await _serials.LoadStandaloneSerialAsync(
                SerialIndexService.FavoritesSerialId, FolderSerialKind.Favorites, token);
            var additional = previousFavorites == null ? new List<AppEntry>() : _serials.MaterializeEntries(
                previousFavorites, [], "/", MappingSnapshot(), preserveChildParentFolderId: true).ToList();
            foreach (var move in moves)
            {
                RemoveEntry(additional, move.Source);
                if (move.Destination.Favorite && job.Kind != LibraryMutationKind.Trash) additional.Add(move.Destination);
            }
            await RebuildFavoritesSerialAsync(additional, token);
            foreach (var claim in claims) claim.MarkApplied();
            return moves.Select(move => move.Destination).ToList();
        }
        finally
        {
            foreach (var folder in affectedFolders) _publishingLibraryFolders.TryRemove(folder, out _);
            Interlocked.Increment(ref _libraryPublicationRevision);
            Exception? releaseFailure = null;
            while (claims.TryPop(out var claim))
            {
                try { await claim.DisposeAsync(); }
                catch (Exception error)
                {
                    AppLogger.Error("Registry.ReleaseLibraryMutation", error);
                    if (job.RemoteMessageId.Length > 0) releaseFailure ??= error;
                }
            }
            if (releaseFailure != null) throw releaseFailure;
        }
    }

    private async Task<List<LibraryRelocation>> PlanLibraryRelocationsAsync(LibraryMutation job, CancellationToken token)
    {
        if (job.Kind == LibraryMutationKind.Trash) await EnsureTrashIndexAsync(token);
        if (job.Kind == LibraryMutationKind.Restore) await RefreshMappingsAsync(token);
        var trashedFolders = await LoadTrashFolderIdsAsync(token);
        var destinations = new Dictionary<string, List<AppEntry>>(StringComparer.OrdinalIgnoreCase);
        var planned = new List<LibraryRelocation>();
        foreach (var change in job.Changes)
        {
            var sourceFolderId = NormalizeFolderId(change.Source.ParentFolderId);
            var restoring = job.Kind == LibraryMutationKind.Restore;
            if (trashedFolders.Contains(sourceFolderId) != restoring)
                throw new InvalidOperationException("A localização do item mudou no servidor.");
            var (source, channel) = await ReadTrashEntryAsync(change.Source, token);
            source.Path = change.Source.Path;
            string targetId;
            string targetPath;
            if (restoring)
            {
                targetId = NormalizeFolderId(source.Trash?.OriginalParentFolderId);
                if (trashedFolders.Contains(targetId) || !_mappings.ContainsKey(targetId)) targetId = RootFolderId;
                targetPath = await ResolveTrashRestorePathAsync(targetId, [], token);
            }
            else
            {
                targetId = change.Destination!.ParentFolderId!;
                targetPath = change.Destination.Path;
                if (job.Kind == LibraryMutationKind.Move && trashedFolders.Contains(targetId))
                    throw new InvalidOperationException("A pasta de destino está na lixeira.");
            }
            var target = source.AtLocation(targetId, targetPath);
            target.MetadataMessageId = "";
            if (restoring) target.Trash = null;
            else if (job.Kind == LibraryMutationKind.Trash)
                target.Trash = new TrashMetadata { OriginalParentFolderId = sourceFolderId, DeletedAt = job.Timestamp };
            else target.ModifiedAt = job.Timestamp;
            if (restoring && target.Type == "folder")
            {
                if (!destinations.TryGetValue(targetId, out var children))
                    destinations[targetId] = children = await LoadFolderSerialAsync(targetId, targetPath, token);
                if (children.Any(entry => entry.Type == "folder" && !SameEntryIdentity(entry, target) &&
                    entry.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Já existe uma pasta com este nome no destino. Renomeie essa pasta antes de restaurar.");
                children.Add(target);
            }
            planned.Add(new(source, target, channel, source.Type == "file" ? MetadataService.CreateFileMetadata(source) : null));
        }
        return planned;
    }

    private async Task<List<AppEntry>> ReadLibraryDestinationAsync(FolderIndexMapping mapping, string path, CancellationToken token)
    {
        var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
        var messages = await _discord.GetMessagesInParallelAsync(mapping.ThreadId, workers, token);
        var envelopes = await _metadata.ReadEnvelopesInParallelAsync(messages, workers, token);
        return _metadata.ToFolderEntries(envelopes, mapping.FolderId, path, MappingSnapshot());
    }

    internal async Task PublishPendingDeletionSerialsAsync(LibraryMutation job, Action checkpoint, CancellationToken token)
    {
        if (job.SerialPublished) return;
        await using var batch = BeginSerialBatch();
        try
        {
            await WriteLibrarySerialsAsync(job.Changes, token);
            job.SerialPublished = true;
            checkpoint();
        }
        finally
        {
            foreach (var change in job.Changes) _publishingLibraryFolders.TryRemove(NormalizeFolderId(change.Source.ParentFolderId), out _);
            Interlocked.Increment(ref _libraryPublicationRevision);
        }
    }

    private async Task WriteLibrarySerialsAsync(IReadOnlyList<LibraryChange> changes, CancellationToken token)
    {
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (change.Destination is { } destination)
            {
                directories[NormalizeFolderId(destination.ParentFolderId)] = destination.Path;
                if (destination.Type == "folder") directories[destination.FolderId] = PathRules.ChildPath(destination);
            }
        }
        foreach (var change in changes) directories.TryAdd(NormalizeFolderId(change.Source.ParentFolderId), change.Source.Path);

        // Hydrate a cold OSER v3 directory once so its unrelated files keep their sizes and history.
        var standalone = new Dictionary<string, FolderSerial>(StringComparer.OrdinalIgnoreCase);
        foreach (var (folderId, path) in directories.ToList())
        {
            if (!_mappings.TryGetValue(folderId, out var mapping))
            {
                var direct = await _serials.LoadStandaloneSerialAsync(folderId, FolderSerialKind.Folder, token);
                if (direct != null)
                {
                    standalone[folderId] = direct;
                    continue;
                }
                if (changes.All(change => change.Destination == null)) { directories.Remove(folderId); continue; }
                await GetMappingAsync(folderId, token);
            }
            var entries = await LoadFolderSerialAsync(folderId, path, token, allowMetadataFallback: false);
            if (entries.Any(entry => entry.Type == "file" && entry.LogicalChunkCount == 0) || string.IsNullOrEmpty(mapping?.SerialMessageId))
                await RefreshFolderAsync(folderId, path, token);
        }

        await _entryMutationGate.WaitAsync(token);
        try
        {
            foreach (var folderId in directories.Keys) _publishingLibraryFolders[folderId] = 0;
            Interlocked.Increment(ref _libraryPublicationRevision);
            var contents = directories.Keys.ToDictionary(id => id, _store.LoadCachedEntriesForFolder, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, serial) in standalone)
                contents[id] = _serials.MaterializeEntries(serial, contents[id], directories[id], MappingSnapshot()).ToList();
            foreach (var change in changes)
            {
                if (contents.TryGetValue(NormalizeFolderId(change.Source.ParentFolderId), out var source))
                    RemoveEntry(source, change.Source);
                if (change.Destination is { } destination)
                {
                    ReplaceEntry(contents[NormalizeFolderId(destination.ParentFolderId)], destination);
                    if (destination.Type == "folder")
                        NormalizeFolderEntries(contents[destination.FolderId], destination.FolderId, PathRules.ChildPath(destination));
                }
            }
            foreach (var (folderId, entries) in contents)
            {
                _store.CacheFolderEntries(folderId, entries);
                RecordDescriptors(entries);
            }
        }
        finally { _entryMutationGate.Release(); }

        foreach (var (folderId, path) in directories)
        {
            var entries = _store.LoadCachedEntriesForFolder(folderId);
            if (standalone.TryGetValue(folderId, out var serial))
                await _serials.SaveStandaloneSerialAsync(_serials.BuildFolderSerial(folderId, serial.ParentFolderId,
                    serial.FolderName, entries, MappingSnapshot()), token);
            else
                await SynchronizeFolderSerialAsync(folderId, path, entries, token, publishImmediately: true);
        }
    }
}
