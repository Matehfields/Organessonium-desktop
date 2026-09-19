using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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

internal static class UploadMemoryPlanner
{
    internal const long LargeFileThresholdBytes = 256L * 1024 * 1024;
    internal const long ProcessTargetBytes = 800L * 1024 * 1024;
    internal const long ProcessHardLimitBytes = 1024L * 1024 * 1024;
    internal const long EstimatedWorkerBytes = 32L * 1024 * 1024;
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

internal sealed class UploadMemoryCoordinator : IDisposable
{
    private static readonly TimeSpan PressureSampleInterval = TimeSpan.FromSeconds(4);
    private readonly object _sync = new();
    private readonly int _requestedWorkers;
    private readonly UploadBufferStrategy _strategy;
    private readonly UploadChunkBufferPool _pool = new();
    private int _activeWorkers;
    private int _allowedWorkers;
    private int _healthySamples;
    private long _lastSampleTimestamp;
    private bool _disposed;

    public UploadMemoryCoordinator(int requestedWorkers, UploadBufferStrategy strategy = UploadBufferStrategy.Auto)
    {
        _requestedWorkers = Math.Clamp(requestedWorkers, 1, 16);
        _strategy = strategy;
    }

    public UploadMemoryPlan SelectPlan(long fileSizeBytes)
    {
        if (!UploadMemorySnapshot.TryCapture(out var snapshot))
        {
            var unavailable = new UploadMemoryPlan(
                _strategy,
                false,
                "Physical memory status could not be read",
                _requestedWorkers,
                0,
                0,
                default);
            LogPlan(fileSizeBytes, unavailable);
            return unavailable;
        }

        var plan = UploadMemoryPlanner.CreatePlan(
            fileSizeBytes,
            _requestedWorkers,
            _strategy,
            snapshot,
            Volatile.Read(ref _activeWorkers));
        if (plan.UseMemory)
        {
            lock (_sync)
            {
                if (_allowedWorkers == 0 || plan.MemorySafeWorkers < _allowedWorkers)
                {
                    _allowedWorkers = plan.MemorySafeWorkers;
                    _healthySamples = 0;
                }
            }
        }

        LogPlan(fileSizeBytes, plan);
        return plan;
    }

    public bool TryRentBuffers(int plaintextLength, out UploadChunkBufferLease? lease)
    {
        lease = null;
        if (plaintextLength < 0 || Volatile.Read(ref _disposed))
        {
            return false;
        }

        RefreshCapacity();
        lock (_sync)
        {
            if (_disposed || _allowedWorkers < 1 || _activeWorkers >= _allowedWorkers)
            {
                return false;
            }

            _activeWorkers++;
        }

        try
        {
            lease = _pool.Rent(
                plaintextLength,
                checked(plaintextLength + EncryptionService.EnvelopeOverheadBytes),
                ReleaseWorker);
            return true;
        }
        catch (OutOfMemoryException)
        {
            lock (_sync)
            {
                _allowedWorkers = Math.Min(_allowedWorkers, Math.Max(0, _activeWorkers - 1));
                _healthySamples = 0;
            }
            ReleaseWorker();
            AppLogger.Debug("Upload.MemoryPipeline", "Disk fallback: buffer allocation failed.");
            return false;
        }
        catch
        {
            ReleaseWorker();
            throw;
        }
    }

