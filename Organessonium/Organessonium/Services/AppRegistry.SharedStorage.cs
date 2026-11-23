using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    internal const string StorageJournalFolderId = "storage-v2";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> StorageJournalGates = new(StringComparer.Ordinal);

    internal async Task<FolderIndexMapping> GetStorageJournalIndexAsync(CancellationToken token)
    {
        var gate = StorageJournalGates.GetOrAdd(_settings.NamesChannelId + ":" + _settings.FoldersForumId,
            _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            // Establish root first, preserving the existing empty-account initialization contract.
            await GetMappingAsync(RootFolderId, token);
            if (_mappings.TryGetValue(StorageJournalFolderId, out var cached))
            {
                await VerifyFolderDestinationAsync(cached, StorageJournalFolderId, token);
                return cached;
            }
            await RefreshMappingsAsync(token);
            if (_mappings.TryGetValue(StorageJournalFolderId, out var existing))
            {
                await VerifyFolderDestinationAsync(existing, StorageJournalFolderId, token);
                return existing;
            }
            // No folder entry is published in root: this is an internal index, not a library card.
            return await CreateFolderIndexAsync(StorageJournalFolderId, RootFolderId, StorageJournalFolderId,
                "/@storage-v2", token, synchronizeSerials: false);
        }
        finally { gate.Release(); }
    }
}
