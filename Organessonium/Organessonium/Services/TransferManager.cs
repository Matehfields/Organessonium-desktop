using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class TransferManager
{
    private readonly TransferStateRepository _repository;
    private readonly SemaphoreSlim _uploadCheckpointGate = new(1, 1);

    public TransferManager(TransferStateRepository repository)
    {
        _repository = repository;
    }

    public TransferStateRepository Repository => _repository;

    public async Task<TransferState> CreateUploadAsync(
        FileInfo source,
        string targetPath,
        string? targetFolderId,
        string mimeType,
        long chunkSizeBytes,
        int totalChunks,
        CancellationToken cancellationToken = default)
    {
        var state = new TransferState
        {
            TransferType = TransferTypes.Upload,
            PayloadLayoutVersion = TransferPayloadLayouts.MultiAttachment,
            Status = TransferStatuses.Preparing,
            SourcePath = source.FullName,
            FileName = source.Name,
            MimeType = mimeType,
            SizeBytes = Math.Max(0, source.Length),
            TargetPath = PathRules.Normalize(targetPath),
            TargetFolderId = string.IsNullOrWhiteSpace(targetFolderId) ? null : targetFolderId,
            ChunkSizeBytes = chunkSizeBytes,
            TotalChunks = totalChunks,
            PendingChunks = Enumerable.Range(0, Math.Max(0, totalChunks)).ToList()
        };
        await SaveAsync(state, cancellationToken);
        return state;
    }

    public async Task<TransferState> CreateDownloadAsync(
        AppEntry entry,
        DirectoryInfo destination,
        CancellationToken cancellationToken = default)
    {
        await _downloadCreationGate.WaitAsync(cancellationToken);
        try
        {
            var finalPath = PartialDownloadFile.Destination(destination, entry.Name);
            if (PartialDownloadFile.IsActive(finalPath)) throw new DownloadAlreadyActiveException();
            var identity = DownloadIdentity(entry);
            foreach (var saved in await RecoverableAsync(cancellationToken))
            {
                if (saved.TransferType != TransferTypes.Download || string.IsNullOrWhiteSpace(saved.DestinationPath)) continue;
                var savedIdentity = saved.DownloadIdentity;
                if (savedIdentity == null && saved.DownloadMetadata != null)
                {
                    var oldEntry = new MetadataService().ToAppEntry(new MetadataEnvelope(saved.MetadataMessageId ?? "", saved.DownloadMetadata));
                    if (oldEntry != null) savedIdentity = DownloadIdentity(oldEntry);
                }
                if (savedIdentity == identity && string.Equals(finalPath,
                    PartialDownloadFile.Destination(new DirectoryInfo(saved.DestinationPath), saved.FileName), StringComparison.OrdinalIgnoreCase))
                    return saved;
            }
            return await CreateDownloadCoreAsync(entry, destination, cancellationToken);
        }
        finally { _downloadCreationGate.Release(); }
    }

    private async Task<TransferState> CreateDownloadCoreAsync(AppEntry entry, DirectoryInfo destination, CancellationToken cancellationToken)
    {
        var chunks = entry.ChunkReferences != null
            ? ChunkReference.Validate(entry.ChunkReferences, entry.ChunkMessageIds).Select(reference => new TransferChunkState
                { Index = reference.Index, MessageId = reference.MessageId, AttachmentId = reference.AttachmentId }).ToList()
            : entry.ChunkMessageIds.Select((messageId, index) => new TransferChunkState
            {
                Index = index,
                MessageId = messageId,
                Downloaded = false
            })
            .ToList();

        var state = new TransferState
        {
            TransferType = TransferTypes.Download,
            DownloadMetadata = MetadataService.CreateFileMetadata(entry),
            MetadataMessageId = entry.MetadataMessageId,
            Storage = entry.Storage,
            PayloadLayoutVersion = entry.Storage?.Shared == true ? TransferPayloadLayouts.SharedStorage :
                entry.ChunkReferences == null ? TransferPayloadLayouts.SingleAttachment : TransferPayloadLayouts.MultiAttachment,
            Status = TransferStatuses.Downloading,
            FileId = entry.FileId,
            FileName = entry.Name,
            MimeType = entry.MimeType,
            SizeBytes = entry.SizeBytes,
            Encryption = entry.Encryption?.Clone(),
            DestinationPath = destination.FullName,
            ThreadId = entry.ThreadId,
            BatchId = entry.BatchId,
            TotalChunks = chunks.Count,
            Chunks = chunks,
            PendingChunks = chunks.Select(chunk => chunk.Index).ToList()
        };
        await SaveAsync(state, cancellationToken);
        return state;
    }

    public async Task MarkUploadChunkAsync(
        TransferState state,
        int index,
        string messageId,
        long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        await _uploadCheckpointGate.WaitAsync(cancellationToken);
        try
        {
            state.Status = TransferStatuses.Uploading;
            var existing = state.UploadedChunks.FirstOrDefault(chunk => chunk.Index == index);
            if (existing == null)
            {
                state.UploadedChunks.Add(new TransferChunkState
                {
                    Index = index,
                    MessageId = messageId,
                    SizeBytes = sizeBytes,
                });
            }
            else
            {
                existing.MessageId = messageId;
                existing.SizeBytes = sizeBytes;
            }

            state.UploadedChunks = state.UploadedChunks.OrderBy(chunk => chunk.Index).ToList();
            state.PendingChunks = Enumerable.Range(0, Math.Max(0, state.TotalChunks))
                .Except(state.UploadedChunks.Select(chunk => chunk.Index))
                .OrderBy(index => index)
                .ToList();
            await SaveAsync(state, cancellationToken);
        }
        finally { _uploadCheckpointGate.Release(); }
    }

    internal async Task RecordUploadChecksumAsync(TransferState state, int index, string hash, CancellationToken token)
    {
        await _uploadCheckpointGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var checksums = state.Checksums ?? throw new InvalidDataException("Checksum de upload ausente.");
            if (!state.UploadChecksumPending || index < 0 || index >= checksums.Chunks.Count || !PayloadChecksums.IsHash(hash))
                throw new InvalidDataException("Checksum de upload inválido.");
            var expected = checksums.Chunks[index];
            if (expected.Length > 0)
            {
                if (expected != hash) throw new PayloadChecksumException("A origem mudou desde a preparação desta parte do upload.");
                return;
            }
            var chunks = checksums.Chunks.ToList();
            chunks[index] = hash;
            state.Checksums = new PayloadChecksums { ChunkSizeBytes = checksums.ChunkSizeBytes, Chunks = chunks };
            await SaveAsync(state, token).ConfigureAwait(false);
        }
        finally { _uploadCheckpointGate.Release(); }
    }

    public async Task MarkUploadChunksAsync(TransferState state, ForumUploadedBatch batch,
        CancellationToken cancellationToken = default)
    {
        if (state.PayloadLayoutVersion is not (TransferPayloadLayouts.MultiAttachment or TransferPayloadLayouts.SharedStorage) || batch.Chunks.Count is < 1 ||
            batch.Chunks.Count > DiscordLimits.AttachmentsPerMessage || string.IsNullOrWhiteSpace(batch.ThreadId) ||
            string.IsNullOrWhiteSpace(batch.BatchId) || batch.Chunks.Any(chunk => chunk.ThreadId != batch.ThreadId ||
                chunk.BatchId != batch.BatchId || chunk.SizeBytes < 0 || chunk.MessageId != batch.Chunks[0].MessageId))
            throw new InvalidDataException("O batch não pertence ao layout/arquivo esperado pela transferência.");
        var references = batch.Chunks.Select(chunk => new ChunkReference
            { Index = chunk.Index, MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId }).ToArray();
        ChunkReference.Validate(references, totalChunks: state.TotalChunks, partial: true);
        await _uploadCheckpointGate.WaitAsync(cancellationToken);
        try
        {
            var updated = state.UploadedChunks.Where(chunk => !references.Any(reference => reference.Index == chunk.Index)).ToList();
            updated.AddRange(batch.Chunks.Select(chunk => new TransferChunkState
            {
                Index = chunk.Index, MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId,
                SizeBytes = chunk.SizeBytes, LogicalSizeBytes = chunk.LogicalSizeBytes
            }));
            state.ThreadId = batch.ThreadId;
            state.BatchId = batch.BatchId;
            state.Status = TransferStatuses.Uploading;
            state.UploadedChunks = updated.OrderBy(chunk => chunk.Index).ToList();
            state.PendingChunks = Enumerable.Range(0, state.TotalChunks).Except(state.UploadedChunks.Select(chunk => chunk.Index)).ToList();
            await SaveAsync(state, cancellationToken);
        }
        finally { _uploadCheckpointGate.Release(); }
    }

    public async Task MarkDownloadChunkAsync(
        TransferState state,
        int index,
        string tempChunkPath,
        long sizeBytes,
        CancellationToken cancellationToken = default,
        bool persist = true)
    {
        state.Status = TransferStatuses.Downloading;
        var chunk = state.Chunks.FirstOrDefault(item => item.Index == index);
        if (chunk == null)
        {
            chunk = new TransferChunkState { Index = index };
            state.Chunks.Add(chunk);
        }

        chunk.Downloaded = true;
        chunk.TempChunkPath = tempChunkPath;
        chunk.SizeBytes = sizeBytes;
        if (!state.DownloadedChunks.Contains(index))
        {
            state.DownloadedChunks.Add(index);
        }

        state.DownloadedChunks = state.DownloadedChunks.OrderBy(value => value).ToList();
        state.PendingChunks = state.Chunks
            .Where(item => !item.Downloaded)
            .Select(item => item.Index)
            .OrderBy(value => value)
            .ToList();
        if (persist)
        {
            await SaveAsync(state, cancellationToken);
        }
    }

    public async Task SaveAsync(TransferState state, CancellationToken cancellationToken = default)
    {
        await _repository.SaveActiveAsync(state, cancellationToken);
    }

    public async Task<IReadOnlyList<TransferState>> RecoverableAsync(CancellationToken cancellationToken = default)
    {
        return await _repository.LoadRecoverableAsync(cancellationToken);
    }

    public async Task CompleteAsync(TransferState state, bool keepHistory, CancellationToken cancellationToken = default)
    {
        await _repository.MoveToCompletedAsync(state, keepHistory, cancellationToken);
    }

    public async Task FailAsync(TransferState state, Exception ex, CancellationToken cancellationToken = default)
    {
        await _repository.MoveToFailedAsync(state, ex.Message, cancellationToken);
    }

    public async Task PauseAsync(TransferState state, Exception ex, CancellationToken cancellationToken = default)
    {
        state.Status = TransferStatuses.Paused;
        state.LastError = ex.Message;
        state.Touch();
        await SaveAsync(state, cancellationToken);
    }

    public async Task CancelAsync(
        TransferState state,
        bool keepHistory,
        bool cleanupTemp,
        CancellationToken cancellationToken = default)
    {
        if (state.TransferType == TransferTypes.Download)
        {
            if (!cleanupTemp)
            {
                await PauseAsync(state, new OperationCanceledException("Download cancelado; dados preservados para retomada."), cancellationToken);
                return;
            }
            CleanupDownload(state);
            await _repository.MoveToCancelledAsync(state, keepHistory, cancellationToken);
            return;
        }
        if (cleanupTemp)
        {
            CleanupTemp(state);
        }

        await _repository.MoveToCancelledAsync(state, keepHistory, cancellationToken);
    }

    private static void CleanupTemp(TransferState state)
    {
        if (string.IsNullOrWhiteSpace(state.TempPath))
        {
            return;
        }

        try
        {
            var dir = new DirectoryInfo(state.TempPath);
            if (dir.Exists)
            {
                dir.Delete(true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Transfer.CleanupTemp", ex, state.TempPath);
        }
    }
}
