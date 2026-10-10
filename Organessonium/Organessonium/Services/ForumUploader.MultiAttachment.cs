using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class ForumUploader
{
    private int _attachmentsPerBatch = DiscordLimits.AttachmentsPerMessage;

    private async Task<ForumUploadResult> UploadGroupedGeneratedAsync(
        int totalParts, Func<int, Task<PreparedUploadPart>> preparePart,
        string existingThreadId, string existingBatchId, IReadOnlyList<ChunkReference> uploadedReferences,
        Action<int>? uploadedParts, Func<ForumUploadedChunk, Task>? onChunkUploaded,
        Func<ForumUploadedBatch, Task>? onBatchUploaded, CancellationToken token,
        Func<TimeSpan, Task>? onWait, int maxParallelParts, SemaphoreSlim? uploadGate,
        EncryptedPayloadContext? encryption, Action<FileInfo>? cleanupPart,
        AdaptiveUploadGate? adaptiveGate, Action<bool>? onActivity, SemaphoreSlim? preparationGate,
        Action<int, long>? onPartTransferProgress = null, int maxPreparedPartBytes = DiscordLimits.AttachmentBytes,
        bool streamPreparedParts = false)
    {
        if (encryption == null)
            throw new InvalidDataException("Uploads novos agrupados exigem o contexto de criptografia dos chunks.");
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        token = pipelineCancellation.Token;
        var confirmed = ChunkReference.Validate(uploadedReferences, totalChunks: totalParts, partial: true);
        var references = new ChunkReference?[totalParts];
        foreach (var reference in confirmed) references[reference.Index] = reference;
        var threadId = existingThreadId ?? "";
        var batchId = string.IsNullOrWhiteSpace(existingBatchId) ? NewBatchId() : existingBatchId;
        if (threadId.Length == 0 && confirmed.Count > 0)
            throw new InvalidDataException("O checkpoint contém chunks confirmados sem o thread de origem.");
        var completed = confirmed.Count;
        var progressSync = new object();
        uploadedParts?.Invoke(completed);
        using var localPreparationGate = preparationGate == null ? new SemaphoreSlim(2, 2) : null;
        var preparationSlots = streamPreparedParts ? RamUploadBudget.PendingMessages : preparationGate ?? localPreparationGate!;
        using var localBodyGate = streamPreparedParts && adaptiveGate == null ? new AdaptiveUploadGate(maxParallelParts, false) : null;
        var bodyGate = streamPreparedParts ? adaptiveGate ?? localBodyGate : null;

        async Task InSlotAsync(Func<Task> action, Func<long> sentBytes)
        {
            if (streamPreparedParts) { await action().ConfigureAwait(false); return; }
            using var lease = adaptiveGate == null ? null : await adaptiveGate.EnterAsync(token);
            if (adaptiveGate == null && uploadGate != null) await uploadGate.WaitAsync(token);
            try
            {
                var started = Stopwatch.GetTimestamp();
                await action();
                adaptiveGate?.ReportCompleted(sentBytes(), Stopwatch.GetElapsedTime(started));
            }
            finally { if (adaptiveGate == null) uploadGate?.Release(); }
        }

        async Task ConfirmAsync(DiscordMessageDto message, int[] indices, IReadOnlyList<PreparedUploadPart> files)
        {
            var chunks = MapAttachments(threadId, batchId, message, indices, files);
            if (onBatchUploaded != null) await onBatchUploaded(new ForumUploadedBatch(threadId, batchId, chunks));
            else if (onChunkUploaded != null)
                foreach (var chunk in chunks) await onChunkUploaded(chunk);
            foreach (var chunk in chunks)
                references[chunk.Index] = new ChunkReference
                    { Index = chunk.Index, MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId };
            lock (progressSync)
            {
                completed += chunks.Count;
                uploadedParts?.Invoke(completed);
            }
        }

        void Cleanup(IReadOnlyList<PreparedUploadPart> files)
        {
            foreach (var file in files)
            {
                try { if (file.File != null) cleanupPart?.Invoke(file.File); }
                catch (Exception ex) { AppLogger.Error("Discord.CleanupGroupedPart", ex); }
                finally { file.Dispose(); }
            }
        }

        async Task SendBatchAsync(int[] indices, IReadOnlyList<PreparedUploadPart> files)
        {
            var learnedLimit = Volatile.Read(ref _attachmentsPerBatch);
            if (files.Count > learnedLimit)
            {
                for (var offset = 0; offset < files.Count; offset += learnedLimit)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(learnedLimit, files.Count - offset);
                    await SendBatchAsync(indices.AsSpan(offset, count).ToArray(), files.Skip(offset).Take(count).ToArray());
                }
                return;
            }
            var content = indices.Length == 1
                ? EncryptedPayloadProtocol.Message(encryption.WithChunkIndex(indices[0]), totalParts)
                : $"file:{encryption.FileId} role:{encryption.Role} chunks:{string.Join(',', indices.Select(index => index + 1))}/{totalParts} encryption:v1";
            try
            {
                var message = await _discord.SendFilesMessageAsync(threadId, _metadata.EncodePayloadReference(content), files,
                    token, onWait, serializeRoute: false, onTransferActivity: onActivity, waitForTransientNetwork: true,
                    uploadBodyGate: bodyGate, uploadBodySharedGate: adaptiveGate == null ? uploadGate : null);
                await ConfirmAsync(message, indices, files);
            }
            catch (DiscordApiException ex) when (files.Count > 1 && IsAttachmentLimitRejection(ex))
            {
                var nextSize = Math.Max(1, files.Count / 2);
                int currentLimit;
                do
                {
                    currentLimit = Volatile.Read(ref _attachmentsPerBatch);
                    if (currentLimit <= nextSize) break;
                } while (Interlocked.CompareExchange(ref _attachmentsPerBatch, nextSize, currentLimit) != currentLimit);
                // Reuse the same encrypted payloads, including nonces, when reducing transport grouping.
                for (var offset = 0; offset < files.Count; offset += nextSize)
                {
                    token.ThrowIfCancellationRequested();
                    var count = Math.Min(nextSize, files.Count - offset);
                    await SendBatchAsync(indices.AsSpan(offset, count).ToArray(), files.Skip(offset).Take(count).ToArray());
                }
            }
        }

        async Task InitializeThreadAsync()
        {
            if (threadId.Length > 0)
            {
                await _discord.UnarchiveThreadAsync(threadId, token, onWait);
                return;
            }
            var initial = new List<PreparedUploadPart>(1);
            try
            {
                await InSlotAsync(async () =>
                {
                    using var initialReservation = preparationGate == null || streamPreparedParts ? null : await RamUploadBudget.ReserveBytesAsync(maxPreparedPartBytes, token);
                    initial.Add(await Task.Run(() => preparePart(0), token));
                    initial[0].TransferProgress = bytes => onPartTransferProgress?.Invoke(0, bytes);
                    var post = await _discord.CreateForumPostWithFileDetailedAsync(_forumId, EncryptedThreadTitle(),
                        PayloadMessage(encryption.WithChunkIndex(0), totalParts), initial[0], token, onWait,
                        serializeRoute: false, onTransferActivity: onActivity, waitForTransientNetwork: true,
                        uploadBodyGate: bodyGate, uploadBodySharedGate: adaptiveGate == null ? uploadGate : null);
                    threadId = post.ThreadId;
                    var message = post.Attachments is { Count: > 0 }
                        ? new DiscordMessageDto { Id = post.MessageId, Attachments = post.Attachments.ToList() }
                        : await _discord.GetMessageAsync(threadId, post.MessageId, token, waitForTransientNetwork: true);
                    await ConfirmAsync(message, [0], initial);
                }, () => initial[0].Length);
            }
            finally { Cleanup(initial); }
        }

        var needsInitialChunk = threadId.Length == 0;
        var batchSize = Volatile.Read(ref _attachmentsPerBatch);
        if (preparationGate != null && !streamPreparedParts)
            batchSize = Math.Min(batchSize, RamUploadBudget.PipelineBatchChunks(maxPreparedPartBytes));
        var batches = Enumerable.Range(0, totalParts).Where(index => references[index] == null && (!needsInitialChunk || index != 0))
            .Chunk(batchSize).ToArray();
        var ready = Channel.CreateBounded<PreparedBatch>(new BoundedChannelOptions(1)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        async Task PrepareBatchesAsync()
        {
            try
            {
                foreach (var indices in batches)
                {
                    // Backpressure before reading: only one ready successor is retained per file.
                    if (!await ready.Writer.WaitToWriteAsync(token).ConfigureAwait(false)) break;
                    var files = new List<PreparedUploadPart>(indices.Length);
                    IDisposable? memoryReservation = null;
                    var reserved = false;
                    try
                    {
                        await preparationSlots.WaitAsync(token).ConfigureAwait(false);
                        reserved = true;
                        if (preparationGate != null && !streamPreparedParts)
                            memoryReservation = await RamUploadBudget.ReserveBytesAsync((long)indices.Length * maxPreparedPartBytes, token).ConfigureAwait(false);
                        foreach (var index in indices)
                        {
                            token.ThrowIfCancellationRequested();
                            var file = await Task.Run(() => preparePart(index), token).ConfigureAwait(false);
                            file.TransferProgress = bytes => onPartTransferProgress?.Invoke(index, bytes);
                            files.Add(file);
                        }
                        if (preparationGate == null && !streamPreparedParts)
                        {
                            preparationSlots.Release();
                            reserved = false;
                        }
                        var batch = new PreparedBatch(indices, files, memoryReservation,
                            reserved ? preparationSlots : null, Cleanup);
                        // The batch now owns all cleanup, including cancellation during publication.
                        memoryReservation = null;
                        reserved = false;
                        files = [];
                        try { await ready.Writer.WriteAsync(batch, token).ConfigureAwait(false); }
                        catch { batch.Dispose(); throw; }
                    }
                    finally
                    {
                        Cleanup(files);
                        memoryReservation?.Dispose();
                        if (reserved) preparationSlots.Release();
                    }
                }
                ready.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                ready.Writer.TryComplete(ex);
                pipelineCancellation.Cancel();
                throw;
            }
        }

        var producer = Task.Run(PrepareBatchesAsync, token);
        try { await InitializeThreadAsync().ConfigureAwait(false); }
        catch
        {
            pipelineCancellation.Cancel();
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally { while (ready.Reader.TryRead(out var abandoned)) abandoned.Dispose(); }
            throw;
        }
        var workerLimit = streamPreparedParts ? DiscordLimits.MaxPendingUploadMessages : adaptiveGate == null ? Math.Clamp(maxParallelParts, 1, 16) : 16;
        var workers = Enumerable.Range(0, Math.Min(workerLimit, batches.Length)).Select(async workerIndex =>
        {
            try
            {
                while (true)
                {
                    if (!streamPreparedParts && adaptiveGate != null && !adaptiveGate.Forced && workerIndex >= Math.Clamp(maxParallelParts, 1, 16))
                    {
                        if (ready.Reader.Completion.IsCompleted) return;
                        await Task.Delay(50, token).ConfigureAwait(false);
                        continue;
                    }
                    if (!await ready.Reader.WaitToReadAsync(token).ConfigureAwait(false)) return;
                    if (!ready.Reader.TryRead(out var batch)) continue;
                    using (batch)
                        await InSlotAsync(() => SendBatchAsync(batch.Indices, batch.Files),
                            () => batch.Files.Sum(file => file.Length)).ConfigureAwait(false);
                }
            }
            catch
            {
                pipelineCancellation.Cancel();
                throw;
            }
        }).ToArray();
        try { await Task.WhenAll(workers.Prepend(producer)).ConfigureAwait(false); }
        finally
        {
            pipelineCancellation.Cancel();
            while (ready.Reader.TryRead(out var abandoned)) abandoned.Dispose();
        }
        if (references.Any(reference => reference == null))
            throw new InvalidDataException("O servidor não confirmou todas as referências do arquivo.");
        return ForumUploadResult.FromReferences(threadId, batchId, references.Select(reference => reference!).ToArray());
    }

    private sealed class PreparedBatch(int[] indices, IReadOnlyList<PreparedUploadPart> files,
        IDisposable? reservation, SemaphoreSlim? slot, Action<IReadOnlyList<PreparedUploadPart>> cleanup) : IDisposable
    {
        internal int[] Indices { get; } = indices;
        internal IReadOnlyList<PreparedUploadPart> Files { get; } = files;
        public void Dispose()
        {
            try { cleanup(Files); }
            finally { reservation?.Dispose(); slot?.Release(); }
        }
    }

    private static IReadOnlyList<ForumUploadedChunk> MapAttachments(string threadId, string batchId,
        DiscordMessageDto message, IReadOnlyList<int> indices, IReadOnlyList<PreparedUploadPart> files)
    {
        if (string.IsNullOrWhiteSpace(message.Id) || message.Attachments == null || message.Attachments.Count != files.Count ||
            files.Select(file => file.FileName).Distinct(StringComparer.Ordinal).Count() != files.Count ||
            message.Attachments.Select(attachment => attachment.Id).Distinct(StringComparer.Ordinal).Count() != files.Count)
            throw new InvalidDataException("A resposta do Discord não confirmou a quantidade/identidade dos attachments enviados.");
        var chunks = new List<ForumUploadedChunk>(files.Count);
        for (var position = 0; position < files.Count; position++)
        {
            var file = files[position];
            var matches = message.Attachments.Where(attachment => attachment.FileName == file.FileName).ToArray();
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Id) || matches[0].Size != file.Length)
                throw new InvalidDataException("O Discord retornou um attachment ausente, duplicado ou com tamanho diferente.");
            chunks.Add(new ForumUploadedChunk(threadId, batchId, indices[position], message.Id, file.Length,
                file.LogicalSizeBytes, matches[0].Id));
        }
        return chunks;
    }

    private static bool IsAttachmentLimitRejection(DiscordApiException exception)
    {
        if (exception.StatusCode == HttpStatusCode.RequestEntityTooLarge || exception.DiscordCode == 40005) return true;
        if (exception.StatusCode != HttpStatusCode.BadRequest || exception.DiscordCode != 50035) return false;
        try
        {
            using var document = JsonDocument.Parse(exception.Body);
            if (!document.RootElement.TryGetProperty("errors", out var errors)) return false;
            return errors.EnumerateObject().Any(field => (field.Name is "attachments" or "files" || field.Name.StartsWith("files[", StringComparison.Ordinal)) &&
                HasLimitCode(field.Value));
        }
        catch (JsonException) { return false; }
    }

    private static bool HasLimitCode(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(field => field.Name == "code" && field.Value.ValueKind == JsonValueKind.String &&
            field.Value.GetString() is "BASE_TYPE_MAX_LENGTH" or "FILE_TOO_LARGE" or "REQUEST_ENTITY_TOO_LARGE" || HasLimitCode(field.Value)),
        JsonValueKind.Array => value.EnumerateArray().Any(HasLimitCode),
        _ => false
    };
}
