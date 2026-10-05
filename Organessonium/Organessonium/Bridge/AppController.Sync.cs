using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.Sync;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private SyncBackgroundHost? _syncHost;
    private Action? _syncChanged;
    private Action<SyncTransferProgress>? _syncProgress;
    private Action<string>? _syncTreeChanged;
    private readonly ConcurrentDictionary<string, byte> _syncDirtyFolders = new();
    private IReadOnlyList<AppEntry> _syncSources = [];
    private int _syncPublishPending;

    internal void AttachSyncHost(SyncBackgroundHost host)
    {
        DetachSyncHost();
        _syncHost = host;
        _syncChanged = () =>
        {
            if (Interlocked.Exchange(ref _syncPublishPending, 1) != 0) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(150);
                Interlocked.Exchange(ref _syncPublishPending, 0);
                if (!_loggingOut && ReferenceEquals(_syncHost, host))
                {
                    await PublishSyncTreeAsync();
                    await PublishSyncStateAsync();
                }
            });
        };
        _syncProgress = p => { if (!_loggingOut) _ = _bridge.CallAsync("_onSyncTransfer", JsonSerializer.SerializeToElement(p, SyncJson.Options)); };
        _syncTreeChanged = folder => { _syncDirtyFolders[folder] = 1; _syncChanged(); };
        host.Changed += _syncChanged;
        host.Progress += _syncProgress;
        host.TreeChanged += _syncTreeChanged;
        _syncChanged();
    }

    private void DetachSyncHost()
    {
        if (_syncHost != null && _syncChanged != null) _syncHost.Changed -= _syncChanged;
        if (_syncHost != null && _syncProgress != null) _syncHost.Progress -= _syncProgress;
        if (_syncHost != null && _syncTreeChanged != null) _syncHost.TreeChanged -= _syncTreeChanged;
        _syncDirtyFolders.Clear();
        _syncHost = null;
    }

    private async Task PublishSyncTreeAsync()
    {
        var ids = _syncDirtyFolders.Keys.Where(id => _syncDirtyFolders.TryRemove(id, out _)).ToList();
        if (ids.Count == 0 || !HasSession) return;
        var folders = ids.Select(id => new FolderLoadResult(id, _store.LoadCachedEntriesForFolder(id))).ToList();
        await _owner.Dispatcher.InvokeAsync(async () =>
        {
            if (_loggingOut) return;
            ReplaceLoadedFolders(folders);
            await _bridge.CallAsync("_onAppsLoaded", _loadedApps);
        }).Task.Unwrap();
    }

    private Task PublishSyncStateAsync()
    {
        if (_syncHost == null) return Task.CompletedTask;
        var host = _syncHost;
        var folders = host.Folders;
        var statuses = host.Engine?.Statuses ?? [];
        return _bridge.CallAsync("_onSyncState", JsonSerializer.SerializeToElement(new
        {
            deviceId = host.State.DeviceId, deviceName = host.State.DeviceName, folders,
            statuses, issues = host.Issues, sources = _syncSources, paused = host.Engine?.Paused ?? false,
            startWithWindows = _store.Load().StartWithWindows, closeToTray = _store.Load().CloseToTray
        }, SyncJson.Options));
    }

    private async Task SyncDispatchAsync(string method, JsonElement[] args)
    {
        var host = _syncHost ?? throw new InvalidOperationException("O serviço de sincronização ainda está iniciando.");
        switch (method)
        {
            case "loadSyncState": await PublishSyncStateAsync(); break;
            case "discoverSyncFolders":
                _syncSources = await host.DiscoverAsync();
                await PublishSyncStateAsync();
                break;
            case "openSyncFolder":
                _syncSources = await host.DiscoverAsync();
                var canonicalFolder = _syncSources.FirstOrDefault(s => s.FolderId == ArgString(args, 0))
                    ?? throw new InvalidOperationException("O índice da pasta sincronizada não foi encontrado.");
                await _bridge.CallAsync("_onSyncNavigate", PathRules.ChildPath(canonicalFolder), canonicalFolder.FolderId);
                break;
            case "chooseSyncFolder":
                var dialog = new OpenFolderDialog { Title = "Selecionar pasta sincronizada" };
                if (dialog.ShowDialog(_owner) == true)
                    await _bridge.CallAsync("_onSyncFolderPicked", dialog.FolderName);
                break;
            case "addSyncFolder":
                var binding = JsonSerializer.Deserialize<SyncFolderBinding>(ArgString(args, 0), SyncJson.Options)
                    ?? throw new InvalidOperationException("Configuração da pasta inválida.");
                var link = ArgBool(args, 1, false);
                if (link)
                {
                    _syncSources = await host.DiscoverAsync();
                    var source = _syncSources.FirstOrDefault(s => s.Sync?.SyncFolderId == binding.SyncFolderId)
                        ?? throw new InvalidOperationException("A pasta remota não foi encontrada.");
                    binding.CloudFolderId = source.Sync?.CanonicalFolderId ?? source.FolderId;
                    var canonical = _syncSources.FirstOrDefault(s => s.FolderId == binding.CloudFolderId) ?? source;
                    binding.CloudPath = PathRules.ChildPath(canonical);
                }
                await host.AddAsync(binding, link);
                if (binding.StartWithWindows)
                {
                    await StartupManager.SetEnabledAsync(true);
                    var settings = _store.Load(); settings.StartWithWindows = true; _store.Save(settings);
                }
                await Registry().RefreshMappingsAsync();
                await LoadSynchronizedDevicesAsync(forceRefresh: true);
                await _bridge.CallAsync("_onSyncFolderSaved");
                await PublishSyncStateAsync();
                break;
            case "updateSyncFolder":
                var updated = JsonSerializer.Deserialize<SyncFolderBinding>(ArgString(args, 0), SyncJson.Options)
                    ?? throw new InvalidOperationException("Configuração inválida.");
                await host.UpdateAsync(updated);
                await PublishSyncStateAsync();
                break;
            case "removeSyncFolder": await host.RemoveAsync(ArgString(args, 0)); await PublishSyncStateAsync(); break;
            case "syncNow":
                var id = ArgString(args, 0);
                if (id.Length == 0) host.RequestAll(true); else host.Request(id, true);
                break;
            case "pauseSync": host.Pause(ArgBool(args, 0, true)); await PublishSyncStateAsync(); break;
            case "resolveSyncConflict": host.Resolve(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2), ArgBool(args, 3, false)); break;
            case "setSyncPreferences":
                var preferences = _store.Load();
                var startup = ArgBool(args, 0, false);
                await StartupManager.SetEnabledAsync(startup);
                preferences.StartWithWindows = startup;
                preferences.CloseToTray = ArgBool(args, 1, false);
                _store.Save(preferences);
                await PublishSyncStateAsync();
                break;
        }
    }
}
