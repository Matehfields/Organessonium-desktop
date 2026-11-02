using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private string CancelOutboxDirectory => Path.Combine(_store.DataDirectory.FullName, "StorageCleanupOutbox",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OwnerId + ":" + _settings.BotToken + ":" + _settings.NamesChannelId + ":" + _settings.AppsForumId))));

    private async Task EnqueueCancelledUploadAsync(TransferState transfer)
    {
        if (string.IsNullOrEmpty(transfer.ThreadId) || transfer.Storage?.Shared != true && transfer.Replacement != null) return;
        var payload = new StoragePayloadVersion
        {
            FileId = transfer.FileId, VersionId = transfer.TransferId, ThreadId = transfer.ThreadId, Storage = transfer.Storage,
            Attachments = transfer.UploadedChunks.Where(chunk => !string.IsNullOrEmpty(chunk.AttachmentId))
                .Select(chunk => new AttachmentReference { MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId! }).ToList()
        };
        if (transfer.ThumbnailRef != null) payload.Attachments.Add(transfer.ThumbnailRef);
        payload.Attachments.AddRange((transfer.VideoPreview480pChunkRefs ?? []).Select(chunk => new AttachmentReference
            { MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId }));
        var request = new StorageCleanupRecord { OperationId = transfer.TransferId, FileId = transfer.FileId,
            Scope = "upload:" + transfer.TransferId, Payloads = [payload] };
        var directory = CancelOutboxDirectory;
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(transfer.TransferId))) + ".json";
        var body = JsonSerializer.Serialize(request, JsonOptions.Web);
        await Task.Run(() =>
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name); var temporary = path + ".tmp";
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { file.Write(Encoding.UTF8.GetBytes(body)); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        });
        RequestCleanup();
    }

    private async Task<bool> ImportCancelledUploadsAsync(CancellationToken token)
    {
        var directory = CancelOutboxDirectory;
        if (!Directory.Exists(directory)) return false;
        var files = Directory.GetFiles(directory, "*.json");
        if (files.Length == 0) return false;
        var imported = new System.Collections.Concurrent.ConcurrentBag<(string Path, StorageCleanupRecord Request)>();
        await using var journalWrite = await _api.AcquireThreadWriteAsync(await GetJournalThreadAsync(token), token);
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (path, cancellation) =>
        {
            var request = JsonSerializer.Deserialize<StorageCleanupRecord>(await File.ReadAllTextAsync(path, cancellation), JsonOptions.Web)
                ?? throw new InvalidDataException("Intent de cancelamento inválido.");
            await WriteRecordAsync(request, null, cancellation);
            await ReleaseReferenceAsync(request.Scope, request.FileId, cancellation);
            imported.Add((path, request));
        });
        // Release every cancelled version in the pool before GC. Otherwise two cancelled
        // versions of one identity could retain each other's reservations indefinitely.
        foreach (var group in imported.SelectMany(item => item.Request.Payloads).Where(payload => payload.Storage?.Shared == true)
            .GroupBy(payload => payload.Storage!.StorageId!))
        {
            if (!_catalog.TryGetValue(group.Key, out var known) || known.OwnerId != OwnerId) continue;
            var row = await ReloadCatalogAsync(known, token);
            var cancelled = group.Select(payload => (payload.FileId, payload.VersionId)).ToHashSet();
            var changed = false;
            foreach (var member in row.Members)
                changed |= member.PendingVersions.RemoveAll(version => cancelled.Contains((member.FileId, version))) > 0;
            if (changed) await SaveCatalogAsync(row, token);
        }
        foreach (var item in imported) File.Delete(item.Path);
        return true;
    }
}
