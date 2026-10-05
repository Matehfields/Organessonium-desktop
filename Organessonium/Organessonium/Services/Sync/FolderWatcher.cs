using System.IO;

namespace Organessonium.Services.Sync;

public sealed class FolderWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private readonly Action<IReadOnlySet<string>?> _changed;
    private readonly object _gate = new();
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private bool _overflow;
    private bool _disposed;
    public FolderWatcher(string root, Action<IReadOnlySet<string>?> changed)
    {
        _changed = changed;
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 32 * 1024
        };
        _watcher.Created += OnChange;
        _watcher.Changed += OnChange;
        _watcher.Deleted += OnChange;
        _watcher.Renamed += OnChange;
        _watcher.Error += (_, _) => Trigger(); // Overflow requests a complete reconciliation, not deletion.
        _watcher.EnableRaisingEvents = true;
    }
    private void OnChange(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Add(e.FullPath);
            if (e is RenamedEventArgs rename) Add(rename.OldFullPath);
            _debounce.Change(2500, Timeout.Infinite);
        }
    }
    private void Add(string path)
    {
        var relative = Path.GetRelativePath(_watcher.Path, path).Replace('\\', '/');
        if (relative.Split('/').Any(SyncPaths.Excluded)) return;
        _paths.Add(relative);
        if (_paths.Count > 4096) { _paths.Clear(); _overflow = true; }
    }
    public void Trigger()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _overflow = true;
            _debounce.Change(2500, Timeout.Infinite);
        }
    }
    private void Flush()
    {
        IReadOnlySet<string>? paths;
        lock (_gate)
        {
            if (_disposed || (!_overflow && _paths.Count == 0)) return;
            paths = _overflow ? null : _paths;
            _paths = new(StringComparer.OrdinalIgnoreCase);
            _overflow = false;
        }
        _changed(paths);
    }
    public void Dispose()
    {
        lock (_gate) { _disposed = true; _debounce.Dispose(); }
        _watcher.Dispose();
    }
}
