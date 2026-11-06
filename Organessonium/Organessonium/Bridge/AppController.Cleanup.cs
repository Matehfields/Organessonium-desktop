using System.Collections.Concurrent;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private readonly ConcurrentDictionary<string, byte> _pendingPhysicalCleanup = new(StringComparer.Ordinal);
    private IDisposable? _cleanupObserver;
    private SharedStorageManager? _cleanupRegistry;
    private int _cleanupGeneration;

    private void ObserveRegistryCleanup(AppRegistry registry)
    {
        var manager = registry.SharedStorage;
        if (ReferenceEquals(_cleanupRegistry, manager)) return;
        _cleanupObserver?.Dispose();
        _cleanupRegistry = manager;
        var generation = Volatile.Read(ref _cleanupGeneration);
        var token = RequireApi().PayloadLifetime;
        _cleanupObserver = manager.ObserveCleanup(update =>
        {
            if (_loggingOut || token.IsCancellationRequested || generation != Volatile.Read(ref _cleanupGeneration)) return false;
            if (update.Pending != null)
                foreach (var id in update.Pending) _pendingPhysicalCleanup.TryAdd(id, 0);
            if (update.Completed != null) _pendingPhysicalCleanup.TryRemove(update.Completed, out _);
            return !_pendingPhysicalCleanup.IsEmpty;
        }, token);
    }

    private Task QueuePhysicalCleanupAsync(AppRegistry registry, IReadOnlyList<StorageCleanupRecord> requests)
    {
        if (requests.Count == 0) return Task.CompletedTask;
        ObserveRegistryCleanup(registry);
        foreach (var request in requests) _pendingPhysicalCleanup.TryAdd(request.OperationId, 0);
        registry.SharedStorage.RequestCleanup();
        return Task.CompletedTask;
    }

    private void ResetCleanupMonitoring()
    {
        Interlocked.Increment(ref _cleanupGeneration);
        _cleanupObserver?.Dispose(); _cleanupObserver = null; _cleanupRegistry = null;
        _pendingPhysicalCleanup.Clear();
    }
}
