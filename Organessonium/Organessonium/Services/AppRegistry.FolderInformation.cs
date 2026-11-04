using Organessonium.Models;

namespace Organessonium.Services;

public sealed record FolderInformation(long FileCount, long SizeBytes, long StorageBytes);

public sealed partial class AppRegistry
{
    public async Task<FolderInformation> GetFolderInformationAsync(string folderId, string path,
        CancellationToken token = default)
    {
        await using var priority = SharedStorage.PrioritizeUploads();
        var pending = new Queue<(string Id, string Path)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue((NormalizeFolderId(folderId), PathRules.Normalize(path)));
        long count = 0, size = 0, storage = 0;
        while (pending.TryDequeue(out var folder))
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(folder.Id)) continue;
            await AppMemoryBudget.ReclaimBeforeLibraryRenderAsync();
            var entries = await LoadFolderSerialAsync(folder.Id, folder.Path, token);
            // OSER v3 has no per-file sizes or version history. Hydrate this index
            // only when the local details cannot supply the requested totals.
            if (entries.Any(entry => entry.Type != "folder" && entry.LogicalChunkCount == 0))
                entries = await RefreshFolderAsync(folder.Id, folder.Path, token);
            foreach (var entry in entries)
            {
                if (entry.Type == "folder")
                {
                    pending.Enqueue((entry.FolderId, PathRules.ChildPath(entry)));
                    continue;
                }
                if (!files.Add(string.IsNullOrEmpty(entry.FileId) ? entry.MetadataMessageId : entry.FileId)) continue;
                checked { count++; size += Math.Max(0, entry.SizeBytes); storage += FileVersioning.StorageBytes(entry); }
            }
        }
        return new FolderInformation(count, size, storage);
    }
}
