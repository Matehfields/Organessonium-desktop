using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    internal sealed record CleanupUpdate(IReadOnlyCollection<string>? Pending = null, string? Completed = null, bool Waiting = false);

    internal IDisposable ObserveCleanup(Func<CleanupUpdate, bool> observer, CancellationToken token) =>
        new CleanupSubscription(_maintenance, observer, token);

    internal void RequestCleanup()
    {
        lock (_maintenance) _maintenance.Pending = true;
        ResumeDeferredMaintenance();
    }

    private bool NotifyCleanup(CleanupUpdate update)
    {
        Func<CleanupUpdate, bool>? observers;
        lock (_maintenance) observers = _maintenance.CleanupProgress;
        var interested = false;
        if (observers != null)
            foreach (Func<CleanupUpdate, bool> observer in observers.GetInvocationList())
                try { interested |= observer(update); }
                catch (Exception error) { AppLogger.Error("SharedStorage.CleanupObserver", error); }
        return interested;
    }

    private sealed class CleanupSubscription : IDisposable
    {
        private readonly MaintenancePriority _owner;
        private Func<CleanupUpdate, bool>? _observer;
        private CancellationTokenRegistration _registration;
        internal CleanupSubscription(MaintenancePriority owner, Func<CleanupUpdate, bool> observer, CancellationToken token)
        {
            _owner = owner; _observer = observer;
            lock (owner) owner.CleanupProgress += observer;
            _registration = token.Register(Dispose);
        }
        public void Dispose()
        {
            var observer = Interlocked.Exchange(ref _observer, null);
            if (observer == null) return;
            lock (_owner) _owner.CleanupProgress -= observer;
            _registration.Dispose();
        }
    }
}
