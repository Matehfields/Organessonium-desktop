using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class SharedUploadContext
{
    internal SharedStorageManager Manager { get; }
    internal SharedStorageReservation Reservation { get; }
    internal Task<SharedUploadCompanionPayload?>? Companion { get; set; }
    internal SharedUploadContext(SharedStorageManager manager, SharedStorageReservation reservation)
    { Manager = manager; Reservation = reservation; }
}

internal sealed record SharedUploadCompanionPayload(EncryptedPayloadContext Context, PreparedUploadPart Part,
    Func<ChunkReference, Task> Confirmed, RamMediaPreview Thumbnail) : IDisposable
{
    public void Dispose() { Part.Dispose(); Thumbnail.Dispose(); }
}

public sealed partial class ForumUploader
{
    private async Task<ForumUploadResult> UploadSharedGeneratedAsync(int count, Func<int, Task<PreparedUploadPart>> prepare,
        string existingBatch, IReadOnlyList<ChunkReference> confirmed, Action<int>? progress,
        Func<ForumUploadedBatch, Task>? checkpoint, CancellationToken token, Func<TimeSpan, Task>? onWait,
        EncryptedPayloadContext encryption, Action<int, long>? wireProgress, AdaptiveUploadGate? bodyGate,
        Action<bool>? activity, SharedUploadContext shared)
    {
        var references = ChunkReference.Validate(confirmed, totalChunks: count, partial: true).ToDictionary(reference => reference.Index);
        var batchId = string.IsNullOrEmpty(existingBatch) ? Guid.NewGuid().ToString("N")[..10] : existingBatch;
        var completed = references.Count;
        progress?.Invoke(completed);
        var missing = Enumerable.Range(0, count).Where(index => !references.ContainsKey(index)).ToArray();
        var pending = missing.Select(async index =>
        {
            using var part = await prepare(index).ConfigureAwait(false);
            part.TransferProgress = bytes => wireProgress?.Invoke(index, bytes);
            async Task ConfirmAsync(ChunkReference reference)
                {
                    if (checkpoint != null) await checkpoint(new ForumUploadedBatch(shared.Reservation.ThreadId, batchId,
                        [new ForumUploadedChunk(shared.Reservation.ThreadId, batchId, index, reference.MessageId,
                            part.Length, part.LogicalSizeBytes, reference.AttachmentId)]));
                }
            var companion = index == missing[0] && encryption.Role == EncryptedPayloadRoles.Original && shared.Companion != null
                ? await shared.Companion.ConfigureAwait(false) : null;
            var context = encryption.WithChunkIndex(index).WithChunkCount(count);
            var reference = companion == null
                ? await shared.Manager.Batcher.SendAsync(shared.Reservation, context, part, ConfirmAsync,
                    token, bodyGate, onWait, activity).ConfigureAwait(false)
                : await shared.Manager.Batcher.SendWithCompanionAsync(shared.Reservation, context, part, ConfirmAsync, companion,
                    token, bodyGate, onWait, activity).ConfigureAwait(false);
            lock (references) references[index] = reference;
            progress?.Invoke(Interlocked.Increment(ref completed));
        }).ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        return ForumUploadResult.FromReferences(shared.Reservation.ThreadId, batchId, references.Values.OrderBy(reference => reference.Index).ToArray());
    }
}
