namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private sealed class MaintenancePriority
    {
        internal int Uploads;
        internal bool Pending;
        internal CancellationTokenSource? Cleanup;
    }

    internal IAsyncDisposable PrioritizeUploads()
    {
        lock (_maintenance)
        {
            _maintenance.Uploads++;
            if (_maintenance.Cleanup != null)
            {
                _maintenance.Pending = true;
                _maintenance.Cleanup.Cancel();
            }
        }
        return new UploadPriorityLease(this);
    }

    private void ResumeDeferredMaintenance()
    {
        lock (_maintenance)
        {
            if (_maintenance.Uploads != 0 || _maintenance.Cleanup != null || !_maintenance.Pending) return;
            _maintenance.Pending = false;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // Logout or an account change must not restart work against the previous bot.
                var current = _store.Load();
                if (current.BotToken != _settings.BotToken || current.NamesChannelId != _settings.NamesChannelId ||
                    current.AppsForumId != _settings.AppsForumId) return;
                await ReconcileAsync();
            }
            catch (Exception error) { AppLogger.Error("SharedStorage.DeferredCleanup", error); }
        });
    }

    private sealed class UploadPriorityLease(SharedStorageManager manager) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            lock (manager._maintenance) manager._maintenance.Uploads--;
            manager.ResumeDeferredMaintenance();
            return ValueTask.CompletedTask;
        }
    }
}
