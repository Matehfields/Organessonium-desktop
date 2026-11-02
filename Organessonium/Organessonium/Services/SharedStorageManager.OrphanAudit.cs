using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    internal async Task AuditOrphansAsync(HashSet<string> payloadThreads, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            var live = await ReadRemoteReferencesAsync(token);
            var files = live.Select(payload => payload.FileId).ToHashSet(StringComparer.Ordinal);
            var referencedThreads = live.Select(payload => payload.ThreadId).ToHashSet(StringComparer.Ordinal);
            var active = await new TransferStateRepository(_store.TransfersDirectory).LoadRecoverableAsync(token);
            var activeFiles = active.Select(state => state.FileId).ToHashSet(StringComparer.Ordinal);
            var retainedThreads = active.Select(state => state.ThreadId).Concat(_catalog.Values.Select(row => row.ThreadId)).ToHashSet(StringComparer.Ordinal);
            var protectedFiles = _references.Values.Where(reference => reference.State == "live" && !reference.Scope.StartsWith("file:"))
                .Select(reference => reference.FileId).ToHashSet(StringComparer.Ordinal);
            foreach (var pool in _catalog.Values.Where(row => row.OwnerId == OwnerId && payloadThreads.Contains(row.ThreadId)).ToArray())
            {
                if (referencedThreads.Contains(pool.ThreadId) || _api.HasThreadWriter(pool.ThreadId) ||
                    pool.Members.Any(member => protectedFiles.Contains(member.FileId) ||
                        _mutations.Values.Any(mutation => mutation.Pending && mutation.FileId == member.FileId))) continue;
                var checkpoints = active.Where(state => state.ThreadId == pool.ThreadId).ToArray();
                if (checkpoints.Any(state => state.UploadedChunks.Count > 0 || state.ThumbnailRef != null ||
                    state.VideoPreview480pChunkRefs is { Count: > 0 } || state.TransferType != TransferTypes.Upload)) continue;
                // Foreign reservations and unknown results remain recoverable. Only a fully empty
                // owned post is retired, after the remote reference scan and authenticated control.
                var messages = await _api.GetMessagesAsync(pool.ThreadId, 2, token);
                if (messages.Count != 1 || messages[0].Id != pool.ControlMessageId || messages[0].Attachments.Count != 0) continue;
                using var control = await _metadata.ReadJsonAsync(messages[0], token);
                if (control?.RootElement.TryGetProperty("entryType", out var type) != true || type.GetString() != "storageControl" ||
                    control.RootElement.GetProperty("storageId").GetString() != pool.StorageId ||
                    control.RootElement.GetProperty("ownerId").GetString() != OwnerId) continue;
                if ((await _api.GetChannelAsync(pool.ThreadId, token)).ThreadMetadata?.Locked == true) continue;
                // Closing the catalogue precedes deleting the post. A crash at either side can
                // resume by allocating a new destination with the unchanged file/version identity.
                if (pool.State != "closed") { pool.State = "closed"; await SaveCatalogAsync(pool, token); }
                token.ThrowIfCancellationRequested();
                if (_api.HasThreadWriter(pool.ThreadId)) continue;
                if (!await _api.DeleteEmptySharedThreadAsync(pool.ThreadId, messages[0], token)) continue;
                foreach (var state in checkpoints)
                {
                    state.ThreadId = ""; state.Storage = null; state.SharedAllocationPending = true;
                    state.PayloadLayoutVersion = TransferPayloadLayouts.MultiAttachment;
                    await new TransferStateRepository(_store.TransfersDirectory).SaveActiveAsync(state, token);
                }
            }
            var orphanMembers = _catalog.Values.Where(row => row.OwnerId == OwnerId).SelectMany(row => row.Members)
                .Count(member => !files.Contains(member.FileId) && !activeFiles.Contains(member.FileId) && !protectedFiles.Contains(member.FileId) &&
                    !_mutations.Values.Any(mutation => mutation.Pending && mutation.FileId == member.FileId));
            // Absence from a serial/cache never authorizes deletion. Keep candidates and
            // uncertain metadata intact; only the established durable cleanup path deletes payloads.
            Diagnostics.DevelopmentDiagnostics.Current?.Record("janitor.orphans", "maintenance", new
            {
                storageMembers = orphanMembers,
                payloadThreads = payloadThreads.Count(thread => !referencedThreads.Contains(thread) && !retainedThreads.Contains(thread)),
                metadataWithoutPayload = live.Where(payload => !payloadThreads.Contains(payload.ThreadId)).Select(payload => payload.FileId).Distinct().Count()
            });
        }
        finally { _gate.Release(); }
    }
}
