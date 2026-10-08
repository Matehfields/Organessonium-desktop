using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime;
using System.Security.Cryptography;
using Organessonium.Models;

namespace Organessonium.Services;

internal enum UploadBufferStrategy
{
    Auto,
    Memory,
    Disk
}

internal readonly record struct UploadMemorySnapshot(
    long TotalPhysicalBytes,
    long AvailablePhysicalBytes,
    long ProcessWorkingSetBytes)
{
    public static bool TryCapture(out UploadMemorySnapshot snapshot)
    {
        snapshot = default;
        try
        {
            var status = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };
            if (!GlobalMemoryStatusEx(ref status))
            {
                return false;
            }

            using var process = Process.GetCurrentProcess();
            snapshot = new UploadMemorySnapshot(
                checked((long)status.TotalPhysical),
                checked((long)status.AvailablePhysical),
                Math.Max(0, process.WorkingSet64));
            return snapshot.TotalPhysicalBytes > 0 && snapshot.AvailablePhysicalBytes >= 0;
        }
        catch
        {
            snapshot = default;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

internal sealed record UploadMemoryPlan(
    UploadBufferStrategy Strategy,
    bool UseMemory,
    string Reason,
    int RequestedWorkers,
    int MemorySafeWorkers,
    long EstimatedBufferBudgetBytes,
    UploadMemorySnapshot Snapshot);

internal static class UploadBufferMemoryReclaimer
{
    private static readonly object Sync = new();

    public static void Reclaim()
    {
        lock (Sync)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }
}

internal static class UploadMemoryPlanner
{
    internal const long LargeFileThresholdBytes = 256L * 1024 * 1024;
    internal const long ProcessTargetBytes = 800L * 1024 * 1024;
    internal const long ProcessHardLimitBytes = 1024L * 1024 * 1024;
    internal const long EstimatedWorkerBytes = 64L * 1024 * 1024;
    private const long MinimumSystemReserveBytes = 2L * 1024 * 1024 * 1024;

    public static UploadMemoryPlan CreatePlan(
        long fileSizeBytes,
        int requestedWorkers,
        UploadBufferStrategy strategy,
        UploadMemorySnapshot snapshot,
        int activeMemoryWorkers = 0)
    {
        requestedWorkers = Math.Clamp(requestedWorkers, 1, 16);
        activeMemoryWorkers = Math.Max(0, activeMemoryWorkers);
        if (strategy == UploadBufferStrategy.Disk)
        {
            return new UploadMemoryPlan(strategy, false, "Disk strategy forced", requestedWorkers, 0, 0, snapshot);
        }

        if (strategy == UploadBufferStrategy.Auto && fileSizeBytes < LargeFileThresholdBytes)
        {
            return new UploadMemoryPlan(strategy, false, "File is below the memory pipeline threshold", requestedWorkers, 0, 0, snapshot);
        }

        if (snapshot.TotalPhysicalBytes <= 0 || snapshot.AvailablePhysicalBytes < 0)
        {
            return new UploadMemoryPlan(strategy, false, "Physical memory is unavailable", requestedWorkers, 0, 0, snapshot);
        }

        // During a pressure sample, discount the bounded buffers already owned by this pipeline.
        // This avoids treating the app's expected in-flight chunks as external memory pressure.
        var residentEstimate = SaturatingMultiply(activeMemoryWorkers, EstimatedWorkerBytes);
        var adjustedAvailable = SaturatingAdd(snapshot.AvailablePhysicalBytes, residentEstimate);
        var adjustedProcess = Math.Max(0, snapshot.ProcessWorkingSetBytes - residentEstimate);
        var processTargetHeadroom = Math.Max(0, ProcessTargetBytes - adjustedProcess);
        var processHardHeadroom = Math.Max(0, ProcessHardLimitBytes - adjustedProcess);
        var systemReserve = Math.Max(MinimumSystemReserveBytes, snapshot.TotalPhysicalBytes / 10);
        var availableAfterReserve = Math.Max(0, adjustedAvailable - systemReserve);
        var systemBudget = Math.Min(adjustedAvailable / 8, availableAfterReserve);
        var budget = Math.Min(Math.Min(processTargetHeadroom, processHardHeadroom), systemBudget);
        var workers = (int)Math.Min(requestedWorkers, budget / EstimatedWorkerBytes);
        if (workers < 1)
        {
            return new UploadMemoryPlan(
                strategy,
                false,
                "Insufficient available physical memory",
                requestedWorkers,
                0,
                Math.Max(0, budget),
                snapshot);
        }

        return new UploadMemoryPlan(
            strategy,
            true,
            "Memory budget available",
            requestedWorkers,
            workers,
            Math.Min(budget, SaturatingMultiply(workers, EstimatedWorkerBytes)),
            snapshot);
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
        {
            return long.MaxValue;
        }

        return left + right;
    }

    private static long SaturatingMultiply(long left, long right)
    {
        if (left <= 0 || right <= 0)
        {
            return 0;
        }

        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }
}

internal static class UploadConcurrencyPlanner
{
    public static int PartsPerFile(int globalWorkers, int activeFiles, bool forceParallelism = false)
    {
        globalWorkers = Math.Clamp(globalWorkers, 1, 16);
        activeFiles = Math.Max(1, activeFiles);
        return forceParallelism ? globalWorkers : Math.Max(1, globalWorkers / activeFiles);
    }
}

internal sealed class UploadMemoryCoordinator : IDisposable
{
    private readonly int _requestedWorkers;
    private bool _disposed;

    public UploadMemoryCoordinator(int requestedWorkers, UploadBufferStrategy strategy = UploadBufferStrategy.Auto,
        bool forceParallelism = false) => _requestedWorkers = Math.Clamp(requestedWorkers, 1, 16);

    internal SemaphoreSlim BatchPreparationGate => RamUploadBudget.Batches;

    public UploadMemoryPlan SelectPlan(long fileSizeBytes)
    {
        UploadMemorySnapshot.TryCapture(out var snapshot);
        return new UploadMemoryPlan(UploadBufferStrategy.Memory, true, "RAM only, shared bounded buffers",
            _requestedWorkers, RamUploadBudget.MaxResidentChunks,
            (RamUploadBudget.MaxResidentChunks + 2L) * DiscordLimits.AttachmentBytes, snapshot);
    }

    public bool TryRentBuffers(int plaintextLength, out UploadChunkBufferLease? lease)
    {
        lease = null;
        if (_disposed || plaintextLength < 0 || !RamUploadBudget.Chunks.Wait(0)) return false;
        try
        {
            lease = RamUploadBudget.Pool.Rent(plaintextLength,
                checked(plaintextLength + EncryptionService.EnvelopeOverheadBytes), () => RamUploadBudget.Chunks.Release());
            return true;
        }
        catch (OutOfMemoryException) { RamUploadBudget.Chunks.Release(); return false; }
        catch { RamUploadBudget.Chunks.Release(); throw; }
    }

    internal Task<PreparedUploadPart> PreparePartAsync(EncryptionService encryption, FileInfo source,
        long expectedLength, int chunkBytes, EncryptedPayloadContext context, CancellationToken token) => Task.Run(async () =>
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        source.Refresh();
        if (source.Length != expectedLength) throw new IOException("O arquivo mudou enquanto o upload estava em andamento.");
        var offset = checked((long)context.ChunkIndex * chunkBytes);
        var length = checked((int)Math.Min(chunkBytes, Math.Max(0, expectedLength - offset)));
        if (context.ChunkIndex > 0 && offset >= expectedLength) throw new EndOfStreamException("Chunk ausente na origem.");
        await RamUploadBudget.Preparation.WaitAsync(token).ConfigureAwait(false);
        UploadChunkBufferLease? lease = null;
        var slot = false;
        try
        {
            await RamUploadBudget.Chunks.WaitAsync(token).ConfigureAwait(false);
            slot = true;
            lease = RamUploadBudget.Pool.Rent(length, checked(length + EncryptionService.EnvelopeOverheadBytes),
                () => RamUploadBudget.Chunks.Release());
            var encryptedLength = await encryption.EncryptFileRangeIntoAsync(source, offset, length,
                lease.Plaintext, lease.Ciphertext, context, token).ConfigureAwait(false);
            lease.ReleasePlaintext();
            return new PreparedUploadPart(EncryptedPayloadProtocol.FileName(context.Role, context.ChunkIndex, context.ChunkCount),
                encryptedLength, length, lease);
        }
        catch
        {
            if (lease != null) lease.Dispose();
            else if (slot) RamUploadBudget.Chunks.Release();
            throw;
        }
        finally { RamUploadBudget.Preparation.Release(); }
    }, token);

    public void Dispose() => _disposed = true;
}

internal sealed class UploadChunkBufferLease : IDisposable
{
    private readonly UploadChunkBufferPool _pool;
    private Action? _releaseWorker;
    private byte[]? _plaintext;
    private byte[]? _ciphertext;

    internal UploadChunkBufferLease(
        UploadChunkBufferPool pool,
        byte[] plaintext,
        byte[] ciphertext,
        Action releaseWorker)
    {
        _pool = pool;
        _plaintext = plaintext;
        _ciphertext = ciphertext;
        _releaseWorker = releaseWorker;
    }

    public Memory<byte> Plaintext => (_plaintext ?? throw new ObjectDisposedException(nameof(UploadChunkBufferLease))).AsMemory();

    public Memory<byte> Ciphertext => (_ciphertext ?? throw new ObjectDisposedException(nameof(UploadChunkBufferLease))).AsMemory();

    public Stream OpenCiphertextStream(int length)
    {
        var buffer = _ciphertext ?? throw new ObjectDisposedException(nameof(UploadChunkBufferLease));
        if (length < 0 || length > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        return new MemoryStream(buffer, 0, length, writable: false, publiclyVisible: true);
    }

    public void ReleasePlaintext()
    {
        var plaintext = Interlocked.Exchange(ref _plaintext, null);
        if (plaintext != null)
        {
            _pool.ReturnPlaintext(plaintext);
        }
    }

    public void Dispose()
    {
        ReleasePlaintext();
        var ciphertext = Interlocked.Exchange(ref _ciphertext, null);
        if (ciphertext != null)
        {
            _pool.ReturnCiphertext(ciphertext);
        }

        Interlocked.Exchange(ref _releaseWorker, null)?.Invoke();
    }
}

internal sealed class UploadChunkBufferPool : IDisposable
{
    private readonly ConcurrentBag<byte[]> _plaintext = new();
    private readonly ConcurrentBag<byte[]> _ciphertext = new();
    private int _disposed;

    private long _allocatedBytes;
    internal long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);

    public UploadChunkBufferLease Rent(int plaintextLength, int ciphertextLength, Action releaseWorker)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(UploadChunkBufferPool));
        }

        var plaintext = Rent(_plaintext, plaintextLength);
        try
        {
            var ciphertext = Rent(_ciphertext, ciphertextLength);
            return new UploadChunkBufferLease(this, plaintext, ciphertext, releaseWorker);
        }
        catch
        {
            ReturnPlaintext(plaintext);
            throw;
        }
    }

    public void ReturnPlaintext(byte[] buffer)
    {
        Return(_plaintext, buffer);
    }

    public void ReturnCiphertext(byte[] buffer)
    {
        Return(_ciphertext, buffer);
    }

    private byte[] Rent(ConcurrentBag<byte[]> buffers, int minimumLength)
    {
        while (buffers.TryTake(out var buffer))
        {
            if (buffer.Length >= minimumLength)
            {
                return buffer;
            }

            CryptographicOperations.ZeroMemory(buffer);
            Interlocked.Add(ref _allocatedBytes, -buffer.Length);
        }

        var allocated = GC.AllocateUninitializedArray<byte>(minimumLength);
        Interlocked.Add(ref _allocatedBytes, allocated.Length);
        return allocated;
    }

    private void Return(ConcurrentBag<byte[]> buffers, byte[] buffer)
    {
        CryptographicOperations.ZeroMemory(buffer);
        if (Volatile.Read(ref _disposed) == 0)
        {
            buffers.Add(buffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Clear(_plaintext);
        Clear(_ciphertext);
    }

    internal void TrimFreeBuffers()
    {
        Clear(_plaintext);
        Clear(_ciphertext);
    }

    private void Clear(ConcurrentBag<byte[]> buffers)
    {
        while (buffers.TryTake(out var buffer))
        {
            CryptographicOperations.ZeroMemory(buffer);
            Interlocked.Add(ref _allocatedBytes, -buffer.Length);
        }
    }
}

public sealed class AdaptiveUploadGate : IDisposable
{
    private const int MinimumNetworkWorkers = 2;
    private const long SlowAggregateRateBytesPerSecond = 384L * 1024;
    private const long HealthyAggregateRateBytesPerSecond = 1024L * 1024;
    private const int SlowSamplesRequired = 4;
    private const int HealthySamplesRequired = 2;
    private readonly object _sync = new();
    private readonly Queue<Waiter> _waiters = new();
    private int _maximumWorkers;
    private bool _adaptive;
    private int _activeWorkers;
    private int _currentLimit;
    private int _slowSamples;
    private int _healthySamples;
    private bool _disposed;

    public AdaptiveUploadGate(int requestedWorkers, bool adaptive = true)
    {
        _maximumWorkers = Math.Clamp(requestedWorkers, 1, 16);
        _currentLimit = _maximumWorkers;
        _adaptive = adaptive;
    }

    internal int CurrentLimit
    {
        get
        {
            lock (_sync)
            {
                return _currentLimit;
            }
        }
    }

    internal bool Forced { get { lock (_sync) return !_adaptive; } }

    public void Reconfigure(int requestedWorkers, bool adaptive)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _maximumWorkers = Math.Clamp(requestedWorkers, 1, 16);
            _adaptive = adaptive;
            _currentLimit = _maximumWorkers;
            _slowSamples = _healthySamples = 0;
            ReleaseWaitingWorkers();
        }
    }

    public Task<Lease> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_activeWorkers < _currentLimit)
            {
                _activeWorkers++;
                return Task.FromResult(new Lease(this));
            }

            var waiter = new Waiter();
            _waiters.Enqueue(waiter);
            return WaitAsync(waiter, cancellationToken);
        }
    }

    public void ReportCompleted(long bytes, TimeSpan elapsed)
    {
        if (!_adaptive || bytes <= 0 || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var workerRate = (long)(bytes / elapsed.TotalSeconds);
        long aggregateRate;
        int previous = 0;
        int next = 0;
        lock (_sync)
        {
            if (_disposed || !_adaptive)
            {
                return;
            }

            previous = _currentLimit;
            var activeWorkers = Math.Max(1, _activeWorkers);
            aggregateRate = workerRate > long.MaxValue / activeWorkers
                ? long.MaxValue
                : workerRate * activeWorkers;
            if (aggregateRate <= SlowAggregateRateBytesPerSecond && _currentLimit > Math.Min(MinimumNetworkWorkers, _maximumWorkers))
            {
                _slowSamples++;
                _healthySamples = 0;
                if (_slowSamples >= SlowSamplesRequired)
                {
                    _currentLimit = Math.Max(
                        Math.Min(MinimumNetworkWorkers, _maximumWorkers),
                        _currentLimit - Math.Max(1, _currentLimit / 4));
                    _slowSamples = 0;
                }
            }
            else if (aggregateRate >= HealthyAggregateRateBytesPerSecond && _currentLimit < _maximumWorkers)
            {
                _healthySamples++;
                _slowSamples = 0;
                if (_healthySamples >= HealthySamplesRequired)
                {
                    _currentLimit++;
                    _healthySamples = 0;
                }
            }
            else
            {
                _slowSamples = 0;
                _healthySamples = 0;
            }

            next = _currentLimit;
            ReleaseWaitingWorkers();
        }

        if (previous != next)
        {
            AppLogger.Debug(
                "Upload.Concurrency",
                $"Upload workers dynamically adjusted: {previous} -> {next}; " +
                $"estimated aggregate rate: {FormatRate(aggregateRate)}; " +
                $"completed worker rate: {FormatRate(workerRate)}.");
        }
    }

    private async Task<Lease> WaitAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(static state =>
        {
            ((Waiter)state!).Source.TrySetCanceled();
        }, waiter);
        return await waiter.Source.Task.ConfigureAwait(false);
    }

    private void Release(Lease lease)
    {
        lock (_sync)
        {
            if (lease.Released || _disposed)
            {
                return;
            }

            lease.Released = true;
            _activeWorkers = Math.Max(0, _activeWorkers - 1);
            ReleaseWaitingWorkers();
        }
    }

    private void ReleaseWaitingWorkers()
    {
        while (_activeWorkers < _currentLimit && _waiters.Count > 0)
        {
            var waiter = _waiters.Dequeue();
            if (waiter.Source.Task.IsCompleted)
            {
                continue;
            }

            if (waiter.Source.TrySetResult(new Lease(this))) _activeWorkers++;
        }
    }

    private static string FormatRate(long bytesPerSecond)
    {
        return bytesPerSecond >= 1024 * 1024
            ? (bytesPerSecond / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MiB/s"
            : (bytesPerSecond / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KiB/s";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AdaptiveUploadGate));
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            while (_waiters.Count > 0)
            {
                _waiters.Dequeue().Source.TrySetException(new ObjectDisposedException(nameof(AdaptiveUploadGate)));
            }
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly AdaptiveUploadGate _owner;
        internal bool Released;

        internal Lease(AdaptiveUploadGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            _owner.Release(this);
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<Lease> Source { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
