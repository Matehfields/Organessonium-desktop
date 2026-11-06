using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedPayloadBatcher
{
    private readonly object _productionSync = new();
    private readonly Dictionary<(string Thread, string Version, string Role), ProductionExpectation> _production = new();

    internal ProductionExpectation ExpectProduction(SharedStorageReservation reservation, string role, CancellationToken token,
        Func<bool>? waitingForPublication = null)
    {
        var key = (reservation.ThreadId, reservation.VersionId, role);
        var expectation = new ProductionExpectation(this, key)
        { Preparing = waitingForPublication == null, WaitingForPublication = waitingForPublication };
        lock (_productionSync) _production.Add(key, expectation);
        expectation.Registration = token.Register(expectation.Dispose);
        if (expectation.Finished) expectation.Registration.Dispose();
        return expectation;
    }

    private bool HasPendingProduction(string threadId)
    {
        lock (_productionSync) return _production.Keys.Any(key => key.Thread == threadId);
    }

    private bool IsExpectedProduction(SharedStorageReservation reservation, string role)
    {
        lock (_productionSync) return _production.ContainsKey((reservation.ThreadId, reservation.VersionId, role));
    }

    private bool HasOtherProduction(string threadId, IEnumerable<string> versions)
    {
        var ready = versions.ToHashSet(StringComparer.Ordinal);
        lock (_productionSync) return _production.Keys.Any(key => key.Thread == threadId && !ready.Contains(key.Version));
    }

    private bool WaitingOnlyForPublication(string threadId)
    {
        lock (_productionSync)
        {
            var pending = _production.Where(pair => pair.Key.Thread == threadId).Select(pair => pair.Value).ToArray();
            return pending.Length > 0 && pending.All(item => !item.Preparing && item.WaitingForPublication?.Invoke() == true);
        }
    }

    private void ProductionReady(SharedStorageReservation reservation, string role)
    {
        ProductionExpectation? expectation;
        lock (_productionSync) _production.TryGetValue((reservation.ThreadId, reservation.VersionId, role), out expectation);
        expectation?.Dispose();
    }

    internal sealed class ProductionExpectation(SharedPayloadBatcher owner, (string Thread, string Version, string Role) key) : IDisposable
    {
        private int _finished;
        internal bool Finished => Volatile.Read(ref _finished) != 0;
        internal CancellationTokenRegistration Registration;
        internal bool Preparing;
        internal Func<bool>? WaitingForPublication;
        internal void Activate()
        {
            lock (owner._productionSync) if (!Finished) Preparing = true;
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            lock (owner._productionSync)
                if (owner._production.TryGetValue(key, out var current) && ReferenceEquals(current, this)) owner._production.Remove(key);
            Registration.Dispose();
        }
    }
}
