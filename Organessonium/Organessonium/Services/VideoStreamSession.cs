using System.IO;

namespace Organessonium.Services;

/// <summary>One video, no disk cache. At most two CDN transfers, with demanded bytes first.</summary>
public sealed class VideoStreamSession : IVirtualFile, IAsyncDisposable, IDisposable
{
    public const int MaximumPrefetchChunks = 6;
    public const int MaximumResidentChunkBytes = 64 * 1024 * 1024;
    private readonly IVideoChunkProvider _provider;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _cancellation;
    private readonly SemaphoreSlim _downloads = new(2, 2);
    private readonly SemaphoreSlim _reads = new(2, 2);
    private readonly Dictionary<int, byte[]> _cache = [];
    private readonly Dictionary<int, PendingChunk> _pending = [];
    private readonly Dictionary<int, int> _readers = [];
    private readonly HashSet<Task> _background = [];
    private CancellationTokenSource? _prefetch;
    private Task _prefetchTask = Task.CompletedTask;
    private int _current;
    private int _demandCount;
    private bool _playing;
    private bool _seeking;
    private bool _closed;
    private bool _resourcesDisposed;
    private int _activeReads;
    private long _reusedBytes;
    private readonly TaskCompletionSource _readsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Exception? _failure;

    public VideoStreamSession(IVideoChunkProvider provider, int prefetchChunks = 1)
    {
        if (provider.Index.Chunks.Any(chunk => chunk.Length > MaximumResidentChunkBytes))
        {
            provider.Dispose();
            throw new InvalidDataException("Este vídeo contém um chunk maior que o orçamento de RAM do player. Use o download completo.");
        }
        _provider = provider;
        _cancellation = _lifetime.Token;
        PrefetchChunks = Math.Clamp(prefetchChunks, 0, MaximumPrefetchChunks);
    }

    public int PrefetchChunks { get; }
    public CancellationToken Cancellation => _cancellation;
    public long Length => _provider.Index.Length;
    public event Action<Exception>? Failed;
    public string State { get; private set; } = "Opening";
    public long CachedBytes { get { lock (_sync) return _cache.Values.Sum(b => b.LongLength); } }
    public int CachedChunks { get { lock (_sync) return _cache.Count; } }
    internal object Snapshot()
    {
        lock (_sync)
            return new
            {
                state = State, ramBytes = _cache.Values.Sum(chunk => chunk.LongLength), diskBytes = 0,
                reusedBytes = _reusedBytes, cachedChunks = _cache.Count, activeChunks = _pending.Count,
                source = (_provider as DiscordVideoChunkProvider)?.Metrics.Snapshot()
            };
    }
    public Task WarmUpAsync() => GetChunkAsync(0, _lifetime.Token, demand: true);

