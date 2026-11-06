using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private async Task<List<UploadTask>> PlanSharedUploadsAsync(List<UploadTask> tasks, List<AppEntry> entries,
        AppRegistry registry, CancellationToken token)
    {
        var planned = tasks;
        var encryptionMode = _store.Load().EncryptionMode;
        var preparation = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token };
        await Parallel.ForEachAsync(Enumerable.Range(0, tasks.Count), preparation, async (index, cancellation) =>
        {
            var task = tasks[index];
            if (task.File.Length > DiscordLimits.DedicatedThreadThresholdBytes) return;
            var size = task.File.Length;
            var partBytes = Math.Min(size, EncryptionService.MaximumPlaintextPayloadBytes);
            var transfer = await _transferManager.CreateUploadAsync(task.File, task.Path, FolderIdForPath(entries, task.Path),
                GuessFileMime(task.File.Name), partBytes, ArchiveSplitter.GetPartCount(size, EncryptionService.MaximumPlaintextPayloadBytes), cancellation, persist: false);
            transfer.FileId = task.Replacement?.Metadata.FileId ?? Guid.NewGuid().ToString("N");
            transfer.Replacement = task.Replacement;
            transfer.Encryption = _encryption.CreateFileEncryption(encryptionMode);
            transfer.SharedAllocationPending = true;
            planned[index] = task with { PreparedTransfer = transfer };
            await _transferManager.SaveAsync(transfer, cancellation);
        });
        return planned;
    }

    private async Task<List<int>> AllocateNextUploadPoolAsync(List<UploadTask> tasks, List<int> pending,
        AppRegistry registry, CancellationToken token)
    {
        var next = pending.FirstOrDefault(index => tasks[index].PreparedTransfer is { } state &&
            (state.SharedAllocationPending || state.Storage?.Shared == true), -1);
        if (next < 0) return pending.ToList();
        var first = tasks[next].PreparedTransfer!;
        if (!first.SharedAllocationPending)
        {
            var existing = pending.Where(index => tasks[index].PreparedTransfer is { Storage.Shared: true } state &&
                state.ThreadId == first.ThreadId).ToList();
            if (await registry.SharedStorage.ValidateUploadPoolAsync(existing.Select(index => tasks[index].PreparedTransfer!).ToArray(), token))
                return existing;
            foreach (var index in existing)
            {
                var state = tasks[index].PreparedTransfer!;
                state.ThreadId = ""; state.Storage = null; state.SharedAllocationPending = true;
                state.PayloadLayoutVersion = TransferPayloadLayouts.MultiAttachment;
                await _transferManager.SaveAsync(state, token);
            }
        }
        var candidates = pending.Where(index => tasks[index].PreparedTransfer?.SharedAllocationPending == true).ToArray();
        var reservations = await registry.SharedStorage.ReserveManyAsync(candidates.Select(index =>
            (tasks[index].PreparedTransfer!.FileId, tasks[index].PreparedTransfer!.TransferId)).ToArray(), token, onePoolOnly: true);
        var allocated = candidates.Where(index => reservations.ContainsKey(tasks[index].PreparedTransfer!.TransferId)).ToList();
        await Parallel.ForEachAsync(allocated, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (index, cancellation) =>
        {
            var state = tasks[index].PreparedTransfer!;
            var reservation = reservations[state.TransferId];
            state.ThreadId = reservation.ThreadId; state.Storage = reservation.Layout;
            state.PayloadLayoutVersion = TransferPayloadLayouts.SharedStorage; state.SharedAllocationPending = false;
            await _transferManager.SaveAsync(state, cancellation);
        });
        return allocated;
    }
}
