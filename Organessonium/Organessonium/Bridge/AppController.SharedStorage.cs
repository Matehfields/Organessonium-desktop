using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private async Task<List<UploadTask>> PlanSharedUploadsAsync(List<UploadTask> tasks, List<AppEntry> entries,
        AppRegistry registry, CancellationToken token)
    {
        var planned = tasks.ToArray();
        var encryptionMode = _store.Load().EncryptionMode;
        var preparation = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token };
        await Parallel.ForEachAsync(Enumerable.Range(0, tasks.Count), preparation, async (index, cancellation) =>
        {
            var task = tasks[index];
            if (task.File.Length > DiscordLimits.DedicatedThreadThresholdBytes) return;
            var size = task.File.Length;
            var partBytes = Math.Min(size, EncryptionService.MaximumPlaintextPayloadBytes);
            var transfer = await _transferManager.CreateUploadAsync(task.File, task.Path, FolderIdForPath(entries, task.Path),
                GuessFileMime(task.File.Name), partBytes, ArchiveSplitter.GetPartCount(size, EncryptionService.MaximumPlaintextPayloadBytes), cancellation);
            transfer.FileId = task.Replacement?.Metadata.FileId ?? Guid.NewGuid().ToString("N");
            transfer.Replacement = task.Replacement;
            transfer.Encryption = _encryption.CreateFileEncryption(encryptionMode);
            transfer.SharedAllocationPending = true;
            await _transferManager.SaveAsync(transfer, cancellation);
            planned[index] = task with { PreparedTransfer = transfer };
        });
        var requests = planned.Where(task => task.PreparedTransfer != null)
            .Select(task => (task.PreparedTransfer!.FileId, task.PreparedTransfer.TransferId)).ToArray();
        var reservations = await registry.SharedStorage.ReserveManyAsync(requests, token);
        await Parallel.ForEachAsync(planned.Where(task => task.PreparedTransfer != null), preparation, async (task, cancellation) =>
        {
            var transfer = task.PreparedTransfer!;
            var reservation = reservations[transfer.TransferId];
            transfer.Storage = reservation.Layout; transfer.ThreadId = reservation.ThreadId;
            transfer.PayloadLayoutVersion = TransferPayloadLayouts.SharedStorage; transfer.SharedAllocationPending = false;
            await _transferManager.SaveAsync(transfer, cancellation);
        });
        return planned.ToList();
    }
}
