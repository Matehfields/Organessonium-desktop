using System.Collections.Concurrent;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    private readonly object _serialBatchSync = new();
    private SerialBatch? _serialBatch;

    internal IAsyncDisposable BeginSerialBatch()
    {
        lock (_serialBatchSync)
        {
            var batch = _serialBatch ??= new SerialBatch();
            batch.Users++;
            return new SerialBatchLease(this, batch);
        }
    }

    private bool DeferFolderSerial(string folderId)
    {
        lock (_serialBatchSync)
        {
            if (_serialBatch == null) return false;
            _serialBatch.Folders[NormalizeFolderId(folderId)] = 0;
            return true;
        }
    }

    private bool DeferFavoritesSerial(IEnumerable<AppEntry> additional)
    {
        lock (_serialBatchSync)
        {
            if (_serialBatch == null) return false;
            _serialBatch.Favorites = true;
            foreach (var entry in additional) _serialBatch.Additional[EntryIdentity(entry)] = entry;
            return true;
        }
    }

    private async Task CompleteSerialBatchAsync(SerialBatch batch)
    {
        // Reconciliations from other tasks defer too. Overlapping operations flush
        // once, after the last scope, instead of publishing intermediate snapshots.
        lock (_serialBatchSync)
        {
            if (--batch.Users > 0) return;
            _serialBatch = null;
        }
        await SynchronizeFolderSerialsAsync(batch.Folders.Keys, CancellationToken.None);
        if (batch.Favorites)
        {
            var current = batch.Folders.Keys.SelectMany(_store.LoadCachedEntriesForFolder)
                .GroupBy(EntryIdentity).ToDictionary(group => group.Key, group => group.Last());
            var additional = batch.Additional.Values.Select(entry => current.GetValueOrDefault(EntryIdentity(entry), entry));
            await RebuildFavoritesSerialAsync(additional, CancellationToken.None);
        }
    }

    private sealed class SerialBatch
    {
        internal int Users;
        internal bool Favorites;
        internal readonly ConcurrentDictionary<string, byte> Folders = new(StringComparer.OrdinalIgnoreCase);
        internal readonly ConcurrentDictionary<string, AppEntry> Additional = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SerialBatchLease(AppRegistry? registry, SerialBatch? batch) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && registry != null && batch != null)
                await registry.CompleteSerialBatchAsync(batch);
        }
    }
}
