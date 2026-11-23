using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Organessonium.Services.Sync;

internal static class SyncJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonOptions.Web)
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
}

public interface ICloudSyncRepository
{
    Task<SyncCloudSnapshot> ReadAsync(SyncFolderBinding folder, CancellationToken token);
    Task<FileMetadata> UploadAsync(SyncFolderBinding folder, SyncLocalFile local, string fileId,
        CancellationToken token, Action<SyncTransferProgress> progress);
    Task<SyncRevision> CommitAsync(SyncFolderBinding folder, SyncRevision revision,
        IReadOnlyList<string> expectedHeads, CancellationToken token);
    Task<FileInfo> DownloadAsync(SyncFolderBinding folder, SyncRevision revision, DirectoryInfo staging,
        CancellationToken token, Action<SyncTransferProgress> progress);
}

public sealed class SyncConcurrencyException : IOException
{
    public SyncConcurrencyException() : base("A versão remota mudou durante a sincronização. As duas versões foram preservadas.") { }
}

public sealed class CloudSyncRepository : ICloudSyncRepository, IDisposable
{
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly DiscordApiClient _api;
    private readonly bool _ownsApi;
    private readonly AppRegistry _registry;
    private readonly MetadataService _metadata;
    private readonly EncryptionService _encryption;
    private readonly TransferManager _transfers;
    private readonly Dictionary<string, JournalCache> _journals = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _journalGate = new(1, 1);
    private readonly SemaphoreSlim _projectionGate = new(1, 1);
    private readonly AdaptiveUploadGate _uploadGate;
    private readonly IDisposable _liveUploads;
    private readonly Func<HttpClient>? _downloadClients;
    public event Action<string>? TreeChanged;

