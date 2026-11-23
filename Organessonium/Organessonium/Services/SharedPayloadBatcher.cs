using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedPayloadBatcher(SharedStorageManager storage, DiscordApiClient api, MetadataService metadata)
{
    private readonly ConcurrentDictionary<string, Lazy<Channel<Work[]>>> _queues = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim NetworkBodies = new(16, 16);
    private readonly ConcurrentDictionary<(string Thread, string Version, string Batch), byte> _uncertain = new();
    internal bool HasUncertainPublication(string thread, string version) => _uncertain.Keys.Any(key => key.Thread == thread && key.Version == version);

    internal async Task<ChunkReference> SendAsync(SharedStorageReservation reservation, EncryptedPayloadContext context,
        PreparedUploadPart part, Func<ChunkReference, Task>? confirmed, CancellationToken token,
        AdaptiveUploadGate? bodyGate = null, Func<TimeSpan, Task>? onWait = null, Action<bool>? activity = null)
    {
        var work = new Work(reservation, context, part, confirmed, token, bodyGate, onWait, activity);
        await QueueAsync(reservation.ThreadId, [work], token).ConfigureAwait(false);
        return await work.Completion.Task.ConfigureAwait(false);
    }

    internal async Task<ChunkReference> SendWithCompanionAsync(SharedStorageReservation reservation, EncryptedPayloadContext context,
        PreparedUploadPart part, Func<ChunkReference, Task>? confirmed, SharedUploadCompanionPayload companion, CancellationToken token,
        AdaptiveUploadGate? bodyGate, Func<TimeSpan, Task>? onWait, Action<bool>? activity)
    {
        var original = new Work(reservation, context, part, confirmed, token, bodyGate, onWait, activity);
        var thumbnail = new Work(reservation, companion.Context, companion.Part, companion.Confirmed, token, bodyGate, onWait, activity);
        await QueueAsync(reservation.ThreadId, [original, thumbnail], token).ConfigureAwait(false);
        await Task.WhenAll(original.Completion.Task, thumbnail.Completion.Task).ConfigureAwait(false);
        return await original.Completion.Task.ConfigureAwait(false);
    }

    private async Task QueueAsync(string thread, Work[] group, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        foreach (var work in group)
        {
            work.Part.SetTransportName(Guid.NewGuid().ToString("N") + ".bin");
            work.TrackedProduction = IsExpectedProduction(work.Reservation, work.Context.Role);
        }
        var queue = _queues.GetOrAdd(thread, id => new Lazy<Channel<Work[]>>(() =>
        {
            var channel = Channel.CreateBounded<Work[]>(new BoundedChannelOptions(DiscordLimits.MaxPendingUploadMessages * DiscordLimits.MaxAttachmentsPerSharedMessage)
            { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
            _ = RunAsync(id, channel.Reader);
            return channel;
        })).Value;
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(token, api.PayloadLifetime);
        await queue.Writer.WriteAsync(group, admission.Token).ConfigureAwait(false);
        foreach (var work in group) ProductionReady(work.Reservation, work.Context.Role);
        // Cancellation after admission cannot abort a multipart containing another file.
    }

    private async Task RunAsync(string threadId, ChannelReader<Work[]> reader)
    {
        Work[]? carry = null;
        Task<bool>? pendingRead = null;
        List<Work>? building = null;
        try
        {
            while (carry != null || await (pendingRead ??= reader.WaitToReadAsync(api.PayloadLifetime).AsTask()).ConfigureAwait(false))
            {
                pendingRead = null;
                await RamUploadBudget.PendingMessages.WaitAsync(api.PayloadLifetime).ConfigureAwait(false);
                var batch = building = new List<Work>(DiscordLimits.MaxAttachmentsPerSharedMessage);
                var deadline = Stopwatch.GetTimestamp();
                var firstWait = deadline;
                while (batch.Count < DiscordLimits.MaxAttachmentsPerSharedMessage)
                {
                    while (batch.Count < DiscordLimits.MaxAttachmentsPerSharedMessage)
                    {
                        var group = carry;
                        if (group == null && !reader.TryRead(out group)) break;
                        carry = null;
                        if (batch.Count + group.Length > DiscordLimits.MaxAttachmentsPerSharedMessage) { carry = group; break; }
                        foreach (var work in group)
                            if (work.Token.IsCancellationRequested) work.Completion.TrySetCanceled(work.Token);
                            else batch.Add(work);
                    }
                    var remaining = DiscordLimits.SharedBatchWaitMilliseconds - Stopwatch.GetElapsedTime(deadline).TotalMilliseconds;
                    if (carry != null || batch.Count == DiscordLimits.MaxAttachmentsPerSharedMessage) break;
                    if (batch.Count > 0 && batch.Any(work => work.TrackedProduction) && batch.All(work => work.Context.ChunkCount == 1) &&
                        !HasOtherProduction(threadId, batch.Select(work => work.Reservation.VersionId)))
                    {
                        if (reader.TryPeek(out _)) continue;
                        break;
                    }
                    if (remaining <= 0)
                    {
                        if (!HasPendingProduction(threadId)) break;
                        // A future producer may need RAM or file admission retained by this partial batch.
                        if (Stopwatch.GetElapsedTime(firstWait).TotalMilliseconds >= DiscordLimits.SharedProducerStallMilliseconds &&
                            !WaitingOnlyForPublication(threadId)) break;
                        deadline = Stopwatch.GetTimestamp();
                        remaining = DiscordLimits.SharedBatchWaitMilliseconds;
                    }
                    pendingRead ??= reader.WaitToReadAsync(api.PayloadLifetime).AsTask();
                    await Task.WhenAny(pendingRead, Task.Delay((int)Math.Ceiling(remaining), api.PayloadLifetime)).ConfigureAwait(false);
                    api.PayloadLifetime.ThrowIfCancellationRequested();
                    if (pendingRead.IsCompleted)
                    {
                        var available = await pendingRead.ConfigureAwait(false);
                        pendingRead = null;
                        if (!available) break;
                    }
                }
                if (batch.Count == 0) { building = null; RamUploadBudget.PendingMessages.Release(); continue; }
                Diagnostics.DevelopmentDiagnostics.Current?.Record("upload.batch", "work", new
                { attachmentCount = batch.Count, waitMilliseconds = Stopwatch.GetElapsedTime(firstWait).TotalMilliseconds, pendingProduction = HasPendingProduction(threadId) });
                _ = CompleteAsync(threadId, batch);
                building = null;
            }
        }
        catch (Exception error)
        {
            if (building != null)
            {
                foreach (var work in building) work.Completion.TrySetException(error);
                RamUploadBudget.PendingMessages.Release();
            }
            if (carry != null) foreach (var work in carry) work.Completion.TrySetException(error);
            while (reader.TryRead(out var group)) foreach (var work in group) work.Completion.TrySetException(error);
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
            foreach (var work in batch) _uncertain[(threadId, work.Reservation.VersionId, plan.BatchId)] = 0;
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
            // Recovery matches the durable plan against the authenticated payload message and attachments.
            var failures = new Exception?[batch.Count];
            for (var index = 0; index < batch.Count; index++)
            {
                try { if (batch[index].Confirmed != null) await batch[index].Confirmed!(references[index]); }
                catch (Exception error) { failures[index] = error; }
            }
            foreach (var version in batch.Select(work => work.Reservation.VersionId).Distinct())
                if (Enumerable.Range(0, batch.Count).Where(index => batch[index].Reservation.VersionId == version).All(index => failures[index] == null))
                    _uncertain.TryRemove((threadId, version, plan.BatchId), out _);
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
        internal bool TrackedProduction { get; set; }
    }
}
