using System.Collections.Concurrent;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
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
