using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;

namespace Organessonium.Services.Diagnostics;

internal sealed class DiagnosticSession : IDisposable
{
    [ThreadStatic] private static bool _observing;
    private readonly DiagnosticOptions _options;
    private readonly ConcurrentQueue<object> _queue = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, DiagnosticOperation> _operations = new();
    private readonly Dictionary<string, long> _signals = new(StringComparer.Ordinal);
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _writer;
    private readonly Thread _watchdog;
    private readonly ManualResetEventSlim _monitorStop = new(false);
    private readonly string _marker;
    private readonly long _started = Stopwatch.GetTimestamp();
    private Dispatcher? _dispatcher;
    private long _dispatcherAck = Stopwatch.GetTimestamp();
    private int _dispatcherPending;
    private bool _dispatcherStalled;
    private int _queued;
    private int _pendingFlush;
    private long _dropped;
    private long _nextOperation;
    private long _sequence;
    private int _throws;
    private int _disposed;
    private volatile bool _stop;
    private volatile bool _healthy = true;
    private bool _clean;
    private bool _fatal;
    private readonly ConcurrentDictionary<long, BrowserHealth> _browsers = new();
    internal string Id { get; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-ffff") + "-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8];
    internal DirectoryInfo Directory { get; }
    internal DiagnosticOptions Options => _options;
    internal bool Healthy => _healthy && !_stop;

    internal DiagnosticSession(DirectoryInfo root, DiagnosticOptions options)
    {
        _options = options;
        Directory = new DirectoryInfo(Path.Combine(root.FullName, Id));
        _marker = Path.Combine(Directory.FullName, "active.json");
        Record("session.started", "bootstrap", new { protocol = 1, processId = Environment.ProcessId,
            version = typeof(App).Assembly.GetName().Version?.ToString(),
            buildId = typeof(App).Assembly.ManifestModule.ModuleVersionId, runtime = Environment.Version.ToString() });
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Organessonium diagnostic observer" };
        _watchdog = new Thread(MonitorLoop) { IsBackground = true, Name = "Organessonium responsiveness observer" };
        _writer.Start();
        _watchdog.Start();
    }

    internal void Attach()
    {
        AppDomain.CurrentDomain.FirstChanceException += OnThrow;
        AppDomain.CurrentDomain.UnhandledException += OnFatal;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
    }

