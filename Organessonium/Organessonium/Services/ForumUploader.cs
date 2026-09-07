using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class ForumUploader
{
    private readonly DiscordApiClient _discord;
    private readonly string _forumId;

    public ForumUploader(DiscordApiClient discord, string forumId)
    {
        _discord = discord;
        _forumId = forumId;
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
        EncryptedPayloadContext? encryptedPayload = null)
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

        async Task<T> InUploadSlotAsync<T>(Func<Task<T>> action)
        {
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
                encryptedPayload == null ? StripExtension(archiveFileName) : EncryptedThreadTitle(encryptedPayload.FileId),
                encryptedPayload == null
                    ? PartContent(batchId, 1, parts.Count)
                    : EncryptedPayloadProtocol.Message(encryptedPayload.WithChunkIndex(0), parts.Count),
                first,
                cancellationToken,
                onRateLimitWait));
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
            using var localGate = new SemaphoreSlim(parallelism, parallelism);
            var uploads = pending.Select(async index =>
            {
                await localGate.WaitAsync(cancellationToken);
                try
                {
                    var message = await InUploadSlotAsync(() => _discord.SendFileMessageAsync(
                        threadId,
                        encryptedPayload == null
                            ? PartContent(batchId, index + 1, parts.Count)
                            : EncryptedPayloadProtocol.Message(encryptedPayload.WithChunkIndex(index), parts.Count),
                        parts[index],
                        cancellationToken,
                        onRateLimitWait));
                    chunkMessages[index] = message.Id;
                    if (onChunkUploaded != null)
                    {
                        await onChunkUploaded(new ForumUploadedChunk(threadId, batchId, index, message.Id, parts[index].Length));
                    }

                    var current = Interlocked.Increment(ref completed);
                    uploadedParts?.Invoke(current);
                }
                finally
                {
                    localGate.Release();
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
        Action<FileInfo>? cleanupPart = null)
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

        async Task<T> InUploadSlotAsync<T>(Func<Task<T>> action)
        {
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
            if (prepared == null || cleanupPart == null)
            {
                return;
            }

            try
            {
                cleanupPart(prepared.File);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Discord.CleanupGeneratedPart", ex, prepared.File.FullName);
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
                        encryptedPayload == null ? StripExtension(archiveFileName) : EncryptedThreadTitle(encryptedPayload.FileId),
                        encryptedPayload == null
                            ? PartContent(batchId, 1, totalParts)
                            : EncryptedPayloadProtocol.Message(encryptedPayload.WithChunkIndex(0), totalParts),
                        first!.File,
                        cancellationToken,
                        onRateLimitWait);
                });
                threadId = post.ThreadId;
                chunkMessages[0] = post.MessageId;
                if (onChunkUploaded != null)
                {
                    await onChunkUploaded(new ForumUploadedChunk(
                        threadId,
                        batchId,
                        0,
                        post.MessageId,
                        first!.File.Length,
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
            using var localGate = new SemaphoreSlim(parallelism, parallelism);
            var uploads = pending.Select(async index =>
            {
                await localGate.WaitAsync(cancellationToken);
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
                                : EncryptedPayloadProtocol.Message(encryptedPayload.WithChunkIndex(index), totalParts),
                            prepared!.File,
                            cancellationToken,
                            onRateLimitWait);
                    });
                    chunkMessages[index] = message.Id;
                    if (onChunkUploaded != null)
                    {
                        await onChunkUploaded(new ForumUploadedChunk(
                            threadId,
                            batchId,
                            index,
                            message.Id,
                            prepared!.File.Length,
                            prepared.LogicalSizeBytes));
                    }

                    var current = Interlocked.Increment(ref completed);
                    uploadedParts?.Invoke(current);
                }
                finally
                {
                    CleanupPart(prepared);
                    localGate.Release();
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
                    : EncryptedPayloadProtocol.Message(encryptedPayload.WithChunkIndex(i), parts.Count),
                parts[i],
                cancellationToken,
                onRateLimitWait);
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
                onRateLimitWait);
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
                    : EncryptedPayloadProtocol.Message(encryptedPayload.WithChunkIndex(i), parts.Count),
                parts[i],
                cancellationToken,
                onRateLimitWait);
            chunkMessages.Add(message.Id);
            uploadedParts?.Invoke(i + 1);
        }

        return chunkMessages;
    }

    private static string StripExtension(string name)
    {
        var without = System.IO.Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(without) ? name : without;
    }

    private static string EncryptedThreadTitle(string fileId)
    {
        var value = string.IsNullOrWhiteSpace(fileId) ? "payload" : fileId[..Math.Min(16, fileId.Length)];
        return "payload-" + value;
    }

    private static string NewBatchId()
    {
        return Guid.NewGuid().ToString("N")[..10];
    }

    private static string PartContent(string batchId, int part, int total)
    {
        return $"\U0001F4E6 batch:{batchId} Parte {part}/{total}";
    }

    private static string PreviewPartContent(string kind, string batchId, int part, int total)
    {
        return $"ORG_PREVIEW|{kind}|batch:{batchId}|Parte {part}/{total}";
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

public sealed record PreparedUploadPart(FileInfo File, long LogicalSizeBytes);
