using System.Diagnostics;
using System.Net.Http;

namespace Organessonium.Services;

// Transmission slots are independent of the bounded number of replies still pending.
internal sealed class UploadBodySlot(AdaptiveUploadGate? gate, SemaphoreSlim? shared, long bytes) : IDisposable
{
    internal static readonly HttpRequestOptionsKey<UploadBodySlot> RequestKey = new("Organessonium.UploadBodySlot");
    private AdaptiveUploadGate.Lease? _lease;
    private int _sharedEntered;
    private long _started;
    private int _completed;

    internal async Task EnterAsync(CancellationToken token)
    {
        if (gate != null) _lease = await gate.EnterAsync(token).ConfigureAwait(false);
        if (shared != null)
        {
            await shared.WaitAsync(token).ConfigureAwait(false);
            Interlocked.Exchange(ref _sharedEntered, 1);
        }
        _started = Stopwatch.GetTimestamp();
    }

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        gate?.ReportCompleted(bytes, Stopwatch.GetElapsedTime(_started));
        Dispose();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _lease, null)?.Dispose();
        if (Interlocked.Exchange(ref _sharedEntered, 0) != 0) shared!.Release();
    }
}
