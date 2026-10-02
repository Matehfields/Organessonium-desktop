namespace Organessonium.Services;

/// <summary>Full downloads yield between network blocks while playback fills a required buffer.</summary>
internal static class VideoNetworkPriority
{
    private static readonly object Sync = new();
    private static int _active;
    private static TaskCompletionSource? _idle;
    public static IDisposable Enter()
    {
        lock (Sync)
        {
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new Lease();
    }
    public static async Task YieldAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? waiting;
            lock (Sync) waiting = _active > 0 ? _idle?.Task : null;
            if (waiting == null) return;
            await waiting.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
    private sealed class Lease : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Sync) if (--_active == 0) { _idle?.TrySetResult(); _idle = null; }
        }
    }
}
