using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed class SharedPayloadBatcher(SharedStorageManager storage, DiscordApiClient api, MetadataService metadata)
{
    private readonly ConcurrentDictionary<string, Lazy<Channel<Work>>> _queues = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim NetworkBodies = new(16, 16);

    internal async Task<ChunkReference> SendAsync(SharedStorageReservation reservation, EncryptedPayloadContext context,
        PreparedUploadPart part, Func<ChunkReference, Task>? confirmed, CancellationToken token,
        AdaptiveUploadGate? bodyGate = null, Func<TimeSpan, Task>? onWait = null, Action<bool>? activity = null)
    {
        token.ThrowIfCancellationRequested();
        part.SetTransportName(Guid.NewGuid().ToString("N") + ".bin");
        var work = new Work(reservation, context, part, confirmed, token, bodyGate, onWait, activity);
        var queue = _queues.GetOrAdd(reservation.ThreadId, id => new Lazy<Channel<Work>>(() =>
        {
            var channel = Channel.CreateBounded<Work>(new BoundedChannelOptions(DiscordLimits.MaxPendingUploadMessages * DiscordLimits.MaxAttachmentsPerSharedMessage)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
            _ = RunAsync(id, channel.Reader);
            return channel;
        })).Value;
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(token, api.PayloadLifetime);
        await queue.Writer.WriteAsync(work, admission.Token).ConfigureAwait(false);
        // Cancellation after admission cannot abort a multipart containing another file.
        return await work.Completion.Task.ConfigureAwait(false);
    }

    private async Task RunAsync(string threadId, ChannelReader<Work> reader)
    {
        try
        {
            while (await reader.WaitToReadAsync(api.PayloadLifetime).ConfigureAwait(false))
            {
                await RamUploadBudget.PendingMessages.WaitAsync(api.PayloadLifetime).ConfigureAwait(false);
                var batch = new List<Work>(DiscordLimits.MaxAttachmentsPerSharedMessage);
                var deadline = Stopwatch.GetTimestamp();
                while (batch.Count < DiscordLimits.MaxAttachmentsPerSharedMessage)
                {
                    while (batch.Count < DiscordLimits.MaxAttachmentsPerSharedMessage && reader.TryRead(out var work))
                    {
                        if (work.Token.IsCancellationRequested) work.Completion.TrySetCanceled(work.Token);
                        else batch.Add(work);
                    }
                    var remaining = DiscordLimits.SharedBatchWaitMilliseconds - Stopwatch.GetElapsedTime(deadline).TotalMilliseconds;
                    if (batch.Count == DiscordLimits.MaxAttachmentsPerSharedMessage || remaining <= 0) break;
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(api.PayloadLifetime);
                    wait.CancelAfter((int)Math.Ceiling(remaining));
                    try { if (!await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false)) break; }
                    catch (OperationCanceledException) { break; }
                }
                if (batch.Count == 0) { RamUploadBudget.PendingMessages.Release(); continue; }
                _ = CompleteAsync(threadId, batch);
            }
        }
        catch (Exception error)
        {
            while (reader.TryRead(out var work)) work.Completion.TrySetException(error);
        }
    }

    private async Task CompleteAsync(string threadId, List<Work> batch)
    {
        try
        {
            await using var lease = await api.AcquireThreadWriteAsync(threadId, api.PayloadLifetime);
            await SendBatchAsync(threadId, batch).ConfigureAwait(false);
        }
        catch (Exception error) { foreach (var work in batch) work.Completion.TrySetException(error); }
        finally { RamUploadBudget.PendingMessages.Release(); }
    }

    private async Task SendBatchAsync(string threadId, IReadOnlyList<Work> batch)
    {
        using var send = CancellationTokenSource.CreateLinkedTokenSource(api.PayloadLifetime);
        var registrations = batch.Select(work => work.Token.Register(() =>
            { if (batch.All(item => item.Token.IsCancellationRequested)) send.Cancel(); })).ToArray();
        try
        {
            var plan = new StorageBatchPlan
            {
                ThreadId = threadId,
                Parts = batch.Select(work => new StorageBatchPart
                {
                    FileId = work.Reservation.FileId,
                    VersionId = work.Reservation.VersionId,
                    Role = work.Context.Role,
                    Index = work.Context.ChunkIndex,
                    Count = work.Context.ChunkCount,
                    Filename = work.Part.FileName,
                    SizeBytes = work.Part.Length
                }).ToList()
            };
            await storage.WriteRecordAsync(plan, null, api.PayloadLifetime);
            DiscordMessageDto message;
            try
            {
                message = await api.SendFilesMessageAsync(threadId,
                    metadata.EncodePayloadReference("shared:v2 batch:" + plan.BatchId), batch.Select(work => work.Part).ToArray(),
                    send.Token, batch[0].OnWait, serializeRoute: false,
                    onTransferActivity: batch[0].Activity, waitForTransientNetwork: true, uploadBodyGate: batch[0].BodyGate,
                    uploadBodySharedGate: NetworkBodies);
            }
            catch (DiscordApiException error) when (batch.Count > 1 && ForumUploader.IsAttachmentLimitRejection(error))
            {
                var half = Math.Max(1, batch.Count / 2);
                foreach (var smaller in batch.Chunk(half)) await SendBatchAsync(threadId, smaller);
                return;
            }
            var mapped = ForumUploader.MapAttachments(threadId, plan.BatchId, message,
                batch.Select(work => work.Context.ChunkIndex).ToArray(), batch.Select(work => work.Part).ToArray());
            var references = mapped.Select(chunk => new ChunkReference
            { Index = chunk.Index, MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId }).ToArray();
            // Remote receipt precedes local checkpoints; it survives a partial multi-file checkpoint write.
            await storage.WriteRecordAsync(new
            {
                schemaVersion = 1,
                entryType = "storageBatchReceipt",
                batchId = plan.BatchId,
                threadId,
                messageId = message.Id,
                attachments = mapped.Select((chunk, index) => new
                { filename = batch[index].Part.FileName, attachmentId = chunk.AttachmentId, sizeBytes = chunk.SizeBytes }).ToArray()
            }, null, CancellationToken.None);
            var failures = new Exception?[batch.Count];
            for (var index = 0; index < batch.Count; index++)
            {
                try { if (batch[index].Confirmed != null) await batch[index].Confirmed!(references[index]); }
                catch (Exception error) { failures[index] = error; }
            }
            for (var index = 0; index < batch.Count; index++)
            {
                var work = batch[index];
                if (failures[index] != null) work.Completion.TrySetException(failures[index]!);
                else if (work.Token.IsCancellationRequested) work.Completion.TrySetCanceled(work.Token);
                else work.Completion.TrySetResult(references[index]);
            }
        }
        finally { foreach (var registration in registrations) registration.Dispose(); }
    }

    private sealed record Work(SharedStorageReservation Reservation, EncryptedPayloadContext Context, PreparedUploadPart Part,
        Func<ChunkReference, Task>? Confirmed, CancellationToken Token, AdaptiveUploadGate? BodyGate,
        Func<TimeSpan, Task>? OnWait, Action<bool>? Activity)
    {
        internal TaskCompletionSource<ChunkReference> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
