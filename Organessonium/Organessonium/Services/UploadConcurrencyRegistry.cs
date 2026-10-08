using System.Collections.Concurrent;

namespace Organessonium.Services;

internal static class UploadConcurrencyRegistry
{
    private static readonly ConcurrentDictionary<AdaptiveUploadGate, byte> Active = new();
    internal static IDisposable Register(AdaptiveUploadGate gate)
    {
        Active[gate] = 0;
        return new Registration(gate);
    }
    internal static void Apply(bool force, int configuredWorkers)
    {
        foreach (var gate in Active.Keys)
        {
            try { gate.Reconfigure(force ? 16 : configuredWorkers, adaptive: !force); }
            catch (ObjectDisposedException) { Active.TryRemove(gate, out _); }
        }
    }
    private sealed class Registration(AdaptiveUploadGate gate) : IDisposable
    {
        public void Dispose() => Active.TryRemove(gate, out _);
    }
}
