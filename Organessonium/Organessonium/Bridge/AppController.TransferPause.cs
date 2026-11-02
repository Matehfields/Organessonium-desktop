using System.Collections.Concurrent;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private readonly ConcurrentDictionary<string, TransferPauseState> _transferPauses = new();
    private readonly ConcurrentDictionary<string, bool> _pendingTransferPauses = new();
    private readonly ConcurrentDictionary<string, TransferState> _operationTransferStates = new();

    private static string TransferKey(string op, int index = -1) => index < 0 ? op : op + ":" + index;
    private bool PauseRequested(string op, int index = -1) =>
        _transferPauses.TryGetValue(TransferKey(op, index), out var pause) && pause.Requested &&
        (index < 0 || !_uploadFileOperations.TryGetValue(TransferKey(op, index), out var lifetime) || !lifetime.IsCancellationRequested);

    private TransferPauseState RegisterTransferPause(string key, CancellationToken lifetime)
    {
        var state = new TransferPauseState(lifetime);
        _transferPauses[key] = state;
        if (_pendingTransferPauses.TryRemove(key, out var requested) && requested) state.Toggle();
        return state;
    }

    private void ToggleTransferPause(string op, int index)
    {
        if (!_transferOperations.ContainsKey(op)) return;
        var key = TransferKey(op, index);
        if (_transferPauses.TryGetValue(key, out var state)) state.Toggle();
        else _pendingTransferPauses.AddOrUpdate(key, true, (_, value) => !value);
    }

    private async Task RunPausableUploadAsync(string op, int index, UploadTask task, CancellationToken lifetime,
        Func<UploadTask, CancellationToken, Task> attempt, Action<UploadTask, CancellationToken> prepareResume)
    {
        var key = TransferKey(op, index);
        using var pause = RegisterTransferPause(key, lifetime);
        try
        {
            while (true)
            {
                try
                {
                    await attempt(task, pause.Token);
                    if (lifetime.IsCancellationRequested)
                        await CancelUploadCheckpointAsync(_operationTransferStates.GetValueOrDefault(key) ?? task.PreparedTransfer);
                    return;
                }
                catch (Exception error) when (!lifetime.IsCancellationRequested &&
                    (error is OperationCanceledException && pause.Requested || TransferMemoryPressureException.Find(error) != null))
                {
                    if (TransferMemoryPressureException.Find(error) != null && !pause.Requested) pause.Toggle(force: true);
                    var checkpoint = _operationTransferStates.GetValueOrDefault(key) ?? task.PreparedTransfer;
                    if (checkpoint != null) await _transferManager.PauseAsync(checkpoint, new OperationCanceledException("Pausado pelo usuário."));
                    await OpsUploadFileProgressAsync(op, index, "paused", -1, TransferMemoryPressureException.Find(error)?.Message ?? "Pausado");
                    await pause.ResumeAsync();
                    await _bridge.CallAsync("_opsTransferResumed", op, index);
                    task = task with { PreparedTransfer = checkpoint, NeedsRemoteRecovery = checkpoint != null &&
                        Registry().SharedStorage.Batcher.HasUncertainPublication(checkpoint.ThreadId, checkpoint.TransferId) };
                    prepareResume(task, pause.Token);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            var checkpoint = _operationTransferStates.GetValueOrDefault(key) ?? task.PreparedTransfer;
            await CancelUploadCheckpointAsync(checkpoint);
            await OpsUploadFileProgressAsync(op, index, "cancelled", -1, "Cancelado");
            if (_transferOperations.TryGetValue(op, out var operation) && operation.IsCancellationRequested) throw;
        }
        finally { _transferPauses.TryRemove(key, out _); _operationTransferStates.TryRemove(key, out _); _pendingTransferPauses.TryRemove(key, out _); }
    }

    private async Task CancelUploadCheckpointAsync(TransferState? state)
    {
        if (state == null || state.Status is TransferStatuses.Completed or TransferStatuses.Cancelled) return;
        await Registry().SharedStorage.CancelReservationAsync(state, CancellationToken.None, deferCleanup: true);
        var settings = _store.Load();
        await _transferManager.CancelAsync(state, settings.KeepTransferHistory, settings.CleanupTempOnCancel, CancellationToken.None);
    }

    private async Task RunPausableDownloadAsync(string op, TransferState checkpoint, CancellationToken lifetime,
        Func<CancellationToken, Task> attempt)
    {
        using var pause = RegisterTransferPause(op, lifetime);
        try
        {
            while (true)
            {
                try { await attempt(pause.Token); return; }
                catch (Exception error) when (!lifetime.IsCancellationRequested &&
                    (error is OperationCanceledException && pause.Requested || TransferMemoryPressureException.Find(error) != null))
                {
                    if (TransferMemoryPressureException.Find(error) != null && !pause.Requested) pause.Toggle(force: true);
                    await _transferManager.PauseAsync(checkpoint, new OperationCanceledException("Pausado pelo usuário."));
                    await OpsUpdateAsync(op, "paused", TransferMemoryPressureException.Find(error)?.Message ?? "Pausado");
                    await pause.ResumeAsync();
                    await _bridge.CallAsync("_opsTransferResumed", op, -1);
                    await OpsUpdateAsync(op, "active", "Retomando...");
                }
            }
        }
        finally { _transferPauses.TryRemove(op, out _); _pendingTransferPauses.TryRemove(op, out _); }
    }
}
