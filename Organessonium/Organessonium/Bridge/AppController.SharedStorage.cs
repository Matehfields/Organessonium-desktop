using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private async Task<List<UploadTask>> PlanSharedUploadsAsync(List<UploadTask> tasks, List<AppEntry> entries,
        AppRegistry registry, CancellationToken token)
    {
        var planned = new List<UploadTask>(tasks.Count);
        foreach (var task in tasks)
        {
            if (task.File.Length > DiscordLimits.DedicatedThreadThresholdBytes) { planned.Add(task); continue; }
            var size = task.File.Length;
            var partBytes = Math.Min(size, EncryptionService.MaximumPlaintextPayloadBytes);
            var transfer = await _transferManager.CreateUploadAsync(task.File, task.Path, FolderIdForPath(entries, task.Path),
                GuessFileMime(task.File.Name), partBytes, ArchiveSplitter.GetPartCount(size, EncryptionService.MaximumPlaintextPayloadBytes), token);
            transfer.FileId = task.Replacement?.Metadata.FileId ?? Guid.NewGuid().ToString("N");
            transfer.Replacement = task.Replacement;
            transfer.Encryption = _encryption.CreateFileEncryption(_store.Load().EncryptionMode);
            transfer.SharedAllocationPending = true;
            await _transferManager.SaveAsync(transfer, token);
            planned.Add(task with { PreparedTransfer = transfer });
        }
        var requests = planned.Where(task => task.PreparedTransfer != null)
            .Select(task => (task.PreparedTransfer!.FileId, task.PreparedTransfer.TransferId)).ToArray();
        var reservations = await registry.SharedStorage.ReserveManyAsync(requests, token);
        foreach (var task in planned.Where(task => task.PreparedTransfer != null))
        {
            var transfer = task.PreparedTransfer!;
            var reservation = reservations[transfer.TransferId];
            transfer.Storage = reservation.Layout; transfer.ThreadId = reservation.ThreadId;
            transfer.PayloadLayoutVersion = TransferPayloadLayouts.SharedStorage; transfer.SharedAllocationPending = false;
            await _transferManager.SaveAsync(transfer, token);
        }
        return planned;
    }
}
