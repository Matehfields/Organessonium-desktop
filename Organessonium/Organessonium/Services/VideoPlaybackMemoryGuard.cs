namespace Organessonium.Services;

/// <summary>Reclaim idle memory without unloading a healthy decoder; stop only at persistent critical pressure.</summary>
internal sealed class VideoPlaybackMemoryGuard : IDisposable
{
    internal const long ReclaimBytes = AppMemoryBudget.AdmissionBytes - 128L * 1024 * 1024;
    internal const long PressureBytes = AppMemoryBudget.AdmissionBytes + 64L * 1024 * 1024;
    private const int CheckIntervalMilliseconds = 100;
    private readonly Timer _timer;
    private readonly Func<bool> _active;
    private readonly Func<Task> _release;
    private readonly Func<Task> _reclaim;
    private readonly Func<long> _committed;
    private int _recovering;
    private long _nextReclaim;
    private volatile bool _disposed;

    internal VideoPlaybackMemoryGuard(Func<bool> active, Func<Task> release, Func<Task> reclaim, Func<long>? committed = null)
    {
        _active = active;
        _release = release;
        _reclaim = reclaim;
        _committed = committed ?? AppMemoryBudget.CommittedBytes;
        _timer = new Timer(Check, null, CheckIntervalMilliseconds, CheckIntervalMilliseconds);
    }

    private async void Check(object? state)
    {
        if (_disposed || Interlocked.CompareExchange(ref _recovering, 1, 0) != 0) return;
        try
        {
            if (_disposed || !_active()) return;
            var used = _committed();
            if (used < ReclaimBytes || (used < PressureBytes && Environment.TickCount64 < _nextReclaim)) return;
            _nextReclaim = Environment.TickCount64 + 5000;
            await _reclaim().ConfigureAwait(false);
            if (!_disposed && _active() && _committed() >= PressureBytes)
                await _release().ConfigureAwait(false);
        }
        catch (Exception error) { AppLogger.Error("Video.MemoryGuard", error); }
        finally { Volatile.Write(ref _recovering, 0); }
    }

    public void Dispose() { _disposed = true; _timer.Dispose(); }
}