    public void SetPlaying(bool playing, bool seeking = false)
    {
        lock (_sync)
        {
            if (_closed) return;
            _playing = playing;
            _seeking = seeking;
            State = seeking ? "Seeking" : playing ? "Playing" : "Idle";
            if (seeking || !playing) CancelPrefetchLocked();
            else SchedulePrefetchLocked();
        }
    }

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        if (offset < 0 || offset > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = linked.Token;
        token.ThrowIfCancellationRequested();
        lock (_sync) { ObjectDisposedException.ThrowIf(_closed, this); _activeReads++; }
        var entered = false;
        try
        {
        await _reads.WaitAsync(token).ConfigureAwait(false);
        entered = true;
        var count = (int)Math.Min(destination.Length, Length - offset);
        var copied = 0;
        while (copied < count)
        {
            var chunk = _provider.Index.Find(offset + copied);
            bool cached;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                if (_current != chunk.Index)
                {
                    CancelPrefetchLocked();
                    _current = chunk.Index;
                }
                _readers[chunk.Index] = _readers.GetValueOrDefault(chunk.Index) + 1;
                cached = _cache.ContainsKey(chunk.Index);
                EvictLocked();
            }
            try
            {
                var bytes = await GetChunkAsync(chunk.Index, token, demand: true).ConfigureAwait(false);
                var inside = checked((int)(offset + copied - chunk.Start));
                var take = Math.Min(count - copied, bytes.Length - inside);
                bytes.AsMemory(inside, take).CopyTo(destination[copied..]);
                if (cached) lock (_sync) _reusedBytes += take;
                copied += take;
            }
            finally
            {
                lock (_sync)
                {
                    if (--_readers[chunk.Index] == 0) _readers.Remove(chunk.Index);
                    EvictLocked();
                    SchedulePrefetchLocked();
                }
            }
        }
        return copied;
        }
        finally
        {
            if (entered) _reads.Release();
            lock (_sync) if (--_activeReads == 0 && _closed) _readsDrained.TrySetResult();
        }
    }

    internal bool TryReadCached(long offset, Span<byte> destination)
    {
        lock (_sync)
        {
            if (_closed || offset < 0 || offset > Length - destination.Length) return false;
            var copied = 0;
            while (copied < destination.Length)
            {
                var chunk = _provider.Index.Find(offset + copied);
                if (!_cache.TryGetValue(chunk.Index, out var bytes)) return false;
                var inside = checked((int)(offset + copied - chunk.Start));
                var count = Math.Min(destination.Length - copied, bytes.Length - inside);
                bytes.AsSpan(inside, count).CopyTo(destination[copied..]);
                copied += count;
            }
            _reusedBytes += copied;
            return true;
        }
    }

    private async Task<byte[]> GetChunkAsync(int index, CancellationToken token, bool demand)
    {
        PendingChunk pending;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_cache.TryGetValue(index, out var cached)) return cached;
            if (demand) _demandCount++;
            if (!_pending.TryGetValue(index, out pending!) || pending.Cancellation.IsCancellationRequested)
            {
                pending = new PendingChunk(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token), demand);
                pending.Task = DownloadChunkAsync(index, pending);
                _pending[index] = pending;
                _background.Add(pending.Task);
                _ = pending.Task.ContinueWith(completed => { _ = completed.Exception; lock (_sync) _background.Remove(completed); }, TaskScheduler.Default);
            }
            if (demand) pending.Demanded = true;
        }
        try { return await pending.Task.WaitAsync(token).ConfigureAwait(false); }
        finally { if (demand) lock (_sync) _demandCount--; }
    }

    private async Task<byte[]> DownloadChunkAsync(int index, PendingChunk pending)
    {
        // Defer execution so the task is registered before any completion.
        await Task.Yield();
        var entered = false;
        try
        {
            await _downloads.WaitAsync(pending.Cancellation.Token).ConfigureAwait(false);
            entered = true;
            lock (_sync) { if (!_closed) State = "Buffering"; }
            var data = await _provider.DownloadAsync(index, pending.Cancellation.Token).ConfigureAwait(false);
            if (data.Length != _provider.Index.Chunks[index].Length)
                throw new InvalidDataException("Chunk de vídeo incompleto.");
            lock (_sync)
            {
                if (_closed) throw new OperationCanceledException(_lifetime.Token);
                _cache[index] = data;
                EvictLocked();
                State = _playing ? "Playing" : "Idle";
                _failure = null;
            }
            return data;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (pending.Demanded)
            {
                lock (_sync) { _failure = ex; State = "Error"; }
                Failed?.Invoke(ex);
            }
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (_pending.TryGetValue(index, out var active) && ReferenceEquals(active, pending)) _pending.Remove(index);
            }
            if (entered) _downloads.Release();
            pending.Cancellation.Dispose();
        }
    }

    private void SchedulePrefetchLocked()
    {
        if (_closed || !_playing || _seeking || PrefetchChunks == 0 || _prefetch != null || _failure != null) return;
        _prefetch = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var source = _prefetch;
        var current = _current;
        _prefetchTask = Task.Run(async () =>
        {
            try
            {
                for (var i = current + 1; i <= Math.Min(current + PrefetchChunks, _provider.Index.Chunks.Count - 1); i++)
                {
                    source.Token.ThrowIfCancellationRequested();
                    // Do not start speculative network work while a required range is waiting.
                    while (Volatile.Read(ref _demandCount) > 0) await Task.Delay(25, source.Token).ConfigureAwait(false);
                    await GetChunkAsync(i, source.Token, demand: false).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* Demanded reads retry through the provider and report permanent errors. */ }
            finally { lock (_sync) { if (ReferenceEquals(_prefetch, source)) _prefetch = null; } source.Dispose(); }
        });
        _background.Add(_prefetchTask);
        _ = _prefetchTask.ContinueWith(task => { lock (_sync) _background.Remove(task); }, TaskScheduler.Default);
    }

    private void CancelPrefetchLocked()
    {
        _prefetch?.Cancel();
        _prefetch = null;
        foreach (var pending in _pending.Values.Where(p => !p.Demanded)) pending.Cancellation.Cancel();
    }

    private void EvictLocked()
    {
        foreach (var index in _cache.Keys.ToArray())
            if (!_readers.ContainsKey(index) && !(!_playing && index == 0) && (index < _current - 1 || index > _current + PrefetchChunks))
                _cache.Remove(index);
        // Cap unusually large legacy chunks too. A demanded chunk is never evicted while copied.
        foreach (var index in _cache.Keys.OrderByDescending(i => Math.Abs(i - _current)).ToArray())
            if (_cache.Values.Sum(b => b.LongLength) > 192L * 1024 * 1024 && !_readers.ContainsKey(index) && index != _current)
                _cache.Remove(index);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            State = "Closed";
            _lifetime.Cancel();
            CancelPrefetchLocked();
            _cache.Clear();
            Failed = null;
            if (_activeReads == 0) _readsDrained.TrySetResult();
        }
        _provider.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] tasks;
        lock (_sync) tasks = _pending.Values.Select(p => (Task)p.Task).Concat(_background).ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
        await _readsDrained.Task.ConfigureAwait(false);
        lock (_sync) { if (_resourcesDisposed) return; _resourcesDisposed = true; }
        _downloads.Dispose();
        _reads.Dispose();
        _lifetime.Dispose();
    }

    private sealed class PendingChunk(CancellationTokenSource cancellation, bool demanded)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public bool Demanded { get; set; } = demanded;
        public Task<byte[]> Task { get; set; } = null!;
    }
}
