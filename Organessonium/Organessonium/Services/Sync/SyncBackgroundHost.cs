using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using Organessonium.Models;

namespace Organessonium.Services.Sync;

public sealed class SyncBackgroundHost : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly Func<AppSettings, ICloudSyncRepository> _repositoryFactory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, FolderWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queued = new();
    private readonly ConcurrentDictionary<string, byte> _fullScans = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _dirtyPaths = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTime Next)> _retry = new();
    private readonly Channel<string> _requests = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private ICloudSyncRepository? _cloud;
    private SyncEngine? _engine;
    private Task? _worker;
    private Task? _periodic;
    private string _account = "";
    public SyncStateStore State { get; }
    public SyncEngine? Engine => _engine;
    public event Action? Changed;
    public event Action<SyncTransferProgress>? Progress;
    public event Action<string>? Notification;
    public event Action<string>? TreeChanged;
    public IReadOnlyList<SyncFolderBinding> Folders => _cloud == null ? [] : State.Folders(_account);
    public IReadOnlyList<SyncIssue> Issues => Folders.SelectMany(f => State.Issues(f.SyncFolderId)).ToList();

    public SyncBackgroundHost(SettingsStore settings, string? identityPath = null,
        Func<AppSettings, ICloudSyncRepository>? repositoryFactory = null)
    {
        _settings = settings;
        State = new SyncStateStore(settings, identityPath);
        _repositoryFactory = repositoryFactory ?? (s => new CloudSyncRepository(settings, s));
    }

    public async Task StartAsync()
    {
        await ReloadAsync();
        _worker = Task.Run(WorkerAsync);
        _periodic = Task.Run(PeriodicAsync);
        RequestAll();
    }

    public async Task ReloadAsync()
    {
        _engine?.Pause(true);
        await _lifecycle.WaitAsync(_lifetime.Token);
        try
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
            if (_engine != null) await _engine.DisposeAsync();
            (_cloud as IDisposable)?.Dispose();
            var settings = _settings.Load();
            NetworkConfig.Current.Apply(settings.Proxy);
            State.EnsureSchema();
            _account = SyncStateStore.AccountKey(settings);
            _cloud = settings.IsConfigured ? _repositoryFactory(settings) : null;
            if (_cloud is CloudSyncRepository projected) projected.TreeChanged += folder => TreeChanged?.Invoke(folder);
            _engine = _cloud == null ? null : new SyncEngine(State, _cloud);
            if (_engine != null)
            {
                _engine.Pause(settings.SyncPaused);
                _engine.Changed += () => Changed?.Invoke();
                _engine.Progress += p => Progress?.Invoke(p);
                _engine.Notification += m => Notification?.Invoke(m);
            }
            RefreshWatchers();
        }
        finally { _lifecycle.Release(); }
        Changed?.Invoke();
        RequestAll();
    }

    public async Task<IReadOnlyList<AppEntry>> DiscoverAsync(CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try { return _cloud is CloudSyncRepository repository ? await repository.DiscoverAsync(token) : []; }
        finally { _lifecycle.Release(); }
    }

    public async Task AddAsync(SyncFolderBinding folder, bool link, CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            folder.LocalPath = Path.GetFullPath(folder.LocalPath);
            if (!Directory.Exists(folder.LocalPath) || File.GetAttributes(folder.LocalPath).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("Selecione uma pasta existente que não seja link ou junction.");
            var root = folder.LocalPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var data = _settings.DataDirectory.FullName.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (data.StartsWith(root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(data, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A pasta de dados internos do Organessônium não pode ser sincronizada.");
            if (Folders.Any(f => (f.LocalPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar).StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                root.StartsWith(f.LocalPath.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Esta pasta já pertence a outro vínculo de sincronização local.");
            if (Folders.Any(f => f.SyncFolderId == folder.SyncFolderId))
                throw new InvalidOperationException("Esta pasta lógica já está vinculada a este computador.");
            folder.Name = PathRules.CleanFileName(folder.Name);
            folder.AccountKey = _account;
            if (_cloud is not CloudSyncRepository repository) throw new InvalidOperationException("Configure a conexão antes de vincular uma pasta.");
            await repository.CreateOrLinkAsync(folder, State, link, token);
            State.SaveFolder(folder);
            RefreshWatchers();
        }
        finally { _lifecycle.Release(); }
        Changed?.Invoke();
        Request(folder.SyncFolderId, true);
    }

    public async Task UpdateAsync(SyncFolderBinding folder)
    {
        if (folder.Paused) _engine?.Pause(true, folder.SyncFolderId);
        await _lifecycle.WaitAsync(_lifetime.Token);
        try
        {
            if (folder.Paused && _engine != null) await _engine.WaitForIdleAsync(folder.SyncFolderId);
            var existing = Folders.Single(f => f.SyncFolderId == folder.SyncFolderId);
            // Updating options cannot redirect a persisted binding to another filesystem root.
            folder.LocalPath = existing.LocalPath;
            folder.CloudFolderId = existing.CloudFolderId;
            folder.CloudPath = existing.CloudPath;
            folder.AccountKey = existing.AccountKey;
            State.SaveFolder(folder);
            _engine?.Pause(folder.Paused, folder.SyncFolderId);
            RefreshWatchers();
        }
        finally { _lifecycle.Release(); }
        Changed?.Invoke();
        if (!folder.Paused) Request(folder.SyncFolderId, true);
    }

    public async Task RemoveAsync(string folderId)
    {
        _engine?.Pause(true, folderId);
        await _lifecycle.WaitAsync(_lifetime.Token);
        try
        {
            if (_engine != null) await _engine.WaitForIdleAsync(folderId);
            State.RemoveFolder(folderId); // Unlink is not cloud/local deletion.
            RefreshWatchers();
        }
        finally { _lifecycle.Release(); }
        Changed?.Invoke();
    }

    public void Pause(bool paused)
    {
        var settings = _settings.Load(); settings.SyncPaused = paused; _settings.Save(settings);
        _engine?.Pause(paused);
        if (!paused) RequestAll(true);
        Changed?.Invoke();
    }

    public void Resolve(string folderId, string issueId, string choice, bool remember)
    {
        var folder = Folders.Single(f => f.SyncFolderId == folderId);
        _engine?.Resolve(folder, issueId, choice, remember);
        Request(folderId, true);
    }

    public void RequestAll(bool manual = false)
    {
        foreach (var folder in Folders.Where(f => manual || f.Automatic)) Request(folder.SyncFolderId, manual);
    }

    public void Request(string id, bool manual = false, IReadOnlySet<string>? changedPaths = null)
    {
        if (manual) _retry.TryRemove(id, out _);
        if (manual || changedPaths == null) _fullScans[id] = 1;
        else foreach (var path in changedPaths) _dirtyPaths.GetOrAdd(id, _ => new(StringComparer.OrdinalIgnoreCase))[path] = 1;
        if (_queued.TryAdd(id, manual ? (byte)1 : (byte)0))
        {
            if (!_requests.Writer.TryWrite(id)) _queued.TryRemove(id, out _);
        }
        else if (manual) _queued[id] = 1;
    }

    public async Task SuspendAccountAsync()
    {
        _engine?.Pause(true);
        await _lifecycle.WaitAsync(_lifetime.Token);
        try
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
            if (_engine != null) await _engine.DisposeAsync();
            _engine = null;
            (_cloud as IDisposable)?.Dispose();
            _cloud = null;
            _account = "";
        }
        finally { _lifecycle.Release(); }
        Changed?.Invoke();
    }

    private void RefreshWatchers()
    {
        foreach (var old in _watchers.Values) old.Dispose();
        _watchers.Clear();
        foreach (var folder in Folders.Where(f => f.Automatic && !f.Paused && Directory.Exists(f.LocalPath)))
        {
            try { _watchers[folder.SyncFolderId] = new FolderWatcher(folder.LocalPath, paths => Request(folder.SyncFolderId, changedPaths: paths)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task WorkerAsync()
    {
        try
        {
            await foreach (var id in _requests.Reader.ReadAllAsync(_lifetime.Token))
            {
                _queued.TryRemove(id, out var manual);
                if (manual == 0 && _retry.TryGetValue(id, out var retry) && retry.Next > DateTime.UtcNow) continue;
                await _lifecycle.WaitAsync(_lifetime.Token);
                SyncEngine? engine;
                SyncFolderBinding? folder;
                try
                {
                    folder = Folders.FirstOrDefault(f => f.SyncFolderId == id);
                    engine = _engine;
                }
                finally { _lifecycle.Release(); }
                if (engine != null && folder != null && (manual != 0 || folder.Automatic))
                {
                    var full = _fullScans.TryRemove(id, out _) || _retry.ContainsKey(id);
                    _dirtyPaths.TryRemove(id, out var paths);
                    await engine.SynchronizeAsync(folder, _lifetime.Token, full ? null :
                        (paths?.Keys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase));
                    if (engine.Statuses.FirstOrDefault(s => s.SyncFolderId == id)?.State is "offline" or "pending")
                    {
                        var failures = _retry.TryGetValue(id, out var prior) ? prior.Failures + 1 : 1;
                        _retry[id] = (failures, DateTime.UtcNow.AddSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(failures, 6)))));
                    }
                    else _retry.TryRemove(id, out _);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task PeriodicAsync()
    {
        // Watcher is only an accelerator. Missed events, offline work and remote changes are reconciled here.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        var ticks = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                var full = ++ticks % 5 == 0;
                foreach (var folder in Folders.Where(f => f.Automatic && !f.Paused))
                    Request(folder.SyncFolderId, changedPaths: full ? null : new HashSet<string>());
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _engine?.Pause(true);
        _requests.Writer.TryComplete();
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        if (_worker != null) await _worker;
        if (_periodic != null) await _periodic;
        if (_engine != null) await _engine.DisposeAsync();
        (_cloud as IDisposable)?.Dispose();
        _lifetime.Dispose();
    }
}