    public CloudSyncRepository(SettingsStore store, AppSettings settings, DiscordApiClient? api = null,
        Func<HttpClient>? downloadClients = null)
    {
        _store = store;
        _settings = settings.Clone();
        _ownsApi = api == null;
        _api = api ?? new DiscordApiClient(settings.BotToken);
        _metadata = new MetadataService(MetadataCodec.ForStore(store), _api.DownloadAttachmentBytesAsync);
        _registry = new AppRegistry(_api, store, settings);
        _encryption = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(store));
        _transfers = new TransferManager(new TransferStateRepository(store.TransfersDirectory));
        _uploadGate = new AdaptiveUploadGate(settings.EffectiveUploadThreads, !settings.ForceUploadParallelism);
        _liveUploads = UploadConcurrencyRegistry.Register(_uploadGate);
        _downloadClients = downloadClients;
    }

    public async Task CreateOrLinkAsync(SyncFolderBinding binding, SyncStateStore state, bool link, CancellationToken token)
    {
        var entry = await _registry.EnsureSyncRootAsync(binding, state.DeviceId, state.DeviceName, link, token);
        if (!link)
        {
            binding.CloudFolderId = entry.FolderId;
            binding.CloudPath = PathRules.ChildPath(entry);
        }
    }

    public Task<IReadOnlyList<AppEntry>> DiscoverAsync(CancellationToken token) => _registry.DiscoverSyncFoldersAsync(token);

    public async Task<SyncCloudSnapshot> ReadAsync(SyncFolderBinding folder, CancellationToken token)
    {
        SyncCloudSnapshot snapshot;
        await _journalGate.WaitAsync(token);
        try
        {
            var mapping = await _registry.SyncMappingAsync(folder.CloudFolderId, token);
            _journals.TryGetValue(folder.SyncFolderId, out var cache);
            var messages = await _api.GetMessagesAsync(mapping.ThreadId, cache == null ? null : 100, token);
            if (cache != null && messages.Count == 100 && messages.All(m => Snowflake(m.Id) > cache.Frontier))
                messages = await _api.GetMessagesAsync(mapping.ThreadId, null, token);
            cache ??= new JournalCache();
            foreach (var message in messages.Where(m => Snowflake(m.Id) > cache.Frontier).OrderBy(m => Snowflake(m.Id)))
            {
                if (message.Content.StartsWith("syncApplied:", StringComparison.Ordinal))
                {
                    cache.Applied[message.Content[12..]] = message.Id;
                    continue;
                }
                using var record = await _metadata.ReadJsonAsync(message, token);
                if (record?.RootElement.TryGetProperty("entryType", out var type) == true && type.GetString() == "syncApplied")
                {
                    if (!record.RootElement.TryGetProperty("revisionId", out var id) || !Guid.TryParseExact(id.GetString(), "N", out _))
                        throw new MetadataCodecException(MetadataError.MissingField);
                    cache.Applied[id.GetString()!] = message.Id;
                    continue;
                }
                var revision = ReadRevisionDocument(record, message.Id);
                if (revision != null)
                {
                    if (revision.SyncFolderId != folder.SyncFolderId || revision.SyncVersion != 1 || revision.SchemaVersion != 2)
                        throw new InvalidDataException("Versão ou identidade de sincronização não suportada.");
                    ValidateRevision(revision);
                    cache.Revisions[revision.RevisionId] = revision;
                }
            }
            if (messages.Count > 0) cache.Frontier = Math.Max(cache.Frontier, messages.Max(m => Snowflake(m.Id)));
            _journals[folder.SyncFolderId] = cache;
            snapshot = Snapshot(cache.Revisions.Values.ToList());
        }
        finally { _journalGate.Release(); }
        foreach (var heads in snapshot.Heads.Values.Where(h => h.Count == 1))
            await ApplyProjectionAsync(folder, heads[0], snapshot.Revisions.Where(r => heads[0].Parents.Contains(r.RevisionId)).ToList(), token);
        return snapshot;
    }

    public static SyncCloudSnapshot Snapshot(IReadOnlyList<SyncRevision> revisions)
    {
        var referenced = revisions.SelectMany(r => r.Parents).ToHashSet(StringComparer.Ordinal);
        var heads = revisions.Where(r => !referenced.Contains(r.RevisionId))
            .GroupBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SyncRevision>)g.OrderBy(r => r.RevisionId, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
        return new SyncCloudSnapshot(revisions, heads);
    }

    public async Task<SyncRevision> CommitAsync(SyncFolderBinding folder, SyncRevision revision,
        IReadOnlyList<string> expectedHeads, CancellationToken token)
    {
        ValidateRevision(revision);
        var before = await ReadAsync(folder, token);
        var current = before.Heads.Values.SelectMany(r => r)
            .Where(r => r.FileId == revision.FileId || r.RelativePath.Equals(revision.RelativePath, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!current.Select(r => r.RevisionId).Order(StringComparer.Ordinal).SequenceEqual(expectedHeads.Order(StringComparer.Ordinal)))
            throw new SyncConcurrencyException();
        var mapping = await _registry.SyncWriteMappingAsync(folder.CloudFolderId, token);
        await _api.UnarchiveThreadAsync(mapping.ThreadId, token);
        var json = _metadata.EncodeJson(JsonSerializer.Serialize(revision, SyncJson.Options));
        DiscordMessageDto message;
        if (json.Length <= DiscordLimits.MessageContentCharacters)
            message = await _api.CreateMessageAsync(mapping.ThreadId, json, token);
        else
        {
            var temporary = Directory.CreateTempSubdirectory("organessonium-sync-metadata-");
            try
            {
                var file = new FileInfo(Path.Combine(temporary.FullName, "sync-revision.json"));
                await File.WriteAllTextAsync(file.FullName, json, MetadataCryptoParameters.Utf8, token);
                message = await _api.SendFileMessageAsync(mapping.ThreadId, "metadata", file, token);
            }
            finally { temporary.Delete(true); }
        }
        // A committed journal record is the source of truth even if updating the card index fails.
        var confirmed = await ReadRevisionAsync(await _api.GetMessageAsync(mapping.ThreadId, message.Id, token), token);
        if (confirmed?.RevisionId != revision.RevisionId || confirmed.Hash != revision.Hash)
            throw new InvalidDataException("A revisão enviada não foi confirmada no servidor.");
        revision.MessageId = message.Id;
        if (revision.Payload != null)
            await _registry.SharedStorage.PublishReferenceAsync("sync:" + folder.SyncFolderId + ":" + revision.RevisionId,
                revision.FileId, SharedStorageManager.Payloads(revision.Payload), mapping.ThreadId, message.Id, token);
        await _journalGate.WaitAsync(token);
        try
        {
            var cache = _journals[folder.SyncFolderId];
            cache.Revisions[revision.RevisionId] = revision;
            // Do not advance the frontier here: a simultaneous commit can have an earlier snowflake.
        }
        finally { _journalGate.Release(); }
        await ApplyProjectionAsync(folder, revision, current, token);
        // Cleanup only superseded ancestors, keeping the latest two payload revisions and all conflict heads.
        await CleanupAsync(folder, token);
        return revision;
    }

    private async Task ApplyProjectionAsync(SyncFolderBinding folder, SyncRevision revision,
        IReadOnlyList<SyncRevision> parents, CancellationToken token)
    {
        await _projectionGate.WaitAsync(token);
        try
        {
            var cache = _journals[folder.SyncFolderId];
            if (cache.Applied.ContainsKey(revision.RevisionId)) return;
            var parentId = await _registry.ProjectSyncRevisionAsync(folder, revision, token);
            foreach (var prior in parents.Where(r => r.FileId != revision.FileId ||
                         !r.RelativePath.Equals(revision.RelativePath, StringComparison.OrdinalIgnoreCase)))
                await _registry.RemoveSyncProjectionAsync(folder, prior, token);
            var mapping = await _registry.SyncWriteMappingAsync(folder.CloudFolderId, token);
            await _api.UnarchiveThreadAsync(mapping.ThreadId, token);
            var marker = await _api.CreateMessageAsync(mapping.ThreadId, _metadata.EncodeJson(JsonSerializer.Serialize(new
                { schemaVersion = 1, entryType = "syncApplied", revisionId = revision.RevisionId }, JsonOptions.Web)), token);
            cache.Applied[revision.RevisionId] = marker.Id;
            await _api.ArchiveThreadAsync(mapping.ThreadId, token);
            TreeChanged?.Invoke(parentId);
        }
        finally { _projectionGate.Release(); }
    }

    public async Task<FileMetadata> UploadAsync(SyncFolderBinding folder, SyncLocalFile local, string fileId,
        CancellationToken token, Action<SyncTransferProgress> progress)
    {
        await using var uploadPriority = _registry.SharedStorage.PrioritizeUploads();
        var sourcePath = SyncPaths.Resolve(folder.LocalPath, local.RelativePath);
        var staging = Directory.CreateDirectory(Path.Combine(_store.DataDirectory.FullName, "SyncStaging", SafeKey(folder.SyncFolderId)));
        var safeFile = SafeKey(fileId);
        var snapshot = new FileInfo(Path.Combine(staging.FullName, safeFile + ".source"));
        // Hold a read-only shared handle while copying: concurrent writers cannot produce mixed chunks.
        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true))
        {
            if (source.Length != local.Size || File.GetLastWriteTimeUtc(sourcePath).Ticks != local.WriteTicks)
                throw new IOException("O arquivo mudou antes do envio; será verificado novamente.");
            await using var destination = new FileStream(snapshot.FullName, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await source.CopyToAsync(destination, token);
        }
        if (await HashAsync(snapshot.FullName, token) != local.Hash)
            throw new IOException("O conteúdo mudou durante a preparação; nada foi publicado.");
        var pending = await _transfers.RecoverableAsync(token);
        var transfer = pending.FirstOrDefault(t => t.SyncFolderId == folder.SyncFolderId && t.FileId == fileId && t.SyncHash == local.Hash);
        var count = ArchiveSplitter.GetPartCount(local.Size, EncryptionService.MaximumPlaintextPayloadBytes);
        if (transfer == null)
        {
            transfer = await _transfers.CreateUploadAsync(snapshot, folder.CloudPath, folder.CloudFolderId,
                AppEntry.File(Path.GetFileName(sourcePath), "", "", local.Size, "/").MimeType,
                EncryptionService.MaximumPlaintextPayloadBytes, count, token);
            transfer.FileId = fileId;
            transfer.FileName = Path.GetFileName(sourcePath);
            transfer.SyncFolderId = folder.SyncFolderId;
            transfer.SyncHash = local.Hash;
            transfer.Encryption = _encryption.CreateFileEncryption(_settings.EncryptionMode);
            if (local.Size <= DiscordLimits.DedicatedThreadThresholdBytes) transfer.SharedAllocationPending = true;
            else transfer.Storage = new StorageLayout { Mode = "dedicated" };
            await _transfers.SaveAsync(transfer, token);
        }
        if (transfer.SharedAllocationPending)
        {
            var allocation = await _registry.SharedStorage.ReserveAsync(fileId, transfer.TransferId, token);
            transfer.Storage = allocation.Layout; transfer.ThreadId = allocation.ThreadId;
            transfer.PayloadLayoutVersion = TransferPayloadLayouts.SharedStorage; transfer.SharedAllocationPending = false;
            await _transfers.SaveAsync(transfer, token);
        }
        SharedUploadContext? shared = null;
        if (transfer.Storage?.Shared == true)
        {
            var allocation = new SharedStorageReservation(fileId, transfer.TransferId, transfer.ThreadId, transfer.Storage);
            await _registry.SharedStorage.VerifyReservationAsync(allocation, token);
            shared = new SharedUploadContext(_registry.SharedStorage, allocation);
            var recovered = await _registry.SharedStorage.RecoverChunksAsync(allocation, EncryptedPayloadRoles.Original, count, token);
            foreach (var reference in recovered.Where(reference => transfer.UploadedChunks.All(chunk => chunk.Index != reference.Index)))
            {
                var message = await _api.ResolvePayloadMessageAsync(transfer.ThreadId, reference.MessageId, token);
                var attachment = message.Attachments.Single(attachment => attachment.Id == reference.AttachmentId);
                var batch = string.IsNullOrEmpty(transfer.BatchId) ? transfer.TransferId[..10] : transfer.BatchId;
                await _transfers.MarkUploadChunksAsync(transfer, new ForumUploadedBatch(transfer.ThreadId, batch,
                    [new ForumUploadedChunk(transfer.ThreadId, batch, reference.Index, reference.MessageId,
                        attachment.Size, attachment.Size - EncryptionService.EnvelopeOverheadBytes, reference.AttachmentId)]), token);
            }
        }
        await using var writeLease = shared == null ? null : await _api.AcquireThreadWriteAsync(transfer.ThreadId, token);
        var chunkBytes = transfer.ChunkSizeBytes > 0 ? checked((int)transfer.ChunkSizeBytes) : EncryptionService.MaximumPlaintextPayloadBytes;
        count = ArchiveSplitter.GetPartCount(local.Size, chunkBytes);
        if (count != transfer.TotalChunks || chunkBytes > EncryptionService.MaximumPlaintextPayloadBytes)
            throw new InvalidDataException("O layout do checkpoint não corresponde ao conteúdo sincronizado.");
        await using var streamingChecksums = await StreamingUploadChecksums.StartAsync(transfer, _transfers,
            () => PreparedVideoUpload.OpenFile(snapshot), local.Size, chunkBytes, token);
        var encryption = new EncryptedPayloadContext(fileId, transfer.Encryption!, EncryptedPayloadRoles.Original, 0, count)
        {
            Checksums = transfer.UploadChecksumPending ? null : transfer.Checksums,
            UploadPlaintextObserved = streamingChecksums == null ? null : streamingChecksums.ObserveAsync
        };
        var uploader = new ForumUploader(_api, _settings.AppsForumId, MetadataCodec.ForStore(_store));
        using var memory = new UploadMemoryCoordinator(_settings.EffectiveUploadThreads);
        try
        {
            var result = await uploader.UploadResumableGeneratedAsync(transfer.FileName, count,
                index => memory.PreparePartAsync(_encryption, snapshot, local.Size, chunkBytes, encryption.WithChunkIndex(index), token,
                    deferBuffers: transfer.PayloadLayoutVersion != TransferPayloadLayouts.SingleAttachment, isolateSendCancellation: shared != null),
                transfer.ThreadId, transfer.BatchId, transfer.UploadedChunks.ToDictionary(c => c.Index, c => c.MessageId),
                completed => progress(new(transfer.TransferId, local.RelativePath, "upload", completed * 90d / count, "uploading")),
                async chunk =>
                {
                    transfer.ThreadId = chunk.ThreadId;
                    transfer.BatchId = chunk.BatchId;
                    await _transfers.MarkUploadChunkAsync(transfer, chunk.Index, chunk.MessageId, chunk.SizeBytes, token);
                }, token, _ => { progress(new(transfer.TransferId, local.RelativePath, "upload", 0, "waiting")); return Task.CompletedTask; },
                maxParallelParts: _settings.EffectiveUploadThreads, adaptiveUploadGate: _uploadGate, encryptedPayload: encryption,
                cleanupPart: file => { if (file.Exists) file.Delete(); },
                useAttachmentReferences: transfer.PayloadLayoutVersion != TransferPayloadLayouts.SingleAttachment,
                uploadedChunkReferences: transfer.PayloadLayoutVersion != TransferPayloadLayouts.SingleAttachment
                    ? transfer.UploadedChunks.Select(chunk => new ChunkReference
                        { Index = chunk.Index, MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId ?? "" }).ToArray() : null,
                onBatchUploaded: batch => _transfers.MarkUploadChunksAsync(transfer, batch, shared == null ? token : CancellationToken.None),
                batchPreparationGate: memory.BatchPreparationGate,
                streamPreparedParts: transfer.PayloadLayoutVersion != TransferPayloadLayouts.SingleAttachment,
                maxPreparedPartBytes: checked(chunkBytes + EncryptionService.EnvelopeOverheadBytes), sharedUpload: shared);
            if (result.ChunkCount != count || (result.ChunkReferences == null && result.ChunkMessageIds.Any(string.IsNullOrWhiteSpace)))
                throw new InvalidDataException("O upload não confirmou todos os chunks.");
            if (streamingChecksums != null) await streamingChecksums.CompleteAsync(token);
            var payload = new FileMetadata
            {
                FileId = fileId, ThreadId = result.ThreadId, Name = transfer.FileName, SizeBytes = local.Size,
                MimeType = transfer.MimeType, UploadedAt = DateTime.UtcNow.ToString("O"), BatchId = result.BatchId,
                Chunks = result.HasAttachmentReferences ? null : result.ChunkMessageIds.ToList(),
                ChunkRefs = result.ChunkReferences?.ToList(), Encryption = transfer.Encryption?.Clone(),
                Checksums = transfer.Checksums,
                Storage = transfer.Storage, VersionId = transfer.TransferId, VersionUploadedAt = DateTime.UtcNow.ToString("O")
            };
            if (_settings.PreviewEnabled)
                payload.Preview = await UploadThumbnailAsync(snapshot, payload, staging, token, shared);
            await uploader.ArchiveThreadAsync(result.ThreadId, token);
            return payload;
        }
        catch (Exception ex)
        {
            await _transfers.PauseAsync(transfer, ex, CancellationToken.None);
            progress(new(transfer.TransferId, local.RelativePath, "upload", 0, "paused"));
            throw;
        }
    }

    public async Task<FileInfo> DownloadAsync(SyncFolderBinding folder, SyncRevision revision, DirectoryInfo staging,
        CancellationToken token, Action<SyncTransferProgress> progress)
    {
        var payload = revision.Payload ?? throw new InvalidDataException("Revisão sem payload.");
        var entry = _metadata.ToAppEntry(new MetadataEnvelope("", payload)) ?? throw new InvalidDataException("Payload inválido.");
        // Stable local-only name, excluded by SyncPaths; same volume as the final synchronized file.
        entry.Name = ".organessonium-sync-download-" + SafeKey(folder.SyncFolderId + "/" + revision.FileId + "/" + revision.Hash) + ".content";
        var cache = new DirectoryInfo(folder.LocalPath);
        _ = SyncPaths.Resolve(cache.FullName, entry.Name);
        var transfer = (await _transfers.RecoverableAsync(token)).FirstOrDefault(t => t.TransferType == TransferTypes.Download &&
            t.SyncFolderId == folder.SyncFolderId && t.FileId == revision.FileId && t.SyncHash == revision.Hash && t.ThreadId == payload.ThreadId)
            ?? await _transfers.CreateDownloadAsync(entry, cache, token);
        transfer.DestinationPath = cache.FullName;
        transfer.SyncFolderId = folder.SyncFolderId;
        transfer.SyncHash = revision.Hash;
        await _transfers.SaveAsync(transfer, token);
        try
        {
            var downloader = new ForumDownloader(_api, _settings, _encryption, _downloadClients, new StorageReadContext(_registry.SharedStorage));
            var file = await downloader.DownloadAndAssembleAsync(entry, cache, transfer, _transfers, _ => { }, token,
                p => progress(new(transfer.TransferId, revision.RelativePath, "download", p.Percent, "downloading")));
            if (file.Length != revision.SizeBytes || await HashAsync(file.FullName, token) != revision.Hash)
                throw new InvalidDataException("O download não passou na validação de tamanho/SHA-256.");
            var candidate = new FileInfo(Path.Combine(staging.FullName, "content"));
            if (!string.Equals(Path.GetPathRoot(file.FullName), Path.GetPathRoot(candidate.FullName), StringComparison.OrdinalIgnoreCase))
                throw new IOException("A finalização da sincronização exige o mesmo volume do destino.");
            File.Move(file.FullName, candidate.FullName, overwrite: false);
            await _transfers.CompleteAsync(transfer, _settings.KeepTransferHistory, token);
            progress(new(transfer.TransferId, revision.RelativePath, "download", 100, "completed"));
            return candidate;
        }
        catch (Exception ex)
        {
            await _transfers.PauseAsync(transfer, ex, CancellationToken.None);
            progress(new(transfer.TransferId, revision.RelativePath, "download", 0, "paused"));
            throw;
        }
    }

    public async Task CompleteUploadAsync(SyncFolderBinding folder, string fileId, string hash, CancellationToken token,
        Action<SyncTransferProgress>? progress = null)
    {
        var pending = (await _transfers.RecoverableAsync(token)).Where(t => t.TransferType == TransferTypes.Upload &&
            t.SyncFolderId == folder.SyncFolderId && t.FileId == fileId).ToList();
        foreach (var transfer in pending.Where(t => t.SyncHash == hash))
        {
            await _transfers.CompleteAsync(transfer, _settings.KeepTransferHistory, token);
            progress?.Invoke(new(transfer.TransferId, transfer.FileName, "upload", 100, "completed"));
            if (File.Exists(transfer.SourcePath)) File.Delete(transfer.SourcePath);
        }
        if (pending.All(t => t.SyncHash == hash)) return;
        var referenced = (await ReadAsync(folder, token)).Revisions.Select(r => r.Payload?.ThreadId).ToHashSet();
        foreach (var obsolete in pending.Where(t => t.SyncHash != hash))
        {
            if (obsolete.Storage?.Shared == true) await _registry.SharedStorage.CancelReservationAsync(obsolete, token);
            else if (!string.IsNullOrWhiteSpace(obsolete.ThreadId) && !referenced.Contains(obsolete.ThreadId))
                await _api.DeleteChannelAsync(obsolete.ThreadId, token);
            await _transfers.CancelAsync(obsolete, _settings.KeepTransferHistory, false, token);
        }
    }

    private async Task CleanupAsync(SyncFolderBinding folder, CancellationToken token)
    {
        var snapshot = await ReadAsync(folder, token);
        var heads = snapshot.Heads.Values.SelectMany(h => h).ToList();
        var protectedIds = heads.Select(h => h.RevisionId).Concat(heads.SelectMany(h => h.Parents)).ToHashSet();
        // No cross-device lock is assumed. Never delete a conflict head or its immediate base.
        var candidates = snapshot.Revisions.Where(r => !protectedIds.Contains(r.RevisionId)).ToList();
        if (candidates.Count == 0) return;
        var mapping = await _registry.SyncMappingAsync(folder.CloudFolderId, token);
        var retainedThreads = snapshot.Revisions.Except(candidates).Select(r => r.Payload?.ThreadId).Where(t => t != null).ToHashSet();
        foreach (var revision in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(revision.MessageId)) continue;
            var cleanup = revision.Payload == null ? null : await _registry.SharedStorage.BeginPayloadCleanupAsync(
                "sync:" + folder.SyncFolderId + ":" + revision.RevisionId, revision.FileId,
                SharedStorageManager.Payloads(revision.Payload), token, mapping.ThreadId, revision.MessageId);
            // The durable intent precedes removing the obsolete journal.
            await _api.DeleteMessageAsync(mapping.ThreadId, revision.MessageId, token);
            await _journalGate.WaitAsync(token);
            try { _journals[folder.SyncFolderId].Revisions.Remove(revision.RevisionId); }
            finally { _journalGate.Release(); }
            if (cleanup != null) await _registry.SharedStorage.CompleteCleanupAsync(cleanup, token);
        }
        var applied = _journals[folder.SyncFolderId].Applied;
        foreach (var marker in applied.Where(p => !protectedIds.Contains(p.Key)).ToList())
        {
            await _api.DeleteMessageAsync(mapping.ThreadId, marker.Value, token);
            applied.Remove(marker.Key);
        }
    }

    private async Task<SyncRevision?> ReadRevisionAsync(DiscordMessageDto message, CancellationToken token)
    {
        using var document = await _metadata.ReadJsonAsync(message, token);
        return ReadRevisionDocument(document, message.Id);
    }

    private static SyncRevision? ReadRevisionDocument(JsonDocument? document, string messageId)
    {
        if (document == null) return null;
        if (!document.RootElement.TryGetProperty("entryType", out var type) || type.GetString() != "syncRevision") return null;
        if (document.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            ChunkReference.ValidateJsonPresence(payload);
        var result = document.RootElement.Deserialize<SyncRevision>(SyncJson.Options) ?? throw new InvalidDataException("Revisão inválida.");
        result.MessageId = messageId;
        return result;
    }

    private async Task<PreviewMetadata?> UploadThumbnailAsync(FileInfo source, FileMetadata payload, DirectoryInfo directory, CancellationToken token,
        SharedUploadContext? shared = null)
    {
        if (!payload.MimeType.StartsWith("image/") && !payload.MimeType.StartsWith("video/")) return null;
        var safeFile = SafeKey(payload.FileId);
        var thumbnail = new FileInfo(Path.Combine(directory.FullName, safeFile + "-preview.jpg"));
        try
        {
            // Shell thumbnail is native and does not initialize WPF/WebView2.
            var extension = Path.GetExtension(Path.GetFileName(payload.Name));
            if (extension.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            var named = new FileInfo(Path.Combine(directory.FullName, safeFile + extension));
            File.Move(source.FullName, named.FullName, true);
            try
            {
                var storage = await StorageFile.GetFileFromPathAsync(named.FullName);
                using var image = await storage.GetThumbnailAsync(ThumbnailMode.SingleItem, 320, ThumbnailOptions.ResizeThumbnail).AsTask(token);
                if (image == null || image.Type != ThumbnailType.Image || image.Size == 0) return null;
                await using var input = image.AsStreamForRead();
                await using var output = thumbnail.Create();
                await input.CopyToAsync(output, token);
            }
            finally { File.Move(named.FullName, source.FullName, true); }
            var context = new EncryptedPayloadContext(payload.FileId, payload.Encryption!, EncryptedPayloadRoles.Preview, 0, 1);
            using var memory = new UploadMemoryCoordinator(1);
            using var budget = await RamUploadBudget.ReserveAsync(1, token);
            using var encrypted = await memory.PreparePartAsync(_encryption, thumbnail, thumbnail.Length,
                EncryptionService.MaximumPlaintextPayloadBytes, context, token);
            if (shared != null)
            {
                var reference = await shared.Manager.Batcher.SendAsync(shared.Reservation, context, encrypted, null, token, _uploadGate);
                return new PreviewMetadata { ThumbnailRef = new AttachmentReference { MessageId = reference.MessageId, AttachmentId = reference.AttachmentId } };
            }
            await _api.UnarchiveThreadAsync(payload.ThreadId, token);
            var message = await _api.SendFileMessageAsync(payload.ThreadId, _metadata.EncodePayloadReference(EncryptedPayloadProtocol.Message(context, 1)), encrypted, token);
            return new PreviewMetadata { ThumbnailMessageId = message.Id };
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; } // A missing thumbnail never invalidates the original upload.
        finally { if (thumbnail.Exists) thumbnail.Delete(); }
    }

    internal static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }

    private static void ValidateRevision(SyncRevision revision)
    {
        SyncPaths.Relative(revision.RelativePath);
        if (revision.SyncVersion != 1 || revision.SchemaVersion != 2 || revision.EntryType != "syncRevision" ||
            !SafeIdentity(revision.FileId) || !SafeIdentity(revision.SyncFolderId) || !SafeIdentity(revision.DeviceId) ||
            !Guid.TryParseExact(revision.RevisionId, "N", out _) || revision.Revision < 1 ||
            revision.Parents == null || revision.Parents.Count > 256 || revision.Parents.Contains(revision.RevisionId) ||
            revision.Parents.Distinct().Count() != revision.Parents.Count || revision.Parents.Any(p => !Guid.TryParseExact(p, "N", out _)) ||
            revision.State is not ("active" or "deleted") ||
            revision.Kind is not ("file" or "directory") || revision.SizeBytes < 0 ||
            !DateTimeOffset.TryParse(revision.ModifiedUtc, out var modified) || modified.Offset != TimeSpan.Zero ||
            (revision.IsDeleted && (!DateTimeOffset.TryParse(revision.DeletedUtc, out var deleted) || deleted.Offset != TimeSpan.Zero)) ||
            (!revision.IsDeleted && revision.Kind == "file" && (revision.Hash?.Length != 64 || revision.Hash.Any(c => !Uri.IsHexDigit(c)) || revision.Payload?.FileId != revision.FileId ||
                revision.Payload.SizeBytes != revision.SizeBytes ||
                (revision.Payload.ChunkRefs == null && revision.Payload.Chunks?.Count is not > 0))))
            throw new InvalidDataException("Metadata de sincronização inválido; os arquivos locais foram preservados.");
        if (revision.Payload?.ChunkRefs != null)
            ChunkReference.Validate(revision.Payload.ChunkRefs, revision.Payload.Chunks);
    }

    private static bool SafeIdentity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string SafeKey(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static ulong Snowflake(string id) => ulong.TryParse(id, out var value) ? value : 0;
    private sealed class JournalCache
    {
        public ulong Frontier;
        public Dictionary<string, SyncRevision> Revisions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Applied { get; } = new(StringComparer.Ordinal);
    }

    public void Dispose()
    {
        _liveUploads.Dispose();
        if (_ownsApi) _api.Dispose();
        _uploadGate.Dispose();
        _journalGate.Dispose();
        _projectionGate.Dispose();
    }
}
