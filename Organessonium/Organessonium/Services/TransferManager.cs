using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class TransferManager
{
    private readonly TransferStateRepository _repository;

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
        var chunks = entry.ChunkMessageIds
            .Select((messageId, index) => new TransferChunkState
            {
                Index = index,
                MessageId = messageId,
                Downloaded = false
            })
            .ToList();

        var state = new TransferState
        {
            TransferType = TransferTypes.Download,
            Status = TransferStatuses.Downloading,
            FileId = entry.FileId,
            FileName = entry.Name,
            MimeType = entry.MimeType,
            SizeBytes = entry.SizeBytes,
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

    public async Task CancelAsync(
        TransferState state,
        bool keepHistory,
        bool cleanupTemp,
        CancellationToken cancellationToken = default)
    {
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
