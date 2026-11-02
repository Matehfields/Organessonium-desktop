namespace Organessonium.Services;

internal sealed class TransferPauseState(CancellationToken lifetime) : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource _attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    private TaskCompletionSource? _resume;
    private bool _finishing;
    private bool _disposed;
    internal CancellationToken Token { get { lock (_sync) return _attempt.Token; } }
    internal bool Requested { get { lock (_sync) return _resume != null; } }

    internal void Toggle(bool force = false)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_resume != null) { _resume.TrySetResult(); return; }
            if (_finishing && !force) return;
            _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _attempt.Cancel();
        }
    }

    internal async Task ResumeAsync()
    {
        Task ready;
        lock (_sync) ready = _resume!.Task;
        await ready.WaitAsync(lifetime);
        lock (_sync)
        {
            _attempt.Dispose();
            _attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _resume = null;
            _finishing = false;
        }
    }

    internal void FinishPublication() { lock (_sync) _finishing = true; }

    public void Dispose() { lock (_sync) { _disposed = true; _attempt.Dispose(); } }
}
