namespace Organessonium.Services;

internal static class RamUploadBudget
{
    internal const int MaxResidentChunks = 20;
    internal static readonly UploadChunkBufferPool Pool = new();
    internal static readonly SemaphoreSlim Batches = new(16, 16);
    internal static readonly SemaphoreSlim PendingMessages = new(DiscordLimits.MaxPendingUploadMessages, DiscordLimits.MaxPendingUploadMessages);
    internal static readonly SemaphoreSlim Chunks = new(16 * DiscordLimits.AttachmentsPerMessage, 16 * DiscordLimits.AttachmentsPerMessage);
    internal static readonly SemaphoreSlim Preparation = new(2, 2);
    private const long ManagedAdmissionBytes = 448L * 1024 * 1024;
    private static readonly object Sync = new();
    private static long _reserved;
    private static long _externalNativeReserved, _externalManagedReserved;
    private static int _priorityWaiters;

    internal static int PipelineBatchChunks(int partBytes)
    {
        lock (Sync)
        {
            var pool = Pool.AllocatedBytes;
            var nativeHeadroom = AppMemoryBudget.AdmissionBytes - Math.Max(0, AppMemoryBudget.CommittedBytes() - pool);
            var managedHeadroom = ManagedAdmissionBytes - Math.Max(0, GC.GetTotalMemory(false) - pool);
            // Leave room for a sending batch, its successor and the two plaintext scratch buffers.
            var chunks = (Math.Min(nativeHeadroom, managedHeadroom) - 2L * DiscordLimits.AttachmentBytes) /
                (2L * Math.Max(1, partBytes));
            return (int)Math.Clamp(chunks, 1, Math.Min(MaxResidentChunks / 2, DiscordLimits.AttachmentsPerMessage));
        }
    }

    internal static Task<IDisposable> ReserveAsync(int chunks, CancellationToken token) =>
        ReserveBytesAsync(checked((long)chunks * DiscordLimits.AttachmentBytes), token);

    internal static Task<IDisposable> ReserveBytesAsync(long bytes, CancellationToken token) =>
        ReserveCoreAsync(bytes, bytes, false, false, token);

    internal static Task<IDisposable> ReserveExternalBytesAsync(long nativeBytes, long managedBytes, CancellationToken token,
        bool priority = false) => ReserveCoreAsync(nativeBytes, managedBytes, true, priority, token);

    private static async Task<IDisposable> ReserveCoreAsync(long bytes, long managedBytes, bool external, bool priority, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (managedBytes < 0 || managedBytes > bytes) throw new ArgumentOutOfRangeException(nameof(managedBytes));
        if (priority) Interlocked.Increment(ref _priorityWaiters);
        try { return await WaitForReservationAsync(bytes, managedBytes, external, priority, token).ConfigureAwait(false); }
        finally { if (priority) Interlocked.Decrement(ref _priorityWaiters); }
    }

    private static async Task<IDisposable> WaitForReservationAsync(long bytes, long managedBytes, bool external,
        bool priority, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            lock (Sync)
            {
                var pool = Pool.AllocatedBytes;
                var buffers = Math.Max(pool, _reserved + (external ? 0 : bytes) + 2L * DiscordLimits.AttachmentBytes);
                var nativeExternal = _externalNativeReserved + (external ? bytes : 0);
                var managedExternal = _externalManagedReserved + (external ? managedBytes : 0);
                var nativeBaseline = Math.Max(0, AppMemoryBudget.CommittedBytes() - pool);
                var managedBaseline = Math.Max(0, GC.GetTotalMemory(false) - pool);
                // Let existing sends drain before new chunks can displace a waiting thumbnail.
                if ((priority || Volatile.Read(ref _priorityWaiters) == 0) &&
                    nativeBaseline + buffers + nativeExternal <= AppMemoryBudget.AdmissionBytes &&
                    managedBaseline + buffers + managedExternal <= ManagedAdmissionBytes)
                {
                    if (external) { _externalNativeReserved += bytes; _externalManagedReserved += managedBytes; }
                    else _reserved += bytes;
                    return new Reservation(bytes, managedBytes, external);
                }
                Pool.TrimFreeBuffers();
            }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    private sealed class Reservation(long bytes, long managedBytes, bool external) : IDisposable
    {
        private long _bytes = bytes;
        public void Dispose()
        {
            var release = Interlocked.Exchange(ref _bytes, 0);
            lock (Sync)
            {
                if (external)
                {
                    _externalNativeReserved -= release;
                    if (release != 0) _externalManagedReserved -= managedBytes;
                }
                else _reserved -= release;
            }
        }
    }
}
