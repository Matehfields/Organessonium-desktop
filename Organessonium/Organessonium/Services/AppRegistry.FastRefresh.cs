using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    internal async Task<List<AppEntry>> LoadFolderSerialAsync(string folderId, string path, CancellationToken token = default,
        bool allowMetadataFallback = true)
    {
        var revision = Volatile.Read(ref _libraryPublicationRevision);
        if (_publishingLibraryFolders.ContainsKey(folderId)) return await LoadCachedFolderAsync(folderId, path, token);
        if (!allowMetadataFallback && !_mappings.ContainsKey(NormalizeFolderId(folderId)))
            return await LoadCachedFolderAsync(folderId, path, token);
        var mapping = await GetMappingAsync(folderId, token);
        var serial = await _serials.LoadFolderAsync(mapping, token);
        if (serial == null) return allowMetadataFallback ? await RefreshFolderAsync(folderId, path, token) : await LoadCachedFolderAsync(folderId, path, token);
        await _entryMutationGate.WaitAsync(token);
        try
        {
            if (_publishingLibraryFolders.ContainsKey(folderId) || revision != Volatile.Read(ref _libraryPublicationRevision))
                return await LoadCachedFolderAsync(folderId, path, token);
            var cached = _store.LoadCachedEntriesForFolder(folderId);
            var entries = _serials.MaterializeEntries(serial, cached, path, MappingSnapshot()).ToList();
            // An open upload index can contain confirmed messages newer than its serial.
            if (Volatile.Read(ref _uploadIndexBatches) > 0)
            {
                var known = entries.Select(EntryIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase);
                entries.AddRange(cached.Where(entry => known.Add(EntryIdentity(entry))));
            }
            NormalizeFolderEntries(entries, folderId, path);
            RecordDescriptors(entries);
            _store.CacheFolderEntries(folderId, entries);
            return entries;
        }
        finally { _entryMutationGate.Release(); }
    }

    internal async Task RefreshSerialIndexAsync(CancellationToken token = default,
        Func<string, IReadOnlyList<AppEntry>, Task>? onFolderRefreshed = null)
    {
        await _serverIndexRefreshGate.WaitAsync(token);
        try
        {
            await RefreshMappingsAsync(token);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            async Task Read(string id, string path)
            {
                if (!visited.Add(id)) return;
                var entries = await LoadFolderSerialAsync(id, path, token, allowMetadataFallback: false);
                if (onFolderRefreshed != null) await onFolderRefreshed(id, entries);
                foreach (var folder in entries.Where(entry => entry.Type == "folder" && _mappings.ContainsKey(entry.FolderId)))
                    await Read(folder.FolderId, PathRules.ChildPath(folder));
            }
            await Read(RootFolderId, "/");
            if (_mappings.ContainsKey(SynchronizedRootFolderId)) await Read(SynchronizedRootFolderId, SynchronizedRootPath);
            if (_mappings.ContainsKey(TrashFolderId)) await Read(TrashFolderId, TrashPath);
        }
        finally { _serverIndexRefreshGate.Release(); }
    }
}
