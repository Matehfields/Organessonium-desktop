using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class ForumDownloader
{
    private async Task<FileInfo> DownloadWithReadLeaseAsync(AppEntry entry, TransferState? checkpoint,
        Func<Task<FileInfo>> download, CancellationToken token)
    {
        if (_storageReads == null) return await download().ConfigureAwait(false);
        var scope = "download:" + (checkpoint?.TransferId ?? Guid.NewGuid().ToString("N"));
        var lease = await _storageReads.Manager.BeginReadAsync(scope, entry.FileId,
            SharedStorageManager.Payloads(MetadataService.CreateFileMetadata(entry)).Take(1).ToList(), token);
        try
        {
            var result = await download().ConfigureAwait(false);
            await lease.DisposeAsync(); return result;
        }
        catch
        {
            if (checkpoint != null && !token.IsCancellationRequested) await lease.LeaveRecoverableAsync();
            else
            {
                try { await lease.DisposeAsync(); }
                catch (Exception error) { AppLogger.Error("Storage.ReadRelease", error); }
            }
            throw;
        }
    }
}