    private void RefreshCapacity()
    {
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(ref _lastSampleTimestamp);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < PressureSampleInterval)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastSampleTimestamp, now, last) != last)
        {
            return;
        }

        if (!UploadMemorySnapshot.TryCapture(out var snapshot))
        {
            return;
        }

        var plan = UploadMemoryPlanner.CreatePlan(
            UploadMemoryPlanner.LargeFileThresholdBytes,
            _requestedWorkers,
            UploadBufferStrategy.Memory,
            snapshot,
            Volatile.Read(ref _activeWorkers));
        var next = plan.MemorySafeWorkers;
        int previous;
        lock (_sync)
        {
            previous = _allowedWorkers;
            if (previous == 0 || next < previous)
            {
                _allowedWorkers = next;
                _healthySamples = 0;
            }
            else if (next > previous)
            {
                _healthySamples++;
                if (_healthySamples >= 2)
                {
                    _allowedWorkers = Math.Min(next, previous + 1);
                    _healthySamples = 0;
                }
            }
            else
            {
                _healthySamples = 0;
            }
        }

        if (previous != _allowedWorkers)
        {
            AppLogger.Debug(
                "Upload.MemoryPipeline",
                $"Memory-safe workers changed: {previous} -> {_allowedWorkers}. Reason: {plan.Reason}.");
        }
    }

    private void ReleaseWorker()
    {
        Interlocked.Decrement(ref _activeWorkers);
    }

    private static void LogPlan(long fileSizeBytes, UploadMemoryPlan plan)
    {
        var snapshot = plan.Snapshot;
        AppLogger.Debug(
            "Upload.MemoryPipeline",
            $"Strategy: {(plan.UseMemory ? "Memory" : "Disk fallback")}; " +
            $"reason: {plan.Reason}; file: {FormatBytes(fileSizeBytes)}; " +
            $"system RAM: {FormatBytes(snapshot.TotalPhysicalBytes)}; " +
            $"available RAM: {FormatBytes(snapshot.AvailablePhysicalBytes)}; " +
            $"process working set: {FormatBytes(snapshot.ProcessWorkingSetBytes)}; " +
            $"chunk: {FormatBytes(EncryptionService.MaximumPlaintextPayloadBytes)}; " +
            $"requested workers: {plan.RequestedWorkers}; memory-safe workers: {plan.MemorySafeWorkers}; " +
            $"estimated upload buffer budget: {FormatBytes(plan.EstimatedBufferBudgetBytes)}.");
    }

    private static string FormatBytes(long value)
    {
        return (Math.Max(0, value) / 1024d / 1024d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MiB";
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
        _pool.Dispose();
    }
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

    private static byte[] Rent(ConcurrentBag<byte[]> buffers, int minimumLength)
    {
        while (buffers.TryTake(out var buffer))
        {
            if (buffer.Length >= minimumLength)
            {
                return buffer;
            }

            CryptographicOperations.ZeroMemory(buffer);
        }

        return GC.AllocateUninitializedArray<byte>(minimumLength);
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

    private static void Clear(ConcurrentBag<byte[]> buffers)
    {
        while (buffers.TryTake(out var buffer))
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}

public sealed class AdaptiveUploadGate : IDisposable
{
    private const int MinimumNetworkWorkers = 2;
    private const long SlowWorkerRateBytesPerSecond = 384L * 1024;
    private const long HealthyWorkerRateBytesPerSecond = 1024L * 1024;
    private readonly object _sync = new();
    private readonly Queue<Waiter> _waiters = new();
    private readonly int _maximumWorkers;
    private int _activeWorkers;
    private int _currentLimit;
    private int _memoryLimit;
    private int _slowSamples;
    private int _healthySamples;
    private bool _disposed;

    public AdaptiveUploadGate(int requestedWorkers)
    {
        _maximumWorkers = Math.Clamp(requestedWorkers, 1, 16);
        _currentLimit = _maximumWorkers;
        _memoryLimit = _maximumWorkers;
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
        if (bytes <= 0 || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var rate = (long)(bytes / elapsed.TotalSeconds);
        int previous = 0;
        int next = 0;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            previous = _currentLimit;
            if (rate <= SlowWorkerRateBytesPerSecond && _currentLimit > Math.Min(MinimumNetworkWorkers, _maximumWorkers))
            {
                _slowSamples++;
                _healthySamples = 0;
                if (_slowSamples >= 2)
                {
                    _currentLimit = Math.Max(
                        Math.Min(MinimumNetworkWorkers, _maximumWorkers),
                        _currentLimit - Math.Max(1, _currentLimit / 4));
                    _slowSamples = 0;
                }
            }
            else if (rate >= HealthyWorkerRateBytesPerSecond && _currentLimit < Math.Min(_maximumWorkers, _memoryLimit))
            {
                _healthySamples++;
                _slowSamples = 0;
                if (_healthySamples >= 4)
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
                $"Upload workers dynamically adjusted: {previous} -> {next}; observed worker rate: {FormatRate(rate)}.");
        }
    }

    public void LimitForMemory(int memorySafeWorkers)
    {
        memorySafeWorkers = Math.Clamp(memorySafeWorkers, 1, _maximumWorkers);
        int previous;
        int next;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            previous = _currentLimit;
            _memoryLimit = Math.Min(_memoryLimit, memorySafeWorkers);
            _currentLimit = Math.Min(_currentLimit, _memoryLimit);
            next = _currentLimit;
            ReleaseWaitingWorkers();
        }

        if (previous != next)
        {
            AppLogger.Debug(
                "Upload.Concurrency",
                $"Upload workers capped by memory plan: {previous} -> {next}.");
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

            _activeWorkers++;
            waiter.Source.TrySetResult(new Lease(this));
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
