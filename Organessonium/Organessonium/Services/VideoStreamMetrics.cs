using System.Diagnostics;

namespace Organessonium.Services;

internal sealed class VideoStreamMetrics
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private long _bytes, _latencyTicks, _firstByteTicks;
    private int _active, _completed, _failed, _retries, _headers, _firstBytes;

    internal void Start(bool retry)
    {
        Interlocked.Increment(ref _active);
        if (retry) Interlocked.Increment(ref _retries);
    }
    internal void Headers(long started)
    {
        Interlocked.Add(ref _latencyTicks, Stopwatch.GetTimestamp() - started);
        Interlocked.Increment(ref _headers);
    }
    internal void Receive(int bytes, long started, bool first)
    {
        Interlocked.Add(ref _bytes, bytes);
        if (!first) return;
        Interlocked.Add(ref _firstByteTicks, Stopwatch.GetTimestamp() - started);
        Interlocked.Increment(ref _firstBytes);
    }
    internal void Finish(bool completed, bool failed)
    {
        if (completed) Interlocked.Increment(ref _completed);
        if (failed) Interlocked.Increment(ref _failed);
        Interlocked.Decrement(ref _active);
    }
    internal object Snapshot()
    {
        var headers = Volatile.Read(ref _headers);
        var firstBytes = Volatile.Read(ref _firstBytes);
        var bytes = Interlocked.Read(ref _bytes);
        return new
        {
            bytes, elapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
            active = Volatile.Read(ref _active), completed = Volatile.Read(ref _completed),
            failed = Volatile.Read(ref _failed), retries = Volatile.Read(ref _retries),
            headersMs = headers == 0 ? (double?)null : Interlocked.Read(ref _latencyTicks) * 1000.0 / Stopwatch.Frequency / headers,
            firstByteMs = firstBytes == 0 ? (double?)null : Interlocked.Read(ref _firstByteTicks) * 1000.0 / Stopwatch.Frequency / firstBytes
        };
    }
}
