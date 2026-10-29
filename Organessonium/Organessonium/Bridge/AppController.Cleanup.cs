using System.Collections.Concurrent;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private readonly ConcurrentDictionary<string, byte> _pendingPhysicalCleanup = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cleanupUiGate = new(1, 1);
    private IDisposable? _cleanupObserver;
    private SharedStorageManager? _cleanupRegistry;
    private string? _cleanupOperationId;
    private bool _cleanupOperationFinished;
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
            _ = UpdateCleanupUiAsync(generation, update.Waiting);
            return !_pendingPhysicalCleanup.IsEmpty;
        }, token);
    }

    private async Task QueuePhysicalCleanupAsync(AppRegistry registry, IReadOnlyList<StorageCleanupRecord> requests)
    {
        if (requests.Count == 0) return;
        ObserveRegistryCleanup(registry);
        foreach (var request in requests) _pendingPhysicalCleanup.TryAdd(request.OperationId, 0);
        await UpdateCleanupUiAsync(Volatile.Read(ref _cleanupGeneration), waiting: true);
        registry.SharedStorage.RequestCleanup();
    }

    private async Task UpdateCleanupUiAsync(int generation, bool waiting)
    {
        await _cleanupUiGate.WaitAsync();
        try
        {
            if (_loggingOut || generation != Volatile.Read(ref _cleanupGeneration)) return;
            var count = _pendingPhysicalCleanup.Count;
            if (count == 0)
            {
                if (_cleanupOperationId != null && !_cleanupOperationFinished)
                {
                    _cleanupOperationFinished = true;
                    await OpsUpdateAsync(_cleanupOperationId, "done", "Limpeza física concluída");
                }
                return;
            }
            if (_cleanupOperationId == null || _cleanupOperationFinished)
            {
                _cleanupOperationId = NewId(); _cleanupOperationFinished = false;
                await OpsAddAsync(_cleanupOperationId, "cleanup", "Limpeza em segundo plano");
            }
            await OpsUpdateAsync(_cleanupOperationId, waiting ? "waiting" : "active", waiting
                ? $"{count} limpeza(s) pendente(s); aguardando nova tentativa"
                : $"Conferindo referências e liberando dados: {count} pendente(s)");
        }
        catch (Exception error) { AppLogger.Error("Cleanup.Progress", error); }
        finally { _cleanupUiGate.Release(); }
    }

    private void ResetCleanupMonitoring()
    {
        Interlocked.Increment(ref _cleanupGeneration);
        _cleanupObserver?.Dispose(); _cleanupObserver = null; _cleanupRegistry = null;
        _pendingPhysicalCleanup.Clear(); _cleanupOperationId = null; _cleanupOperationFinished = false;
    }
}
