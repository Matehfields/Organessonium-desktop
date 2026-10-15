using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedPayloadBatcher
{
    private readonly object _productionSync = new();
    private readonly Dictionary<(string Thread, string Version, string Role), ProductionExpectation> _production = new();

    internal ProductionExpectation ExpectProduction(SharedStorageReservation reservation, string role, CancellationToken token)
    {
        var key = (reservation.ThreadId, reservation.VersionId, role);
        var expectation = new ProductionExpectation(this, key);
        lock (_productionSync) _production.Add(key, expectation);
        expectation.Registration = token.Register(expectation.Dispose);
        if (expectation.Finished) expectation.Registration.Dispose();
        return expectation;
    }

    private bool HasPendingProduction(string threadId)
    {
        lock (_productionSync) return _production.Keys.Any(key => key.Thread == threadId);
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
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            lock (owner._productionSync)
                if (owner._production.TryGetValue(key, out var current) && ReferenceEquals(current, this)) owner._production.Remove(key);
            Registration.Dispose();
        }
    }
}
