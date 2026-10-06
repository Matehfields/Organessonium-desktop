using System.IO;
using System.Diagnostics;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

public sealed class ForumUploader
{
    private readonly DiscordApiClient _discord;
    private readonly string _forumId;
    private readonly MetadataService _metadata;

    public ForumUploader(DiscordApiClient discord, string forumId, IMetadataCodec? codec = null)
    {
        _discord = discord;
        _forumId = forumId;
        _metadata = new MetadataService(codec);
    }

    public async Task<ForumUploadResult> UploadAsync(
        string archiveFileName,
        IReadOnlyList<FileInfo> parts,
        Action<int>? uploadedParts = null,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        return await UploadResumableAsync(
            archiveFileName,
            parts,
            "",
            "",
            new Dictionary<int, string>(),
            uploadedParts,
            null,
            cancellationToken,
            onRateLimitWait);
    }

    public async Task<ForumUploadResult> UploadResumableAsync(
        string archiveFileName,
        IReadOnlyList<FileInfo> parts,
        string existingThreadId,
        string existingBatchId,
        IReadOnlyDictionary<int, string> uploadedChunkMessages,
        Action<int>? uploadedParts = null,
        Func<ForumUploadedChunk, Task>? onChunkUploaded = null,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        int maxParallelParts = 1,
        SemaphoreSlim? uploadGate = null,
        EncryptedPayloadContext? encryptedPayload = null,
        AdaptiveUploadGate? adaptiveUploadGate = null,
        Action<bool>? onTransferActivity = null)
    {
        if (parts.Count == 0)
        {
            throw new ArgumentException("Nenhuma parte para upload.", nameof(parts));
        }

        var batchId = string.IsNullOrWhiteSpace(existingBatchId) ? NewBatchId() : existingBatchId;
        var threadId = existingThreadId ?? "";
        var chunkMessages = Enumerable.Repeat("", parts.Count).ToArray();
        foreach (var pair in uploadedChunkMessages)
        {
            if (pair.Key >= 0 && pair.Key < chunkMessages.Length && !string.IsNullOrWhiteSpace(pair.Value))
            {
                chunkMessages[pair.Key] = pair.Value;
            }
        }

        var completed = chunkMessages.Count(id => !string.IsNullOrWhiteSpace(id));
        uploadedParts?.Invoke(completed);

        async Task<T> InUploadSlotAsync<T>(Func<Task<T>> action, Action<TimeSpan>? onCompleted = null)
        {
            if (adaptiveUploadGate != null)
            {
                using var lease = await adaptiveUploadGate.EnterAsync(cancellationToken);
                var started = Stopwatch.GetTimestamp();
                var result = await action();
                onCompleted?.Invoke(Stopwatch.GetElapsedTime(started));
                return result;
            }

            if (uploadGate == null)
            {
                return await action();
            }

            await uploadGate.WaitAsync(cancellationToken);
            try
            {
                return await action();
            }
            finally
            {
                uploadGate.Release();
            }
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            var first = parts[0];
            var firstWasPending = string.IsNullOrWhiteSpace(chunkMessages[0]);
            var post = await InUploadSlotAsync(() => _discord.CreateForumPostWithFileDetailedAsync(
                _forumId,
                EncryptedThreadTitle(),
                encryptedPayload == null
                    ? PartContent(batchId, 1, parts.Count)
                    : PayloadMessage(encryptedPayload.WithChunkIndex(0), parts.Count),
                first,
                cancellationToken,
                onRateLimitWait,
                serializeRoute: false,
                onTransferActivity: onTransferActivity,
                waitForTransientNetwork: true),
                elapsed => adaptiveUploadGate?.ReportCompleted(first.Length, elapsed));
            threadId = post.ThreadId;
            chunkMessages[0] = post.MessageId;
            if (onChunkUploaded != null)
            {
                await onChunkUploaded(new ForumUploadedChunk(threadId, batchId, 0, post.MessageId, first.Length));
            }

            if (firstWasPending)
            {
                completed = Interlocked.Increment(ref completed);
            }

            uploadedParts?.Invoke(completed);
        }
        else
        {
            await _discord.UnarchiveThreadAsync(threadId, cancellationToken, onRateLimitWait);
        }

        var pending = Enumerable.Range(0, parts.Count)
            .Where(index => string.IsNullOrWhiteSpace(chunkMessages[index]))
            .ToArray();
        if (pending.Length > 0)
        {
            var parallelism = Math.Min(Math.Clamp(maxParallelParts, 1, 16), pending.Length);
            var nextPendingIndex = -1;
            var uploads = Enumerable.Range(0, parallelism).Select(async _ =>
            {
                while (true)
                {
                    var pendingIndex = Interlocked.Increment(ref nextPendingIndex);
                    if (pendingIndex >= pending.Length)
                    {
                        return;
                    }

                    var index = pending[pendingIndex];
                    var part = parts[index];
                    var message = await InUploadSlotAsync(
                        () => _discord.SendFileMessageAsync(
                            threadId,
                            encryptedPayload == null
                                ? PartContent(batchId, index + 1, parts.Count)
                                : PayloadMessage(encryptedPayload.WithChunkIndex(index), parts.Count),
                            part,
                            cancellationToken,
                            onRateLimitWait,
                            serializeRoute: false,
                            onTransferActivity: onTransferActivity,
                            waitForTransientNetwork: true),
                        elapsed => adaptiveUploadGate?.ReportCompleted(part.Length, elapsed));
                    chunkMessages[index] = message.Id;
                    if (onChunkUploaded != null)
                    {
                        await onChunkUploaded(new ForumUploadedChunk(threadId, batchId, index, message.Id, part.Length));
                    }

                    var current = Interlocked.Increment(ref completed);
                    uploadedParts?.Invoke(current);
                }
            });
            await Task.WhenAll(uploads);
        }

        return new ForumUploadResult(threadId, batchId, parts.Count, chunkMessages);
    }

