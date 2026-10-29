using System.IO;
using System.Net;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    internal async Task<StorageCleanupRecord> BeginCleanupAsync(AppEntry entry, CancellationToken token)
    {
        var request = new StorageCleanupRecord
        {
            FileId = entry.FileId,
            Scope = "file:" + entry.FileId,
            Payloads = Payloads(MetadataService.CreateFileMetadata(entry)),
            MetadataMessageId = entry.MetadataMessageId,
            MetadataThreadId = await LocateMetadataAsync(entry, token)
        };
        await WriteRecordAsync(request, null, token);
        return request;
    }

    internal async Task<bool> CompleteCleanupAsync(StorageCleanupRecord request, CancellationToken token)
    {
        await ReleaseReferenceAsync(request.Scope, request.FileId, token);
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            return await CleanAsync(request, token);
        }
        finally { _gate.Release(); }
    }

    internal async Task CompleteCleanupBatchAsync(IReadOnlyList<StorageCleanupRecord> requests,
        Func<StorageCleanupRecord, bool, Task> onCompleted, CancellationToken token)
    {
        if (requests.Count == 0) return;
        await Parallel.ForEachAsync(requests,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (request, cancellation) => await ReleaseReferenceAsync(request.Scope, request.FileId, cancellation));
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            await CleanBatchAsync(requests, onCompleted, token);
        }
        finally { _gate.Release(); }
    }

    private async Task CleanBatchAsync(IReadOnlyList<StorageCleanupRecord> requests,
        Func<StorageCleanupRecord, bool, Task> onCompleted, CancellationToken token, bool continueOnFailure = true)
    {
        if (requests.Count == 0) return;
        await using var writes = await HoldWritesAsync(requests.SelectMany(request => request.Payloads)
            .Where(payload => payload.Storage?.Shared == true && _catalog.Values.Any(row => row.ThreadId == payload.ThreadId && row.OwnerId == OwnerId))
            .Select(payload => payload.ThreadId), token);
        // Keep the owner gate for one fresh reference proof shared by this batch.
        // CleanAsync still requires a durable release (or verifies that metadata was removed).
        var live = await ReadCleanupReferencesAsync(token);
        var journal = requests.Any(request => request.Payloads.Any(payload => payload.Storage?.Shared == true))
            ? await ReadJournalMessagesAsync(null, token) : null;
        var pools = new Dictionary<string, IReadOnlyList<DiscordMessageDto>>(StringComparer.Ordinal);
        async Task<IReadOnlyList<DiscordMessageDto>> ReadPool(string thread)
        {
            if (pools.TryGetValue(thread, out var messages)) return messages;
            messages = await _api.GetMessagesAsync(thread, null, token);
            if (pools.Count == 4) pools.Remove(pools.Keys.First());
            return pools[thread] = messages;
        }
        foreach (var request in requests)
        {
            var cleaned = false;
            try { cleaned = await CleanAsync(request, token, live, journal, ReadPool); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (continueOnFailure) { AppLogger.Error("SharedStorage.BatchCleanup", error); }
            await onCompleted(request, cleaned);
        }
    }

    internal async Task ReconcileAsync(CancellationToken token = default)
    {
        var deferUntilNextAction = false;
        CancellationTokenSource lifetime;
        lock (_maintenance)
        {
            if (_maintenance.Uploads > 0 || _maintenance.Cleanup != null)
            {
                _maintenance.Pending = true;
                return;
            }
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _maintenance.Cleanup = lifetime;
            _maintenance.Pending = false;
        }
        try
        {
            await _gate.WaitAsync(lifetime.Token);
            try
            {
                await RefreshJournalAsync(lifetime.Token);
                await RecoverOwnedMutationsAsync(lifetime.Token);
                await RecoverOwnedReadsAsync(lifetime.Token);
                foreach (var applied in _cleanups.Values.Where(request => _appliedCleanups.Contains(request.OperationId)))
                    NotifyCleanup(new(Completed: applied.OperationId));
                var pending = _cleanups.Values.Where(request => !_appliedCleanups.Contains(request.OperationId)).ToArray();
                if (pending.Length > 0) NotifyCleanup(new(Pending: pending.Select(request => request.OperationId).ToArray()));
                await CleanBatchAsync(pending, (request, cleaned) =>
                {
                    var interested = NotifyCleanup(cleaned ? new(Completed: request.OperationId) : new(Waiting: true));
                    deferUntilNextAction |= !cleaned && interested;
                    return Task.CompletedTask;
                }, lifetime.Token, continueOnFailure: false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && lifetime.IsCancellationRequested)
        {
            NotifyCleanup(new(Waiting: true));
            lock (_maintenance) _maintenance.Pending = true;
        }
        catch
        {
            deferUntilNextAction = NotifyCleanup(new(Waiting: true));
            throw;
        }
        finally
        {
            lock (_maintenance) { _maintenance.Cleanup = null; lifetime.Dispose(); }
            if (deferUntilNextAction) { lock (_maintenance) _maintenance.Pending = true; }
            else if (!token.IsCancellationRequested) ResumeDeferredMaintenance();
        }
    }

    private async Task<bool> CleanAsync(StorageCleanupRecord request, CancellationToken token,
        StoragePayloadVersion[]? batchReferences = null,
        IReadOnlyList<DiscordMessageDto>? batchJournal = null,
        Func<string, Task<IReadOnlyList<DiscordMessageDto>>>? readPool = null)
    {
        if (_appliedCleanups.Contains(request.OperationId)) return true;
        if (_mutations.Values.Any(mutation => mutation.Pending && mutation.FileId == request.FileId)) return false;
        // Deletion is authorized only by a durable release, never by a missing local cache entry.
        if (!_references.TryGetValue(request.Scope, out var subject) || subject.State != "released")
        {
            if (!request.Scope.StartsWith("upload:", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(request.MetadataThreadId) || string.IsNullOrWhiteSpace(request.MetadataMessageId)) return false;
                try { await _api.GetMessageAsync(request.MetadataThreadId, request.MetadataMessageId, token); return false; }
                catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
            }
            await ReleaseReferenceAsync(request.Scope, request.FileId, token);
            _references[request.Scope] = new StorageReferenceRecord { Scope = request.Scope, FileId = request.FileId, State = "released" };
        }
        foreach (var payload in request.Payloads.Where(payload => payload.Storage?.Shared == true && payload.VersionId.Length > 0))
        {
            var recovered = await RecoverPayloadReferencesAsync(payload.FileId, payload.VersionId, payload.ThreadId, token,
                batchJournal, readPool != null ? await readPool(payload.ThreadId) : null);
            payload.Attachments = payload.Attachments.Concat(recovered.Select(item => new AttachmentReference
            { MessageId = item.Reference.MessageId, AttachmentId = item.Reference.AttachmentId })).Distinct().ToList();
        }
        var live = batchReferences ?? await ReadCleanupReferencesAsync(token);
        var protectedAttachments = live.SelectMany(payload => payload.Attachments.Select(attachment =>
            (payload.ThreadId, attachment.MessageId, attachment.AttachmentId))).ToHashSet();
        foreach (var threadGroup in request.Payloads.GroupBy(payload => payload.ThreadId, StringComparer.Ordinal))
        {
            var shared = _catalog.Values.FirstOrDefault(row => row.ThreadId == threadGroup.Key);
            if (shared != null)
            {
                _api.RegisterSharedThread(shared.ThreadId);
                if (shared.OwnerId != OwnerId) return false; // Another device records intent, not a destructive PATCH.
                shared = await ReloadCatalogAsync(shared, token);
                var completedVersions = _references.Values.Where(reference => reference.State == "live")
                    .SelectMany(reference => reference.Payloads).Where(payload => payload.FileId == request.FileId)
                    .Select(payload => payload.VersionId).ToHashSet(StringComparer.Ordinal);
                var member = shared.Members.FirstOrDefault(member => member.FileId == request.FileId);
                if (member != null && member.PendingVersions.Any(version => !completedVersions.Contains(version) &&
                    !request.Payloads.Any(payload => payload.VersionId == version))) return false;
                if (threadGroup.Any(payload => payload.Storage?.Shared != true)) return false;
                if (threadGroup.All(payload => payload.Attachments.Count == 0) && _plans.Values.Any(plan =>
                    plan.Parts.Any(part => part.FileId == request.FileId && threadGroup.Any(payload => payload.VersionId == part.VersionId)))) return false;
                await using var lease = await _api.AcquireThreadWriteAsync(shared.ThreadId, token);
                foreach (var messages in threadGroup.SelectMany(payload => payload.Attachments).Distinct().GroupBy(reference => reference.MessageId))
                {
                    if (messages.Key == shared.ControlMessageId) throw new InvalidDataException("A mensagem de controle não pode ser removida.");
                    DiscordMessageDto message;
                    try { message = await _api.GetMessageAsync(shared.ThreadId, messages.Key, token); }
                    catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { continue; }
                    var remove = messages.Where(reference => !protectedAttachments.Contains((shared.ThreadId, reference.MessageId, reference.AttachmentId)))
                        .Select(reference => reference.AttachmentId).ToHashSet(StringComparer.Ordinal);
                    var keep = message.Attachments.Where(attachment => !remove.Contains(attachment.Id)).Select(attachment => attachment.Id).ToArray();
                    if (messages.Any(reference => protectedAttachments.Contains((shared.ThreadId, reference.MessageId, reference.AttachmentId)))) return false;
                    if (keep.Length == message.Attachments.Count) continue;
                    if (keep.Length == 0)
                    {
                        // The same fresh/validated PATCH path is used before deleting a now empty message.
                        await _api.PreserveMessageAttachmentsAsync(shared.ThreadId, message.Id, [], token,
                            message.Attachments.Select(attachment => attachment.Id).ToArray());
                        await _api.DeleteMessageAsync(shared.ThreadId, message.Id, token);
                    }
                    else await _api.PreserveMessageAttachmentsAsync(shared.ThreadId, message.Id, keep, token,
                        message.Attachments.Select(attachment => attachment.Id).ToArray());
                }
                if (!live.Any(payload => payload.ThreadId == shared.ThreadId && payload.FileId == request.FileId))
                {
                    shared.Members.RemoveAll(member => member.FileId == request.FileId);
                    await SaveCatalogAsync(shared, token);
                }
            }
            else
            {
                // Explicitly shared metadata without a readable catalogue is not a legacy dedicated thread.
                if (threadGroup.Any(payload => payload.Storage?.Shared == true) || _api.IsSharedThread(threadGroup.Key)) return false;
                if (live.Any(payload => payload.ThreadId == threadGroup.Key)) return false;
                try { await _api.DeleteChannelAsync(threadGroup.Key, token); }
                catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
            }
        }
        await WriteRecordAsync(new { schemaVersion = 1, entryType = "storageCleanupApplied", operationId = request.OperationId }, null, token);
        _appliedCleanups.Add(request.OperationId);
        return true;
    }

    private async Task<StoragePayloadVersion[]> ReadCleanupReferencesAsync(CancellationToken token)
    {
        var live = (await ReadRemoteReferencesAsync(token)).Concat(_references.Values
            .Where(reference => reference.State == "live" && (reference.Scope.StartsWith("upload:", StringComparison.Ordinal) ||
                reference.Scope.StartsWith("download:", StringComparison.Ordinal) || reference.Scope.StartsWith("preview:", StringComparison.Ordinal)))
            .SelectMany(reference => reference.Payloads)).ToList();
        foreach (var active in await new TransferStateRepository(_store.TransfersDirectory).LoadRecoverableAsync(token))
            if (active.TransferType == TransferTypes.Download && active.DownloadMetadata != null)
                live.AddRange(Payloads(active.DownloadMetadata));
        return live.ToArray();
    }

    internal async Task CancelReservationAsync(TransferState transfer, CancellationToken token)
    {
        if (transfer.Storage?.Shared != true) return;
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            if (!_catalog.TryGetValue(transfer.Storage.StorageId!, out var row) || row.OwnerId != OwnerId) return;
            row = await ReloadCatalogAsync(row, token);
            var member = row.Members.FirstOrDefault(member => member.FileId == transfer.FileId);
            member?.PendingVersions.Remove(transfer.TransferId);
            await SaveCatalogAsync(row, token);
        }
        finally { _gate.Release(); }
        var payload = new StoragePayloadVersion
        {
            FileId = transfer.FileId,
            VersionId = transfer.TransferId,
            ThreadId = transfer.ThreadId,
            Storage = transfer.Storage,
            Attachments = transfer.UploadedChunks.Select(chunk => new AttachmentReference
            { MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId ?? throw new InvalidDataException("Checkpoint sem attachmentId.") }).ToList()
        };
        if (transfer.ThumbnailRef != null) payload.Attachments.Add(transfer.ThumbnailRef);
        payload.Attachments.AddRange((transfer.VideoPreview480pChunkRefs ?? []).Select(reference => new AttachmentReference
        { MessageId = reference.MessageId, AttachmentId = reference.AttachmentId }));
        var request = new StorageCleanupRecord { FileId = transfer.FileId, Scope = "upload:" + transfer.TransferId, Payloads = [payload] };
        await WriteRecordAsync(request, null, token);
        await CompleteCleanupAsync(request, token);
    }
}
