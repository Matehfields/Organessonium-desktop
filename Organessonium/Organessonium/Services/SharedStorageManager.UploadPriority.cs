namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private sealed class MaintenancePriority
    {
        internal int Uploads;
        internal bool Pending;
        internal CancellationTokenSource? Cleanup;
        internal readonly HashSet<CancellationTokenSource> Background = [];
        internal Func<CleanupUpdate, bool>? CleanupProgress;
    }

    internal IAsyncDisposable PrioritizeUploads()
    {
        lock (_maintenance)
        {
            _maintenance.Uploads++;
            foreach (var background in _maintenance.Background) background.Cancel();
            if (_maintenance.Cleanup != null)
            {
                _maintenance.Pending = true;
                _maintenance.Cleanup.Cancel();
            }
        }
        return new UploadPriorityLease(this);
    }

    // Interactive metadata writes share the same preemption and deferred-cleanup contract.
    internal IAsyncDisposable PrioritizeMutations() => PrioritizeUploads();

    internal async Task RunBackgroundAsync(Func<CancellationToken, Task> work, CancellationToken lifetime)
    {
        while (true)
        {
            lifetime.ThrowIfCancellationRequested();
            using var run = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            var admitted = false;
            lock (_maintenance)
                if (_maintenance.Uploads == 0) { _maintenance.Background.Add(run); admitted = true; }
            if (!admitted) { await Task.Delay(1000, lifetime); continue; }
            try { await work(run.Token); return; }
            catch (OperationCanceledException) when (run.IsCancellationRequested && !lifetime.IsCancellationRequested) { }
            finally { lock (_maintenance) _maintenance.Background.Remove(run); }
            await Task.Delay(1000, lifetime);
        }
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
                using var operation = Diagnostics.DevelopmentDiagnostics.Begin("cleanup-background");
                await ReconcileAsync(_api.PayloadLifetime);
            }
            catch (OperationCanceledException) when (_api.PayloadLifetime.IsCancellationRequested) { }
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