    public async Task<ForumUploadResult> UploadResumableGeneratedAsync(
        string archiveFileName,
        int totalParts,
        Func<int, Task<PreparedUploadPart>> preparePart,
        string existingThreadId,
        string existingBatchId,
        IReadOnlyDictionary<int, string> uploadedChunkMessages,
        Action<int>? uploadedParts = null,
        Func<ForumUploadedChunk, Task>? onChunkUploaded = null,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        int maxParallelParts = 1,
        SemaphoreSlim? uploadGate = null,
        EncryptedPayloadContext? encryptedPayload = null,
        Action<FileInfo>? cleanupPart = null,
        AdaptiveUploadGate? adaptiveUploadGate = null,
        Action<bool>? onTransferActivity = null)
    {
        if (totalParts <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalParts));
        }

        ArgumentNullException.ThrowIfNull(preparePart);

        var batchId = string.IsNullOrWhiteSpace(existingBatchId) ? NewBatchId() : existingBatchId;
        var threadId = existingThreadId ?? "";
        var chunkMessages = Enumerable.Repeat("", totalParts).ToArray();
        foreach (var pair in uploadedChunkMessages)
        {
            if (pair.Key >= 0 && pair.Key < chunkMessages.Length && !string.IsNullOrWhiteSpace(pair.Value))
            {
                chunkMessages[pair.Key] = pair.Value;
            }
        }

        var completed = chunkMessages.Count(id => !string.IsNullOrWhiteSpace(id));
        uploadedParts?.Invoke(completed);

        async Task<T> InUploadSlotAsync<T>(Func<Task<T>> action, Action<TimeSpan>? onCompleted = null)
        {
            if (adaptiveUploadGate != null)
            {
                using var lease = await adaptiveUploadGate.EnterAsync(cancellationToken);
                var started = Stopwatch.GetTimestamp();
                var result = await action();
                onCompleted?.Invoke(Stopwatch.GetElapsedTime(started));
                return result;
            }

            if (uploadGate == null)
            {
                return await action();
            }

            await uploadGate.WaitAsync(cancellationToken);
            try
            {
                return await action();
            }
            finally
            {
                uploadGate.Release();
            }
        }

        void CleanupPart(PreparedUploadPart? prepared)
        {
            if (prepared == null)
            {
                return;
            }

            try
            {
                if (prepared.File != null && cleanupPart != null)
                {
                    cleanupPart(prepared.File);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Discord.CleanupGeneratedPart", ex, prepared.FileName);
            }
            finally
            {
                prepared.Dispose();
            }
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            var firstWasPending = string.IsNullOrWhiteSpace(chunkMessages[0]);
            PreparedUploadPart? first = null;
            try
            {
                var post = await InUploadSlotAsync(async () =>
                {
                    first = await preparePart(0);
                    return await _discord.CreateForumPostWithFileDetailedAsync(
                        _forumId,
                        EncryptedThreadTitle(),
                        encryptedPayload == null
                            ? PartContent(batchId, 1, totalParts)
                            : PayloadMessage(encryptedPayload.WithChunkIndex(0), totalParts),
                        first!,
                        cancellationToken,
                        onRateLimitWait,
                        serializeRoute: false,
                        onTransferActivity: onTransferActivity,
                        waitForTransientNetwork: true);
                }, elapsed => adaptiveUploadGate?.ReportCompleted(first!.Length, elapsed));
                threadId = post.ThreadId;
                chunkMessages[0] = post.MessageId;
                if (onChunkUploaded != null)
                {
                    await onChunkUploaded(new ForumUploadedChunk(
                        threadId,
                        batchId,
                        0,
                        post.MessageId,
                        first!.Length,
                        first.LogicalSizeBytes));
                }

                if (firstWasPending)
                {
                    completed = Interlocked.Increment(ref completed);
                }
                uploadedParts?.Invoke(completed);
            }
            finally
            {
                CleanupPart(first);
            }
        }
        else
        {
            await _discord.UnarchiveThreadAsync(threadId, cancellationToken, onRateLimitWait);
        }

        var pending = Enumerable.Range(0, totalParts)
            .Where(index => string.IsNullOrWhiteSpace(chunkMessages[index]))
            .ToArray();
        if (pending.Length > 0)
        {
            var parallelism = Math.Min(Math.Clamp(maxParallelParts, 1, 16), pending.Length);
            var nextPendingIndex = -1;
            var uploads = Enumerable.Range(0, parallelism).Select(async _ =>
            {
                while (true)
                {
                    var pendingIndex = Interlocked.Increment(ref nextPendingIndex);
                    if (pendingIndex >= pending.Length)
                    {
                        return;
                    }

                    var index = pending[pendingIndex];
                    PreparedUploadPart? prepared = null;
                    try
                    {
                        var message = await InUploadSlotAsync(async () =>
                        {
                            prepared = await preparePart(index);
                            return await _discord.SendFileMessageAsync(
                                threadId,
                                encryptedPayload == null
                                    ? PartContent(batchId, index + 1, totalParts)
                                    : PayloadMessage(encryptedPayload.WithChunkIndex(index), totalParts),
                                prepared!,
                                cancellationToken,
                                onRateLimitWait,
                                serializeRoute: false,
                                onTransferActivity: onTransferActivity,
                                waitForTransientNetwork: true);
                        }, elapsed => adaptiveUploadGate?.ReportCompleted(prepared!.Length, elapsed));
                        chunkMessages[index] = message.Id;
                        if (onChunkUploaded != null)
                        {
                            await onChunkUploaded(new ForumUploadedChunk(
                                threadId,
                                batchId,
                                index,
                                message.Id,
                                prepared!.Length,
                                prepared.LogicalSizeBytes));
                        }

                        var current = Interlocked.Increment(ref completed);
                        uploadedParts?.Invoke(current);
                    }
                    finally
                    {
                        CleanupPart(prepared);
                    }
                }
            });
            await Task.WhenAll(uploads);
        }

        return new ForumUploadResult(threadId, batchId, totalParts, chunkMessages);
    }

    public async Task<ForumUploadResult> UploadToThreadAsync(
        string threadId,
        string archiveFileName,
        IReadOnlyList<FileInfo> parts,
        Action<int>? uploadedParts = null,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        EncryptedPayloadContext? encryptedPayload = null)
    {
        if (parts.Count == 0)
        {
            throw new ArgumentException("Nenhuma parte para upload.", nameof(parts));
        }

        var batchId = NewBatchId();
        uploadedParts?.Invoke(0);
        var chunkMessages = new List<string>();
        await _discord.UnarchiveThreadAsync(threadId, cancellationToken, onRateLimitWait);
        for (var i = 0; i < parts.Count; i++)
        {
            var message = await _discord.SendFileMessageAsync(
                threadId,
                encryptedPayload == null
                    ? PartContent(batchId, i + 1, parts.Count)
                    : PayloadMessage(encryptedPayload.WithChunkIndex(i), parts.Count),
                parts[i],
                cancellationToken,
                onRateLimitWait,
                serializeRoute: false,
                waitForTransientNetwork: true);
            chunkMessages.Add(message.Id);
            uploadedParts?.Invoke(i + 1);
        }

        return new ForumUploadResult(threadId, batchId, parts.Count, chunkMessages);
    }

    public Task ArchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        return _discord.ArchiveThreadAsync(threadId, cancellationToken, onRateLimitWait);
    }

    public async Task<string> UploadPreviewFileToThreadAsync(
        string threadId,
        string content,
        FileInfo file,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        try
        {
            var message = await _discord.SendFileMessageAsync(
                threadId,
                content,
                file,
                cancellationToken,
                onRateLimitWait,
                waitForTransientNetwork: true);
            return message.Id;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Discord.UploadPreviewFile", ex, $"{content} | {file.FullName}");
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> UploadPreviewToThreadAsync(
        string threadId,
        string kind,
        IReadOnlyList<FileInfo> parts,
        Action<int>? uploadedParts = null,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        EncryptedPayloadContext? encryptedPayload = null)
    {
        if (parts.Count == 0)
        {
            throw new ArgumentException("Nenhuma parte de prévia para upload.", nameof(parts));
        }

        var batchId = NewBatchId();
        uploadedParts?.Invoke(0);
        var chunkMessages = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var message = await _discord.SendFileMessageAsync(
                threadId,
                encryptedPayload == null
                    ? PreviewPartContent(kind, batchId, i + 1, parts.Count)
                    : PayloadMessage(encryptedPayload.WithChunkIndex(i), parts.Count),
                parts[i],
                cancellationToken,
                onRateLimitWait,
                waitForTransientNetwork: true);
            chunkMessages.Add(message.Id);
            uploadedParts?.Invoke(i + 1);
        }

        return chunkMessages;
    }

    private static string EncryptedThreadTitle()
    {
        return "payload-" + Guid.NewGuid().ToString("N");
    }

    private string PayloadMessage(EncryptedPayloadContext context, int count) =>
        _metadata.EncodePayloadReference(EncryptedPayloadProtocol.Message(context, count));

    private static string NewBatchId()
    {
        return Guid.NewGuid().ToString("N")[..10];
    }

    private string PartContent(string batchId, int part, int total)
    {
        return _metadata.EncodePayloadReference($"\U0001F4E6 batch:{batchId} Parte {part}/{total}");
    }

    private string PreviewPartContent(string kind, string batchId, int part, int total)
    {
        return _metadata.EncodePayloadReference($"ORG_PREVIEW|{kind}|batch:{batchId}|Parte {part}/{total}");
    }
}

