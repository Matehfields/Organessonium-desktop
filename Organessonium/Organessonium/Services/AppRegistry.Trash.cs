using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    public async Task EnsureTrashIndexAsync(CancellationToken cancellationToken = default)
    {
        await using var priority = SharedStorage.PrioritizeMutations();
        await _entryMutationGate.WaitAsync(cancellationToken);
        try { await EnsureTrashIndexCoreAsync(cancellationToken); }
        finally { _entryMutationGate.Release(); }
    }

    private async Task EnsureTrashIndexCoreAsync(CancellationToken cancellationToken)
    {
        if (_mappings.ContainsKey(TrashFolderId)) return;
        await RefreshMappingsAsync(cancellationToken);
        if (!_mappings.ContainsKey(TrashFolderId))
        {
            await GetMappingAsync(RootFolderId, cancellationToken);
            await CreateFolderIndexAsync(TrashFolderId, RootFolderId, TrashFolderId, TrashPath, cancellationToken);
        }
    }

    public Task<AppEntry> MoveToTrashAsync(AppEntry entry, CancellationToken cancellationToken = default) =>
        RelocateTrashEntryAsync(entry, restore: false, cancellationToken);

    public Task<AppEntry> RestoreFromTrashAsync(AppEntry entry, CancellationToken cancellationToken = default) =>
        RelocateTrashEntryAsync(entry, restore: true, cancellationToken);

    internal async Task<IAsyncDisposable> BeginTrashWriteBatchAsync(IReadOnlyList<AppEntry> entries, bool restore,
        CancellationToken token = default)
    {
        if (!restore) await EnsureTrashIndexAsync(token);
        await GetMappingAsync(RootFolderId, token);
        var folderIds = entries.Select(entry => NormalizeFolderId(entry.ParentFolderId))
            .Concat(entries.Where(entry => entry.Trash != null).Select(entry => NormalizeFolderId(entry.Trash!.OriginalParentFolderId)))
            .Append(RootFolderId).Append(TrashFolderId).Distinct(StringComparer.Ordinal);
        return await SharedStorage.HoldWritesAsync(folderIds.Where(_mappings.ContainsKey)
            .Select(folderId => _mappings[folderId].ThreadId), token);
    }

    private async Task<AppEntry> RelocateTrashEntryAsync(AppEntry entry, bool restore, CancellationToken cancellationToken)
    {
        await using var priority = SharedStorage.PrioritizeMutations();
        await using var writes = await BeginTrashWriteBatchAsync([entry], restore, cancellationToken);
        await _entryMutationGate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Type == "folder" && entry.FolderId is RootFolderId or SynchronizedRootFolderId or TrashFolderId)
                throw new InvalidOperationException("Esta pasta técnica não pode ser enviada à lixeira.");

            var sourceFolderId = NormalizeFolderId(entry.ParentFolderId);
            var trashedFolders = await LoadTrashFolderIdsAsync(cancellationToken);
            if (restore ? sourceFolderId != TrashFolderId : trashedFolders.Contains(sourceFolderId))
                throw new InvalidOperationException(restore
                    ? "Restaure o item pela raiz da lixeira."
                    : "O item já está na lixeira. Use Shift+Delete para apagar permanentemente.");

            // OSER does not include payload chunks or trash recovery data. Never move a serial-only entry.
            var (detailed, sourceChannelId) = await ReadTrashEntryAsync(entry, cancellationToken);
            await using var mutation = await SharedStorage.AcquireMetadataMutationAsync(detailed, cancellationToken);
            if (detailed.Storage?.Shared == true) (detailed, sourceChannelId) = await ReadTrashEntryAsync(entry, cancellationToken);
            var destinationFolderId = TrashFolderId;
            var destinationPath = TrashPath;
            if (restore)
            {
                if (detailed.Trash == null)
                    throw new InvalidOperationException("O item não possui os metadados de restauração da lixeira.");
                destinationFolderId = NormalizeFolderId(detailed.Trash.OriginalParentFolderId);
                await RefreshMappingsAsync(cancellationToken);
                if (trashedFolders.Contains(destinationFolderId) || !_mappings.ContainsKey(destinationFolderId))
                    destinationFolderId = RootFolderId;
                destinationPath = await ResolveTrashRestorePathAsync(destinationFolderId, [], cancellationToken);
                detailed.Trash = null;
            }
            else
            {
                await EnsureTrashIndexCoreAsync(cancellationToken);
                detailed.Trash = new TrashMetadata
                {
                    OriginalParentFolderId = sourceFolderId,
                    DeletedAt = DateTimeOffset.UtcNow.ToString("O")
                };
            }

            _mappings.TryGetValue(sourceFolderId, out var sourceMapping);
            var sourceEntries = sourceMapping != null && sourceChannelId == sourceMapping.ThreadId
                ? await RefreshFolderAsync(sourceFolderId, entry.Path, cancellationToken)
                : await LoadStandaloneTrashSourceAsync(sourceFolderId, entry.Path, cancellationToken);
            var destinationEntries = await RefreshFolderAsync(destinationFolderId, destinationPath, cancellationToken);
            if (restore && detailed.Type == "folder" && destinationEntries.Any(candidate => candidate.Type == "folder" &&
                candidate.Name.Equals(detailed.Name, StringComparison.OrdinalIgnoreCase) && !SameEntryIdentity(candidate, detailed)))
                throw new InvalidOperationException("Já existe uma pasta com este nome no destino. Renomeie essa pasta antes de restaurar.");
            var destination = await GetMappingAsync(destinationFolderId, cancellationToken);
            detailed.ParentFolderId = destinationFolderId;
            detailed.Path = destinationPath;
            detailed.MetadataMessageId = "";

            await using var sourceWrite = sourceChannelId == _settings.NamesChannelId
                ? null : await _discord.AcquireThreadWriteAsync(sourceChannelId, cancellationToken);
            await using var destinationWrite = await _discord.AcquireThreadWriteAsync(destination.ThreadId, cancellationToken);
            try
            {
                // Publish first. A failed destination must leave the original metadata and payloads intact.
                await _metadata.SendEntryAsync(_discord, destination.ThreadId, detailed, cancellationToken);
                try
                {
                    await _metadata.DeleteEntryAsync(_discord, sourceChannelId, entry, cancellationToken);
                }
                catch
                {
                    await _metadata.DeleteEntryAsync(_discord, destination.ThreadId, detailed, CancellationToken.None);
                    throw;
                }
            }
            finally
            {
                await ArchiveFolderThreadAsync(destination.ThreadId, cancellationToken);
                if (sourceChannelId != _settings.NamesChannelId)
                    await ArchiveFolderThreadAsync(sourceChannelId, cancellationToken);
            }

            RemoveEntry(sourceEntries, entry);
            ReplaceEntry(destinationEntries, detailed);
            _store.CacheFolderEntries(sourceFolderId, sourceEntries);
            _store.CacheFolderEntries(destinationFolderId, destinationEntries);
            RecordDescriptors(destinationEntries);
            if (sourceMapping != null)
                await SynchronizeFolderSerialAsync(sourceFolderId, entry.Path, sourceEntries, cancellationToken);
            else
            {
                var sourceSerial = await _serials.LoadStandaloneSerialAsync(sourceFolderId, FolderSerialKind.Folder, cancellationToken);
                await _serials.SaveStandaloneSerialAsync(_serials.BuildFolderSerial(
                    sourceFolderId, sourceSerial?.ParentFolderId ?? RootFolderId,
                    sourceSerial?.FolderName ?? sourceFolderId, sourceEntries, MappingSnapshot()), cancellationToken);
            }
            await SynchronizeFolderSerialAsync(destinationFolderId, destinationPath, destinationEntries, cancellationToken);
            if (detailed.Type == "folder" && _mappings.ContainsKey(detailed.FolderId))
            {
                var path = PathRules.ChildPath(detailed);
                var children = await LoadCachedFolderAsync(detailed.FolderId, path, cancellationToken);
                _store.CacheFolderEntries(detailed.FolderId, children);
                await SynchronizeFolderSerialAsync(detailed.FolderId, path, children, cancellationToken);
            }

            // Preserve favorites published outside mapped index threads, while excluding the entire trash tree.
            var previousFavorites = await _serials.LoadStandaloneSerialAsync(
                SerialIndexService.FavoritesSerialId, FolderSerialKind.Favorites, cancellationToken);
            var additional = previousFavorites == null ? new List<AppEntry>() : _serials.MaterializeEntries(
                previousFavorites, [], "/", MappingSnapshot(), preserveChildParentFolderId: true).ToList();
            additional.RemoveAll(candidate => SameEntryIdentity(candidate, detailed));
            if (restore && detailed.Favorite) additional.Add(detailed);
            await RebuildFavoritesSerialAsync(additional, cancellationToken);
            if (detailed.Type == "file") await SharedStorage.PublishFileReferenceAsync(detailed, destination.ThreadId, cancellationToken);
            return detailed;
        }
        finally { _entryMutationGate.Release(); }
    }

    private async Task<(AppEntry Entry, string ChannelId)> ReadTrashEntryAsync(AppEntry entry, CancellationToken cancellationToken)
    {
        var channels = new List<string>();
        if (_mappings.TryGetValue(NormalizeFolderId(entry.ParentFolderId), out var mapping)) channels.Add(mapping.ThreadId);
        channels.Add(_settings.NamesChannelId);
        foreach (var channel in channels.Distinct(StringComparer.Ordinal))
        {
            try
            {
                var message = await _discord.GetMessageAsync(channel, entry.MetadataMessageId, cancellationToken);
                var envelope = await _metadata.ReadEnvelopeAsync(message, cancellationToken);
                var detailed = envelope == null ? null : _metadata.ToAppEntry(envelope);
                if (detailed == null || !SameEntryIdentity(detailed, entry)) continue;
                detailed.ParentFolderId ??= entry.ParentFolderId;
                if (NormalizeFolderId(detailed.ParentFolderId) != NormalizeFolderId(entry.ParentFolderId))
                    throw new InvalidOperationException("A pasta do item mudou no servidor. Atualize a lista antes de continuar.");
                if (detailed.Type == "file" && detailed.SizeBytes > 0 && detailed.LogicalChunkCount == 0)
                    throw new InvalidOperationException("Os metadados não contêm os chunks do arquivo; nenhuma exclusão foi realizada.");
                if (detailed.Type == "folder") detailed.ThreadId = entry.ThreadId;
                return (detailed, channel);
            }
            catch (DiscordApiException ex) when ((int)ex.StatusCode == 404) { }
        }
        throw new InvalidOperationException("Os metadados completos do item não foram encontrados; nenhuma exclusão foi realizada.");
    }

    private async Task<List<AppEntry>> LoadStandaloneTrashSourceAsync(string folderId, string path, CancellationToken cancellationToken)
    {
        var serial = await _serials.LoadStandaloneSerialAsync(folderId, FolderSerialKind.Folder, cancellationToken)
            ?? throw new InvalidOperationException("O serial da pasta de origem não foi encontrado; nenhuma exclusão foi realizada.");
        return _serials.MaterializeEntries(serial, _store.LoadCachedEntriesForFolder(folderId), path, MappingSnapshot()).ToList();
    }

    private async Task<string> ResolveTrashRestorePathAsync(string folderId, HashSet<string> visited, CancellationToken cancellationToken)
    {
        if (folderId == RootFolderId) return "/";
        if (folderId == SynchronizedRootFolderId) return SynchronizedRootPath;
        if (!visited.Add(folderId)) throw new InvalidOperationException("A árvore de restauração possui um ciclo.");
        var mapping = await GetMappingAsync(folderId, cancellationToken);
        var serial = await _serials.LoadFolderAsync(mapping, cancellationToken)
            ?? throw new InvalidOperationException("O serial da pasta de destino não pôde ser lido.");
        var parent = await ResolveTrashRestorePathAsync(NormalizeFolderId(serial.ParentFolderId), visited, cancellationToken);
        return PathRules.Normalize((parent == "/" ? "" : parent) + "/" + serial.FolderName);
    }

    private async Task<HashSet<string>> LoadTrashFolderIdsAsync(CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { TrashFolderId };
        var pending = new Queue<string>();
        pending.Enqueue(TrashFolderId);
        while (pending.TryDequeue(out var folderId))
        {
            if (!_mappings.TryGetValue(folderId, out var mapping)) continue;
            var serial = await _serials.LoadFolderAsync(mapping, cancellationToken);
            if (serial == null) continue;
            foreach (var folder in serial.Children.Where(child => child.IsFolder))
                if (result.Add(folder.Id)) pending.Enqueue(folder.Id);
        }
        return result;
    }
}