    internal void AttachDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Interlocked.Exchange(ref _dispatcherAck, Stopwatch.GetTimestamp());
        dispatcher.UnhandledException += OnDispatcherError;
        Record("dispatcher.attached", "wpf");
    }

    private void OnDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs args) =>
        Exception("exception.dispatcher", "wpf", args.Exception);
    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args) =>
        Exception("exception.unobserved", "task", args.Exception);
    private void OnFatal(object sender, UnhandledExceptionEventArgs args)
    {
        _fatal |= args.IsTerminating;
        if (args.ExceptionObject is Exception exception) Exception("exception.unhandled", "runtime", exception);
        Flush(TimeSpan.FromMilliseconds(500));
    }

    private void OnThrow(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (_observing || _stop) return;
        // First-chance exceptions include normal, caught exceptions. Sample rather than flooding storage.
        if (Interlocked.Increment(ref _throws) <= 16) Exception("exception.observed", "runtime", args.Exception);
    }

    internal bool Exception(string kind, string area, Exception exception)
    {
        if (_observing || _stop) return false;
        _observing = true;
        try
        {
            var chain = new List<object>();
            for (var current = exception; current != null && chain.Count < 4; current = current.InnerException)
            {
                var frames = new StackTrace(current, true).GetFrames().Take(16).Select(frame =>
                {
                    var method = frame.GetMethod();
                    return new { method = method?.DeclaringType?.FullName + "." + method?.Name,
                        line = frame.GetFileLineNumber(), ilOffset = frame.GetILOffset() };
                }).ToArray();
                chain.Add(new { type = current.GetType().FullName, hresult = current.HResult, frames });
            }
            return Record(kind, area, new { exceptions = chain }, critical: true); // Never Exception.Message/ToString/Data.
        }
        catch { return false; }
        finally { _observing = false; }
    }

    internal void Signal(string kind, string area)
    {
        if (!Healthy) return;
        var key = Safe(kind) + "." + DiagnosticLabels.Area(area);
        lock (_signals)
        {
            var now = Environment.TickCount64;
            if (_signals.TryGetValue(key, out var last) && now - last < _options.SampleMilliseconds) return;
            if (_signals.Count >= 128 && !_signals.ContainsKey(key)) return;
            _signals[key] = now;
        }
        Record(kind, area);
    }

    internal DiagnosticOperation Begin(string kind, string label, DiagnosticOperation? parent, Action<DiagnosticOperation?> restore)
    {
        var operation = new DiagnosticOperation(this, Interlocked.Increment(ref _nextOperation), DiagnosticLabels.Area(label), parent, restore);
        if (_operations.Count < 128) _operations.TryAdd(operation.Id, operation);
        Record("operation.begin", kind, new { id = operation.Id, parentId = parent?.Id ?? 0, label = operation.Label });
        return operation;
    }

    internal void End(DiagnosticOperation operation, int? statusCode)
    {
        _operations.TryRemove(operation.Id, out _);
        Record("operation.end", operation.Label, new { id = operation.Id,
            elapsedMilliseconds = Stopwatch.GetElapsedTime(operation.Started).TotalMilliseconds, statusCode });
    }

    internal bool Record(string kind, string area, object? data = null, bool critical = false)
    {
        lock (_gate)
        {
            if (_stop || !_healthy) return false;
            var limit = critical ? _options.QueueCapacity : Math.Max(1, _options.QueueCapacity - 64);
            if (_queued >= limit)
            {
                Interlocked.Increment(ref _dropped);
                if (!critical) return false;
                while (_queue.TryDequeue(out var oldest))
                {
                    Interlocked.Decrement(ref _queued);
                    if (oldest is not TaskCompletionSource<bool>) break;
                    _queue.Enqueue(oldest); Interlocked.Increment(ref _queued);
                }
            }
            Interlocked.Increment(ref _queued);
            _queue.Enqueue(new { utc = DateTimeOffset.UtcNow, elapsedMilliseconds = Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
                sequence = Interlocked.Increment(ref _sequence), kind = Safe(kind), area = DiagnosticLabels.Area(area),
                threadId = Environment.CurrentManagedThreadId, operationId = DevelopmentDiagnostics.OperationId, data });
            _wake.Set();
            return true;
        }
    }

    internal long AddBrowser()
    {
        var id = Interlocked.Increment(ref _nextOperation);
        _browsers[id] = new BrowserHealth();
        Record("webview.attached", "webview", new { id });
        return id;
    }
    internal void RemoveBrowser(long id) { _browsers.TryRemove(id, out _); Record("webview.detached", "webview", new { id }); }
    internal void BrowserHeartbeat(long id, bool visible)
    {
        if (!_browsers.TryGetValue(id, out var browser)) return;
        browser.Visible = visible;
        browser.AwaitingHeartbeat = false;
        Interlocked.Exchange(ref browser.Ack, Stopwatch.GetTimestamp());
    }
    internal void BrowserNavigation(long id, bool started)
    {
        if (!_browsers.TryGetValue(id, out var browser)) return;
        browser.AwaitingHeartbeat = started;
        if (started) Interlocked.Exchange(ref browser.Ack, Stopwatch.GetTimestamp());
        else browser.Visible = false;
    }

    private void Sample()
    {
        using var process = Process.GetCurrentProcess();
        Record("runtime.sample", "runtime", new { privateBytes = process.PrivateMemorySize64,
            workingSetBytes = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false),
            committedGcBytes = GC.GetGCMemoryInfo().TotalCommittedBytes, cpuMilliseconds = process.TotalProcessorTime.TotalMilliseconds,
            jobCommittedBytes = AppMemoryBudget.CommittedBytes(),
            threads = process.Threads.Count, threadPoolThreads = ThreadPool.ThreadCount,
            threadPoolPending = ThreadPool.PendingWorkItemCount, gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2),
            observedExceptions = Interlocked.Exchange(ref _throws, 0), droppedEvents = Interlocked.Exchange(ref _dropped, 0),
            activeOperations = _operations.Values.Take(16).Select(op => new { id = op.Id, label = op.Label,
                elapsedMilliseconds = Stopwatch.GetElapsedTime(op.Started).TotalMilliseconds }).ToArray() });
        var dispatcher = _dispatcher;
        if (dispatcher != null && !dispatcher.HasShutdownStarted)
        {
            var delay = Stopwatch.GetElapsedTime(Interlocked.Read(ref _dispatcherAck)).TotalMilliseconds;
            var stalled = delay >= _options.UiStallMilliseconds;
            if (stalled != _dispatcherStalled)
            {
                Record(stalled ? "ui.stalled" : "ui.recovered", "wpf", new { delayMilliseconds = delay });
                _dispatcherStalled = stalled;
            }
            if (Interlocked.CompareExchange(ref _dispatcherPending, 1, 0) == 0)
                dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                { Interlocked.Exchange(ref _dispatcherAck, Stopwatch.GetTimestamp()); Interlocked.Exchange(ref _dispatcherPending, 0); }));
        }
        foreach (var (id, browser) in _browsers)
        {
            var delay = Stopwatch.GetElapsedTime(Interlocked.Read(ref browser.Ack)).TotalMilliseconds;
            var stalled = (browser.Visible || browser.AwaitingHeartbeat) && delay >= _options.BrowserStallMilliseconds;
            if (stalled == browser.Stalled) continue;
            Record(stalled ? browser.AwaitingHeartbeat ? "webview.heartbeatMissing" : "ui.stalled" : "ui.recovered",
                "javascript", new { id, delayMilliseconds = delay });
            browser.Stalled = stalled;
        }
    }

    private void MonitorLoop()
    {
        _observing = true;
        try
        {
            while (!_monitorStop.Wait(_options.SampleMilliseconds))
            {
                if (!_healthy) break;
                try { Sample(); }
                catch (Exception exception) { Record("observer.sampleFailed", "runtime", new { type = exception.GetType().Name }); }
            }
        }
        finally { _observing = false; }
    }

    private void WriteLoop()
    {
        _observing = true; // The observer's I/O errors must not recursively log themselves.
        FileStream? stream = null;
        try
        {
            Directory.Create();
            RecoverPrevious(Directory.Parent!);
            using (var process = Process.GetCurrentProcess())
                File.WriteAllText(_marker, JsonSerializer.Serialize(new { processId = Environment.ProcessId,
                    startTicks = process.StartTime.ToUniversalTime().Ticks }), new UTF8Encoding(false));
            var part = 0;
            stream = OpenPart(part);
            var lastSample = Environment.TickCount64;
            while (!_stop || !_queue.IsEmpty)
            {
                for (var count = 0; count < 128 && _queue.TryDequeue(out var item); count++)
                {
                    Interlocked.Decrement(ref _queued);
                    if (item is TaskCompletionSource<bool> barrier)
                    { stream.Flush(true); barrier.TrySetResult(true); Interlocked.Decrement(ref _pendingFlush); continue; }
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(item);
                    if (stream.Length > 0 && stream.Length + bytes.Length + 1 > _options.FileBytes)
                    { stream.Dispose(); stream = OpenPart(++part); }
                    stream.Write(bytes); stream.WriteByte((byte)'\n');
                    if (part >= _options.FilesPerSession)
                    {
                        var stale = Path.Combine(Directory.FullName, $"trace-{part - _options.FilesPerSession:D4}.jsonl");
                        if (File.Exists(stale)) File.Delete(stale);
                    }
                }
                if (!_stop && Environment.TickCount64 - lastSample >= _options.SampleMilliseconds)
                { stream.Flush(true); lastSample = Environment.TickCount64; }
                if (!_stop) _wake.WaitOne(50);
            }
            stream.Flush(true);
        }
        catch
        {
            _healthy = false;
            while (_queue.TryDequeue(out var item))
                if (item is TaskCompletionSource<bool> barrier) barrier.TrySetResult(false);
        }
        finally { stream?.Dispose(); _observing = false; }
    }

    private FileStream OpenPart(int part) => new(Path.Combine(Directory.FullName, $"trace-{part:D4}.jsonl"),
        FileMode.CreateNew, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.SequentialScan);

    internal bool Flush(TimeSpan timeout)
    {
        if (!Healthy || Thread.CurrentThread == _writer) return false;
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_stop || !_healthy || _pendingFlush >= 8) return false;
            Interlocked.Increment(ref _pendingFlush);
            Interlocked.Increment(ref _queued); _queue.Enqueue(barrier); _wake.Set();
        }
        return barrier.Task.Wait(timeout) && barrier.Task.Result;
    }

    internal void Complete(int exitCode)
    { Flush(TimeSpan.FromMilliseconds(500)); Record("session.ended", "bootstrap", new { exitCode }, critical: true); _clean = true; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        AppDomain.CurrentDomain.FirstChanceException -= OnThrow;
        AppDomain.CurrentDomain.UnhandledException -= OnFatal;
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
        if (_dispatcher != null) _dispatcher.UnhandledException -= OnDispatcherError;
        lock (_gate) { _stop = true; _wake.Set(); _monitorStop.Set(); }
        if (_watchdog.Join(500)) _monitorStop.Dispose();
        if (_writer.Join(1000))
        {
            _wake.Dispose();
            if (_clean && !_fatal && _healthy) { try { File.Delete(_marker); } catch { } }
        }
        DevelopmentDiagnostics.Release(this);
    }

    private void RecoverPrevious(DirectoryInfo root)
    {
        var inactive = new List<DirectoryInfo>();
        foreach (var directory in root.EnumerateDirectories().Where(dir => dir.Name.Length > 24 &&
            dir.Name.All(c => char.IsAsciiDigit(c) || c is '-' || c is >= 'a' and <= 'f')).OrderByDescending(dir => dir.Name))
        {
            if (directory.FullName == Directory.FullName) continue;
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var marker = Path.Combine(directory.FullName, "active.json");
            var alive = false;
            if (File.Exists(marker))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(marker));
                    using var process = Process.GetProcessById(json.RootElement.GetProperty("processId").GetInt32());
                    alive = process.StartTime.ToUniversalTime().Ticks == json.RootElement.GetProperty("startTicks").GetInt64();
                }
                catch { }
                if (!alive) Record("previous.uncleanExit", "bootstrap", new { sessionId = directory.Name });
            }
            if (!alive) inactive.Add(directory);
        }
        // Only generated diagnostic sessions are eligible, never existing .log files or other app data.
        foreach (var directory in inactive.Skip(_options.RetainedSessions - 1))
        {
            try
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var file in directory.EnumerateFiles().Where(file => file.Name == "active.json" ||
                    (file.Name.StartsWith("trace-", StringComparison.Ordinal) && file.Extension == ".jsonl" &&
                     Path.GetFileNameWithoutExtension(file.Name)[6..].All(char.IsAsciiDigit)))) file.Delete();
                directory.Delete(); // Non-recursive: unexpected content is preserved.
            }
            catch { }
        }
    }

    private static string Safe(string value) => value.Length is > 0 and <= 160 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+') ? value : "redacted";
    private sealed class BrowserHealth
    { internal long Ack = Stopwatch.GetTimestamp(); internal volatile bool Visible; internal volatile bool AwaitingHeartbeat; internal bool Stalled; }
}