public sealed record ForumUploadResult(
    string ThreadId,
    string BatchId,
    int ChunkCount,
    IReadOnlyList<string> ChunkMessageIds);

public sealed record ForumUploadedChunk(
    string ThreadId,
    string BatchId,
    int Index,
    string MessageId,
    long SizeBytes,
    long LogicalSizeBytes = -1);

public sealed class PreparedUploadPart : IDisposable
{
    private UploadChunkBufferLease? _memoryLease;
    private readonly int _memoryLength;

    public PreparedUploadPart(FileInfo file, long logicalSizeBytes)
    {
        File = file ?? throw new ArgumentNullException(nameof(file));
        FileName = file.Name;
        Length = file.Length;
        LogicalSizeBytes = logicalSizeBytes;
    }

    internal PreparedUploadPart(
        string fileName,
        int length,
        long logicalSizeBytes,
        UploadChunkBufferLease memoryLease)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("O nome do attachment é obrigatório.", nameof(fileName));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        FileName = fileName;
        Length = length;
        LogicalSizeBytes = logicalSizeBytes;
        _memoryLease = memoryLease ?? throw new ArgumentNullException(nameof(memoryLease));
        _memoryLength = length;
    }

    public FileInfo? File { get; }

    public string FileName { get; }

    public long Length { get; }

    public long LogicalSizeBytes { get; }

    internal Stream OpenRead()
    {
        if (File != null)
        {
            return File.OpenRead();
        }

        var lease = _memoryLease ?? throw new ObjectDisposedException(nameof(PreparedUploadPart));
        return lease.OpenCiphertextStream(_memoryLength);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _memoryLease, null)?.Dispose();
    }
}
