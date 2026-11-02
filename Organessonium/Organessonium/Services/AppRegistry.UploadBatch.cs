using System.Collections.Concurrent;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    private async Task<string> AddUploadFileEntryAsync(Models.AppEntry entry, CancellationToken token,
        Func<TimeSpan, Task>? onRateLimitWait)
    {
        entry.Normalize();
        entry.ParentFolderId = NormalizeFolderId(entry.ParentFolderId);
        var parent = await GetMappingAsync(entry.ParentFolderId, token);
        await _entryMutationGate.WaitAsync(token);
        try
        {
            await OpenEntryIndexAsync(parent.ThreadId, false, token, onRateLimitWait);
            // Preserve the publication/cache ordering used by folder reconciliation.
            // The journal's independent confirmations do not need this index gate.
            await _metadata.SendEntryAsync(_discord, parent.ThreadId, entry, token, onRateLimitWait);
            _store.CacheUploadedEntry(entry);
        }
        finally { _entryMutationGate.Release(); }
        return entry.MetadataMessageId;
    }

    private readonly ConcurrentDictionary<string, byte> _uploadIndexThreads = new(StringComparer.Ordinal);
    private int _uploadIndexBatches;

    internal async Task<IAsyncDisposable> BeginUploadIndexBatchAsync()
    {
        await _entryMutationGate.WaitAsync();
        try
        {
            _uploadIndexBatches++;
            return new UploadIndexBatch(this);
        }
        finally { _entryMutationGate.Release(); }
    }

    // Called under the entry mutation gate; overlapping upload batches share the open indices.
    private async Task OpenEntryIndexAsync(string threadId, bool synchronizeSerials, CancellationToken token,
        Func<TimeSpan, Task>? onRateLimitWait)
    {
        var deferred = !synchronizeSerials && _uploadIndexBatches > 0;
        if (deferred && !_uploadIndexThreads.TryAdd(threadId, 0)) return;
        try { await _discord.UnarchiveThreadAsync(threadId, token, onRateLimitWait); }
        catch
        {
            if (deferred) _uploadIndexThreads.TryRemove(threadId, out _);
            throw;
        }
    }

    private async Task EndUploadIndexBatchAsync()
    {
        await _entryMutationGate.WaitAsync();
        try
        {
            if (--_uploadIndexBatches != 0) return;
            foreach (var threadId in _uploadIndexThreads.Keys)
            {
                _uploadIndexThreads.TryRemove(threadId, out _);
                await ArchiveFolderThreadAsync(threadId, CancellationToken.None);
            }
        }
        finally { _entryMutationGate.Release(); }
    }

    private sealed class UploadIndexBatch(AppRegistry owner) : IAsyncDisposable
    {
        private AppRegistry? _owner = owner;
        public async ValueTask DisposeAsync()
        {
            var registry = Interlocked.Exchange(ref _owner, null);
            if (registry != null) await registry.EndUploadIndexBatchAsync();
        }
    }
}
