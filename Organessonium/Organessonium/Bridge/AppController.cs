using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;
using Organessonium.Models;
using Organessonium.Services;
using System.Xml;

namespace Organessonium.Bridge;

public sealed partial class AppController : IAsyncDisposable
{
    private static readonly JsonSerializerOptions ProxyJsonOptions = CreateProxyJsonOptions();

    private static JsonSerializerOptions CreateProxyJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonOptions.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
    private const long ArchivePreviewLimit = 1024L * 1024 * 1024;
    private const int DocumentPageUnitLimit = 36;
    private const int SpreadsheetMaxRows = 80;
    private const int SpreadsheetMaxColumns = 32;
    private const int SpreadsheetMaxSheets = 20;
    private const int PresentationMaxSlides = 80;
    private const long MarkdownInlineImageLimit = EncryptionService.MaximumPlaintextPayloadBytes;
    private sealed record FolderFileStatistic(string FolderId, long FileCount);
    private sealed record FolderLoadResult(string FolderId, IReadOnlyList<AppEntry> Entries);
    private sealed class TransferActivityClock
    {
        private readonly object _sync = new();
        private int _activeRequests;
        private long _activeStarted;
        private double _elapsedSeconds;

        public void SetActive(bool active)
        {
            lock (_sync)
            {
                if (active)
                {
                    if (_activeRequests++ == 0)
                    {
                        _activeStarted = Stopwatch.GetTimestamp();
                    }

                    return;
                }

                if (_activeRequests <= 0)
                {
                    return;
                }

                if (--_activeRequests == 0)
                {
                    _elapsedSeconds += Stopwatch.GetElapsedTime(_activeStarted).TotalSeconds;
                }
            }
        }

        public double ElapsedSeconds
        {
            get
            {
                lock (_sync)
                {
                    var elapsed = _elapsedSeconds;
                    if (_activeRequests > 0)
                    {
                        elapsed += Stopwatch.GetElapsedTime(_activeStarted).TotalSeconds;
                    }

                    return Math.Max(0, elapsed);
                }
            }
        }

        public bool HasActiveRequests
        {
            get
            {
                lock (_sync)
                {
                    return _activeRequests > 0;
                }
            }
        }
    }
    private readonly Window _owner;
    private readonly SettingsStore _store;
    private WebMessageBridge _bridge;
    private readonly Func<DiscordSession, Task> _onConnected;
    private readonly Action<string> _onThemeChanged;
    private readonly ConcurrentDictionary<string, DirectoryInfo> _previewDirs = new();
    private readonly ConcurrentDictionary<string, string> _previewUrls = new();
    private readonly ConcurrentDictionary<string, byte> _thumbnailLoads = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _thumbnailRequests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _previewRequests = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _accountWork = new();
    private readonly object _thumbnailRequestSync = new();
    private readonly SemaphoreSlim _thumbnailDeliveryGate = new(1, 1);
    private CancellationTokenSource? _thumbnailRequestCts;
    private string _thumbnailRequestKey = "";
    private readonly ConcurrentDictionary<string, TaskCompletionSource<UploadConflictResponse>> _uploadConflictResponses = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transferOperations = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _uploadFileOperations = new();
    private int _interruptedTransfersChecked;
    private int _resumingTransfers;
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _transferCompletions = new();
    private readonly SemaphoreSlim _registryLoadGate = new(1, 1);
    private readonly TransferManager _transferManager;
    private readonly EncryptionService _encryption;
    private readonly PreviewResourceRegistry _previewResources = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _videoOpenRequests = new();
    private readonly ConcurrentDictionary<string, VideoStreamSession> _videoSessions = new();
    private readonly VideoPlaybackTracker _videoProgress;
    public PreviewResourceRegistry PreviewResources => _previewResources;
    private readonly Action<bool> _onSplashChanged;
    private IReadOnlyList<AppEntry> _loadedApps = [];
    private IReadOnlyList<AppEntry> _favoriteApps = [];
    private IReadOnlyList<AppEntry> _deviceApps = [];
    private int _favoriteLoadVersion;
    private int _deviceLoadVersion;
    private DiscordSession? _session;
    private AppRegistry? _registry;
    private string _currentPath = "/";
    private volatile bool _loggingOut;
    private int _unindexedPayloadScanStarted;

    public AppController(
        Window owner,
        SettingsStore store,
        WebMessageBridge bridge,
        Func<DiscordSession, Task> onConnected,
        Action<string>? onThemeChanged = null,
        Action<bool>? onSplashChanged = null)
    {
        _owner = owner;
        _store = store;
        _bridge = bridge;
        _onConnected = onConnected;
        _onThemeChanged = onThemeChanged ?? (_ => { });
        _onSplashChanged = onSplashChanged ?? (_ => { });
        _transferManager = new TransferManager(new TransferStateRepository(store.TransfersDirectory));
        _encryption = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(store));
        var playbackStore = new VideoPlaybackProgressStore(store.DatabasePath);
        playbackStore.Prune();
        _videoProgress = new VideoPlaybackTracker(playbackStore);
        _videoMemoryGuard = new VideoPlaybackMemoryGuard(() => !_loggingOut && !_videoSessions.IsEmpty, ReleaseVideoMemoryAsync);
        if (Application.Current is App { SyncHost: { } syncHost }) AttachSyncHost(syncHost);
    }

    public void SetSession(DiscordSession session)
    {
        ResetCleanupMonitoring();
        _session = session;
        _registry = null;
        Volatile.Write(ref _unindexedPayloadScanStarted, 0);
    }

    public bool HasSession => _session != null;

    public void AttachBridge(WebMessageBridge bridge)
    {
        _bridge = bridge;
    }

    public async Task HandleJsonMessageAsync(string json)
    {
        BridgeMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(json, JsonOptions.Web);
        }
        catch
        {
            return;
        }

        if (message?.Method == null)
        {
            return;
        }

        using var diagnosticOperation = Services.Diagnostics.DevelopmentDiagnostics.Begin("bridge", message.Method);
        try
        {
            await DispatchAsync(message.Method, message.Args ?? []);
        }
        catch (Exception ex)
        {
            AppLogger.Error("BridgeMessage." + message.Method, ex);
            await ErrorAsync(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _videoMemoryGuard.Dispose();
        DetachSyncHost();
        _loggingOut = true;
        ResetCleanupMonitoring();
        foreach (var request in _videoOpenRequests.Values) request.Cancel();
        await Task.Run(_videoProgress.Suspend);
        foreach (var video in _videoSessions.Values) await video.DisposeAsync();
        _videoSessions.Clear();
        CancellationTokenSource? thumbnailRequest;
        lock (_thumbnailRequestSync)
        {
            thumbnailRequest = _thumbnailRequestCts;
            _thumbnailRequestCts = null;
            _thumbnailRequestKey = "";
        }
        try { thumbnailRequest?.Cancel(); } catch { }

        foreach (var transfer in _transferOperations.Values)
        {
            try { transfer.Cancel(); } catch { }
            transfer.Dispose();
        }

        _transferOperations.Clear();
        _transferCompletions.Clear();
        _thumbnailRequests.Clear();

        foreach (var dir in _previewDirs.Values)
        {
            CleanDir(dir);
        }

        _previewDirs.Clear();
        _previewUrls.Clear();

        _previewResources.Dispose();
        if (_session != null)
        {
            await _session.DisposeAsync();
        }
    }

    private Task DispatchAsync(string method, JsonElement[] args)
    {
        switch (method)
        {
            case "loadSyncState": case "discoverSyncFolders": case "chooseSyncFolder":
            case "openSyncFolder":
            case "addSyncFolder": case "updateSyncFolder": case "removeSyncFolder":
            case "syncNow": case "pauseSync": case "resolveSyncConflict": case "setSyncPreferences":
                return SyncDispatchAsync(method, args);
            case "connect": return ConnectAsync(args);
            case "logout": _ = RunAsync(LogoutWithSplashAsync, accountWork: false); return Task.CompletedTask;
            case "openGuide": return OpenGuideAsync();
            case "setCurrentPath":
                var requestedPath = PathRules.Normalize(ArgString(args, 0));
                var requestedFolderId = ArgString(args, 1);
                _currentPath = requestedPath;
                _ = RunAsync(() => LoadFolderAtPathAsync(requestedPath, requestedFolderId));
                return Task.CompletedTask;
            case "loadApps": _ = RunAsync(async () => { await LoadAppsAsync(); await NotifyInterruptedTransfersAsync(); }); return Task.CompletedTask;
            case "loadFavorites": _ = RunAsync(() => LoadFavoritesAsync(ArgBool(args, 0, false))); return Task.CompletedTask;
            case "loadSynchronizedDevices": _ = RunAsync(() => LoadSynchronizedDevicesAsync(ArgBool(args, 0, false))); return Task.CompletedTask;
            case "setFavorite": _ = RunAsync(() => SetFavoriteAsync(ArgString(args, 0), ArgBool(args, 1, false))); return Task.CompletedTask;
            case "refreshCurrentFolder": _ = RunAsync(RefreshCurrentFolderAsync); return Task.CompletedTask;
            case "refreshServerIndex": _ = RunAsync(() => RefreshServerIndexAsync(ArgBool(args, 0, false))); return Task.CompletedTask;
            case "loadVisibleThumbnails": _ = RunAsync(() => RunTrackedThumbnailRequestAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "loadVisibleFolderStatistics": _ = RunAsync(() => LoadVisibleFolderStatisticsAsync(ArgString(args, 0))); return Task.CompletedTask;
            case "requestPreview": _ = RunAsync(() => RunTrackedPreviewAsync(() => RequestPreviewAsync(ArgString(args, 0), ArgString(args, 1)))); return Task.CompletedTask;
            case "requestVideoQuality": _ = RequestVideoQualitySafeAsync(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2)); return Task.CompletedTask;
            case "closePreview": ClosePreview(ArgString(args, 0)); return Task.CompletedTask;
            case "videoPlayback": _ = RunAsync(() => UpdateVideoPlaybackAsync(args)); return Task.CompletedTask;
            case "clearPreviewCache": _ = RunAsync(ClearPreviewCacheAsync); return Task.CompletedTask;
            case "getPreviewCacheSize": _ = RunAsync(SendPreviewCacheSizeAsync); return Task.CompletedTask;
            case "openPreviewMedia": _ = RunAsync(() => RunTrackedPreviewAsync(() => OpenPreviewMediaAsync(ArgString(args, 0)))); return Task.CompletedTask;
            case "requestPdfPages": return Task.CompletedTask;
            case "createFolder": _ = RunAsync(() => CreateFolderAsync(ArgString(args, 0))); return Task.CompletedTask;
            case "moveItems": _ = RunAsync(() => MoveItemsAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "moveItem": _ = RunAsync(() => MoveItemsAsync(JsonSerializer.Serialize(new[] { ArgString(args, 0) }), ArgString(args, 1))); return Task.CompletedTask;
            case "updateFolderColor": _ = RunAsync(() => UpdateFolderColorAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "renameItem": _ = RunAsync(() => RenameItemAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "requestFileInformation": _ = RunAsync(() => SendFileInformationAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "copyFile": _ = RunAsync(() => CopyFileAsync(ArgString(args, 0))); return Task.CompletedTask;
            case "downloadVersion": _ = RunAsync(() => StartVersionDownloadAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "startUpload": return StartUploadDialogAsync();
            case "startFolderUpload": return StartFolderUploadDialogAsync();
            case "resolveUploadConflict": ResolveUploadConflict(args); return Task.CompletedTask;
            case "cancelTransfers": CancelTransfers(ArgString(args, 0)); return Task.CompletedTask;
            case "cancelUploadFile": CancelUploadFile(ArgString(args, 0), ArgInt(args, 1, -1)); return Task.CompletedTask;
            case "toggleTransferPause": ToggleTransferPause(ArgString(args, 0), ArgInt(args, 1, -1)); return Task.CompletedTask;
            case "resumeTransfers": _ = RunAsync(ResumeInterruptedTransfersAsync); return Task.CompletedTask;
            case "failInterruptedTransfers": _ = RunAsync(FailInterruptedTransfersAsync); return Task.CompletedTask;
            case "startDownload": return StartDownloadAsync(ArgString(args, 0), ArgString(args, 1));
            case "deleteApp": _ = RunAsync(() => DeleteAppAsync(ArgString(args, 0), ArgString(args, 1), ArgBool(args, 2, false))); return Task.CompletedTask;
            case "deleteItems": _ = RunAsync(() => DeleteItemsAsync(ArgString(args, 0), ArgBool(args, 1, false))); return Task.CompletedTask;
            case "emptyTrash": _ = RunAsync(EmptyTrashAsync); return Task.CompletedTask;
            case "restoreItems": _ = RunAsync(() => RelocateTrashItemsAsync(ArgString(args, 0), restore: true)); return Task.CompletedTask;
            case "saveMarkdown": _ = RunAsync(() => SaveMarkdownAsync(args)); return Task.CompletedTask;
            case "uploadMarkdownImage": _ = RunAsync(() => UploadMarkdownImageAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "uploadMarkdownImageData": _ = RunAsync(() => UploadMarkdownImageDataAsync(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2), ArgString(args, 3))); return Task.CompletedTask;
            case "resolveMarkdownImage": _ = RunAsync(() => ResolveMarkdownImageAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "resolveEncryptedMarkdownImage": _ = RunAsync(() => ResolveEncryptedMarkdownImageAsync(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2))); return Task.CompletedTask;
            case "resolveEncryptedDriveMarkdownImage": _ = RunAsync(() => ResolveEncryptedDriveMarkdownImageAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "configureCustomEncryption": return ConfigureCustomEncryptionAsync(ArgString(args, 0), ArgBool(args, 1, false));
            case "setEncryptionMode": return SetEncryptionModeAsync(ArgString(args, 0));
            case "exportCustomEncryptionKey": return ExportCustomEncryptionKeyAsync();
            case "importCustomEncryptionKey": return ImportCustomEncryptionKeyAsync();
            case "saveSettings": SaveSettings(args); return Task.CompletedTask;
            case "testProxy": return TestProxyAsync(args);
            case "applyWindowTheme": _onThemeChanged(ArgString(args, 0, _store.Load().Theme)); return Task.CompletedTask;
            case "setHostFullscreen": SetHostFullscreen(ArgBool(args, 0, false)); return Task.CompletedTask;
            case "chooseDownloadPath": return ChoosePathAsync("download", "Pasta de downloads");
            case "chooseDataPath": return ChooseDataPathAsync();
            default: return Task.CompletedTask;
        }
    }

    public Task UploadDroppedPathsAsync(IEnumerable<string> paths)
    {
        _ = RunAsync(() => UploadPathsAsync(paths));
        return Task.CompletedTask;
    }

    private async Task ConnectAsync(JsonElement[] args)
    {
        _loggingOut = false;
        _videoProgress.Resume();
        var token = ArgString(args, 0).Trim();
        var names = ArgString(args, 1, "names").Trim();
        var folders = ArgString(args, 2, "folders").Trim();
        var forum = ArgString(args, 3, "files").Trim();
        var serial = ArgString(args, 4, "serial").Trim();
        var allowCreate = ArgBool(args, 5, false);
        var proxyJson = ArgString(args, 6);

        if (string.IsNullOrWhiteSpace(token)) { await _bridge.CallAsync("_onSetupError", "Insira o token do bot."); return; }
        if (string.IsNullOrWhiteSpace(names)) { names = "names"; }
        if (string.IsNullOrWhiteSpace(folders)) { folders = "folders"; }
        if (string.IsNullOrWhiteSpace(forum)) { forum = "files"; }
        if (string.IsNullOrWhiteSpace(serial)) { serial = "serial"; }

        var settings = _store.Load();
        settings.BotToken = token;
        if (!string.IsNullOrWhiteSpace(proxyJson))
        {
            try
            {
                var proxy = JsonSerializer.Deserialize<ProxySettings>(proxyJson, ProxyJsonOptions) ?? new ProxySettings();
                settings.Proxy = proxy;
            }
            catch
            {
                await _bridge.CallAsync("_onSetupError", "Configuração de proxy inválida.", true);
                return;
            }
        }

        if (settings.Proxy.Mode == ProxyMode.Manual && !settings.Proxy.IsValidManual(out var proxyError))
        {
            await _bridge.CallAsync("_onSetupError", "Proxy manual inválido: " + proxyError, true);
            return;
        }

        NetworkConfig.Current.Apply(settings.Proxy);
        _store.Save(settings);

        await _bridge.CallAsync("_onSetupConnecting", "Procurando canais no servidor...");
        if (LooksLikeDiscordId(names) && LooksLikeDiscordId(folders) && LooksLikeDiscordId(forum) && LooksLikeDiscordId(serial))
        {
            settings.NamesChannelId = names;
            settings.FoldersForumId = folders;
            settings.AppsForumId = forum;
            settings.SerialChannelId = serial;
        }
        else
        {
            try
            {
                var resolved = await ResolveDiscordChannelsAsync(token, names, folders, forum, serial, allowCreate);
                settings.NamesChannelId = resolved.NamesChannelId;
                settings.FoldersForumId = resolved.FoldersForumId;
                settings.AppsForumId = resolved.AppsForumId;
                settings.SerialChannelId = resolved.SerialChannelId;
            }
            catch (SetupChannelsMissingException ex)
            {
                await _bridge.CallAsync("_onSetupChannelsMissing", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                await ReportSetupConnectionErrorAsync(ex);
                return;
            }
        }

        var session = new DiscordSession();
        try
        {
            await _bridge.CallAsync("_onSetupConnecting", "Conectando ao Discord...");
            await session.ConnectAsync(settings);
            _store.Save(settings);
            await ReplaceSessionAsync(session);
            if (_syncHost != null) await _syncHost.ReloadAsync();
            await _onConnected(session);
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            await ReportSetupConnectionErrorAsync(ex);
        }
    }

    private async Task ReplaceSessionAsync(DiscordSession session)
    {
        var old = _session;
        _session = session;
        if (old != null)
        {
            await old.DisposeAsync();
        }
    }

    private async Task LogoutWithSplashAsync()
    {
        _onSplashChanged(true);
        try
        {
            await _owner.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            await LogoutAsync();
        }
        catch
        {
            _onSplashChanged(false);
            throw;
        }
    }

    private async Task LogoutAsync()
    {
        _loggingOut = true;
        ResetCleanupMonitoring();
        if (_syncHost != null) await _syncHost.SuspendAccountAsync();
        if (_store.Load().StartWithWindows) await Organessonium.Services.Sync.StartupManager.SetEnabledAsync(false);
        _loadedApps = [];
        _favoriteApps = [];
        _deviceApps = [];
        _syncSources = [];
        Interlocked.Increment(ref _favoriteLoadVersion);
        Interlocked.Increment(ref _deviceLoadVersion);
        var session = _session;
        _session = null;
        _registry = null;

        await CancelActiveTransfersAndWaitAsync();
        await CancelThumbnailRequestsAndWaitAsync();
        ClearPreviewState();
        await Task.Run(_videoProgress.Suspend);
        _encryption.ClearCustomKey();

        if (session != null)
        {
            await session.DisposeAsync();
        }

        try
        {
            await Task.WhenAll(_previewRequests.Values.Select(completion => completion.Task)).WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(_accountWork.Values.Select(completion => completion.Task)).WaitAsync(TimeSpan.FromSeconds(15));
            if (_owner is MainWindow main) await main.ClearAccountBrowserDataAsync();
            await Task.Run(_store.ClearLocalDataForLogout);
        }
        catch (Exception)
        {
            _onSplashChanged(false);
            await _bridge.CallAsync("_onLogoutFailed", "Não foi possível limpar todos os dados locais. Tente sair novamente.");
            return;
        }
        await _bridge.SetSettingsAsync(new AppSettings
        {
            DataPath = _store.DataDirectory.FullName
        });
        await _bridge.CallAsync("_onLoggedOut");
    }

    private Task OpenGuideAsync()
    {
        Process.Start(new ProcessStartInfo("https://github.com/Matehfields/organessonium") { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async Task<ResolvedDiscordChannels> ResolveDiscordChannelsAsync(
        string token,
        string namesInput,
        string foldersInput,
        string filesInput,
        string serialInput,
        bool allowCreate)
    {
        var namesName = NormalizeDiscordChannelName(namesInput, "names");
        var foldersName = NormalizeDiscordChannelName(foldersInput, "folders");
        var filesName = NormalizeDiscordChannelName(filesInput, "files");
        var serialName = NormalizeDiscordChannelName(serialInput, "serial");
        var channelNames = new[] { namesName, foldersName, filesName, serialName };
        if (channelNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != channelNames.Length)
        {
            throw new SetupChannelsMissingException("Os canais names, folders, files e serial precisam ter nomes diferentes.");
        }

        using var api = new DiscordApiClient(token);
        var guilds = await api.GetCurrentUserGuildsAsync();
        if (guilds.Count == 0)
        {
            throw new SetupChannelsMissingException("O bot não está em nenhum servidor. Adicione o bot a um servidor e tente novamente.");
        }

        var completeMatches = new List<(DiscordGuildDto Guild, DiscordChannelDto Names, DiscordChannelDto Folders, DiscordChannelDto Files, DiscordChannelDto Serial)>();
        var wrongTypes = new List<string>();
        foreach (var guild in guilds)
        {
            var channels = await api.GetGuildChannelsAsync(guild.Id);
            var namesChannels = channels
                .Where(c => SameDiscordChannelName(c.Name, namesName) && IsTextChannel(c.Type))
                .ToList();
            var foldersForums = channels
                .Where(c => SameDiscordChannelName(c.Name, foldersName) && IsForumChannel(c.Type))
                .ToList();
            var filesForums = channels
                .Where(c => SameDiscordChannelName(c.Name, filesName) && IsForumChannel(c.Type))
                .ToList();
            var serialChannels = channels
                .Where(c => SameDiscordChannelName(c.Name, serialName) && IsSerialChannel(c.Type))
                .ToList();

            if (namesChannels.Count > 1 || foldersForums.Count > 1 || filesForums.Count > 1 || serialChannels.Count > 1)
            {
                throw new SetupChannelsMissingException(
                    "Encontrei mais de um canal com o mesmo nome no servidor \"" + guild.Name +
                    "\". Informe nomes diferentes para evitar usar o canal errado.");
            }

            if (channels.Any(c => SameDiscordChannelName(c.Name, namesName) && !IsTextChannel(c.Type)))
            {
                wrongTypes.Add("\"" + namesName + "\" no servidor \"" + guild.Name + "\" não é canal de texto.");
            }

            if (channels.Any(c => SameDiscordChannelName(c.Name, foldersName) && !IsForumChannel(c.Type)))
            {
                wrongTypes.Add("\"" + foldersName + "\" no servidor \"" + guild.Name + "\" não é fórum.");
            }

            if (channels.Any(c => SameDiscordChannelName(c.Name, filesName) && !IsForumChannel(c.Type)))
            {
                wrongTypes.Add("\"" + filesName + "\" no servidor \"" + guild.Name + "\" não é fórum.");
            }

            if (channels.Any(c => SameDiscordChannelName(c.Name, serialName) && !IsSerialChannel(c.Type)))
            {
                wrongTypes.Add("\"" + serialName + "\" no servidor \"" + guild.Name + "\" não é canal de texto.");
            }

            if (namesChannels.Count == 1 && foldersForums.Count == 1 && filesForums.Count == 1 && serialChannels.Count == 1)
            {
                completeMatches.Add((guild, namesChannels[0], foldersForums[0], filesForums[0], serialChannels[0]));
            }
        }

        if (completeMatches.Count == 1)
        {
            return new ResolvedDiscordChannels(
                completeMatches[0].Names.Id,
                completeMatches[0].Folders.Id,
                completeMatches[0].Files.Id,
                completeMatches[0].Serial.Id);
        }

        if (completeMatches.Count > 1)
        {
            throw new SetupChannelsMissingException(
                "Encontrei canais \"" + namesName + "\", \"" + foldersName + "\", \"" + filesName + "\" e \"" + serialName +
                "\" em mais de um servidor. Use nomes diferentes para evitar usar o servidor errado.");
        }

        if (!allowCreate)
        {
            var message = "Não encontrei os canais \"" + namesName + "\", \"" + foldersName + "\", \"" + filesName + "\" e \"" + serialName +
                "\". Posso criá-los automaticamente ou você pode informar nomes diferentes.";
            if (wrongTypes.Count > 0)
            {
                message += " Também encontrei canal com nome parecido, mas de tipo incorreto: " +
                    string.Join(" ", wrongTypes.Distinct(StringComparer.OrdinalIgnoreCase));
            }

            throw new SetupChannelsMissingException(message);
        }

        if (guilds.Count != 1)
        {
            throw new SetupChannelsMissingException(
                "O bot está em mais de um servidor. Para criar canais automaticamente com segurança, deixe o bot em apenas um servidor ou informe nomes diferentes.");
        }

        var targetGuild = guilds[0];
        var targetChannels = await api.GetGuildChannelsAsync(targetGuild.Id);
        var existingWrongType = targetChannels.FirstOrDefault(c =>
            (SameDiscordChannelName(c.Name, namesName) && !IsTextChannel(c.Type)) ||
            (SameDiscordChannelName(c.Name, foldersName) && !IsForumChannel(c.Type)) ||
            (SameDiscordChannelName(c.Name, filesName) && !IsForumChannel(c.Type)) ||
            (SameDiscordChannelName(c.Name, serialName) && !IsSerialChannel(c.Type)));
        if (existingWrongType != null)
        {
            throw new SetupChannelsMissingException(
                "Ja existe um canal chamado \"" + existingWrongType.Name +
                "\" com tipo diferente do necessário. Informe outro nome para evitar configurar o canal errado.");
        }

        var namesExisting = targetChannels.Where(c => SameDiscordChannelName(c.Name, namesName) && IsTextChannel(c.Type)).ToList();
        var foldersExisting = targetChannels.Where(c => SameDiscordChannelName(c.Name, foldersName) && IsForumChannel(c.Type)).ToList();
        var filesExisting = targetChannels.Where(c => SameDiscordChannelName(c.Name, filesName) && IsForumChannel(c.Type)).ToList();
        var serialExisting = targetChannels.Where(c => SameDiscordChannelName(c.Name, serialName) && IsSerialChannel(c.Type)).ToList();
        if (namesExisting.Count > 1 || foldersExisting.Count > 1 || filesExisting.Count > 1 || serialExisting.Count > 1)
        {
            throw new SetupChannelsMissingException(
                "Encontrei canais duplicados no servidor. Informe nomes diferentes para evitar usar o canal errado.");
        }

        var namesChannel = namesExisting.Count == 1
            ? namesExisting[0]
            : await api.CreateGuildChannelAsync(targetGuild.Id, namesName, 0);
        var foldersChannel = foldersExisting.Count == 1
            ? foldersExisting[0]
            : await api.CreateGuildChannelAsync(targetGuild.Id, foldersName, 15);
        var filesChannel = filesExisting.Count == 1
            ? filesExisting[0]
            : await api.CreateGuildChannelAsync(targetGuild.Id, filesName, 15);
        var serialChannel = serialExisting.Count == 1
            ? serialExisting[0]
            : await api.CreateGuildChannelAsync(targetGuild.Id, serialName, 0);
        return new ResolvedDiscordChannels(namesChannel.Id, foldersChannel.Id, filesChannel.Id, serialChannel.Id);
    }

    private async Task LoadAppsAsync()
    {
        await _registryLoadGate.WaitAsync();
        var readStateHandedToRefresh = false;
        try
        {
            await _bridge.CallAsync("_onServerReadState", true);
            var registry = Registry();
            await _bridge.CallAsync("_onFolderLoadState", "/", true);
            var previousStatistics = _store.LoadLibraryStatistics();
            if (previousStatistics != null) await SendLibraryStatisticsAsync(previousStatistics);
            else
            {
                var localStatistics = await registry.GetCachedLibraryStatisticsAsync();
                if (localStatistics != null) await SendLibraryStatisticsAsync(localStatistics);
            }
            var cached = await registry.LoadCachedFolderAsync(AppRegistry.RootFolderId, "/");
            _loadedApps = cached;
            var hasCache = cached.Count > 0;
            await _bridge.CallAsync("_onRegistryLoadStarted", hasCache);
            if (hasCache)
            {
                await _bridge.CallAsync("_onCachedAppsLoaded", cached);
            }
            if (hasCache) await _bridge.CallAsync("_onAppsLoaded", _loadedApps);

            _ = RunAsync(async () =>
            {
                try
                {
                    await registry.RefreshMappingsAsync();
                    var refreshed = await registry.LoadFolderSerialAsync(AppRegistry.RootFolderId, "/");
                    ReplaceLoadedFolder(AppRegistry.RootFolderId, refreshed);
                    await _bridge.CallAsync("_onAppsLoaded", _loadedApps);
                    await PublishLibraryFileCountAsync(registry);
                    QueueRegistryMaintenance(registry);
                }
                catch (OperationCanceledException)
                {
                    // App shutdown cancels the silent reconciliation.
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Registry.RootRefresh", ex);
                }
                finally
                {
                    await _bridge.CallAsync("_onFolderLoadState", "/", false);
                    await _bridge.CallAsync("_onServerReadState", false);
                }
            });
            readStateHandedToRefresh = true;
        }
        finally
        {
            if (!readStateHandedToRefresh)
            {
                await _bridge.CallAsync("_onFolderLoadState", "/", false);
                await _bridge.CallAsync("_onServerReadState", false);
            }
            _registryLoadGate.Release();
        }
    }

    private async Task LoadFavoritesAsync(bool forceRefresh)
    {
        var version = Interlocked.Increment(ref _favoriteLoadVersion);
        await _bridge.CallAsync("_onServerReadState", true);
        try
        {
            var entries = await Registry().LoadFavoritesAsync(forceRefresh: forceRefresh);
            if (version != Volatile.Read(ref _favoriteLoadVersion))
            {
                return;
            }
            _favoriteApps = entries;
            await _bridge.CallAsync("_onSpecialItemsLoaded", "favorites", entries);
        }
        catch
        {
            if (version != Volatile.Read(ref _favoriteLoadVersion))
            {
                return;
            }
            await _bridge.CallAsync("_onSpecialItemsLoaded", "favorites", _favoriteApps);
            throw;
        }
        finally
        {
            await _bridge.CallAsync("_onServerReadState", false);
        }
    }

    private async Task LoadSynchronizedDevicesAsync(bool forceRefresh)
    {
        var version = Interlocked.Increment(ref _deviceLoadVersion);
        await _bridge.CallAsync("_onServerReadState", true);
        try
        {
            var entries = await Registry().LoadSynchronizedDevicesAsync(forceRefresh: forceRefresh);
            if (version != Volatile.Read(ref _deviceLoadVersion))
            {
                return;
            }
            _deviceApps = entries;
            await _bridge.CallAsync("_onSpecialItemsLoaded", "devices", entries);
        }
        catch
        {
            if (version != Volatile.Read(ref _deviceLoadVersion))
            {
                return;
            }
            await _bridge.CallAsync("_onSpecialItemsLoaded", "devices", _deviceApps);
            throw;
        }
        finally
        {
            await _bridge.CallAsync("_onServerReadState", false);
        }
    }

    private async Task SetFavoriteAsync(string metadataMessageId, bool favorite)
    {
        var entry = LoadedEntriesForLookup().FirstOrDefault(candidate =>
            candidate.MetadataMessageId.Equals(metadataMessageId, StringComparison.Ordinal));
        if (entry == null)
        {
            await ErrorAsync("Item não encontrado.");
            return;
        }

        await _bridge.CallAsync("_onServerReadState", true);
        try
        {
            await Registry().SetFavoriteAsync(entry, favorite);
            entry.Favorite = favorite;
            _favoriteApps = favorite
                ? _favoriteApps.Where(app => EntryCacheKey(app) != EntryCacheKey(entry)).Append(entry).ToList()
                : _favoriteApps.Where(app => EntryCacheKey(app) != EntryCacheKey(entry)).ToList();
            await _bridge.CallAsync("_onFavoriteChanged", entry);
        }
        finally
        {
            await _bridge.CallAsync("_onServerReadState", false);
        }
    }

    private async Task CleanupSupersededRemoteSerialsAsync(AppRegistry registry)
    {
        try
        {
            await registry.CleanupSupersededRemoteSerialsAsync();
        }
        catch (OperationCanceledException)
        {
            // App shutdown can interrupt this best-effort maintenance task.
        }
        catch (Exception ex)
        {
            AppLogger.Error("Registry.SerialCleanup", ex);
        }
    }

    private async Task RefreshCurrentFolderAsync()
    {
        await RefreshServerIndexAsync(rebuildSerials: false);
    }

    private async Task RefreshServerIndexAsync(bool rebuildSerials)
    {
        await _bridge.CallAsync("_onServerReadState", true);
        try
        {
            var registry = Registry();
            async Task Refreshed(string folderId, IReadOnlyList<AppEntry> entries)
            {
                ReplaceLoadedFolder(folderId, entries);
                await Task.CompletedTask;
            }
            if (rebuildSerials) await registry.RefreshServerIndexAsync(true, onFolderRefreshed: Refreshed);
            else await registry.RefreshSerialIndexAsync(onFolderRefreshed: Refreshed);
            await _bridge.CallAsync("_onAppsLoaded", _loadedApps);
            await PublishLibraryFileCountAsync(registry);
            await _bridge.CallAsync("_onServerIndexRefreshed", rebuildSerials);
        }
        finally
        {
            await _bridge.CallAsync("_onServerReadState", false);
        }
        QueueRegistryMaintenance(Registry());
    }

    private async Task LoadFolderAtPathAsync(
        string folderPath,
        string folderIdsJson,
        bool refreshMappings = false)
    {
        var normalizedPath = PathRules.Normalize(folderPath);
        var folderIds = ParseFolderIds(folderIdsJson);
        if (folderIds.Count == 0)
        {
            folderIds = FindLoadedFolderIdsAtPath(normalizedPath);
        }

        var registry = Registry();
        try
        {
            await _bridge.CallAsync("_onServerReadState", true);
            await _bridge.CallAsync("_onFolderLoadState", normalizedPath, true);
            if (normalizedPath == AppRegistry.TrashPath)
            {
                await registry.EnsureTrashIndexAsync();
                folderIds = [AppRegistry.TrashFolderId];
            }
            if (refreshMappings)
            {
                await registry.RefreshMappingsAsync();
            }

            if (folderIds.Count == 0)
            {
                var resolved = await registry.ResolveFolderIdByPathAsync(normalizedPath);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    folderIds = [resolved];
                }
            }

            if (folderIds.Count == 0)
            {
                return;
            }

            var cached = await Task.WhenAll(folderIds.Select(folderId =>
                LoadFolderResultAsync(registry, folderId, normalizedPath, refresh: false)));
            ReplaceLoadedFolders(cached);
            if (normalizedPath == _currentPath)
            {
                await _bridge.CallAsync("_onAppsLoaded", _loadedApps);
            }

            var refreshed = await Task.WhenAll(folderIds.Select(folderId =>
                LoadFolderResultAsync(registry, folderId, normalizedPath, refresh: true)));
            ReplaceLoadedFolders(refreshed);
            if (normalizedPath == _currentPath)
            {
                await _bridge.CallAsync("_onAppsLoaded", _loadedApps);
            }
        }
        finally
        {
            await _bridge.CallAsync("_onFolderLoadState", normalizedPath, false);
            await _bridge.CallAsync("_onServerReadState", false);
        }
    }

    private IReadOnlyList<string> FindLoadedFolderIdsAtPath(string path)
    {
        var normalizedPath = PathRules.Normalize(path);
        if (normalizedPath == "/")
        {
            return [AppRegistry.RootFolderId];
        }

        return _loadedApps
            .Where(entry => IsFolderEntry(entry) &&
                            PathRules.ChildPath(entry).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(entry.FolderId))
            .Select(entry => entry.FolderId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ParseFolderIds(string folderIdsJson)
    {
        if (string.IsNullOrWhiteSpace(folderIdsJson))
        {
            return [];
        }

        try
        {
            var ids = JsonSerializer.Deserialize<List<string>>(folderIdsJson, JsonOptions.Web);
            if (ids != null)
            {
                return ids
                    .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Older WebView content sends one folderId rather than a JSON array.
        }

        return folderIdsJson.Length <= 128 ? [folderIdsJson] : [];
    }

    private async Task<FolderLoadResult?> LoadFolderResultAsync(
        AppRegistry registry,
        string folderId,
        string folderPath,
        bool refresh)
    {
        try
        {
            var entries = refresh
                ? await registry.RefreshFolderAsync(folderId, folderPath)
                : await registry.LoadCachedFolderAsync(folderId, folderPath);
            return new FolderLoadResult(folderId, entries);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error(refresh ? "Registry.FolderRefresh" : "Registry.FolderCache", ex, folderId);
            return null;
        }
    }

    private void ReplaceLoadedFolders(IEnumerable<FolderLoadResult?> results)
    {
        foreach (var result in results.Where(result => result != null))
        {
            ReplaceLoadedFolder(result!.FolderId, result.Entries);
        }
    }
    private IEnumerable<AppEntry> LoadedEntriesForLookup() =>
        CurrentLocalEntries().Concat(_favoriteApps).Concat(_deviceApps);

    private void ReplaceLoadedFolder(string folderId, IReadOnlyList<AppEntry> entries)
    {
        var normalizedFolderId = string.IsNullOrWhiteSpace(folderId) ? AppRegistry.RootFolderId : folderId;
        var byId = _loadedApps
            .Where(entry => !string.Equals(entry.ParentFolderId ?? AppRegistry.RootFolderId, normalizedFolderId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(EntryCacheKey, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            byId[EntryCacheKey(entry)] = entry;
        }

        _loadedApps = byId.Values.ToList();
    }

    private static string EntryCacheKey(AppEntry app)
    {
        if (app.Type == "file" && !string.IsNullOrWhiteSpace(app.FileId)) return "file:" + app.FileId;
        if (!string.IsNullOrWhiteSpace(app.ThreadId))
        {
            return "thread:" + app.ThreadId;
        }

        if (!string.IsNullOrWhiteSpace(app.FileId))
        {
            return "file:" + app.FileId;
        }

        if (!string.IsNullOrWhiteSpace(app.FolderId))
        {
            return "folder:" + app.FolderId;
        }

        return "metadata:" + app.MetadataMessageId;
    }

    private List<AppEntry> CurrentLocalEntries()
    {
        return _loadedApps.Count > 0
            ? _loadedApps.ToList()
            : _store.LoadCachedEntriesForFolder(AppRegistry.RootFolderId);
    }

    private static void ReplaceLocalEntry(List<AppEntry> entries, AppEntry replacement)
    {
        var key = EntryCacheKey(replacement);
        entries.RemoveAll(entry => EntryCacheKey(entry).Equals(key, StringComparison.OrdinalIgnoreCase));
        entries.Add(replacement);
    }

    private static void TrackFolderSerial(ISet<string> folderIds, string? folderId)
    {
        folderIds.Add(string.IsNullOrWhiteSpace(folderId) ? AppRegistry.RootFolderId : folderId);
    }

    private async Task PublishLocalRegistryAsync(AppRegistry registry, List<AppEntry> entries, bool refreshStatistics = true, bool incremental = false, AppEntry? changedEntry = null)
    {
        foreach (var entry in entries)
        {
            entry.Normalize();
        }

        _loadedApps = entries;
        IReadOnlyList<AppEntry> publishedEntries = changedEntry == null ? entries : [changedEntry];
        await _bridge.CallAsync(incremental ? "_onAppsBatch" : "_onLocalAppsChanged", publishedEntries);
        if (refreshStatistics) _ = RunAsync(() => PublishLibraryFileCountAsync(registry));
    }

    private async Task PublishLibraryFileCountAsync(AppRegistry registry)
    {
        if (_loggingOut)
        {
            return;
        }

        try
        {
            var statistics = await registry.GetLibraryStatisticsAsync(AppRegistry.RootFolderId);
            if (statistics != null && !_loggingOut)
            {
                _store.SaveLibraryStatistics(statistics);
                await SendLibraryStatisticsAsync(statistics);
            }
        }
        catch (OperationCanceledException) when (_loggingOut)
        {
            // Logout intentionally stops background registry work.
        }
        catch (Exception ex)
        {
            AppLogger.Error("Registry.LibraryFileCount", ex);
        }
    }

    private Task SetBackgroundActivityAsync(string message, bool active)
    {
        return _bridge.CallAsync("_onBackgroundActivity", message, active);
    }

    private async Task SendLibraryStatisticsAsync(LibraryStatistics statistics)
    {
        await _bridge.CallAsync("_onLibraryStatistics", new
        {
            fileCount = statistics.FileCount,
            folderCount = statistics.FolderCount,
            totalBytes = statistics.TotalBytes
        });
        await _bridge.CallAsync("_onLibraryFileCount", statistics.FileCount);
    }

    private async Task LoadVisibleFolderStatisticsAsync(string visibleFolderIdsJson, string? requestedPath = null)
    {
        if (_loggingOut)
        {
            return;
        }

        var folderIds = ParseVisibleFolderIds(visibleFolderIdsJson);
        if (folderIds.Count == 0)
        {
            return;
        }

        var registry = Registry();
        var path = requestedPath ?? _currentPath;
        var statistics = new ConcurrentBag<FolderFileStatistic>();
        var workers = Math.Clamp(_store.Load().SearchWorkers, 1, 4);
        await Parallel.ForEachAsync(
            folderIds,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            async (folderId, cancellationToken) =>
            {
                try
                {
                    var count = await registry.GetFolderFileCountAsync(folderId, cancellationToken);
                    if (count.HasValue)
                    {
                        statistics.Add(new FolderFileStatistic(folderId, count.Value));
                    }
                }
                catch (OperationCanceledException) when (_loggingOut)
                {
                    // Logout intentionally stops pending background work.
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Registry.FolderStatistics", ex, folderId);
                }
            });

        if (!_loggingOut && statistics.Count > 0)
        {
            await _bridge.CallAsync(
                "_onFolderStatistics",
                path,
                statistics.OrderBy(statistic => statistic.FolderId, StringComparer.Ordinal).ToList());
        }
    }

    private async Task LoadVisibleThumbnailsAsync(string visibleIdsJson, string libraryView)
    {
        if (_loggingOut)
        {
            return;
        }

        var settings = _store.Load();
        if (!settings.PreviewEnabled)
        {
            return;
        }

        var visibleIds = ParseVisibleThumbnailIds(visibleIdsJson);
        if (visibleIds.Count == 0)
        {
            return;
        }
        var visibleIdSet = visibleIds.ToHashSet(StringComparer.Ordinal);

        var path = _currentPath;
        var favoritesView = libraryView.Equals("favorites", StringComparison.Ordinal);
        var requestKey = libraryView + "|" + path + "|" + string.Join(",", visibleIds);
        CancellationTokenSource request;
        CancellationTokenSource? previous;
        lock (_thumbnailRequestSync)
        {
            if (_thumbnailRequestCts is { IsCancellationRequested: false } &&
                string.Equals(_thumbnailRequestKey, requestKey, StringComparison.Ordinal))
            {
                return;
            }

            previous = _thumbnailRequestCts;
            request = new CancellationTokenSource();
            _thumbnailRequestCts = request;
            _thumbnailRequestKey = requestKey;
        }
        try { previous?.Cancel(); } catch { }

        var cancellationToken = request.Token;
        try
        {
            await Task.Delay(80, cancellationToken);
            var appsByThread = (libraryView == "favorites" ? _favoriteApps :
                    libraryView == "devices" ? _deviceApps.Concat(_loadedApps) :
                    CurrentLocalEntries())
                .Where(app => visibleIdSet.Contains(app.RequestId))
                .GroupBy(app => app.RequestId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var apps = visibleIds
                .Where(appsByThread.ContainsKey)
                .Select(threadId => appsByThread[threadId])
                .ToList();
            var downloader = Downloader();
            using var http = NetworkConfig.Current.CreateClient();
            using var previewSlots = new SemaphoreSlim(Math.Clamp(settings.PreviewThreads, 1, 8));

            async Task<string?> LoadThumbnailAsync(AppEntry app)
            {
                await previewSlots.WaitAsync(cancellationToken);
                try
                {
                    if (path != _currentPath || !IsThumbnailCandidate(app, path, favoritesView))
                    {
                        return null;
                    }
                    if (app.Type == "file" && app.LogicalChunkCount == 0)
                        app = await Registry().LoadFileDetailsAsync(app, cancellationToken);

                    var ownsLoad = false;
                    for (var attempt = 0; attempt < 5 && !ownsLoad; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ownsLoad = _thumbnailLoads.TryAdd(app.PayloadCacheKey, 0);
                        if (!ownsLoad)
                        {
                            await Task.Delay(40, cancellationToken);
                        }
                    }
                    if (!ownsLoad)
                    {
                        return null;
                    }

                    try
                    {
                        var cached = ThumbnailCache.Get(app.ThumbnailCacheKey);
                        if (cached != null)
                        {
                            return cached;
                        }

                        var previewBytes = !string.IsNullOrWhiteSpace(app.PreviewMessageId)
                            ? await downloader.DownloadMessageAttachmentBytesAsync(
                                app.ThreadId,
                                app.PreviewMessageId,
                                cancellationToken: cancellationToken,
                                encryption: app.Encryption == null
                                    ? null
                                    : new EncryptedPayloadContext(
                                        app.FileId,
                                        app.Encryption,
                                        EncryptedPayloadRoles.Preview,
                                        0), attachmentId: app.ThumbnailRef?.AttachmentId)
                            : app.Storage?.Shared == true ? null : await downloader.DownloadMarkedPreviewBytesAsync(
                                app.ThreadId,
                                "PREVIEW|",
                                cancellationToken: cancellationToken) ??
                              await downloader.DownloadPreviewBytesAsync(
                                  app.ThreadId,
                                  "image-200",
                                  cancellationToken: cancellationToken);
                        string? thumb;
                        if (previewBytes is { Length: > 0 })
                        {
                            thumb = ThumbnailCache.Store(app.ThumbnailCacheKey, previewBytes);
                        }
                        else
                        {
                            if (app.Encryption != null)
                            {
                                return null;
                            }

                            if (!ThumbnailCache.IsImage(app.Name) || app.SizeBytes > 10L * 1024 * 1024)
                            {
                                return null;
                            }

                            var url = await downloader.GetFirstAttachmentUrlAsync(app.ThreadId, cancellationToken);
                            if (string.IsNullOrWhiteSpace(url))
                            {
                                return null;
                            }

                            var data = await http.GetByteArrayAsync(url, cancellationToken);
                            thumb = ThumbnailCache.GenerateAndStore(app.PayloadCacheKey, data);
                        }

                        return thumb;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Thumbnail.Load", ex, app.ThreadId);
                        return null;
                    }
                    finally
                    {
                        _thumbnailLoads.TryRemove(app.PayloadCacheKey, out _);
                    }
                }
                finally
                {
                    previewSlots.Release();
                }
            }

            var thumbnailTasks = apps.Select(LoadThumbnailAsync).ToList();
            try
            {
                for (var index = 0; index < apps.Count; index++)
                {
                    var thumbnail = await thumbnailTasks[index];
                    if (thumbnail != null)
                    {
                        await DeliverThumbnailAsync(
                            apps[index].RequestId,
                            thumbnail,
                            path,
                            visibleIdSet,
                            cancellationToken);
                    }
                }
            }
            finally
            {
                try
                {
                    await Task.WhenAll(thumbnailTasks);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // All scheduled loads observe the superseding viewport request.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer viewport superseded this batch.
        }
        finally
        {
            lock (_thumbnailRequestSync)
            {
                if (ReferenceEquals(_thumbnailRequestCts, request))
                {
                    _thumbnailRequestCts = null;
                    _thumbnailRequestKey = "";
                }
            }
            request.Dispose();
        }
    }

    private async Task RunTrackedThumbnailRequestAsync(string visibleIdsJson, string libraryView)
    {
        var requestId = NewId();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _thumbnailRequests[requestId] = completion;
        try
        {
            if (!_loggingOut)
            {
                await Task.Run(() => LoadVisibleThumbnailsAsync(visibleIdsJson, libraryView));
            }
        }
        finally
        {
            _thumbnailRequests.TryRemove(requestId, out _);
            completion.TrySetResult();
        }
    }

    private async Task DeliverThumbnailAsync(
        string threadId,
        string dataUrl,
        string path,
        IReadOnlySet<string> requestedIds,
        CancellationToken cancellationToken)
    {
        if (path != _currentPath || !requestedIds.Contains(threadId))
        {
            return;
        }

        await _thumbnailDeliveryGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path == _currentPath)
            {
                await _bridge.SetThumbAsync(threadId, dataUrl);
                await Task.Delay(16, cancellationToken);
            }
        }
        finally
        {
            _thumbnailDeliveryGate.Release();
        }
    }
    private static List<string> ParseVisibleThumbnailIds(string visibleIdsJson)
    {
        if (string.IsNullOrWhiteSpace(visibleIdsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(visibleIdsJson, JsonOptions.Web)?
                .Where(LooksLikeDiscordId)
                .Distinct(StringComparer.Ordinal)
                .Take(120)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static HashSet<string> ParseVisibleFolderIds(string visibleFolderIdsJson)
    {
        if (string.IsNullOrWhiteSpace(visibleFolderIdsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(visibleFolderIdsJson, JsonOptions.Web)?
                .Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool IsThumbnailCandidate(AppEntry app, string path, bool favoritesView)
    {
        return app.Type == "file" &&
               (favoritesView || PathRules.Normalize(app.Path) == path) &&
               (ThumbnailCache.IsImage(app.Name) || ThumbnailCache.IsVideo(app.Name));
    }

    private async Task RunTrackedPreviewAsync(Func<Task> operation)
    {
        if (_loggingOut) return;
        var id = NewId();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _previewRequests[id] = completion;
        try { if (!_loggingOut) await Task.Run(operation); }
        finally { _previewRequests.TryRemove(id, out _); completion.TrySetResult(); }
    }

    private async Task RequestPreviewAsync(string threadId, string name)
    {
        var settings = _store.Load();
        if (!settings.PreviewEnabled)
        {
            return;
        }

        var type = PreviewType(name);
        if (type == null)
        {
            await _bridge.CallAsync("_onPreviewNotSupported");
            return;
        }

        await _bridge.CallAsync("_onPreviewLoading");
        await _bridge.CallAsync("_onPreviewProgress", 0, "locating", 0, 0);
        ClosePreview(threadId);
        var previewRequest = new CancellationTokenSource();
        _videoOpenRequests[threadId] = previewRequest;
        var cancellation = previewRequest.Token;
        DirectoryInfo? tempDir = null;
        try
        {
            var entry = await FindLoadedFileDetailsAsync(threadId, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (type == "archive" && await RegisteredFileSizeAsync(threadId) > ArchivePreviewLimit)
            {
                await SendPreviewReadyAsync("archive", name, RenderArchiveTooLargePreview(await RegisteredFileSizeAsync(threadId)));
                return;
            }

            if (type == "video")
            {
                if (entry == null) throw new InvalidOperationException("Metadados do vídeo não encontrados.");
                var low = ShouldUseVideoPreview(entry, settings) && HasVideoPreview(entry);
                var streamUrl = await GetOrCreateVideoStreamUrlAsync(entry, low, cancellation);
                var progress = await Task.Run(() => _videoProgress.Load(entry.FileId));
                cancellation.ThrowIfCancellationRequested();
                var qualities = HasVideoPreview(entry) ? new[] { "480p", "full" } : new[] { "full" };
                await SendPreviewReadyAsync("video", name, JsonSerializer.Serialize(new
                {
                    src = streamUrl, mime = low ? "video/mp4" : MediaMime(name), quality = low ? "480p" : "full",
                    qualities, fileId = entry.FileId, resumeSeconds = progress?.ResumeAt() ?? 0,
                    savedDuration = progress?.DurationSeconds ?? 0
                }, JsonOptions.Web), threadId);
                return;
            }

            tempDir = CreatePreviewTempDir(threadId);

            var file = type == "markdown"
                ? entry != null
                    ? await Downloader().DownloadAndAssembleAsync(
                        entry,
                        tempDir,
                        _ => { },
                        cancellationToken: cancellation,
                        onDetailedProgress: progress => ReportPreviewProgress(progress, "generic"))
                    : await Downloader().DownloadAndAssembleAsync(
                        threadId,
                        tempDir,
                        _ => { },
                        cancellationToken: cancellation,
                        onDetailedProgress: progress => ReportPreviewProgress(progress, "generic"))
                : await GetOrDownloadFullPreviewFileAsync(threadId, name, tempDir, "generic", cancellation);

            cancellation.ThrowIfCancellationRequested();
            switch (type)
            {
                case "image":
                    var imageUrl = _previewResources.Register(file, GuessMime(name));
                    TrackPreviewUrl(threadId, "default", imageUrl);
                    await SendPreviewReadyAsync("image", name, imageUrl, threadId);
                    break;
                case "pdf":
                    var pdfUrl = _previewResources.Register(file, "application/pdf");
                    TrackPreviewUrl(threadId, "default", pdfUrl);
                    await SendPreviewReadyAsync("pdf", name, JsonSerializer.Serialize(new { threadId, directUrl = pdfUrl, totalPages = 0, batchSize = 0 }, JsonOptions.Web));
                    break;
                case "markdown":
                    await SendPreviewReadyAsync("markdown", name, await File.ReadAllTextAsync(file.FullName, Encoding.UTF8));
                    break;
                case "text":
                    using (var textReader = new StreamReader(OpenPreviewRead(file)))
                        await SendPreviewReadyAsync("text", name, await textReader.ReadToEndAsync(cancellation));
                    break;
                case "document":
                    await SendPreviewReadyAsync("document", name, ExtractDocumentPreview(file, name));
                    break;
                case "archive":
                    await SendPreviewReadyAsync("archive", name, RenderArchivePreview(file, name));
                    break;
                case "video":
                case "audio":
                    var url = _previewResources.Register(file, MediaMime(name));
                    TrackPreviewUrl(threadId, type == "video" ? "full" : "default", url);
                    var qualities = type == "video" && HasVideoPreview(entry)
                        ? new[] { "480p", "full" }
                        : new[] { "full" };
                    await SendPreviewReadyAsync(
                        type,
                        name,
                        JsonSerializer.Serialize(
                            type == "video"
                                ? new { src = url, mime = MediaMime(name), quality = "full", qualities }
                                : new { src = url, mime = MediaMime(name), quality = "full", qualities = new[] { "full" } },
                            JsonOptions.Web));
                    break;
            }

            if (tempDir != null)
            {
                CleanDir(tempDir);
                tempDir = null;
            }
        }
        catch (OperationCanceledException) { if (tempDir != null) CleanDir(tempDir); }
        catch (Exception ex)
        {
            if (tempDir != null)
            {
                CleanDir(tempDir);
            }

            await _bridge.CallAsync("_onPreviewError", ex.Message);
        }
    }

    private async Task ClearPreviewCacheAsync()
    {
        foreach (var threadId in _previewUrls.Keys
                     .Select(key => key.Split('|', 2)[0])
                     .Distinct(StringComparer.Ordinal)
                     .ToArray())
        {
            ClosePreview(threadId);
        }

        foreach (var threadId in _previewDirs.Keys.ToArray())
        {
            ClosePreview(threadId);
        }

        ThumbnailCache.Clear();
        _store.ClearPreviewCache();
        await _bridge.CallAsync("_onPreviewCacheCleared");
        await SendPreviewCacheSizeAsync();
    }

    private void ClosePreview(string threadId)
    {
        if (_videoOpenRequests.TryRemove(threadId, out var request)) { request.Cancel(); request.Dispose(); }
        var entry = FindLoadedEntry(threadId);
        if (entry != null) _videoProgress.Flush(entry.FileId);
        foreach (var key in _videoSessions.Keys.Where(key => key.StartsWith(threadId + "|", StringComparison.Ordinal)).ToArray())
            if (_videoSessions.TryRemove(key, out var video)) _ = video.DisposeAsync();
        foreach (var key in _previewUrls.Keys.Where(key => key.StartsWith(threadId + "|", StringComparison.Ordinal)).ToArray())
        {
            if (_previewUrls.TryRemove(key, out var url))
            {
                _previewResources.RemoveUrl(url);
            }
        }

        if (_previewDirs.TryRemove(threadId, out var dir))
        {
            CleanDir(dir);
        }
    }

    private async Task OpenPreviewMediaAsync(string threadId)
    {
        var url = GetPreviewUrl(threadId, "full") ?? GetPreviewUrl(threadId, "default") ?? GetPreviewUrl(threadId, "480p");
        FileInfo? file = null;
        if (url != null)
        {
            file = _previewResources.GetFileFromUrl(url);
            if (file?.Exists != true)
            {
                file = null;
            }
        }

        if (file == null)
        {
            var entry = FindLoadedEntry(threadId);
            if (entry == null || !entry.Type.Equals("file", StringComparison.OrdinalIgnoreCase))
            {
                await ErrorAsync("Arquivo não encontrado para abrir em tela cheia.");
                return;
            }

            await _bridge.CallAsync("_onPreviewProgress", 0, "full", 0, Math.Max(0, entry.SizeBytes));
            var dir = _previewDirs.GetOrAdd(threadId, _ => CreatePreviewTempDir(threadId));
            file = await GetOrDownloadFullPreviewFileAsync(threadId, entry.Name, dir, "full");
            var fullUrl = _previewResources.Register(file, GuessFileMime(entry.Name));
            TrackPreviewUrl(threadId, "full", fullUrl);
        }

        if (EncryptedPreviewCache.IsEncrypted(file))
        {
            var dir = _previewDirs.GetOrAdd(threadId, _ => CreatePreviewTempDir(threadId));
            var readable = new FileInfo(Path.Combine(dir.FullName, Path.GetFileName(FindLoadedEntry(threadId)?.Name ?? "preview")));
            await using (var input = EncryptedPreviewCache.OpenRead(file))
            await using (var output = readable.Create()) await input.CopyToAsync(output);
            file = readable;
        }
        Process.Start(new ProcessStartInfo(file.FullName) { UseShellExecute = true });
    }

    private async Task RequestVideoQualityAsync(string threadId, string name, string quality)
    {
        if (_loggingOut || !_videoOpenRequests.TryGetValue(threadId, out var opening)) return;
        var cancellation = opening.Token;
        quality = quality.Equals("480p", StringComparison.OrdinalIgnoreCase) ? "480p" : "full";
        var existing = GetPreviewUrl(threadId, quality);
        if (existing != null)
        {
            await _bridge.CallAsync("_onVideoQualityReady", threadId, quality, existing, quality == "480p" ? "video/mp4" : MediaMime(name));
            return;
        }

        var entry = await FindLoadedFileDetailsAsync(threadId, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (entry != null)
        {
            var streamUrl = await GetOrCreateVideoStreamUrlAsync(entry, quality == "480p", cancellation);
            foreach (var key in _videoSessions.Keys.Where(key => key.StartsWith(threadId + "|", StringComparison.Ordinal) && key != threadId + "|" + quality).ToArray())
            {
                if (_videoSessions.TryRemove(key, out var old)) await old.DisposeAsync();
                if (_previewUrls.TryRemove(key, out var oldUrl)) _previewResources.RemoveUrl(oldUrl);
            }
            await _bridge.CallAsync("_onVideoQualityReady", threadId, quality, streamUrl, quality == "480p" ? "video/mp4" : MediaMime(name));
            return;
        }
        throw new InvalidOperationException("Metadados do vídeo não encontrados.");
    }

    private async Task RequestVideoQualitySafeAsync(string threadId, string name, string quality)
    {
        try
        {
            await RequestVideoQualityAsync(threadId, name, quality);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await _bridge.CallAsync("_onVideoQualityError", threadId, ex.Message);
        }
    }

    private void TrackPreviewUrl(string threadId, string slot, string url)
    {
        _previewUrls[threadId + "|" + slot] = url;
    }

    private string? GetPreviewUrl(string threadId, string slot)
    {
        return _previewUrls.TryGetValue(threadId + "|" + slot, out var url) ? url : null;
    }

    private AppEntry? FindLoadedEntry(string threadId)
    {
        var entries = LoadedEntriesForLookup().ToList();
        var exact = entries.FirstOrDefault(app => app.RequestId == threadId || app.FileId == threadId);
        if (exact != null) return exact;
        // A physical thread is a legacy locator only when it identifies one entry.
        var legacy = entries.Where(app => app.ThreadId == threadId && app.Storage?.Shared != true).Take(2).ToList();
        return legacy.Count == 1 ? legacy[0] : null;
    }

    private async Task<AppEntry?> FindLoadedFileDetailsAsync(string threadId, CancellationToken cancellationToken = default)
    {
        var entry = FindLoadedEntry(threadId);
        return entry == null ? null : await Registry().LoadFileDetailsAsync(entry, cancellationToken);
    }

    private static bool ShouldUseVideoPreview(AppEntry? entry, AppSettings settings)
    {
        return entry != null &&
               !settings.VideoPreviewQuality.Equals("original", StringComparison.OrdinalIgnoreCase) &&
               ThumbnailCache.IsVideo(entry.Name);
    }

    private static bool HasVideoPreview(AppEntry? entry)
    {
        return entry?.VideoPreviewChunkReferences != null || entry?.VideoPreviewChunkMessageIds.Count > 0 || !string.IsNullOrWhiteSpace(entry?.VideoPreviewMessageId);
    }


    private async Task<string> GetOrCreateVideoStreamUrlAsync(AppEntry entry, bool lowQuality, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (_loggingOut) throw new OperationCanceledException();
        var quality = lowQuality ? "480p" : "full";
        var existing = GetPreviewUrl(entry.RequestId, quality);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var provider = await Downloader().OpenVideoChunksAsync(entry, lowQuality, cancellation);
        var video = new VideoStreamSession(provider, _store.Load().VideoPrefetchChunks);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var url = _previewResources.RegisterVideo(video, lowQuality ? "video/mp4" : MediaMime(entry.Name));
            _videoSessions[entry.RequestId + "|" + quality] = video;
            TrackPreviewUrl(entry.RequestId, quality, url);
            video.Failed += ex =>
            {
                AppLogger.Error("Video.Stream", new IOException("Falha permanente ao carregar bytes autenticados do vídeo."));
                if (GetPreviewUrl(entry.RequestId, quality) == url)
                    _ = _bridge.CallAsync("_onVideoStreamError", entry.RequestId, ex is EncryptedPayloadException ? ex.Message : "Não foi possível carregar o vídeo. Verifique a conexão e tente abrir novamente.");
            };
            return url;
        }
        catch { await video.DisposeAsync(); throw; }
    }

    private Task UpdateVideoPlaybackAsync(JsonElement[] args)
    {
        if (_loggingOut) return Task.CompletedTask;
        var threadId = ArgString(args, 0);
        var entry = FindLoadedEntry(threadId);
        if (entry == null || entry.FileId != ArgString(args, 1)) return Task.CompletedTask;
        if (args.Length < 4 || !args[2].TryGetDouble(out var position) || !args[3].TryGetDouble(out var duration)) return Task.CompletedTask;
        foreach (var pair in _videoSessions.Where(pair => pair.Key.StartsWith(threadId + "|", StringComparison.Ordinal)))
            pair.Value.SetPlaying(ArgBool(args, 4, false), ArgBool(args, 6, false));
        _videoProgress.Update(entry.FileId, position, duration, ArgBool(args, 5, false));
        return Task.CompletedTask;
    }
    private async Task<FileInfo> GetOrDownloadFullPreviewFileAsync(
        string threadId,
        string name,
        DirectoryInfo tempDir,
        string phase,
        CancellationToken cancellationToken = default)
    {
        var cached = _store.GetCachedPreviewFile(threadId, "full", name);
        if (cached != null)
        {
            var length = EncryptedPreviewCache.PlaintextLength(cached);
            await _bridge.CallAsync("_onPreviewProgress", 100, "cache", length, length);
            return cached;
        }

        var entry = await FindLoadedFileDetailsAsync(threadId, cancellationToken);
        var downloaded = entry != null
            ? await Downloader().DownloadAndAssembleAsync(
                entry,
                tempDir,
                _ => { },
                cancellationToken,
                onDetailedProgress: progress => ReportPreviewProgress(progress, phase))
            : await Downloader().DownloadAndAssembleAsync(
                threadId,
                tempDir,
                _ => { },
                cancellationToken,
                onDetailedProgress: progress => ReportPreviewProgress(progress, phase));
        var stored = await CopyToPreviewCacheAsync(downloaded, threadId, "full", name, cancellationToken);
        await SendPreviewCacheSizeAsync();
        return stored;
    }

    private async Task<FileInfo?> GetOrDownloadVideoPreview480Async(
        string threadId,
        string name,
        AppEntry? entry,
        DirectoryInfo tempDir)
    {
        var cached = _store.GetCachedPreviewFile(threadId, "480p", VideoPreviewCacheName(name));
        if (cached != null)
        {
            var length = EncryptedPreviewCache.PlaintextLength(cached);
            await _bridge.CallAsync("_onPreviewProgress", 100, "cache", length, length);
            return cached;
        }

        var preview = entry?.VideoPreviewChunkReferences != null || entry?.VideoPreviewChunkMessageIds.Count > 0
            ? await Downloader().DownloadVideoPreview480Async(
                entry,
                tempDir,
                progress => ReportPreviewProgress(progress, "video480"))
            : LooksLikeDiscordId(entry?.VideoPreviewMessageId ?? "")
                ? await Downloader().DownloadMessageAttachmentAsync(
                    entry!.ThreadId,
                    entry.VideoPreviewMessageId,
                    tempDir,
                    progress => ReportPreviewProgress(progress, "video480"),
                    encryption: entry.Encryption == null
                        ? null
                        : new EncryptedPayloadContext(
                            entry.FileId,
                            entry.Encryption,
                            EncryptedPayloadRoles.Video480p,
                            0),
                    outputName: "preview_480p_" + name)
            : entry?.Storage?.Shared == true ? null : await Downloader().DownloadMarkedPreviewFileAsync(
                entry?.ThreadId ?? threadId,
                "VIDEO_PREVIEW_480P|",
                tempDir,
                progress => ReportPreviewProgress(progress, "video480")) ??
              await Downloader().DownloadPreviewAndAssembleAsync(
                entry?.ThreadId ?? threadId,
                "video-480",
                tempDir,
                progress => ReportPreviewProgress(progress, "video480"));
        if (preview == null)
        {
            return null;
        }

        var stored = await CopyToPreviewCacheAsync(preview, threadId, "480p", VideoPreviewCacheName(name));
        await SendPreviewCacheSizeAsync();
        return stored;
    }

    private async Task<FileInfo> CopyToPreviewCacheAsync(FileInfo source, string threadId, string slot, string name, CancellationToken cancellationToken = default)
    {
        if (_loggingOut) throw new OperationCanceledException();
        var target = _store.GetPreviewCacheFile(threadId, slot, name);
        target.Directory?.Create();
        if (!SamePath(source, target))
        {
            await using var input = source.OpenRead();
            await EncryptedPreviewCache.StoreAsync(target, input, cancellationToken);
        }

        target.Refresh();
        return target;
    }

    private Task SendPreviewCacheSizeAsync()
    {
        return _bridge.CallAsync("_onPreviewCacheSize", _store.GetPreviewCacheSizeBytes());
    }

    private void ReportPreviewProgress(DownloadProgress progress, string phase)
    {
        _ = _bridge.CallAsync(
            "_onPreviewProgress",
            progress.Percent,
            phase,
            progress.DownloadedBytes,
            progress.TotalBytes);
    }

    private async Task CreateFolderAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await SetBackgroundActivityAsync("Em andamento...", true);
        try
        {
            var clean = PathRules.CleanFileName(name.Trim());
            var registry = Registry();
            var all = CurrentLocalEntries();
            var folder = AppEntry.Folder(clean, _currentPath);
            folder.ParentFolderId = FolderIdForPath(all, _currentPath);
            var requestedFolderId = folder.FolderId;
            await registry.AddEntryAsync(folder);
            var latest = CurrentLocalEntries();
            ReplaceLocalEntry(latest, folder);
            await PublishLocalRegistryAsync(registry, latest, incremental: true, changedEntry: folder);
            await _bridge.CallAsync(
                "_onStatus",
                folder.FolderId.Equals(requestedFolderId, StringComparison.OrdinalIgnoreCase)
                    ? "OK Pasta criada."
                    : "OK A pasta já existe.",
                false);
        }
        finally
        {
            await SetBackgroundActivityAsync("", false);
        }
    }

    private async Task MoveItemsAsync(string idsJson, string newPath)
    {
        var ids = (JsonSerializer.Deserialize<string[]>(idsJson, JsonOptions.Web) ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            await ErrorAsync("Nenhum item selecionado.");
            return;
        }

        var registry = Registry();
        await using var serialBatch = registry.BeginSerialBatch();
        var all = CurrentLocalEntries();
        var opId = NewId();
        await OpsAddAsync(opId, "move", ids.Count == 1 ? "Movendo item" : "Movendo itens");

        var moved = 0;
        try
        {
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var entry = all.FirstOrDefault(a => a.MetadataMessageId == id);
                if (entry == null)
                {
                    await OpsProgressAsync(opId, Percent(i + 1, ids.Count), $"Ignorado ({i + 1}/{ids.Count})");
                    continue;
                }

                await OpsProgressAsync(opId, Percent(i, ids.Count), $"Movendo {i + 1}/{ids.Count}: {entry.Name}");
                await MoveEntryAsync(registry, all, entry, PathRules.Normalize(newPath));
                moved++;
                await OpsProgressAsync(opId, Percent(i + 1, ids.Count), $"Movido {i + 1}/{ids.Count}: {entry.Name}");
            }

            await serialBatch.DisposeAsync();
            await OpsUpdateAsync(opId, "done", $"OK {moved} item(ns) movido(s).");
            await _bridge.CallAsync("_onStatus", $"OK {moved} item(ns) movido(s).", false);
            await PublishLocalRegistryAsync(registry, all);
        }
        catch (Exception ex)
        {
            await OpsUpdateAsync(opId, "error", ex.Message);
            throw;
        }
    }

    private static string MovedChildPath(string oldFolderPath, string newFolderPath, string childPath)
    {
        return PathRules.Normalize(newFolderPath + childPath[oldFolderPath.Length..]);
    }

    private async Task MoveEntryAsync(AppRegistry registry, List<AppEntry> all, AppEntry entry, string newPath)
    {
        var targetFolderId = FolderIdForPath(all.Where(candidate => !ReferenceEquals(candidate, entry)), newPath)
            ?? AppRegistry.RootFolderId;
        var oldFolderPath = entry.Type == "folder" ? PathRules.ChildPath(entry) : "";
        await registry.MoveEntryAsync(entry, targetFolderId, newPath);
        if (entry.Type == "folder")
        {
            var newFolderPath = PathRules.Normalize(newPath == "/" ? "/" + entry.Name : newPath + "/" + entry.Name);
            foreach (var child in all.Where(child => !ReferenceEquals(child, entry)).ToList())
            {
                var childPath = PathRules.Normalize(child.Path);
                if (childPath == oldFolderPath || childPath.StartsWith(oldFolderPath + "/", StringComparison.Ordinal))
                {
                    child.Path = MovedChildPath(oldFolderPath, newFolderPath, childPath);
                }
            }
        }
    }

    private async Task UpdateFolderColorAsync(string metadataMessageId, string color)
    {
        var safeColor = Regex.IsMatch(color ?? "", "^#[0-9a-fA-F]{6}$") ? color : "#C4C7C5";

        var registry = Registry();
        var all = CurrentLocalEntries();
        var entry = all.FirstOrDefault(a => a.MetadataMessageId == metadataMessageId && a.Type == "folder");
        if (entry == null)
        {
            await ErrorAsync("Pasta não encontrada.");
            return;
        }

        entry.FolderColor = (safeColor ?? "#C4C7C5").ToUpperInvariant();
        await registry.UpdateEntryAsync(entry);
        await PublishLocalRegistryAsync(registry, all);
        await _bridge.CallAsync("_onStatus", "OK Cor da pasta atualizada.", false);
    }

    private async Task RenameItemAsync(string metadataMessageId, string newName)
    {
        if (string.IsNullOrWhiteSpace(metadataMessageId))
        {
            await ErrorAsync("Item inválido para renomear.");
            return;
        }

        var clean = PathRules.CleanFileName(newName.Trim());
        if (string.IsNullOrWhiteSpace(clean))
        {
            await ErrorAsync("Nome inválido.");
            return;
        }

        var registry = Registry();
        await using var serialBatch = registry.BeginSerialBatch();
        var all = CurrentLocalEntries();
        var entry = all.FirstOrDefault(a => a.MetadataMessageId == metadataMessageId);
        if (entry == null)
        {
            await ErrorAsync("Item não encontrado.");
            return;
        }

        if (entry.Name == clean)
        {
            await _bridge.CallAsync("_onStatus", "OK Item renomeado.", false);
            return;
        }

        if (IsFolderEntry(entry))
        {
            var oldFolderPath = PathRules.ChildPath(entry);
            var parentPath = PathRules.Normalize(entry.Path);
            var newFolderPath = PathRules.Normalize(parentPath == "/" ? "/" + clean : parentPath + "/" + clean);
            entry.Name = clean;
            entry.ModifiedAt = TransferState.Now();
            await registry.UpdateEntryAsync(entry);
            foreach (var child in all.Where(child => !ReferenceEquals(child, entry) && IsInsideFolder(child, oldFolderPath)))
            {
                child.Path = MovedChildPath(oldFolderPath, newFolderPath, PathRules.Normalize(child.Path));
            }
        }
        else
        {
            entry = await registry.LoadFileDetailsAsync(entry, forceRefresh: true);
            ReplaceLocalEntry(all, entry);
            entry.Name = clean;
            entry.ModifiedAt = TransferState.Now();
            await registry.UpdateEntryAsync(entry);
        }

        await serialBatch.DisposeAsync();
        await PublishLocalRegistryAsync(registry, all);
        await _bridge.CallAsync("_onStatus", "OK Item renomeado.", false);
    }

    private async Task StartUploadDialogAsync()
    {
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Selecionar arquivos",
                Multiselect = true
            };

            if (dialog.ShowDialog(_owner) == true)
            {
                _ = RunAsync(() => UploadPathsAsync(dialog.FileNames));
            }
        });
    }

    private async Task StartFolderUploadDialogAsync()
    {
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Selecionar pasta para upload"
            };

            if (dialog.ShowDialog(_owner) == true)
            {
                _ = RunAsync(() => UploadPathsAsync([dialog.FolderName]));
            }
        });
    }

    private async Task UploadPathsAsync(IEnumerable<string> paths)
    {
        using var networkWait = NetworkRetryPolicy.TransferScope();
        var basePath = _currentPath;
        var tasks = new List<UploadTask>();
        var folderCount = 0;
        var skipped = 0;
        var registry = Registry();
        await using var serialBatch = registry.BeginSerialBatch();
        await using var indexBatch = await registry.BeginUploadIndexBatchAsync();
        var all = CurrentLocalEntries();
        var serialFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                var rootFolder = await CreateUploadFolderEntryAsync(registry, all, dir.Name, basePath, serialFolderIds);
                folderCount++;
                skipped += await CollectFolderUploadTasksAsync(
                    registry,
                    all,
                    dir,
                    PathRules.ChildPath(rootFolder),
                    tasks,
                    serialFolderIds,
                    () => folderCount++);
            }
            else if (File.Exists(path))
            {
                tasks.Add(new UploadTask(new FileInfo(path), basePath));
            }
            else
            {
                skipped++;
            }
        }

        if (tasks.Count == 0)
        {
            if (folderCount > 0)
            {
                await registry.SynchronizeFolderSerialsAsync(serialFolderIds);
                await serialBatch.DisposeAsync();
                await PublishLocalRegistryAsync(registry, CurrentLocalEntries(), incremental: true);
                await _bridge.CallAsync("_onStatus", "OK Pasta criada sem arquivos para enviar.", false);
            }
            else
            {
                await ErrorAsync("Nenhum arquivo acessivel para upload.");
            }

            return;
        }

        var resolved = await ResolveUploadConflictsAsync(tasks, all);
        tasks = resolved.Tasks;

        if (tasks.Count == 0)
        {
            await registry.SynchronizeFolderSerialsAsync(serialFolderIds);
            await PublishLocalRegistryAsync(registry, CurrentLocalEntries());
            await _bridge.CallAsync("_onStatus", "OK Upload cancelado.", false);
            return;
        }

        var opId = NewId();
        var cancellationToken = BeginTransferOperation(opId);
        await OpsUploadBatchAsync(opId, tasks.Select(task => task.File.Name).ToArray(), tasks.Select(task => task.File.Length).ToArray());
        if (skipped > 0)
        {
            await OpsProgressAsync(opId, 0, $"{skipped} item(ns) sem acesso foram ignorados");
        }

        try
        {
            await using var uploadPriority = registry.SharedStorage.PrioritizeUploads();
            tasks = await PlanSharedUploadsAsync(tasks, all, registry, cancellationToken);
            await RunUploadQueueAsync(opId, tasks, all, serialFolderIds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await OpsUpdateAsync(opId, "cancelled", "Cancelado");
            await _bridge.CallAsync("_onStatus", "Upload cancelado.", false);
        }
        finally
        {
            try
            {
                await registry.SynchronizeFolderSerialsAsync(serialFolderIds, CancellationToken.None);
                await serialBatch.DisposeAsync();
                await PublishLocalRegistryAsync(registry, CurrentLocalEntries(), incremental: true);
            }
            finally
            {
                EndTransferOperation(opId);
            }
        }
    }

    private void ResolveUploadConflict(JsonElement[] args)
    {
        var requestId = ArgString(args, 0);
        var action = NormalizeUploadConflictAction(ArgString(args, 1, "keep"));
        var applyToAll = ArgBool(args, 2, false);
        if (!string.IsNullOrWhiteSpace(requestId) && _uploadConflictResponses.TryRemove(requestId, out var response))
        {
            response.TrySetResult(new UploadConflictResponse(action, applyToAll));
        }
    }

    private async Task<UploadConflictResolution> ResolveUploadConflictsAsync(
        IReadOnlyList<UploadTask> input,
        IReadOnlyList<AppEntry> existingEntries)
    {
        var existingByKey = existingEntries
            .Where(entry => !IsFolderEntry(entry))
            .GroupBy(entry => UploadConflictKey(entry.Name, entry.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var conflictTotal = CountUploadConflicts(input, existingByKey);
        if (conflictTotal == 0)
        {
            return new UploadConflictResolution(input.ToList());
        }

        var tasks = new List<UploadTask>();
        var acceptedByKey = new Dictionary<string, List<UploadTask>>(StringComparer.OrdinalIgnoreCase);
        string? applyAllAction = null;
        var conflictIndex = 0;

        void Accept(string key, UploadTask task)
        {
            tasks.Add(task);
            if (!acceptedByKey.TryGetValue(key, out var accepted))
            {
                accepted = [];
                acceptedByKey[key] = accepted;
            }

            accepted.Add(task);
        }

        foreach (var task in input)
        {
            var cleanName = CleanUploadName(task.File.Name);
            var normalizedPath = PathRules.Normalize(task.Path);
            var key = UploadConflictKey(cleanName, normalizedPath);
            existingByKey.TryGetValue(key, out var existing);
            acceptedByKey.TryGetValue(key, out var batchConflicts);

            existing ??= [];
            batchConflicts ??= [];
            if (existing.Count == 0 && batchConflicts.Count == 0)
            {
                Accept(key, task);
                continue;
            }

            conflictIndex++;
            var action = applyAllAction;
            if (string.IsNullOrWhiteSpace(action))
            {
                var response = await AskUploadConflictAsync(
                    task,
                    existing.Count,
                    batchConflicts.Count,
                    conflictIndex,
                    conflictTotal);
                action = response.Action;
                if (response.ApplyToAll)
                {
                    applyAllAction = action;
                }
            }

            if (action == "overwrite")
            {
                FileReplacement? replacement = batchConflicts.LastOrDefault()?.Replacement;
                if (existing.Count > 0)
                {
                    var current = await Registry().LoadFileDetailsAsync(existing[0], forceRefresh: true);
                    replacement = FileVersioning.PrepareReplacement(current);
                }

                if (batchConflicts.Count > 0)
                {
                    tasks.RemoveAll(candidate => batchConflicts.Any(conflict => ReferenceEquals(conflict, candidate)));
                    batchConflicts.Clear();
                }

                Accept(key, task with { Replacement = replacement });
            }
            else if (action == "keep")
            {
                Accept(key, task);
            }
        }

        return new UploadConflictResolution(tasks);
    }

    private async Task<UploadConflictResponse> AskUploadConflictAsync(
        UploadTask task,
        int existingCount,
        int batchConflictCount,
        int conflictIndex,
        int conflictTotal)
    {
        var requestId = NewId();
        var response = new TaskCompletionSource<UploadConflictResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uploadConflictResponses[requestId] = response;

        var payload = new
        {
            fileName = CleanUploadName(task.File.Name),
            path = PathRules.Normalize(task.Path),
            existingCount,
            batchConflictCount,
            conflictIndex,
            conflictTotal,
            multipleConflicts = conflictTotal > 1
        };

        await _bridge.CallAsync("_onUploadConflict", requestId, payload);
        var completed = await Task.WhenAny(response.Task, Task.Delay(TimeSpan.FromMinutes(10)));
        if (completed == response.Task)
        {
            return await response.Task;
        }

        _uploadConflictResponses.TryRemove(requestId, out _);
        return new UploadConflictResponse("skip", false);
    }

    private static int CountUploadConflicts(
        IReadOnlyList<UploadTask> input,
        IReadOnlyDictionary<string, List<AppEntry>> existingByKey)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var task in input)
        {
            var key = UploadConflictKey(CleanUploadName(task.File.Name), task.Path);
            if (existingByKey.ContainsKey(key) || !seen.Add(key))
            {
                count++;
            }
        }

        return count;
    }

    private static string UploadConflictKey(string name, string path)
    {
        return PathRules.Normalize(path) + "\u001f" + CleanUploadName(name);
    }

    private static string NormalizeUploadConflictAction(string action)
    {
        return action.Equals("overwrite", StringComparison.OrdinalIgnoreCase)
            ? "overwrite"
            : action.Equals("skip", StringComparison.OrdinalIgnoreCase)
                ? "skip"
                : "keep";
    }

    private async Task RunUploadQueueAsync(
        string opId,
        List<UploadTask> tasks,
        List<AppEntry> allEntries,
        ISet<string> serialFolderIds,
        CancellationToken cancellationToken)
    {
        using var networkWait = NetworkRetryPolicy.TransferScope(cancellationToken);
        var settings = _store.Load();
        var uploader = new ForumUploader(RequireApi(), settings.AppsForumId, Organessonium.Services.MetadataEncryption.MetadataCodec.ForEncryption(_encryption, () => _store.Load().EncryptionMode));
        var registry = Registry();
        var entrySnapshot = allEntries.ToList();
        await using var uploadPriority = registry.SharedStorage.PrioritizeUploads();
        var uploadedEntries = new ConcurrentDictionary<int, AppEntry>();
        var done = 0;
        var failed = 0;
        var cancelled = 0;
        var active = 0;
        var publishing = 0;
        var stoppedByApi = 0;
        var fileParallelism = Math.Min(Math.Clamp(settings.MaxParallelUploads, 1, 8), tasks.Count);
        using var originalFiles = new SemaphoreSlim(Math.Max(1, fileParallelism), Math.Max(1, fileParallelism));
        using var smallOriginals = new SemaphoreSlim(DiscordLimits.MaxActiveSharedOriginalFiles, DiscordLimits.MaxActiveSharedOriginalFiles);
        using var pendingFiles = new SemaphoreSlim(DiscordLimits.MaxPendingUploadFiles, DiscordLimits.MaxPendingUploadFiles);
        await using var poolWrites = await registry.SharedStorage.HoldWritesAsync(tasks.Where(task => task.PreparedTransfer?.Storage?.Shared == true)
            .Select(task => task.PreparedTransfer!.ThreadId), cancellationToken);
        var uploadWorkers = settings.EffectiveUploadThreads;
        var partsPerFile = UploadConcurrencyPlanner.PartsPerFile(uploadWorkers, fileParallelism, settings.ForceUploadParallelism);
        using var uploadGate = new AdaptiveUploadGate(uploadWorkers, adaptive: !settings.ForceUploadParallelism);
        using var liveUpload = UploadConcurrencyRegistry.Register(uploadGate);
        using var memoryCoordinator = new UploadMemoryCoordinator(uploadWorkers, forceParallelism: settings.ForceUploadParallelism);
        AppLogger.Debug(
            "Upload.Concurrency",
            $"Upload batch configured with {uploadWorkers} network workers, {fileParallelism} regular files, {DiscordLimits.MaxActiveSharedOriginalFiles} shared descriptors and {partsPerFile} parts per regular file.");
        var transferActivity = new TransferActivityClock();
        var totalBytes = tasks.Sum(task => Math.Max(0, task.File.Length));
        long uploadedBytes = 0;
        long transmittedBytes = 0;
        var fileCancellations = tasks.Select(_ => CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)).ToArray();
        for (var index = 0; index < fileCancellations.Length; index++)
            _uploadFileOperations[opId + ":" + index] = fileCancellations[index];

        var producers = tasks.Select((task, index) => task.PreparedTransfer is { Storage.Shared: true } planned && planned.UploadedChunks.Count < planned.TotalChunks
            ? registry.SharedStorage.Batcher.ExpectProduction(new SharedStorageReservation(planned.FileId, planned.TransferId,
                planned.ThreadId, planned.Storage), EncryptedPayloadRoles.Original, fileCancellations[index].Token,
                waitingForPublication: () => Volatile.Read(ref publishing) > 0) : null).ToArray();
        Task RunWorkersAsync() => Task.WhenAll(Enumerable.Range(0, tasks.Count).Select(index =>
            RunPausableUploadAsync(opId, index, tasks[index], fileCancellations[index].Token, async (task, fileToken) =>
        {
            var admission = task.PreparedTransfer is { Storage.Shared: true, TotalChunks: 1 } ? smallOriginals : originalFiles;
            var originalHeld = 0;
            var pendingHeld = false;
            var activeCounted = false;
            var publishingCounted = 0;
            void ReleaseOriginal()
            {
                if (Interlocked.Exchange(ref originalHeld, 0) == 1) admission.Release();
            }
            try
            {
                await admission.WaitAsync(fileToken);
                originalHeld = 1;
                await pendingFiles.WaitAsync(fileToken);
                pendingHeld = true;
                producers[index]?.Activate();
                if (Volatile.Read(ref stoppedByApi) != 0) return;
                fileToken.ThrowIfCancellationRequested();
                var activeNow = Interlocked.Increment(ref active);
                activeCounted = true;
                await OpsUploadFileProgressAsync(opId, index, "active", 0, "Preparando...");
                await OpsProgressAsync(opId, Percent(Volatile.Read(ref done) + Volatile.Read(ref failed), tasks.Count), $"{activeNow} upload(s) ativo(s)");
                var uploaded = await Task.Run(() => UploadSingleAsync(
                    opId,
                    index,
                    tasks.Count,
                    task.File,
                    task.Path,
                    entrySnapshot,
                    uploader,
                    registry,
                    settings,
                    new UiThrottle(),
                    fileToken,
                    existingTransferState: task.PreparedTransfer,
                    adaptiveUploadGate: uploadGate,
                    memoryCoordinator: memoryCoordinator,
                    maxParallelPartsPerFile: partsPerFile,
                    useBatchUi: true,
                    onBytesUploaded: async (bytes, wireBytes) =>
                    {
                        var currentBytes = Interlocked.Add(ref uploadedBytes, Math.Max(0, bytes));
                        var currentWireBytes = Interlocked.Add(ref transmittedBytes, Math.Max(0, wireBytes));
                        await OpsTransferMetricsAsync(
                            opId,
                            "upload",
                            Volatile.Read(ref done),
                            tasks.Count,
                            currentBytes,
                            totalBytes,
                            transferActivity.ElapsedSeconds,
                            currentWireBytes);
                    },
                    transferActivity: transferActivity,
                    deferSerialSynchronization: true,
                    replacement: task.Replacement,
                    onPreviewSize: size => Interlocked.Add(ref totalBytes, size), scheduledTransfer: task.PreparedTransfer != null && !task.Resuming,
                    onOriginalUploaded: ReleaseOriginal,
                    onPublishingMetadata: () =>
                    {
                        if (_transferPauses.TryGetValue(TransferKey(opId, index), out var pause)) pause.FinishPublication();
                        if (Interlocked.Exchange(ref publishingCounted, 1) == 0) Interlocked.Increment(ref publishing);
                    }), fileToken);
                uploadedEntries[index] = uploaded;
                Task publication;
                lock (allEntries)
                {
                    ReplaceLocalEntry(allEntries, uploaded);
                    var visibleEntries = CurrentLocalEntries();
                    ReplaceLocalEntry(visibleEntries, uploaded);
                    publication = PublishLocalRegistryAsync(registry, visibleEntries, refreshStatistics: false, incremental: true, changedEntry: uploaded);
                }
                await publication;
                var completed = Interlocked.Increment(ref done);
                await OpsUploadFileProgressAsync(opId, index, "done", 100, "Concluído");
                await OpsTransferMetricsAsync(
                    opId,
                    "upload",
                    completed,
                    tasks.Count,
                    Interlocked.Read(ref uploadedBytes),
                    totalBytes,
                    transferActivity.ElapsedSeconds,
                    Interlocked.Read(ref transmittedBytes));
                await OpsProgressAsync(opId, Percent(completed + Volatile.Read(ref failed), tasks.Count), $"{completed}/{tasks.Count} concluídos");
            }
            catch (OperationCanceledException)
            {
                if (PauseRequested(opId, index) && !fileCancellations[index].IsCancellationRequested) throw;
                await OpsUploadFileProgressAsync(opId, index, "cancelled", -1, "Cancelado");
                Interlocked.Increment(ref cancelled);
                if (cancellationToken.IsCancellationRequested) throw;
            }
            catch (Exception error) when (TransferMemoryPressureException.Find(error) is { } pressure) { throw pressure; }
            catch (Exception ex)
            {
                Debug.WriteLine("[Upload] " + ex.Message);
                AppLogger.Error("UploadSingle", ex, task.File.FullName);
                var blockedByApi = IsBlockingUploadApiError(ex);
                if (blockedByApi)
                {
                    Volatile.Write(ref stoppedByApi, 1);
                    foreach (var producer in producers) producer?.Dispose();
                }

                var failedNow = Interlocked.Increment(ref failed);
                var message = FriendlyUploadError(ex);
                await OpsUploadFileProgressAsync(opId, index, blockedByApi ? "waiting" : "error", -1, message);
                await OpsProgressAsync(opId, Percent(Volatile.Read(ref done) + failedNow, tasks.Count), $"{failedNow} upload(s) falharam");
            }
            finally
            {
                ReleaseOriginal();
                producers[index]?.Dispose();
                if (pendingHeld) pendingFiles.Release();
                if (activeCounted) Interlocked.Decrement(ref active);
                if (Interlocked.Exchange(ref publishingCounted, 0) != 0) Interlocked.Decrement(ref publishing);
            }
        }, (task, token) =>
        {
            producers[index] = task.PreparedTransfer is { Storage.Shared: true } planned && planned.UploadedChunks.Count < planned.TotalChunks
                ? registry.SharedStorage.Batcher.ExpectProduction(new SharedStorageReservation(planned.FileId, planned.TransferId,
                    planned.ThreadId, planned.Storage), EncryptedPayloadRoles.Original, token,
                    waitingForPublication: () => Volatile.Read(ref publishing) > 0) : null;
        })));

        try
        {
            await OpsTransferMetricsAsync(opId, "upload", 0, tasks.Count, 0, totalBytes, 0);
            await RunWorkersAsync();
            await registry.SharedStorage.ReconcileReservationsAsync(cancellationToken);
        }
        finally
        {
            for (var index = 0; index < fileCancellations.Length; index++)
            {
                producers[index]?.Dispose();
                _uploadFileOperations.TryRemove(opId + ":" + index, out _);
                fileCancellations[index].Dispose();
            }
            foreach (var pair in uploadedEntries.OrderBy(pair => pair.Key))
            {
                ReplaceLocalEntry(allEntries, pair.Value);
                TrackFolderSerial(serialFolderIds, pair.Value.ParentFolderId);
            }
        }

        var status = Volatile.Read(ref stoppedByApi) != 0
            ? "waiting"
            : failed > 0 ? "error" : done == 0 && cancelled > 0 ? "cancelled" : "done";
        var summary = Volatile.Read(ref stoppedByApi) != 0
            ? $"{done}/{tasks.Count} concluídos - limite do Discord; upload preservado para retomar"
            : failed > 0
                ? $"{done}/{tasks.Count} concluídos, {failed} falharam"
                : cancelled > 0 ? $"{done}/{tasks.Count} concluídos, {cancelled} cancelado(s)"
                : $"{done}/{tasks.Count} concluídos";
        await OpsUpdateAsync(opId, status, summary);
    }

    private static bool IsBlockingUploadApiError(Exception ex)
    {
        return ex is DiscordApiException api &&
               ((int)api.StatusCode == 429 || api.DiscordCode == DiscordApiClient.MaxActiveThreadsCode);
    }

    private static string FriendlyUploadError(Exception ex)
    {
        if (ex is DiscordApiException api)
        {
            if ((int)api.StatusCode == 429)
            {
                return "Limite do Discord atingido. Upload pausado e preservado para retomar.";
            }

            if (api.DiscordCode == DiscordApiClient.MaxActiveThreadsCode)
            {
                return "Limite de threads do Discord atingido. Upload pausado e preservado para retomar.";
            }

            if ((int)api.StatusCode >= 500)
            {
                return "falha temporária da API do Discord";
            }
        }

        return string.IsNullOrWhiteSpace(ex.Message) ? "erro inesperado" : ex.Message;
    }

    private async Task<AppEntry> UploadSingleAsync(
        string opId,
        int fileIndex,
        int fileTotal,
        FileInfo file,
        string targetPath,
        List<AppEntry> allEntries,
        ForumUploader uploader,
        AppRegistry registry,
        AppSettings settings,
        UiThrottle throttle,
        CancellationToken cancellationToken,
        TransferState? existingTransferState = null,
        AdaptiveUploadGate? adaptiveUploadGate = null,
        UploadMemoryCoordinator? memoryCoordinator = null,
        int? maxParallelPartsPerFile = null,
        bool useBatchUi = false,
        Func<long, long, Task>? onBytesUploaded = null,
        TransferActivityClock? transferActivity = null,
        bool deferSerialSynchronization = false,
        FileReplacement? replacement = null,
        Action<long>? onPreviewSize = null,
        bool scheduledTransfer = false,
        Action? onOriginalUploaded = null,
        Action? onPublishingMetadata = null)
    {
        await using var uploadPriority = registry.SharedStorage.PrioritizeUploads();
        using var networkWait = NetworkRetryPolicy.TransferScope(cancellationToken);
        using var ownedGate = adaptiveUploadGate == null ? new AdaptiveUploadGate(settings.EffectiveUploadThreads, !settings.ForceUploadParallelism) : null;
        using var liveOwnedGate = ownedGate == null ? null : UploadConcurrencyRegistry.Register(ownedGate);
        adaptiveUploadGate ??= ownedGate;
        var name = CleanUploadName(existingTransferState?.FileName ?? file.Name);
        var originalFile = file;
        var partParallelism = Math.Clamp(maxParallelPartsPerFile ?? settings.EffectiveUploadThreads, 1, 16);
        var sourceLength = file.Length;
        var progressTracker = new UploadProgressTracker(ThumbnailCache.IsImage(name),
            PreviewType(name) == "video" && settings.GenerateVideoPreviews);
        await ReportUploadItemProgressAsync(
            opId,
            fileIndex,
            fileTotal,
            progressTracker.Preparing(0),
            "Preparando arquivo",
            throttle,
            true,
            useBatchUi,
            progressTracker: progressTracker);

        using var preparedVideo = existingTransferState?.UploadedChunks.Count > 0 && !existingTransferState.FastStartApplied
            ? new PreparedVideoUpload(file, false)
            : await Task.Run(() => Mp4FastStart.PrepareAsync(file, cancellationToken), cancellationToken);
        if (existingTransferState?.FastStartApplied == true && !preparedVideo.Applied)
            throw new IOException("Não foi possível reconstruir o faststart desta transferência. Os chunks já enviados foram preservados.");
        file = preparedVideo.File;
        sourceLength = file.Length;

        var totalChunks = ArchiveSplitter.GetPartCount(sourceLength, EncryptionService.MaximumPlaintextPayloadBytes);
        var chunkSizeBytes = sourceLength > 0
            ? Math.Min(sourceLength, EncryptionService.MaximumPlaintextPayloadBytes)
            : 0;
        if (existingTransferState?.UploadedChunks.Count > 0 && existingTransferState.ChunkSizeBytes > 0)
        {
            chunkSizeBytes = existingTransferState.ChunkSizeBytes;
            if (chunkSizeBytes > (existingTransferState.Encryption == null ? DiscordLimits.AttachmentBytes : EncryptionService.MaximumPlaintextPayloadBytes) ||
                ArchiveSplitter.GetPartCount(sourceLength, (int)chunkSizeBytes) != existingTransferState.TotalChunks)
                throw new IOException("O tamanho/layout da origem mudou; os chunks confirmados foram preservados.");
            totalChunks = existingTransferState.TotalChunks;
        }

        var threadId = "";
        TransferState? transferState = existingTransferState;
        var hadSharedAllocation = existingTransferState?.Storage?.Shared == true;
        IAsyncDisposable? payloadLease = null;
        SharedUploadContext? sharedUpload = null;
        try
        {
            var plannedReplacement = transferState?.Replacement ?? replacement;
            if (plannedReplacement != null && (sourceLength <= DiscordLimits.DedicatedThreadThresholdBytes ||
                SharedStorageManager.Payloads(plannedReplacement.Metadata).Any(payload => payload.Storage?.Shared == true)))
                await registry.SharedStorage.EnsureMetadataAvailableAsync(plannedReplacement.Metadata.FileId, cancellationToken);
            var parentFolderId = string.IsNullOrWhiteSpace(transferState?.TargetFolderId)
                ? FolderIdForPath(allEntries, targetPath)
                : transferState.TargetFolderId;
            if (transferState == null)
            {
                transferState = await _transferManager.CreateUploadAsync(
                    originalFile,
                    targetPath,
                    parentFolderId,
                    GuessFileMime(name),
                    chunkSizeBytes,
                    totalChunks,
                    cancellationToken);
                transferState.Replacement = replacement;
                transferState.FileId = replacement?.Metadata.FileId ?? Guid.NewGuid().ToString("N");
                transferState.ThreadId = sourceLength > DiscordLimits.DedicatedThreadThresholdBytes && replacement?.Metadata.Storage?.Shared != true
                    ? replacement?.Metadata.ThreadId ?? "" : "";
                transferState.Encryption = _encryption.CreateFileEncryption(settings.EncryptionMode);
                transferState.SharedAllocationPending = sourceLength <= DiscordLimits.DedicatedThreadThresholdBytes;
                if (!transferState.SharedAllocationPending) transferState.Storage = new StorageLayout { Mode = "dedicated" };
            }
            else
            {
                transferState.Status = TransferStatuses.Recovering;
                transferState.SourcePath = file.FullName;
                transferState.FileName = name;
                transferState.MimeType = string.IsNullOrWhiteSpace(transferState.MimeType) ? GuessFileMime(name) : transferState.MimeType;
                transferState.SizeBytes = Math.Max(0, sourceLength);
                transferState.TargetPath = PathRules.Normalize(string.IsNullOrWhiteSpace(transferState.TargetPath) ? targetPath : transferState.TargetPath);
                transferState.TargetFolderId = parentFolderId;
                transferState.TotalChunks = totalChunks;
                transferState.ChunkSizeBytes = chunkSizeBytes;
                transferState.PendingChunks = Enumerable.Range(0, totalChunks)
                    .Except(transferState.UploadedChunks.Select(chunk => chunk.Index))
                    .ToList();
                transferState.FileId = string.IsNullOrWhiteSpace(transferState.FileId)
                    ? Guid.NewGuid().ToString("N")
                    : transferState.FileId;
                if (transferState.Encryption == null && transferState.UploadedChunks.Count == 0)
                {
                    transferState.Encryption = _encryption.CreateFileEncryption(settings.EncryptionMode);
                }
            }

            _operationTransferStates[TransferKey(opId, fileIndex)] = transferState;
            if (!transferState.UploadChecksumPending) transferState.Checksums?.Validate(sourceLength, totalChunks);
            if (transferState.SharedAllocationPending)
            {
                await _transferManager.SaveAsync(transferState, cancellationToken);
                var reservation = await registry.SharedStorage.ReserveAsync(transferState.FileId, transferState.TransferId, cancellationToken);
                transferState.Storage = reservation.Layout; transferState.ThreadId = reservation.ThreadId;
                transferState.PayloadLayoutVersion = TransferPayloadLayouts.SharedStorage; transferState.SharedAllocationPending = false;
            }
            if (transferState.Storage?.Shared == true)
            {
                var reservation = new SharedStorageReservation(transferState.FileId, transferState.TransferId, transferState.ThreadId, transferState.Storage);
                if (!scheduledTransfer) await registry.SharedStorage.VerifyReservationAsync(reservation, cancellationToken);
                sharedUpload = new SharedUploadContext(registry.SharedStorage, reservation);
                payloadLease = await registry.SharedStorage.HoldWritesAsync([transferState.ThreadId], cancellationToken);
            }
            transferState.FileName = name;
            transferState.SourcePath = originalFile.FullName;
            transferState.FastStartApplied = preparedVideo.Applied;
            await _transferManager.SaveAsync(transferState, cancellationToken);
            if (transferState.Replacement != null)
                await registry.SharedStorage.PublishReferenceAsync("upload:" + transferState.TransferId, transferState.FileId,
                    SharedStorageManager.Payloads(transferState.Replacement.Metadata), "", "", cancellationToken);
            await ReportUploadItemProgressAsync(
                opId,
                fileIndex,
                fileTotal,
                progressTracker.Preparing(100),
                "Arquivo preparado",
                throttle,
                true,
                useBatchUi,
                progressTracker: progressTracker);
            targetPath = PathRules.Normalize(string.IsNullOrWhiteSpace(transferState.TargetPath) ? targetPath : transferState.TargetPath);
            var useMemoryPipeline = transferState.Encryption != null;
            if (useMemoryPipeline)
            {
                if (IsUploadPartDirectoryForSource(file, transferState.TempPath))
                {
                    CleanDir(new DirectoryInfo(transferState.TempPath));
                    transferState.TempPath = "";
                    await _transferManager.SaveAsync(transferState, cancellationToken);
                }
            }

            async Task ReportRateLimitWaitAsync(TimeSpan remaining)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                await ReportUploadItemProgressAsync(
                    opId,
                    fileIndex,
                    fileTotal,
                    -1,
                    $"Aguardando conexão com o Discord - retomando em {seconds}s",
                    throttle,
                    true,
                    useBatchUi,
                    "waiting",
                    progressTracker);
            }

            async Task ReportChunkRateLimitWaitAsync(TimeSpan remaining)
            {
                if (totalChunks > 1 && transferActivity?.HasActiveRequests == true)
                {
                    return;
                }

                await ReportRateLimitWaitAsync(remaining);
            }

            if (sharedUpload != null && existingTransferState != null && !scheduledTransfer && hadSharedAllocation &&
                transferState.UploadedChunks.Count < totalChunks)
            {
                await ReportUploadItemProgressAsync(opId, fileIndex, fileTotal, -1,
                    "Verificando partes do upload anterior...", throttle, true, useBatchUi, progressTracker: progressTracker);
                var recovered = await sharedUpload.Manager.RecoverChunksAsync(sharedUpload.Reservation,
                    EncryptedPayloadRoles.Original, totalChunks, cancellationToken);
                foreach (var reference in recovered.Where(reference => !transferState.UploadedChunks.Any(chunk => chunk.Index == reference.Index)))
                {
                    var message = await RequireApi().ResolvePayloadMessageAsync(transferState.ThreadId, reference.MessageId, cancellationToken);
                    var attachment = message.Attachments.Single(item => item.Id == reference.AttachmentId);
                    var recoveredBatch = string.IsNullOrEmpty(transferState.BatchId) ? transferState.TransferId[..10] : transferState.BatchId;
                    await _transferManager.MarkUploadChunksAsync(transferState, new ForumUploadedBatch(transferState.ThreadId, recoveredBatch,
                        [new ForumUploadedChunk(transferState.ThreadId, recoveredBatch, reference.Index, reference.MessageId,
                            attachment.Size, attachment.Size - EncryptionService.EnvelopeOverheadBytes, reference.AttachmentId)]), cancellationToken);
                }
            }
            var uploadedMap = transferState.UploadedChunks
                .Where(chunk => !string.IsNullOrWhiteSpace(chunk.MessageId))
                .ToDictionary(chunk => chunk.Index, chunk => chunk.MessageId);
            using var checkpointGate = new SemaphoreSlim(1, 1);
            var byteProgress = new UploadByteProgress(sourceLength, chunkSizeBytes, transferState.Encryption != null,
                (logical, wire) => { if (onBytesUploaded != null) _ = onBytesUploaded(logical, wire); });
            foreach (var index in uploadedMap.Keys) byteProgress.Confirm(index);
            var confirmedParts = uploadedMap.Count;
            var partsSync = new object();
            void ReportUploadedParts(int uploadedParts)
            {
                lock (partsSync)
                {
                    uploadedParts = Math.Max(confirmedParts, uploadedParts);
                    Interlocked.Exchange(ref confirmedParts, uploadedParts);
                    var bytePercent = sourceLength <= 0 ? 0 : (int)Math.Clamp(byteProgress.LogicalBytes * 100d / sourceLength, 0, 100);
                    var partPercent = bytePercent;
                    var message = byteProgress.LogicalBytes >= sourceLength && sourceLength > 0
                        ? "Concluindo"
                        : $"{FormatTransferBytes(byteProgress.LogicalBytes)} / {FormatTransferBytes(sourceLength)} enviados";
                    _ = ReportUploadItemProgressAsync(
                        opId,
                        fileIndex,
                        fileTotal,
                        progressTracker.Original(partPercent),
                        message,
                        throttle,
                        uploadedParts >= totalChunks || bytePercent >= 100,
                        useBatchUi,
                        progressTracker: progressTracker);
                }
            }

            async Task HandleUploadedChunkAsync(ForumUploadedChunk chunk)
            {
                await checkpointGate.WaitAsync(cancellationToken);
                try
                {
                    transferState.ThreadId = chunk.ThreadId;
                    transferState.BatchId = chunk.BatchId;
                    threadId = chunk.ThreadId;
                    await _transferManager.MarkUploadChunkAsync(
                        transferState,
                        chunk.Index,
                        chunk.MessageId,
                        chunk.SizeBytes,
                        cancellationToken);
                    byteProgress.Confirm(chunk.Index);
                }
                finally
                {
                    checkpointGate.Release();
                }
            }

            async Task HandleUploadedBatchAsync(ForumUploadedBatch batch)
            {
                var checkpointToken = sharedUpload == null ? cancellationToken : CancellationToken.None;
                await checkpointGate.WaitAsync(checkpointToken);
                try
                {
                    await _transferManager.MarkUploadChunksAsync(transferState, batch, checkpointToken);
                    threadId = batch.ThreadId;
                    foreach (var chunk in batch.Chunks) byteProgress.Confirm(chunk.Index);
                }
                finally { checkpointGate.Release(); }
            }

            await using var streamingChecksums = transferState.Encryption == null ? null :
                await StreamingUploadChecksums.StartAsync(transferState, _transferManager, preparedVideo.OpenRead,
                    sourceLength, checked((int)chunkSizeBytes), cancellationToken);
            ForumUploadResult upload;
            if (transferState.Encryption != null)
            {
                var encryptionContext = new EncryptedPayloadContext(
                    transferState.FileId,
                    transferState.Encryption,
                    EncryptedPayloadRoles.Original,
                    0,
                    totalChunks)
                {
                    Checksums = transferState.UploadChecksumPending ? null : transferState.Checksums,
                    UploadPlaintextObserved = streamingChecksums == null ? null : streamingChecksums.ObserveAsync
                };

                async Task<PreparedUploadPart> PrepareOriginalPartAsync(int index)
                {
                    using var owned = memoryCoordinator == null ? new UploadMemoryCoordinator(partParallelism) : null;
                    return await (memoryCoordinator ?? owned!).PreparePartAsync(_encryption, file, sourceLength,
                        (int)transferState.ChunkSizeBytes, encryptionContext.WithChunkIndex(index), cancellationToken,
                        deferBuffers: transferState.PayloadLayoutVersion != TransferPayloadLayouts.SingleAttachment,
                        openSource: preparedVideo.OpenRead, isolateSendCancellation: sharedUpload != null);
                }

                upload = await uploader.UploadResumableGeneratedAsync(
                    name,
                    totalChunks,
                    PrepareOriginalPartAsync,
                    transferState.ThreadId,
                    transferState.BatchId,
                    uploadedMap,
                    ReportUploadedParts,
                    HandleUploadedChunkAsync,
                    cancellationToken,
                    ReportChunkRateLimitWaitAsync,
                    maxParallelParts: partParallelism,
                    encryptedPayload: encryptionContext,
                    cleanupPart: TryDeleteFile,
                    adaptiveUploadGate: adaptiveUploadGate,
                    onTransferActivity: transferActivity == null ? null : transferActivity.SetActive,
                    useAttachmentReferences: transferState.PayloadLayoutVersion != TransferPayloadLayouts.SingleAttachment,
                    uploadedChunkReferences: transferState.PayloadLayoutVersion != TransferPayloadLayouts.SingleAttachment
                        ? transferState.UploadedChunks.Select(chunk => new ChunkReference
                            { Index = chunk.Index, MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId ?? "" }).ToArray() : null,
                    onBatchUploaded: HandleUploadedBatchAsync,
                    batchPreparationGate: RamUploadBudget.Batches,
                    streamPreparedParts: transferState.PayloadLayoutVersion != TransferPayloadLayouts.SingleAttachment,
                    maxPreparedPartBytes: checked((int)transferState.ChunkSizeBytes + EncryptionService.EnvelopeOverheadBytes),
                    onPartTransferProgress: (index, wire) =>
                    {
                        byteProgress.Report(index, wire);
                        ReportUploadedParts(Volatile.Read(ref confirmedParts));
                    }, sharedUpload: sharedUpload);
            }
            else
            {
                upload = await uploader.UploadResumableGeneratedAsync(
                    name,
                    totalChunks,
                    index => UploadMemoryCoordinator.PreparePlainPartAsync(file, sourceLength,
                        checked((int)chunkSizeBytes), index, cancellationToken, preparedVideo.OpenRead),
                    transferState.ThreadId,
                    transferState.BatchId,
                    uploadedMap,
                    ReportUploadedParts,
                    HandleUploadedChunkAsync,
                    cancellationToken,
                    ReportChunkRateLimitWaitAsync,
                    maxParallelParts: partParallelism,
                    adaptiveUploadGate: adaptiveUploadGate,
                    onTransferActivity: transferActivity == null ? null : transferActivity.SetActive,
                    onPartTransferProgress: (index, wire) => { byteProgress.Report(index, wire); ReportUploadedParts(Volatile.Read(ref confirmedParts)); });
            }
            threadId = upload.ThreadId;
            if (streamingChecksums != null) await streamingChecksums.CompleteAsync(cancellationToken);
            // All original bodies/ACKs/checkpoints have completed. Do not retain their admission or MP4 index during finalization.
            preparedVideo.Dispose();
            onOriginalUploaded?.Invoke();

            var previewIds = await TryUploadGeneratedPreviewAsync(
                opId,
                fileIndex,
                fileTotal,
                file,
                name,
                threadId,
                uploader,
                settings,
                throttle,
                ReportRateLimitWaitAsync,
                cancellationToken,
                transferState,
                useBatchUi,
                progressTracker,
                transferState.Encryption == null
                    ? null
                    : new EncryptedPayloadContext(
                        transferState.FileId,
                        transferState.Encryption,
                        EncryptedPayloadRoles.Preview,
                        0),
                adaptiveUploadGate, transferActivity, onPreviewSize, onBytesUploaded, sharedUpload);

            onPublishingMetadata?.Invoke();
            await ReportUploadItemProgressAsync(
                opId,
                fileIndex,
                fileTotal,
                progressTracker.Metadata(0),
                "Concluindo",
                throttle,
                true,
                useBatchUi,
                progressTracker: progressTracker);
            var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
            var entry = AppEntry.File(name, threadId, now, sourceLength, targetPath);
            entry.VersionId = transferState.TransferId;
            entry.VersionUploadedAt = now;
            entry.ModifiedAt = now;
            entry.FileId = transferState.FileId;
            entry.Storage = transferState.Storage;
            entry.ThumbnailRef = transferState.ThumbnailRef;
            entry.Encryption = transferState.Encryption?.Clone();
            entry.ParentFolderId = parentFolderId;
            entry.BatchId = upload.BatchId;
            entry.ChunkCount = upload.ChunkCount;
            entry.ChunkMessageIds = upload.ChunkMessageIds.ToList();
            entry.ChunkReferences = upload.ChunkReferences?.ToList();
            entry.Checksums = transferState.Checksums;
            entry.MimeType = GuessFileMime(name);
            entry.PreviewMessageId = previewIds.PreviewMessageId;
            entry.VideoPreviewMessageId = previewIds.VideoPreviewMessageId;
            entry.VideoPreviewChunkMessageIds = previewIds.VideoPreviewChunkMessageIds.ToList();
            entry.VideoPreviewChunkReferences = previewIds.VideoPreviewChunkReferences?.ToList();
            entry.VideoPreviewSizeBytes = previewIds.VideoPreviewSizeBytes;
            if (transferState.Replacement != null)
                entry = await PublishFileVersionAsync(registry, transferState.Replacement, entry, cancellationToken, ReportRateLimitWaitAsync);
            else if (scheduledTransfer && entry.Storage?.Shared == true)
                await registry.AddNewUploadEntryAsync(entry, cancellationToken, ReportRateLimitWaitAsync);
            else
                await registry.AddEntryAsync(entry, cancellationToken, ReportRateLimitWaitAsync,
                    synchronizeSerials: !deferSerialSynchronization);
            await ReportUploadItemProgressAsync(
                opId,
                fileIndex,
                fileTotal,
                progressTracker.Metadata(70),
                "Concluindo",
                throttle,
                true,
                useBatchUi,
                progressTracker: progressTracker);
            transferState.FileId = entry.FileId;
            transferState.MetadataMessageId = entry.MetadataMessageId;
            await _transferManager.CompleteAsync(transferState, settings.KeepTransferHistory, CancellationToken.None);
            try
            {
                await uploader.ArchiveThreadAsync(threadId, CancellationToken.None, ReportRateLimitWaitAsync);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error("UploadArchiveThread", ex, threadId);
            }

            await ReportUploadItemProgressAsync(
                opId,
                fileIndex,
                fileTotal,
                progressTracker.Done(),
                "Concluído",
                throttle,
                true,
                useBatchUi,
                progressTracker: progressTracker);

            return entry;
        }
        catch (OperationCanceledException)
        {
            if (PauseRequested(opId, fileIndex)) throw;
            if (transferState?.Storage?.Shared == true)
            {
                try { await registry.SharedStorage.CancelReservationAsync(transferState, CancellationToken.None); }
                catch (Exception error) { AppLogger.Error("SharedStorage.CancelCleanup", error); }
            }
            else if (!string.IsNullOrWhiteSpace(threadId) && transferState?.Replacement == null)
            {
                try { await Downloader().DeleteThreadAsync(threadId); } catch { }
            }

            if (transferState != null)
            {
                var loadedSettings = _store.Load();
                await _transferManager.CancelAsync(
                    transferState,
                    loadedSettings.KeepTransferHistory,
                    loadedSettings.CleanupTempOnCancel,
                    CancellationToken.None);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (transferState != null)
            {
                if (IsResumableUploadFailure(ex))
                {
                    await _transferManager.PauseAsync(transferState, ex, CancellationToken.None);
                }
                else
                {
                    await _transferManager.FailAsync(transferState, ex, CancellationToken.None);
                }
            }

            throw;
        }
        finally
        {
            if (transferState?.Replacement != null && transferState.Status is TransferStatuses.Completed or TransferStatuses.Cancelled or TransferStatuses.Failed)
            {
                try { await registry.SharedStorage.ReleaseReferenceAsync("upload:" + transferState.TransferId, transferState.FileId, CancellationToken.None); }
                catch (Exception error) { AppLogger.Error("Storage.UploadRelease", error); }
            }
            if (payloadLease != null) await payloadLease.DisposeAsync();
        }
    }

    private async Task<PreviewUploadIds> TryUploadGeneratedPreviewAsync(
        string opId, int fileIndex, int fileTotal, FileInfo file, string name, string threadId,
        ForumUploader uploader, AppSettings settings, UiThrottle throttle,
        Func<TimeSpan, Task> onRateLimitWait, CancellationToken cancellationToken,
        TransferState? transferState = null, bool useBatchUi = false,
        UploadProgressTracker? progressTracker = null, EncryptedPayloadContext? previewEncryption = null,
        AdaptiveUploadGate? adaptiveUploadGate = null, TransferActivityClock? transferActivity = null,
        Action<long>? onPreviewSize = null, Func<long, long, Task>? onPreviewBytes = null,
        SharedUploadContext? sharedUpload = null)
    {
        var previewMessageId = transferState?.ThumbnailMessageId ?? "";
        IReadOnlyList<string> videoChunks = transferState?.VideoPreview480pChunks ?? [];
        IReadOnlyList<ChunkReference>? videoReferences = transferState?.VideoPreview480pChunkRefs;
        var videoSize = transferState?.VideoPreview480pSizeBytes ?? 0;
        var image = ThumbnailCache.IsImage(name);
        var video = PreviewType(name) == "video";
        if (!image && !video) return new PreviewUploadIds("", "", [], 0);
        try
        {
            if (previewMessageId.Length == 0)
            {
                using var producing = sharedUpload?.Manager.Batcher.ExpectProduction(sharedUpload.Reservation,
                    EncryptedPayloadRoles.Preview, cancellationToken);
                await ReportUploadItemProgressAsync(opId, fileIndex, fileTotal,
                    progressTracker?.ThumbnailGeneration(0) ?? 0, "Gerando miniatura",
                    throttle, true, useBatchUi, progressTracker: progressTracker);
                using var thumbnail = await VideoPreviewGenerator.CreateThumbnailAsync(file, cancellationToken);
                if (sharedUpload == null) ThumbnailCache.Store(threadId, thumbnail.Bytes);
                await ReportUploadItemProgressAsync(opId, fileIndex, fileTotal,
                    progressTracker?.ThumbnailUpload(0) ?? 99, "Enviando miniatura",
                    throttle, true, useBatchUi, progressTracker: progressTracker);
                using var memory = new UploadMemoryCoordinator(1);
                var source = new FileInfo(Path.Combine(AppContext.BaseDirectory, "ram-thumbnail" + (image ? ".png" : ".jpg")));
                using var part = previewEncryption != null
                    ? await memory.PreparePartAsync(_encryption, source, thumbnail.Length,
                        EncryptionService.MaximumPlaintextPayloadBytes, previewEncryption.WithChunkCount(1),
                        cancellationToken, deferBuffers: sharedUpload != null, openSource: thumbnail.OpenRead,
                        isolateSendCancellation: sharedUpload != null)
                    : await UploadMemoryCoordinator.PreparePlainPartAsync(source, thumbnail.Length,
                        DiscordLimits.AttachmentBytes, 0, cancellationToken, thumbnail.OpenRead);
                part.TransferProgress = wire =>
                {
                    var percent = (int)Math.Clamp(wire * 100d / Math.Max(1, part.Length), 0, 100);
                    _ = ReportUploadItemProgressAsync(opId, fileIndex, fileTotal,
                        progressTracker?.ThumbnailUpload(percent) ?? 99,
                        percent >= 100 ? "Concluindo" : "Enviando miniatura",
                        throttle, percent >= 100, useBatchUi, progressTracker: progressTracker);
                };
                if (sharedUpload != null)
                {
                    var reference = await sharedUpload.Manager.Batcher.SendAsync(sharedUpload.Reservation, previewEncryption!.WithChunkCount(1), part,
                        async reference =>
                        {
                            transferState!.ThumbnailRef = new AttachmentReference { MessageId = reference.MessageId, AttachmentId = reference.AttachmentId };
                            transferState.ThumbnailMessageId = reference.MessageId;
                            await _transferManager.SaveAsync(transferState, CancellationToken.None);
                        }, cancellationToken, adaptiveUploadGate, onRateLimitWait, transferActivity == null ? null : transferActivity.SetActive);
                    previewMessageId = reference.MessageId;
                    ThumbnailCache.Store("file-" + sharedUpload.Reservation.FileId + "-" + sharedUpload.Reservation.VersionId + "-" +
                        reference.MessageId + "-" + reference.AttachmentId, thumbnail.Bytes);
                }
                else previewMessageId = await uploader.UploadPreviewFileToThreadAsync(threadId,
                    previewEncryption == null ? PayloadReference("PREVIEW|" + StripExtension(name)) : PayloadMessage(previewEncryption, 1),
                    part, cancellationToken, onRateLimitWait: onRateLimitWait);
                await ReportUploadItemProgressAsync(opId, fileIndex, fileTotal,
                    progressTracker?.ThumbnailUpload(100) ?? 99, "Concluindo",
                    throttle, true, useBatchUi, progressTracker: progressTracker);
                if (transferState != null)
                {
                    transferState.ThumbnailMessageId = previewMessageId;
                    await _transferManager.SaveAsync(transferState, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (TransferMemoryPressureException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Error("PreviewUpload", ex);
            await _bridge.CallAsync("_onNonfatalError", "O arquivo original foi enviado, mas a miniatura não pôde ser gerada.");
        }

        if (video && settings.GenerateVideoPreviews && videoChunks.Count == 0)
        {
            try
            {
                VideoPreviewUploadResult result;
                var encryption = previewEncryption == null ? null : previewEncryption with { Role = EncryptedPayloadRoles.Video480p };
                var references = transferState?.PayloadLayoutVersion is TransferPayloadLayouts.MultiAttachment or TransferPayloadLayouts.SharedStorage;
                if (FfmpegMediaService.Available)
                {
                    void Report(string status) => _ = ReportUploadItemProgressAsync(opId, fileIndex, fileTotal,
                        progressTracker?.Current ?? 80, status, throttle,
                        status == "Concluindo", useBatchUi, progressTracker: progressTracker);
                    long previewSent = 0;
                    var streamed = await FfmpegPreviewUpload.UploadAsync(file, VideoPreviewFileName(name), threadId,
                        uploader, _encryption, encryption, references, settings.HardwareAcceleration, Report,
                        (sent, total) =>
                        {
                            var delta = Math.Max(0, sent - Interlocked.Exchange(ref previewSent, sent));
                            if (onPreviewBytes != null) _ = onPreviewBytes(delta, delta);
                            _ = ReportUploadItemProgressAsync(opId, fileIndex, fileTotal,
                                progressTracker?.VideoPreviewUpload((int)Math.Clamp(sent * 100d / Math.Max(1, total), 0, 100)) ?? 99,
                                sent >= total ? "Concluindo" : $"Prévia 480p: {FormatTransferBytes(sent)} / {FormatTransferBytes(total)} enviados",
                                throttle, sent >= total, useBatchUi, progressTracker: progressTracker);
                        },
                        cancellationToken, onRateLimitWait,
                        generationProgress: percent => _ = ReportUploadItemProgressAsync(opId, fileIndex, fileTotal,
                            progressTracker?.VideoPreviewGeneration(percent) ?? 80, $"Gerando prévia 480p - {percent}%",
                            throttle, percent is 0 or 100, useBatchUi, progressTracker: progressTracker),
                        maxParallelParts: settings.EffectiveUploadThreads, adaptiveUploadGate: adaptiveUploadGate,
                        onTransferActivity: transferActivity == null ? null : transferActivity.SetActive,
                        onPreviewSize: onPreviewSize, sharedUpload: sharedUpload);
                    result = new VideoPreviewUploadResult(streamed.SizeBytes, streamed.Upload.ChunkMessageIds, streamed.Upload.ChunkReferences);
                }
                else
                {
                    using var generated = await VideoPreviewGenerator.Create480pAsync(file, settings.HardwareAcceleration,
                        progress => _ = ReportUploadItemProgressAsync(opId, fileIndex, fileTotal,
                            progressTracker?.VideoPreviewGeneration(progress) ?? 99,
                            $"Gerando prévia 480p - {progress}%", throttle, progress is 0 or 100, useBatchUi, progressTracker: progressTracker),
                        cancellationToken);
                    result = await UploadVideoPreview480Async(opId, fileIndex, fileTotal, threadId, name,
                        generated, uploader, throttle, onRateLimitWait, cancellationToken, useBatchUi,
                        settings.EffectiveUploadThreads, progressTracker, encryption, references, sharedUpload);
                }
                videoChunks = result.ChunkMessageIds;
                videoReferences = result.ChunkReferences;
                videoSize = result.SizeBytes;
                if (transferState != null)
                {
                    transferState.VideoPreview480pChunks = videoChunks.ToList();
                    transferState.VideoPreview480pChunkRefs = videoReferences?.ToList();
                    transferState.VideoPreview480pSizeBytes = videoSize;
                    await _transferManager.SaveAsync(transferState, cancellationToken);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (TransferMemoryPressureException) { throw; }
            catch (Exception ex)
            {
                AppLogger.Error("VideoPreviewUpload", ex);
                await _bridge.CallAsync("_onNonfatalError", "O arquivo original foi enviado, mas a prévia 480p não pôde ser gerada. " + ex.Message);
            }
        }
        return new PreviewUploadIds(previewMessageId, videoReferences?.FirstOrDefault()?.MessageId ?? videoChunks.FirstOrDefault() ?? "",
            videoChunks, videoSize, videoReferences);
    }

    private async Task<VideoPreviewUploadResult> UploadVideoPreview480Async(
        string opId, int fileIndex, int fileTotal, string threadId, string originalName,
        RamMediaPreview generated, ForumUploader uploader, UiThrottle throttle,
        Func<TimeSpan, Task> onRateLimitWait, CancellationToken cancellationToken, bool useBatchUi,
        int maxParallelParts, UploadProgressTracker? progressTracker, EncryptedPayloadContext? videoEncryption,
        bool useAttachmentReferences = false, SharedUploadContext? sharedUpload = null)
    {
        using var prepared = await Mp4FastStart.PrepareAsync(generated, cancellationToken);
        var chunkBytes = videoEncryption == null ? DiscordLimits.AttachmentBytes : EncryptionService.MaximumPlaintextPayloadBytes;
        var totalParts = ArchiveSplitter.GetPartCount(generated.Length, chunkBytes);
        var context = videoEncryption?.WithChunkCount(totalParts);
        using var memory = new UploadMemoryCoordinator(maxParallelParts);
        var bytes = new UploadByteProgress(generated.Length, chunkBytes, context != null, (_, _) => { });
        void Report()
        {
            var message = bytes.LogicalBytes >= generated.Length ? "Concluindo"
                : $"Prévia 480p: {FormatTransferBytes(bytes.LogicalBytes)} / {FormatTransferBytes(generated.Length)} enviados";
            _ = ReportUploadItemProgressAsync(opId, fileIndex, fileTotal,
                progressTracker?.VideoPreviewUpload((int)Math.Clamp(bytes.LogicalBytes * 100d / Math.Max(1, generated.Length), 0, 100)) ?? 99, message, throttle,
                bytes.LogicalBytes >= generated.Length, useBatchUi, progressTracker: progressTracker);
        }
        var result = await uploader.UploadResumableGeneratedAsync(VideoPreviewFileName(originalName), totalParts,
            index => context != null
                ? memory.PreparePartAsync(_encryption, prepared.File, generated.Length, chunkBytes,
                    context.WithChunkIndex(index), cancellationToken, deferBuffers: useAttachmentReferences, openSource: prepared.OpenRead,
                    isolateSendCancellation: sharedUpload != null)
                : UploadMemoryCoordinator.PreparePlainPartAsync(prepared.File, generated.Length, chunkBytes, index, cancellationToken, prepared.OpenRead),
            threadId, "", new Dictionary<int, string>(), _ => Report(),
            cancellationToken: cancellationToken, onRateLimitWait: onRateLimitWait,
            maxParallelParts: maxParallelParts, encryptedPayload: context,
            useAttachmentReferences: context != null && useAttachmentReferences,
            batchPreparationGate: memory.BatchPreparationGate,
            streamPreparedParts: context != null && useAttachmentReferences,
            onPartTransferProgress: (index, wire) => { bytes.Report(index, wire); Report(); },
            legacyPreviewKind: context == null ? "video-480" : null, sharedUpload: sharedUpload);
        return new VideoPreviewUploadResult(generated.Length, result.ChunkMessageIds, result.ChunkReferences);
    }

    private static string FormatTransferBytes(long value)
    {
        var amount = (double)Math.Max(0, value);
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return amount.ToString(unit == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private async Task StartDownloadAsync(string threadId, string name, DirectoryInfo? selectedDestination = null, AppEntry? selectedVersion = null)
    {
        using var networkWait = NetworkRetryPolicy.TransferScope();
        if (string.IsNullOrWhiteSpace(threadId) || threadId.StartsWith("folder_", StringComparison.Ordinal))
        {
            await StartFolderDownloadAsync(threadId, name);
            return;
        }

        var dest = selectedDestination ?? await ResolveDownloadDirectoryAsync("Salvar em...");
        if (dest == null)
        {
            return;
        }

        var opId = NewId();
        var cancellationToken = BeginTransferOperation(opId);
        await OpsAddAsync(opId, "download", name);
        _ = RunAsync(async () =>
        {
            TransferState? transferState = null;
            long totalDownloadBytes = 0;
            long totalReceivedBytes = 0;
            try
            {
                await OpsUpdateAsync(opId, "active", "Preparando download...");
                var entry = selectedVersion ?? FindLoadedEntry(threadId);
                if (entry != null && selectedVersion == null)
                {
                    entry = await Registry().LoadFileDetailsAsync(entry, cancellationToken);
                }
                totalDownloadBytes = Math.Max(0, entry?.SizeBytes ?? 0);
                await OpsTransferMetricsAsync(opId, "download", 0, 1, 0, totalDownloadBytes);
                if (entry != null)
                {
                    transferState = await _transferManager.CreateDownloadAsync(entry, dest, cancellationToken);
                    await RunPausableDownloadAsync(opId, transferState, cancellationToken, async attemptToken =>
                    {
                        await Downloader().DownloadAndAssembleAsync(
                        entry,
                        dest,
                        transferState,
                        _transferManager,
                        msg => _ = OpsUpdateAsync(opId, "active", msg),
                        attemptToken,
                        progress =>
                        {
                            _ = OpsProgressAsync(opId, Math.Min(99, progress.Percent), progress.Assembling ? progress.Message
                                : $"{FormatTransferBytes(progress.DownloadedBytes)} / {FormatTransferBytes(progress.TotalBytes)} baixados");
                            if (!progress.Assembling)
                            {
                                Interlocked.Exchange(ref totalDownloadBytes, Math.Max(0, progress.TotalBytes));
                                Interlocked.Exchange(ref totalReceivedBytes, progress.RateBytes ?? progress.DownloadedBytes);
                                _ = OpsTransferMetricsAsync(opId, "download", 0, 1, progress.DownloadedBytes, progress.TotalBytes, rateBytes: progress.RateBytes);
                            }
                        });
                    });
                    await _transferManager.CompleteAsync(transferState, _store.Load().KeepTransferHistory, cancellationToken);
                }
                else
                {
                    await Downloader().DownloadAndAssembleAsync(threadId, dest, msg => _ = OpsUpdateAsync(opId, "active", msg), cancellationToken,
                        progress =>
                        {
                            _ = OpsProgressAsync(opId, Math.Min(99, progress.Percent), progress.Assembling ? progress.Message
                                : $"{FormatTransferBytes(progress.DownloadedBytes)} / {FormatTransferBytes(progress.TotalBytes)} baixados");
                            if (!progress.Assembling)
                            {
                                Interlocked.Exchange(ref totalDownloadBytes, Math.Max(0, progress.TotalBytes));
                                Interlocked.Exchange(ref totalReceivedBytes, progress.RateBytes ?? progress.DownloadedBytes);
                                _ = OpsTransferMetricsAsync(opId, "download", 0, 1, progress.DownloadedBytes, progress.TotalBytes, rateBytes: progress.RateBytes);
                            }
                        });
                }

                var completedBytes = Interlocked.Read(ref totalDownloadBytes);
                await OpsTransferMetricsAsync(opId, "download", 1, 1, completedBytes, completedBytes, rateBytes: Interlocked.Read(ref totalReceivedBytes));
                await OpsUpdateAsync(opId, "done", "Concluído");
            }
            catch (OperationCanceledException)
            {
                if (transferState != null)
                {
                    var loadedSettings = _store.Load();
                    await _transferManager.CancelAsync(
                        transferState,
                        loadedSettings.KeepTransferHistory,
                        loadedSettings.CleanupTempOnCancel,
                        CancellationToken.None);
                }

                await OpsUpdateAsync(opId, "cancelled", "Cancelado");
            }
            catch (VerifiedPayloadSizeMismatchException mismatch)
            {
                if (transferState != null)
                {
                    await _transferManager.CompleteAsync(transferState, _store.Load().KeepTransferHistory, CancellationToken.None);
                }

                var opened = await OfferOpenVerifiedSizeMismatchAsync(mismatch);
                var completedBytes = Interlocked.Read(ref totalDownloadBytes);
                await OpsTransferMetricsAsync(opId, "download", 1, 1, completedBytes, completedBytes, rateBytes: Interlocked.Read(ref totalReceivedBytes));
                await OpsUpdateAsync(opId, "done", opened ? "Concluído com aviso; arquivo aberto." : "Concluído com aviso.");
            }
            catch (Exception ex)
            {
                if (transferState != null)
                {
                    await _transferManager.RecordDownloadFailureAsync(transferState, ex, CancellationToken.None);
                }

                await OpsUpdateAsync(opId, "error", ex.Message);
                if (ex is EncryptedPayloadIntegrityException integrity &&
                    await OfferRetryEncryptedDownloadAsync(integrity))
                {
                    await StartDownloadAsync(threadId, name, dest, selectedVersion);
                }
            }
            finally
            {
                EndTransferOperation(opId);
            }
        });
    }

    private async Task StartFolderDownloadAsync(string folderThreadId, string folderName)
    {
        using var networkWait = NetworkRetryPolicy.TransferScope();
        var baseDir = await ResolveDownloadDirectoryAsync("Salvar pasta em...");
        if (baseDir == null)
        {
            return;
        }

        _ = RunAsync(async () =>
        {
            var all = CurrentLocalEntries();
            var entry = all.FirstOrDefault(a => a.Type == "folder" && a.Name == folderName && a.ThreadId == folderThreadId);
            var folderPath = entry != null ? PathRules.ChildPath(entry) : PathRules.Normalize(_currentPath == "/" ? "/" + folderName : _currentPath + "/" + folderName);
            var toDownload = entry == null
                ? []
                : (await Registry().LoadFolderTreeAsync(entry, allowOrphanThread: false))
                    .Where(a => a.Type != "folder")
                    .ToList();
            if (toDownload.Count == 0)
            {
                await ErrorAsync("A pasta está vazia.");
                return;
            }

            var opId = NewId();
            var cancellationToken = BeginTransferOperation(opId);
            await OpsAddAsync(opId, "download", $"{folderName} ({toDownload.Count} arquivo(s))");
            var done = 0;
            var failed = 0;
            var totalBytes = toDownload.Sum(app => Math.Max(0, app.SizeBytes));
            long downloadedBytes = 0;
            long receivedBytes = 0;
            await OpsTransferMetricsAsync(opId, "download", 0, toDownload.Count, 0, totalBytes);
            try
            {
                for (var i = 0; i < toDownload.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var app = await Registry().LoadFileDetailsAsync(toDownload[i], cancellationToken);
                    var rel = PathRules.Normalize(app.Path)[folderPath.Length..].TrimStart('/');
                    var destDir = string.IsNullOrWhiteSpace(rel)
                        ? new DirectoryInfo(System.IO.Path.Combine(baseDir.FullName, folderName))
                        : new DirectoryInfo(System.IO.Path.Combine(baseDir.FullName, folderName, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    Directory.CreateDirectory(destDir.FullName);
                    await OpsUpdateAsync(opId, "active", $"({i + 1}/{toDownload.Count}) {app.Name}");
                    TransferState? transferState = null;
                    long currentFileBytes = 0;
                    long currentReceivedBytes = 0;
                    var receivedBeforeFile = Interlocked.Read(ref receivedBytes);
                    long currentFileTotal = Math.Max(0, app.SizeBytes);
                    var completedBeforeFile = Interlocked.Read(ref downloadedBytes);
                    try
                    {
                        transferState = await _transferManager.CreateDownloadAsync(app, destDir, cancellationToken);
                        await RunPausableDownloadAsync(opId, transferState, cancellationToken, async attemptToken =>
                    {
                        await Downloader().DownloadAndAssembleAsync(
                            app,
                            destDir,
                            transferState,
                            _transferManager,
                            msg => _ = OpsUpdateAsync(opId, "active", $"({i + 1}/{toDownload.Count}) {msg}"),
                            attemptToken,
                            progress =>
                            {
                                Interlocked.Exchange(ref currentFileBytes, Math.Max(0, progress.DownloadedBytes));
                                Interlocked.Exchange(ref currentFileTotal, Math.Max(0, progress.TotalBytes));
                                var aggregate = completedBeforeFile + Math.Max(0, progress.DownloadedBytes);
                                var overallPercent = totalBytes > 0
                                    ? Math.Clamp((int)Math.Round((completedBeforeFile + Math.Max(0, app.SizeBytes) * progress.Percent / 100d) * 100.0 / totalBytes), 0, 100)
                                    : Percent(done, toDownload.Count);
                                _ = OpsProgressAsync(opId, Math.Min(99, overallPercent),
                                    progress.Assembling ? progress.Message : $"{FormatTransferBytes(aggregate)} / {FormatTransferBytes(totalBytes)} baixados");
                                if (!progress.Assembling)
                                {
                                    Interlocked.Exchange(ref currentReceivedBytes, progress.RateBytes ?? progress.DownloadedBytes);
                                    _ = OpsTransferMetricsAsync(opId, "download", done, toDownload.Count, aggregate, totalBytes,
                                        rateBytes: receivedBeforeFile + Interlocked.Read(ref currentReceivedBytes));
                                }
                            });
                    });
                        await _transferManager.CompleteAsync(transferState, _store.Load().KeepTransferHistory, cancellationToken);
                        var measuredTotal = Math.Max(Math.Max(0, app.SizeBytes), Math.Max(Interlocked.Read(ref currentFileTotal), Interlocked.Read(ref currentFileBytes)));
                        Interlocked.Exchange(ref downloadedBytes, completedBeforeFile + measuredTotal);
                        Interlocked.Exchange(ref receivedBytes, receivedBeforeFile + Interlocked.Read(ref currentReceivedBytes));
                        done++;
                        await OpsTransferMetricsAsync(opId, "download", done, toDownload.Count, Interlocked.Read(ref downloadedBytes), totalBytes, rateBytes: Interlocked.Read(ref receivedBytes));
                    }
                    catch (OperationCanceledException)
                    {
                        if (transferState != null)
                        {
                            var loadedSettings = _store.Load();
                            await _transferManager.CancelAsync(
                                transferState,
                                loadedSettings.KeepTransferHistory,
                                loadedSettings.CleanupTempOnCancel,
                                CancellationToken.None);
                        }

                        throw;
                    }
                    catch (VerifiedPayloadSizeMismatchException mismatch)
                    {
                        if (transferState != null)
                        {
                            await _transferManager.CompleteAsync(transferState, _store.Load().KeepTransferHistory, CancellationToken.None);
                        }

                        await OfferOpenVerifiedSizeMismatchAsync(mismatch);
                        var measuredTotal = Math.Max(Math.Max(0, app.SizeBytes), Math.Max(Interlocked.Read(ref currentFileTotal), Interlocked.Read(ref currentFileBytes)));
                        Interlocked.Exchange(ref downloadedBytes, completedBeforeFile + measuredTotal);
                        Interlocked.Exchange(ref receivedBytes, receivedBeforeFile + Interlocked.Read(ref currentReceivedBytes));
                        done++;
                        await OpsTransferMetricsAsync(opId, "download", done, toDownload.Count, Interlocked.Read(ref downloadedBytes), totalBytes, rateBytes: Interlocked.Read(ref receivedBytes));
                    }
                    catch (Exception ex)
                    {
                        if (transferState != null)
                        {
                            await _transferManager.RecordDownloadFailureAsync(transferState, ex, CancellationToken.None);
                        }

                        if (ex is EncryptedPayloadIntegrityException integrity &&
                            await OfferRetryEncryptedDownloadAsync(integrity))
                        {
                            i--;
                            continue;
                        }

                        failed++;
                        Debug.WriteLine("[FolderDL] " + ex.Message);
                    }
                }

                await OpsUpdateAsync(
                    opId,
                    failed == 0 ? "done" : "error",
                    failed == 0
                        ? $"{done}/{toDownload.Count} baixados"
                        : $"{done}/{toDownload.Count} baixados; {failed} falharam.");
            }
            catch (OperationCanceledException)
            {
                await OpsUpdateAsync(opId, "cancelled", $"{done}/{toDownload.Count} baixados; cancelado");
            }
            finally
            {
                EndTransferOperation(opId);
            }
        });
    }

    private async Task DeleteAppAsync(string threadId, string metadataMessageId, bool permanently)
    {
        if (!permanently)
        {
            await RelocateTrashItemsAsync(JsonSerializer.Serialize(new[] { metadataMessageId }), restore: false);
            return;
        }
        await SetBackgroundActivityAsync("Em andamento...", true);
        try
        {
            var registry = Registry();
            var all = LoadedEntriesForLookup().DistinctBy(EntryCacheKey).ToList();
            var entry = all.FirstOrDefault(a => a.MetadataMessageId == metadataMessageId);
            if (entry != null)
            {
                var result = await DeleteEntriesWithProgressAsync(registry, all, [entry]);
                _ = RunAsync(() => PublishLibraryFileCountAsync(registry));
                if (result.Failed > 0)
                {
                    await ErrorAsync($"{result.Deleted}/{result.Total} apagados; {result.Failed} falharam.");
                    return;
                }
            }
            else
            {
                await ErrorAsync("Item não encontrado na pasta carregada.");
                return;
            }

            await _bridge.CallAsync("_onStatus", "OK Item apagado.", false);
        }
        finally
        {
            await SetBackgroundActivityAsync("", false);
        }
    }

    private async Task EmptyTrashAsync()
    {
        await _bridge.CallAsync("_onTrashEmptying", true);
        try
        {
            var entries = await Registry().RefreshFolderAsync(AppRegistry.TrashFolderId, AppRegistry.TrashPath);
            ReplaceLoadedFolder(AppRegistry.TrashFolderId, entries);
            if (entries.Count > 0)
                await DeleteItemsAsync(JsonSerializer.Serialize(entries.Select(entry => entry.MetadataMessageId), JsonOptions.Web), permanently: true);
            else
                await _bridge.CallAsync("_onAppsLoaded", _loadedApps);
        }
        finally { await _bridge.CallAsync("_onTrashEmptying", false); }
    }

    private async Task DeleteItemsAsync(string idsJson, bool permanently)
    {
        if (!permanently)
        {
            await RelocateTrashItemsAsync(idsJson, restore: false);
            return;
        }
        var ids = JsonSerializer.Deserialize<string[]>(idsJson, JsonOptions.Web) ?? [];
        if (ids.Length == 0)
        {
            await ErrorAsync("Nenhum item selecionado.");
            return;
        }

        await SetBackgroundActivityAsync("Em andamento...", true);
        try
        {
            var registry = Registry();
            var all = LoadedEntriesForLookup().DistinctBy(EntryCacheKey).ToList();
            var selected = ids
                .Select(id => all.FirstOrDefault(a => a.MetadataMessageId == id))
                .Where(entry => entry != null)
                .Cast<AppEntry>()
                .ToList();

            if (selected.Count == 0)
            {
                await ErrorAsync("Nenhum item encontrado para apagar.");
                return;
            }

            var result = await DeleteEntriesWithProgressAsync(registry, all, selected);
            _ = RunAsync(() => PublishLibraryFileCountAsync(registry));
            if (result.Failed == 0)
            {
                await _bridge.CallAsync("_onStatus", $"OK {result.Deleted} item(ns) apagado(s).", false);
            }
            else
            {
                await ErrorAsync($"{result.Deleted}/{result.Total} apagados; {result.Failed} falharam.");
            }
        }
        finally
        {
            await SetBackgroundActivityAsync("", false);
        }
    }

    private async Task PublishDeletedEntriesAsync(IReadOnlyList<AppEntry> deleted)
    {
        Interlocked.Increment(ref _favoriteLoadVersion);
        Interlocked.Increment(ref _deviceLoadVersion);
        var keys = deleted.Select(EntryCacheKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _loadedApps = _loadedApps.Where(entry => !keys.Contains(EntryCacheKey(entry))).ToList();
        _favoriteApps = _favoriteApps.Where(entry => !keys.Contains(EntryCacheKey(entry))).ToList();
        _deviceApps = _deviceApps.Where(entry => !keys.Contains(EntryCacheKey(entry))).ToList();
        await _bridge.CallAsync("_onAppsRemoved", deleted);
    }

    private async Task RelocateTrashItemsAsync(string idsJson, bool restore)
    {
        var ids = JsonSerializer.Deserialize<string[]>(idsJson, JsonOptions.Web) ?? [];
        var all = LoadedEntriesForLookup().DistinctBy(EntryCacheKey).ToList();
        var selected = all.Where(entry => ids.Contains(entry.MetadataMessageId, StringComparer.Ordinal)).ToList();
        selected = selected.Where(entry => !selected.Any(parent => parent != entry && IsFolderEntry(parent) &&
            IsDescendantOfFolder(entry, parent, all))).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("Nenhum item encontrado para esta ação.");

        var registry = Registry();
        await using var serialBatch = registry.BeginSerialBatch();
        var opId = NewId();
        await OpsAddAsync(opId, "move", restore ? "Restaurando da lixeira" : "Enviando à lixeira");
        await SetBackgroundActivityAsync("Em andamento...", true);
        await using var priority = registry.SharedStorage.PrioritizeMutations();
        var devicesChanged = false;
        try
        {
            await using var writes = await registry.BeginTrashWriteBatchAsync(selected, restore);
            for (var i = 0; i < selected.Count; i++)
            {
                var entry = selected[i];
                var changed = new List<AppEntry>();
                var oldFolderPath = IsFolderEntry(entry) ? PathRules.ChildPath(entry) : "";
                var moved = restore
                    ? await registry.RestoreFromTrashAsync(entry)
                    : await registry.MoveToTrashAsync(entry);
                devicesChanged |= entry.ParentFolderId == AppRegistry.SynchronizedRootFolderId ||
                                  moved.ParentFolderId == AppRegistry.SynchronizedRootFolderId;
                if (IsFolderEntry(entry))
                {
                    var newFolderPath = PathRules.ChildPath(moved);
                    foreach (var child in all.Where(child => child != entry && IsDescendantOfFolder(child, entry, all) &&
                                                           IsInsideFolder(child, oldFolderPath)))
                    {
                        child.Path = MovedChildPath(oldFolderPath, newFolderPath, PathRules.Normalize(child.Path));
                        changed.Add(child);
                    }
                }
                ReplaceLocalEntry(all, moved);
                changed.Add(moved);
                var latest = CurrentLocalEntries();
                foreach (var item in changed) ReplaceLocalEntry(latest, item);
                _loadedApps = latest;
                await _bridge.CallAsync("_onAppsBatch", changed);
                await OpsProgressAsync(opId, Percent(i + 1, selected.Count), moved.Name);
            }
            await serialBatch.DisposeAsync();
            Interlocked.Increment(ref _favoriteLoadVersion);
            _favoriteApps = await registry.LoadFavoritesAsync();
            await _bridge.CallAsync("_onSpecialItemsLoaded", "favorites", _favoriteApps);
            if (devicesChanged)
            {
                Interlocked.Increment(ref _deviceLoadVersion);
                _deviceApps = await registry.LoadSynchronizedDevicesAsync();
                await _bridge.CallAsync("_onSpecialItemsLoaded", "devices", _deviceApps);
            }
            var status = restore ? "OK Item(ns) restaurado(s)." : "OK Item(ns) enviado(s) à lixeira.";
            _ = RunAsync(() => PublishLibraryFileCountAsync(registry));
            await OpsUpdateAsync(opId, "done", status);
            await _bridge.CallAsync("_onStatus", status, false);
        }
        catch (Exception ex)
        {
            await OpsUpdateAsync(opId, "error", ex.Message);
            throw;
        }
        finally { await SetBackgroundActivityAsync("", false); }
    }

    private async Task<DeleteResult> DeleteEntriesWithProgressAsync(AppRegistry registry, List<AppEntry> all, IReadOnlyList<AppEntry> selected)
    {
        await using var priority = registry.SharedStorage.PrioritizeMutations();
        await using var serialBatch = registry.BeginSerialBatch();
        foreach (var folder in selected.Where(IsFolderEntry).ToList())
        {
            var descendants = await registry.LoadFolderTreeAsync(folder, allowOrphanThread: true);
            foreach (var descendant in descendants)
            {
                ReplaceLocalEntry(all, descendant);
            }
        }

        var plan = BuildDeletePlan(all, selected);
        var total = plan.Files.Count + plan.Folders.Count;
        if (total == 0)
        {
            return new DeleteResult(0, 0, 0);
        }

        var fileTotal = plan.Files.Count;
        var progressTotal = fileTotal > 0 ? fileTotal : plan.Folders.Count;
        var opId = NewId();
        await OpsAddAsync(opId, "delete", DeletePlanLabel(selected, plan));

        var deletedFiles = 0;
        var deletedFolders = 0;
        var failed = 0;
        var serialFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingCleanup = false;
        if (fileTotal > 0)
        {
            var cleanups = new List<StorageCleanupRecord>();
            using var progressGate = new SemaphoreSlim(1, 1);
            var completed = 0;
            var mappings = _store.LoadFolderIndexMappings();
            var indices = plan.Files.Select(file => mappings.GetValueOrDefault(file.ParentFolderId ?? AppRegistry.RootFolderId)?.ThreadId)
                .Where(thread => !string.IsNullOrWhiteSpace(thread)).Cast<string>();
            var journal = await registry.GetStorageJournalIndexAsync(CancellationToken.None);
            await using var writes = await registry.SharedStorage.HoldWritesAsync(indices.Append(journal.ThreadId), CancellationToken.None);
            await Parallel.ForEachAsync(plan.Files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (file, _) =>
            {
                StorageCleanupRecord? cleanup = null;
                try
                {
                    var details = await registry.LoadFileDetailsAsync(file, forceRefresh: true, cacheDetails: false);
                    cleanup = await registry.SharedStorage.BeginCleanupAsync(details, CancellationToken.None);
                    await registry.DeleteEntryAsync(details, synchronizeSerials: false);
                    ThumbnailCache.Remove(details.ThumbnailCacheKey);
                    _videoProgress.Delete(details.FileId);
                }
                catch (Exception ex)
                {
                    cleanup = null;
                    Interlocked.Increment(ref failed);
                    Debug.WriteLine("[Delete] " + ex.Message);
                }

                await progressGate.WaitAsync();
                try
                {
                    if (cleanup != null)
                    {
                        cleanups.Add(cleanup);
                        TrackFolderSerial(serialFolderIds, file.ParentFolderId);
                        deletedFiles++;
                        all.Remove(file);
                        await PublishDeletedEntriesAsync([file]);
                    }
                    await OpsProgressAsync(opId, 80 * ++completed / fileTotal,
                        $"Removendo metadados ({completed}/{fileTotal}): {file.Name}");
                }
                finally { progressGate.Release(); }
            });

            await QueuePhysicalCleanupAsync(registry, cleanups);
            pendingCleanup = cleanups.Count > 0;
        }

        var foldersToDelete = plan.Folders;
        if (failed > 0 && plan.Files.Count > 0 && plan.Folders.Count > 0)
        {
            foldersToDelete = [];
            await OpsUpdateAsync(opId, "active", "Alguns arquivos falharam; mantendo pastas para nova tentativa...");
        }
        else if (plan.Folders.Count > 0 && plan.Files.Count > 0)
        {
            await OpsUpdateAsync(opId, "active", "Removendo pastas vazias...");
        }

        for (var i = 0; i < foldersToDelete.Count; i++)
        {
            var folder = foldersToDelete[i];
            if (plan.Files.Count == 0)
            {
                await OpsProgressAsync(opId, Percent(i, progressTotal), DeleteProgressMessage("pasta", i, foldersToDelete.Count, folder.Name));
            }

            try
            {
                await DeleteStoredEntryAsync(registry, folder, synchronizeSerials: false);
                TrackFolderSerial(serialFolderIds, folder.ParentFolderId);
                deletedFolders++;
                all.Remove(folder);
                await PublishDeletedEntriesAsync([folder]);
            }
            catch (Exception ex)
            {
                failed++;
                Debug.WriteLine("[Delete] " + ex.Message);
            }

            if (plan.Files.Count == 0)
            {
                await OpsProgressAsync(opId, Percent(i + 1, progressTotal), DeleteProgressMessage("pasta", i + 1, foldersToDelete.Count, folder.Name));
            }
        }

        var status = failed == 0 ? "done" : "error";
        var visibleTotal = fileTotal > 0 ? fileTotal : plan.Folders.Count;
        var visibleDeleted = fileTotal > 0 ? deletedFiles : deletedFolders;
        var visibleKind = fileTotal > 0 ? "arquivo(s)" : "pasta(s)";
        await registry.SynchronizeFolderSerialsAsync(serialFolderIds);
        await serialBatch.DisposeAsync();
        if (failed == 0) await OpsProgressAsync(opId, 100, $"{visibleDeleted}/{visibleTotal} {visibleKind} apagado(s)");
        await OpsUpdateAsync(opId, status, failed == 0
            ? $"{visibleDeleted}/{visibleTotal} {visibleKind} removido(s) do índice" + (pendingCleanup ? "; limpeza física em segundo plano" : "")
            : $"{visibleDeleted}/{visibleTotal} {visibleKind} apagado(s), {failed} falharam");
        return new DeleteResult(visibleTotal, visibleDeleted, failed);
    }

    private async Task DeleteEntryAsync(AppRegistry registry, List<AppEntry> all, AppEntry entry)
    {
        // The delete plan already expands folders recursively in a deterministic order.
        await DeleteStoredEntryAsync(registry, entry);
    }

    private async Task DeleteStoredEntryAsync(
        AppRegistry registry,
        AppEntry entry,
        bool synchronizeSerials = true)
    {
        var isFolder = entry.Type == "folder" || entry.ThreadId.StartsWith("folder_", StringComparison.Ordinal);
        if (!isFolder)
        {
            entry = await registry.LoadFileDetailsAsync(entry, forceRefresh: true);
            var cleanup = await registry.SharedStorage.BeginCleanupAsync(entry, CancellationToken.None);
            await registry.DeleteEntryAsync(entry, synchronizeSerials: synchronizeSerials);
            if (!await registry.SharedStorage.CompleteCleanupAsync(cleanup, CancellationToken.None))
                await _bridge.CallAsync("_onStatus", "Arquivo removido. A limpeza segura dos payloads ficou pendente.", false);
            ThumbnailCache.Remove(entry.ThumbnailCacheKey);
            _videoProgress.Delete(entry.FileId);
            return;
        }

        await registry.DeleteEntryAsync(entry, synchronizeSerials: synchronizeSerials);
        if (!isFolder) _videoProgress.Delete(entry.FileId);
    }

    private static DeletePlan BuildDeletePlan(List<AppEntry> all, IReadOnlyList<AppEntry> selected)
    {
        var files = new List<AppEntry>();
        var folders = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(AppEntry entry)
        {
            if (!seen.Add(DeleteEntryKey(entry)))
            {
                return;
            }

            if (IsFolderEntry(entry))
            {
                folders.Add(entry);
            }
            else
            {
                files.Add(entry);
            }
        }

        foreach (var entry in selected)
        {
            if (IsFolderEntry(entry))
            {
                foreach (var child in all.Where(child => child != entry && IsDescendantOfFolder(child, entry, all)))
                {
                    Add(child);
                }

                Add(entry);
            }
            else
            {
                Add(entry);
            }
        }

        folders = folders
            .OrderByDescending(folder => PathRules.ChildPath(folder).Count(ch => ch == '/'))
            .ThenByDescending(folder => PathRules.ChildPath(folder).Length)
            .ToList();
        return new DeletePlan(files, folders);
    }

    private static bool IsInsideFolder(AppEntry entry, string folderPath)
    {
        var path = PathRules.Normalize(entry.Path);
        return path == folderPath || path.StartsWith(folderPath + "/", StringComparison.Ordinal);
    }

    private static bool IsDescendantOfFolder(AppEntry entry, AppEntry folder, IEnumerable<AppEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(folder.FolderId)) return IsInsideFolder(entry, PathRules.ChildPath(folder));
        var parent = entry.ParentFolderId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(parent) && visited.Add(parent))
        {
            if (parent.Equals(folder.FolderId, StringComparison.OrdinalIgnoreCase)) return true;
            parent = entries.FirstOrDefault(candidate => IsFolderEntry(candidate) &&
                candidate.FolderId.Equals(parent, StringComparison.OrdinalIgnoreCase))?.ParentFolderId;
        }
        return false;
    }

    private static string? FolderIdForPath(IEnumerable<AppEntry> entries, string path)
    {
        var normalized = PathRules.Normalize(path);
        if (normalized == "/")
        {
            return null;
        }

        return entries
            .Where(entry => IsFolderEntry(entry))
            .Where(entry => PathRules.ChildPath(entry).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.MetadataMessageId, StringComparer.Ordinal)
            .FirstOrDefault()
            ?.FolderId;
    }

    private static bool IsFolderEntry(AppEntry entry)
    {
        return entry.Type == "folder" || entry.ThreadId.StartsWith("folder_", StringComparison.Ordinal);
    }

    private static string DeleteEntryKey(AppEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.MetadataMessageId))
        {
            return "metadata:" + entry.MetadataMessageId;
        }

        return $"{entry.Type}:{entry.ThreadId}:{PathRules.Normalize(entry.Path)}:{entry.Name}";
    }

    private static string DeletePlanLabel(IReadOnlyList<AppEntry> selected, DeletePlan plan)
    {
        if (selected.Count == 1 && IsFolderEntry(selected[0]))
        {
            return $"{selected[0].Name} ({plan.Files.Count} arquivo(s))";
        }

        if (plan.Folders.Count > 0)
        {
            return $"{plan.Files.Count} arquivo(s), {plan.Folders.Count} pasta(s)";
        }

        return $"{plan.Files.Count} arquivo(s)";
    }

    private static string DeleteProgressMessage(string kind, int done, int total, string name)
    {
        var remaining = Math.Max(0, total - done);
        if (done <= 0)
        {
            return $"Apagando {total} {kind}(s) - faltam {remaining}";
        }

        return $"({done}/{total}) {name} - faltam {remaining}";
    }

    private static int Percent(int done, int total)
    {
        if (total <= 0)
        {
            return 100;
        }

        return Math.Clamp((int)Math.Round(done * 100.0 / total), 0, 100);
    }

    private async Task SaveMarkdownAsync(JsonElement[] args)
    {
        var tabId = ArgString(args, 0);
        var threadId = ArgString(args, 1);
        var metadataMessageId = ArgString(args, 2);
        var name = ArgString(args, 3);
        var path = ArgString(args, 4);
        var markdown = ArgString(args, 5);
        DirectoryInfo? tempDir = null;
        FileInfo? file = null;

        try
        {
            var safeName = string.IsNullOrWhiteSpace(name) ? "document.md" : name;
            if (!Regex.IsMatch(safeName, @".*\.(md|markdown)$", RegexOptions.IgnoreCase))
            {
                safeName += ".md";
            }

            tempDir = Directory.CreateTempSubdirectory("md_save_");
            file = new FileInfo(System.IO.Path.Combine(tempDir.FullName, PathRules.CleanFileName(safeName)));
            await File.WriteAllTextAsync(file.FullName, markdown ?? "", Encoding.UTF8);

            var settings = _store.Load();
            var registry = Registry();
            var all = CurrentLocalEntries();
            var existing = all.FirstOrDefault(app => app.MetadataMessageId == metadataMessageId && app.Type == "file");
            if (existing != null) existing = await registry.LoadFileDetailsAsync(existing, forceRefresh: true);
            var replacement = existing == null ? null : FileVersioning.PrepareReplacement(existing);
            var uploader = new ForumUploader(RequireApi(), settings.AppsForumId,
                Organessonium.Services.MetadataEncryption.MetadataCodec.ForEncryption(_encryption, () => _store.Load().EncryptionMode));
            var operation = NewId();
            await OpsAddAsync(operation, "upload", safeName);
            var updated = await UploadSingleAsync(operation, 0, 1, file, path, all, uploader, registry,
                settings, new UiThrottle(), CancellationToken.None, replacement: replacement);
            ReplaceLocalEntry(all, updated);
            await PublishLocalRegistryAsync(registry, all);
            await OpsUpdateAsync(operation, "done", "Concluído");
            await _bridge.CallAsync("_onMarkdownSaved", tabId, updated);
        }
        catch (Exception ex)
        {
            await _bridge.CallAsync("_onMarkdownSaveError", tabId, ex.Message);
        }
        finally
        {
            try { file?.Delete(); } catch { }
            if (tempDir != null)
            {
                CleanDir(tempDir);
            }
        }
    }

    private async Task UploadMarkdownImageAsync(string tabId, string threadId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(threadId) || threadId.StartsWith("folder_", StringComparison.Ordinal))
        {
            await _bridge.CallAsync("_onMarkdownImageError", tabId, "Salve o Markdown antes de anexar uma imagem local.");
            return;
        }

        string? selected = null;
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Selecionar imagem",
                Multiselect = false,
                Filter = "Imagens|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.svg;*.tif;*.tiff|Todos os arquivos|*.*"
            };

            if (dialog.ShowDialog(_owner) == true)
            {
                selected = dialog.FileName;
            }
        });

        if (string.IsNullOrWhiteSpace(selected))
        {
            await _bridge.CallAsync("_onMarkdownImageCancelled", tabId);
            return;
        }

        var file = new FileInfo(selected);
        if (!file.Exists)
        {
            await _bridge.CallAsync("_onMarkdownImageError", tabId, "Imagem não encontrada.");
            return;
        }

        if (file.Length > MarkdownInlineImageLimit)
        {
            await _bridge.CallAsync("_onMarkdownImageError", tabId, "Imagem local maior que 10 MB não pode ser inserida diretamente no Markdown.");
            return;
        }

        try
        {
            await SendMarkdownImageFileAsync(tabId, threadId, file);
            await _bridge.CallAsync("_onStatus", "OK Imagem anexada ao post Markdown.", false);
        }
        catch (Exception ex)
        {
            await _bridge.CallAsync("_onMarkdownImageError", tabId, ex.Message);
        }
    }

    private async Task UploadMarkdownImageDataAsync(string tabId, string threadId, string fileName, string dataUrl)
    {
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(threadId) || threadId.StartsWith("folder_", StringComparison.Ordinal))
        {
            await _bridge.CallAsync("_onMarkdownImageError", tabId, "Salve o Markdown antes de anexar uma imagem local.");
            return;
        }

        var comma = dataUrl.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            await _bridge.CallAsync("_onMarkdownImageError", tabId, "Imagem invalida.");
            return;
        }

        DirectoryInfo? tempDir = null;
        FileInfo? file = null;
        try
        {
            var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
            if (bytes.Length > MarkdownInlineImageLimit)
            {
                await _bridge.CallAsync("_onMarkdownImageError", tabId, "Imagem local maior que 10 MB não pode ser inserida diretamente no Markdown.");
                return;
            }

            tempDir = Directory.CreateDirectory(Path.Combine(_store.DataDirectory.FullName, "Temp", "markdown-image-" + NewId()));
            var safeName = CleanUploadName(string.IsNullOrWhiteSpace(fileName) ? "clipboard.png" : fileName);
            if (string.IsNullOrWhiteSpace(Path.GetExtension(safeName)))
            {
                safeName += ".png";
            }

            file = new FileInfo(Path.Combine(tempDir.FullName, safeName));
            await File.WriteAllBytesAsync(file.FullName, bytes);
            await SendMarkdownImageFileAsync(tabId, threadId, file);
            await _bridge.CallAsync("_onStatus", "OK Imagem anexada ao post Markdown.", false);
        }
        catch (Exception ex)
        {
            await _bridge.CallAsync("_onMarkdownImageError", tabId, ex.Message);
        }
        finally
        {
            try { file?.Delete(); } catch { }
            if (tempDir != null)
            {
                CleanDir(tempDir);
            }
        }
    }

    private async Task SendMarkdownImageFileAsync(string tabId, string threadId, FileInfo file)
    {
        await _bridge.CallAsync("_onStatus", "Enviando imagem do Markdown...", true);
        var api = RequireApi();
        var settings = _store.Load();
        var encryption = _encryption.CreateFileEncryption(settings.EncryptionMode);
        var fileId = Guid.NewGuid().ToString("N");
        var context = new EncryptedPayloadContext(fileId, encryption, EncryptedPayloadRoles.MarkdownImage, 0);
        DiscordMessageDto message;
        try
        {
            await api.UnarchiveThreadAsync(threadId);
            using var memory = new UploadMemoryCoordinator(1);
            using var budget = await RamUploadBudget.ReserveAsync(1, default);
            using var staged = await memory.PreparePartAsync(_encryption, file, file.Length,
                EncryptionService.MaximumPlaintextPayloadBytes, context, default);
            message = await api.SendFileMessageAsync(
                threadId,
                PayloadMessage(context, 1),
                staged);
        }
        finally
        {
            try
            {
                await api.ArchiveThreadAsync(threadId);
            }
            catch (Exception ex)
            {
                AppLogger.Error("MarkdownImageArchiveThread", ex, threadId);
            }
        }

        var attachment = message.Attachments.FirstOrDefault();
        if (attachment == null)
        {
            throw new InvalidOperationException("Discord não retornou o anexo da imagem.");
        }

        var reference = new MarkdownEncryptedImageReference(fileId, encryption, message.Id, GuessFileMime(file.Name));
        await _bridge.CallAsync("_onMarkdownImageReady", tabId, new { url = reference.ToUrl(), name = file.Name });
    }

    private async Task ResolveMarkdownImageAsync(string tabId, string threadId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(threadId) || threadId.StartsWith("folder_", StringComparison.Ordinal))
        {
            await _bridge.CallAsync("_onMarkdownImageError", tabId, "Imagem invalida.");
            return;
        }

        try
        {
            var all = CurrentLocalEntries();
            var image = FindLoadedEntry(threadId);
            if (image?.Encryption != null)
            {
                await _bridge.CallAsync("_onMarkdownImageReady", tabId, new { url = "organessonium-drive-image://" + threadId, name = image.Name });
                return;
            }

            var url = await Downloader().GetFirstAttachmentUrlAsync(threadId);
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new InvalidOperationException("Nenhum anexo encontrado para esta imagem.");
            }

            await _bridge.CallAsync("_onMarkdownImageReady", tabId, new { url, name = image?.Name ?? "imagem" });
        }
        catch (Exception ex)
        {
            await _bridge.CallAsync("_onMarkdownImageError", tabId, ex.Message);
        }
    }


    private void SetHostFullscreen(bool enabled)
    {
        if (_owner is Organessonium.MainWindow mainWindow)
        {
            mainWindow.SetBridgeFullscreen(enabled);
        }
    }

    private async Task ResolveEncryptedMarkdownImageAsync(string tabId, string threadId, string value)
    {
        if (!MarkdownEncryptedImageReference.TryParse(value, out var reference))
        {
            throw new InvalidOperationException("A referência da imagem criptografada é inválida.");
        }

        var bytes = await Downloader().DownloadMessageAttachmentBytesAsync(
            threadId,
            reference.MessageId,
            encryption: new EncryptedPayloadContext(
                reference.FileId,
                reference.Encryption,
                EncryptedPayloadRoles.MarkdownImage,
                0));
        if (bytes is not { Length: > 0 })
        {
            throw new InvalidOperationException("Não foi possível abrir a imagem criptografada.");
        }

        var url = _previewResources.RegisterBytes(bytes, reference.MimeType);
        await _bridge.CallAsync("_onEncryptedMarkdownImageReady", tabId, value, url);
    }

    private async Task ResolveEncryptedDriveMarkdownImageAsync(string tabId, string imageThreadId)
    {
        var image = FindLoadedEntry(imageThreadId);
        if (image?.Encryption == null) throw new InvalidDataException("Imagem criptografada não encontrada.");
        if (image == null)
        {
            throw new InvalidOperationException("Não foi possível localizar a imagem criptografada.");
        }

        var tempDir = Directory.CreateTempSubdirectory("organessonium_markdown_image_");
        _previewDirs["markdown-image-" + NewId()] = tempDir;
        var file = await Downloader().DownloadAndAssembleAsync(image, tempDir, _ => { });
        var url = _previewResources.Register(file, string.IsNullOrWhiteSpace(image.MimeType) ? GuessFileMime(image.Name) : image.MimeType);
        await _bridge.CallAsync("_onEncryptedMarkdownImageReady", tabId, "organessonium-drive-image://" + imageThreadId, url);
    }

    private async Task ConfigureCustomEncryptionAsync(string passphrase, bool acknowledged)
    {
        if (!acknowledged)
        {
            throw new InvalidOperationException("Confirme que entende a recuperação necessária da chave personalizada.");
        }

        _encryption.ConfigureCustomKey(passphrase);
        var settings = _store.Load();
        settings.EncryptionMode = EncryptionModes.Custom;
        settings.CustomKeyAvailable = _encryption.HasCustomKey;
        _store.Save(settings);
        await _bridge.SetSettingsAsync(settings);
        await _bridge.CallAsync("_onEncryptionSettingsChanged", settings.EncryptionMode, settings.CustomKeyAvailable);
    }

    private async Task SetEncryptionModeAsync(string mode)
    {
        var normalized = EncryptionModes.Normalize(mode);
        if (normalized == EncryptionModes.Custom && !_encryption.HasCustomKey)
        {
            throw new InvalidOperationException("Configure ou importe uma chave personalizada antes de ativá-la.");
        }

        var settings = _store.Load();
        settings.EncryptionMode = normalized;
        settings.CustomKeyAvailable = _encryption.HasCustomKey;
        _store.Save(settings);
        await _bridge.SetSettingsAsync(settings);
        await _bridge.CallAsync("_onEncryptionSettingsChanged", settings.EncryptionMode, settings.CustomKeyAvailable);
    }

    private async Task ExportCustomEncryptionKeyAsync()
    {
        if (!_encryption.HasCustomKey)
        {
            throw new InvalidOperationException("Nenhuma chave personalizada está configurada.");
        }

        string? selected = null;
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "Exportar chave de recuperação",
                Filter = "Chave de recuperação do Organessônium|*.organessonium-key",
                DefaultExt = ".organessonium-key",
                AddExtension = true,
                FileName = "organessonium-recovery.organessonium-key"
            };
            if (dialog.ShowDialog(_owner) == true)
            {
                selected = dialog.FileName;
            }
        });

        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        await _encryption.ExportCustomKeyAsync(selected);
        await _bridge.CallAsync("_onEncryptionKeyExported");
    }

    private async Task ImportCustomEncryptionKeyAsync()
    {
        string? selected = null;
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Importar chave de recuperação",
                Filter = "Chave de recuperação do Organessônium|*.organessonium-key"
            };
            if (dialog.ShowDialog(_owner) == true)
            {
                selected = dialog.FileName;
            }
        });

        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        await _encryption.ImportCustomKeyAsync(selected);
        var settings = _store.Load();
        settings.EncryptionMode = EncryptionModes.Custom;
        settings.CustomKeyAvailable = _encryption.HasCustomKey;
        _store.Save(settings);
        await _bridge.SetSettingsAsync(settings);
        await _bridge.CallAsync("_onEncryptionSettingsChanged", settings.EncryptionMode, settings.CustomKeyAvailable);
    }

    private void SaveSettings(JsonElement[] args)
    {
        var settings = _store.Load();
        settings.CardSize = ArgString(args, 0, "medium");
        settings.DownloadThreads = Math.Clamp(ArgInt(args, 1, 4), 1, 16);
        settings.Theme = NormalizeTheme(ArgString(args, 2, "system"));
        settings.DownloadPath = WindowsDownloadsFolder.Resolve(ArgString(args, 3));
        settings.PreviewEnabled = ArgBool(args, 5, true);
        settings.Language = NormalizeLanguage(ArgString(args, 6, "system"));
        settings.AnimationsEnabled = ArgBool(args, 7, true);
        settings.HardwareAcceleration = ArgBool(args, 8, true);
        settings.VideoPreviewQuality = NormalizeVideoPreviewQuality(ArgString(args, 9, "low"));
        settings.DataPath = ArgString(args, 10, settings.DataPath);
        settings.UploadThreads = Math.Clamp(ArgInt(args, 11, settings.UploadThreads), 1, 16);
        settings.MaxParallelUploads = Math.Clamp(ArgInt(args, 12, settings.MaxParallelUploads), 1, 8);
        settings.Proxy.Mode = SettingsStore.NormalizeProxyMode(ArgString(args, 13, "automatic"));
        settings.Proxy.Host = ArgString(args, 14, settings.Proxy.Host);
        settings.Proxy.Port = Math.Clamp(ArgInt(args, 15, settings.Proxy.Port), 0, 65535);
        settings.Proxy.Protocol = SettingsStore.NormalizeProxyProtocol(ArgString(args, 16, "auto"));
        settings.Proxy.AuthenticationEnabled = ArgBool(args, 17, settings.Proxy.AuthenticationEnabled);
        settings.Proxy.Username = ArgString(args, 18, settings.Proxy.Username);
        settings.Proxy.Password = ArgString(args, 19, settings.Proxy.Password);
        settings.PreviewThreads = Math.Clamp(ArgInt(args, 20, settings.PreviewThreads), 1, 8);
        settings.VideoPrefetchChunks = Math.Clamp(ArgInt(args, 21, settings.VideoPrefetchChunks), 0, VideoStreamSession.MaximumPrefetchChunks);
        settings.ForceUploadParallelism = ArgBool(args, 22, settings.ForceUploadParallelism);
        settings.AccentColorMode = WindowsAccentService.NormalizeMode(ArgString(args, 23, settings.AccentColorMode));
        settings.AccentColor = WindowsAccentService.NormalizeColor(ArgString(args, 24, settings.AccentColor));
        settings.GenerateVideoPreviews = ArgBool(args, 25, settings.GenerateVideoPreviews);
        if (settings.Proxy.Mode == ProxyMode.Manual && !settings.Proxy.IsValidManual(out var _))
        {
            settings.Proxy.Mode = ProxyMode.Automatic;
        }

        NetworkConfig.Current.Apply(settings.Proxy);
        UploadConcurrencyRegistry.Apply(settings.ForceUploadParallelism, settings.UploadThreads);
        _store.Save(settings);
        settings.DataPath = _store.PrepareDataDirectoryForNextStartup(settings.DataPath);
        _store.Save(settings);
        _ = _bridge.SetSettingsAsync(settings);
        _onThemeChanged(settings.Theme);
    }

    private async Task TestProxyAsync(JsonElement[] args)
    {
        var settings = new ProxySettings
        {
            Mode = ProxyMode.Manual,
            Host = ArgString(args, 0).Trim(),
            Port = Math.Clamp(ArgInt(args, 1, 0), 0, 65535),
            Protocol = SettingsStore.NormalizeProxyProtocol(ArgString(args, 2, "auto")),
            AuthenticationEnabled = ArgBool(args, 3, false),
            Username = ArgString(args, 4),
            Password = ArgString(args, 5)
        };

        if (settings.Port == 0)
        {
            await _bridge.CallAsync("_onProxyTestResult", false, "Informe a porta do proxy.", "");
            return;
        }

        var result = await ProxyTester.TestAsync(settings, includeDiscordCheck: ArgBool(args, 6, true));
        await _bridge.CallAsync("_onProxyTestResult", result.Success, result.Message, ProxyProtocolLabel(result.DetectedProtocol));
    }

    private static string ProxyProtocolLabel(ProxyProtocol? protocol)
    {
        return protocol switch
        {
            ProxyProtocol.Http => "HTTP",
            ProxyProtocol.Https => "HTTPS",
            ProxyProtocol.Socks5 => "SOCKS5",
            ProxyProtocol.Socks4a => "SOCKS4a",
            ProxyProtocol.Socks4 => "SOCKS4",
            _ => ""
        };
    }

    private async Task ChoosePathAsync(string type, string title)
    {
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog { Title = title };
            if (dialog.ShowDialog(_owner) == true)
            {
                _ = _bridge.CallAsync("_onPathChosen", type, dialog.FolderName);
            }
        });
    }


    private async Task ChooseDataPathAsync()
    {
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog { Title = "Pasta onde salvar OrganessoniumData" };
            if (dialog.ShowDialog(_owner) == true)
            {
                var dataPath = SettingsStore.NormalizeDataDirectorySelection(dialog.FolderName);
                _ = _bridge.CallAsync("_onPathChosen", "data", dataPath);
            }
        });
    }
    private Task<DirectoryInfo?> ResolveDownloadDirectoryAsync(string title)
    {
        var path = WindowsDownloadsFolder.Resolve(_store.Load().DownloadPath);
        return Task.FromResult<DirectoryInfo?>(Directory.CreateDirectory(path));
    }

    private AppRegistry Registry()
    {
        var registry = _registry ??= new AppRegistry(RequireApi(), _store, _store.Load());
        ObserveRegistryCleanup(registry);
        return registry;
    }

    private ForumDownloader Downloader()
    {
        return new ForumDownloader(RequireApi(), _store.Load(), _encryption, storageReads: new StorageReadContext(Registry().SharedStorage));
    }

    private DiscordApiClient RequireApi()
    {
        return _session?.RequireApi() ?? throw new InvalidOperationException("Discord ainda não conectado.");
    }

    private async Task RunAsync(Func<Task> work, bool accountWork = true)
    {
        if (_loggingOut && accountWork) return;
        var id = NewId();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (accountWork) _accountWork[id] = completion;
        using var diagnosticOperation = Services.Diagnostics.DevelopmentDiagnostics.Begin("work");
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            AppLogger.Error("RunAsync", ex);
            if (!_loggingOut) await ErrorAsync(ex.Message);
        }
        finally { if (accountWork) _accountWork.TryRemove(id, out _); completion.TrySetResult(); }
    }

    private async Task ErrorAsync(string message)
    {
        await _bridge.CallAsync("_onError", message);
    }

    private async Task OpsAddAsync(string id, string type, string name)
    {
        await _bridge.CallAsync("_opsAdd", id, type, name);
    }

    private async Task OpsUploadBatchAsync(string id, IReadOnlyList<string> names, IReadOnlyList<long>? sizes = null)
    {
        await _bridge.CallAsync("_opsUploadBatch", id, names, sizes);
    }

    private async Task OpsUploadFileProgressAsync(string id, int fileIndex, string status, int percent, string message)
    {
        await _bridge.CallAsync("_opsUploadFileProgress", id, fileIndex, status, percent, message);
    }
    private async Task OpsUpdateAsync(string id, string status, string message)
    {
        await _bridge.CallAsync("_opsUpdate", id, status, message);
    }

    private async Task OpsProgressAsync(string id, int percent, string message)
    {
        await _bridge.CallAsync("_opsProgress", id, percent, message);
    }

    private async Task OpsTransferMetricsAsync(
        string id,
        string direction,
        int completedItems,
        int totalItems,
        long transferredBytes,
        long totalBytes,
        double? activeTransferSeconds = null,
        long? rateBytes = null)
    {
        await _bridge.CallAsync(
            "_opsTransferMetrics",
            id,
            direction,
            Math.Max(0, completedItems),
            Math.Max(1, totalItems),
            Math.Max(0, transferredBytes),
            Math.Max(0, totalBytes),
            activeTransferSeconds.HasValue
                ? Math.Max(0, activeTransferSeconds.Value)
                : null,
            rateBytes);
    }

    private CancellationToken BeginTransferOperation(string opId)
    {
        if (_loggingOut)
        {
            return new CancellationToken(true);
        }

        var cts = new CancellationTokenSource();
        _transferOperations[opId] = cts;
        _transferCompletions[opId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return cts.Token;
    }

    private void EndTransferOperation(string opId)
    {
        if (_transferOperations.TryRemove(opId, out var cts))
        {
            cts.Dispose();
        }

        if (_transferCompletions.TryRemove(opId, out var completion))
        {
            completion.TrySetResult();
        }
    }

    private async Task CancelActiveTransfersAndWaitAsync()
    {
        foreach (var transfer in _transferOperations.Values)
        {
            try { transfer.Cancel(); } catch { }
        }

        var completions = _transferCompletions.Values.Select(completion => completion.Task).ToArray();
        if (completions.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException ex)
        {
            AppLogger.Error("Logout.WaitTransfers", ex);
        }
    }

    private async Task CancelThumbnailRequestsAndWaitAsync()
    {
        CancellationTokenSource? request;
        lock (_thumbnailRequestSync)
        {
            request = _thumbnailRequestCts;
            _thumbnailRequestCts = null;
            _thumbnailRequestKey = "";
        }

        try { request?.Cancel(); } catch { }
        _thumbnailLoads.Clear();

        var completions = _thumbnailRequests.Values.Select(completion => completion.Task).ToArray();
        if (completions.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException ex)
        {
            AppLogger.Error("Logout.WaitThumbnails", ex);
        }
    }

    private void ClearPreviewState()
    {
        var previewIds = _previewDirs.Keys
            .Concat(_previewUrls.Keys.Select(key => key.Split('|', 2)[0]))
            .Concat(_videoOpenRequests.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var threadId in previewIds)
        {
            ClosePreview(threadId);
        }

        ThumbnailCache.Clear();
        _thumbnailLoads.Clear();
    }

    private void CancelUploadFile(string opId, int fileIndex)
    {
        if (_uploadFileOperations.TryGetValue(opId + ":" + fileIndex, out var cancellation))
        {
            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void CancelTransfers(string idsJson)
    {
        string[] ids;
        try
        {
            ids = JsonSerializer.Deserialize<string[]>(idsJson, JsonOptions.Web) ?? [];
        }
        catch
        {
            ids = [];
        }

        var targets = ids.Length > 0
            ? ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : _transferOperations.Keys.ToList();

        foreach (var id in targets)
        {
            if (_transferOperations.TryGetValue(id, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        _ = _bridge.CallAsync("_onStatus", "Cancelando transferência...", true);
    }

    private async Task NotifyInterruptedTransfersAsync()
    {
        if (Interlocked.Exchange(ref _interruptedTransfersChecked, 1) != 0) return;
        var states = (await _transferManager.RecoverableAsync()).Where(state => state.SyncFolderId == null).ToList();
        if (states.Count == 0)
        {
            return;
        }

        await _bridge.CallAsync("_onInterruptedTransfers", states.Count);
    }

    private async Task FailInterruptedTransfersAsync()
    {
        var states = (await _transferManager.RecoverableAsync()).Where(state => state.SyncFolderId == null).ToList();
        foreach (var state in states)
        {
            await _transferManager.FailAsync(state, new InvalidOperationException("Marcada como falha pelo usuário."), CancellationToken.None);
        }

        await _bridge.CallAsync("_onStatus", "Transferências interrompidas marcadas como falha.", false);
    }

    private Task ResumeInterruptedTransfersAsync()
    {
        return ResumeInterruptedTransfersAsync(null, silent: false);
    }

    private async Task ResumeInterruptedTransfersAsync(
        IReadOnlyList<TransferState>? selectedStates,
        bool silent)
    {
        if (Interlocked.CompareExchange(ref _resumingTransfers, 1, 0) != 0) return;
        try { await ResumeInterruptedTransfersCoreAsync(selectedStates, silent); }
        finally { Volatile.Write(ref _resumingTransfers, 0); }
    }

    private async Task ResumeInterruptedTransfersCoreAsync(IReadOnlyList<TransferState>? selectedStates, bool silent)
    {
        using var networkWait = NetworkRetryPolicy.TransferScope();
        var states = (selectedStates ?? await _transferManager.RecoverableAsync()).Where(state => state.SyncFolderId == null).ToList();
        if (states.Count == 0)
        {
            if (!silent)
            {
                await _bridge.CallAsync("_onStatus", "Nenhuma transferência interrompida.", false);
            }
            return;
        }

        if (!silent)
        {
            await _bridge.CallAsync("_onStatus", $"Retomando {states.Count} transferência(s)...", true);
        }
        var settings = _store.Load();
        var registry = Registry();
        await using var serialBatch = registry.BeginSerialBatch();
        await using var indexBatch = await registry.BeginUploadIndexBatchAsync();
        var all = CurrentLocalEntries();
        var resumed = 0;
        var serialFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uploads = new List<UploadTask>();
        foreach (var state in states.Where(state => state.TransferType == TransferTypes.Upload))
        {
            var source = new FileInfo(state.SourcePath);
            if (!await Task.Run(() => source.Exists))
            {
                await OpsAddAsync(state.TransferId, "upload", state.FileName);
                await OpsUpdateAsync(state.TransferId, "error", "Arquivo de origem não existe mais.");
                continue;
            }
            uploads.Add(new UploadTask(source, state.TargetPath, state.Replacement) { PreparedTransfer = state, Resuming = true });
        }
        if (uploads.Count > 0)
        {
            var uploadId = NewId();
            var token = BeginTransferOperation(uploadId);
            await OpsUploadBatchAsync(uploadId, uploads.Select(task => task.PreparedTransfer!.FileName).ToArray(), uploads.Select(task => task.File.Length).ToArray());
            try
            {
                await RunUploadQueueAsync(uploadId, uploads, all, serialFolderIds, token);
                resumed += uploads.Count;
            }
            finally { EndTransferOperation(uploadId); }
        }

        foreach (var state in states.Where(state => state.TransferType == TransferTypes.Download))
        {
            var opId = state.TransferId;
            var cancellationToken = BeginTransferOperation(opId);
            await OpsAddAsync(opId, state.TransferType, state.FileName);
            try
            {
                state.Status = TransferStatuses.Recovering;
                await _transferManager.SaveAsync(state, cancellationToken);

                if (state.TransferType.Equals(TransferTypes.Download, StringComparison.OrdinalIgnoreCase))
                {
                    var entry = state.DownloadMetadata == null ? all.FirstOrDefault(app =>
                        (!string.IsNullOrWhiteSpace(state.FileId) && app.FileId == state.FileId) ||
                        (state.Storage?.Shared != true && !string.IsNullOrWhiteSpace(state.ThreadId) && app.ThreadId == state.ThreadId))
                        : new MetadataService().ToAppEntry(new MetadataEnvelope(state.MetadataMessageId ?? "", state.DownloadMetadata));
                    if (entry == null)
                    {
                        throw new InvalidOperationException("Arquivo não encontrado na listagem atual para retomar download.");
                    }
                    if (state.DownloadMetadata == null) entry = await registry.LoadFileDetailsAsync(entry, cancellationToken);

                    var destination = new DirectoryInfo(string.IsNullOrWhiteSpace(state.DestinationPath)
                        ? (settings.DownloadPath.Trim().Length > 0 ? settings.DownloadPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                        : state.DestinationPath);
                    var downloadBytes = Math.Max(0, entry.SizeBytes);
                    long receivedBytes = 0;
                    await OpsTransferMetricsAsync(opId, "download", 0, 1, 0, downloadBytes);
                    await RunPausableDownloadAsync(opId, state, cancellationToken, async attemptToken =>
                    {
                        await Downloader().DownloadAndAssembleAsync(
                        entry,
                        destination,
                        state,
                        _transferManager,
                        msg => _ = OpsUpdateAsync(opId, "active", msg),
                        attemptToken,
                        progress =>
                        {
                            _ = OpsProgressAsync(opId, Math.Min(99, progress.Percent), progress.Message);
                            if (!progress.Assembling)
                            {
                                Interlocked.Exchange(ref receivedBytes, progress.RateBytes ?? progress.DownloadedBytes);
                                _ = OpsTransferMetricsAsync(opId, "download", 0, 1, progress.DownloadedBytes, progress.TotalBytes, rateBytes: progress.RateBytes);
                            }
                        });
                    });
                    await _transferManager.CompleteAsync(state, settings.KeepTransferHistory, cancellationToken);
                    await OpsTransferMetricsAsync(opId, "download", 1, 1, downloadBytes, downloadBytes, rateBytes: Interlocked.Read(ref receivedBytes));
                    resumed++;
                    await OpsUpdateAsync(opId, "done", "Concluído");
                }
            }
            catch (OperationCanceledException)
            {
                await _transferManager.CancelAsync(
                    state,
                    settings.KeepTransferHistory,
                    settings.CleanupTempOnCancel,
                    CancellationToken.None);
                await OpsUpdateAsync(opId, "cancelled", "Cancelado");
            }
            catch (VerifiedPayloadSizeMismatchException mismatch)
            {
                await _transferManager.CompleteAsync(state, settings.KeepTransferHistory, CancellationToken.None);
                await OfferOpenVerifiedSizeMismatchAsync(mismatch);
                resumed++;
                await OpsUpdateAsync(opId, "done", "Concluído com aviso.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Transfer.Resume", ex, state.TransferId);
                await _transferManager.RecordDownloadFailureAsync(state, ex, CancellationToken.None);
                await OpsUpdateAsync(opId, "error", ex.Message);
            }
            finally
            {
                EndTransferOperation(opId);
            }
        }

        await registry.SynchronizeFolderSerialsAsync(serialFolderIds);
        await serialBatch.DisposeAsync();
        await PublishLocalRegistryAsync(registry, CurrentLocalEntries(), incremental: true);
        if (!silent)
        {
            await _bridge.CallAsync("_onStatus", $"{resumed}/{states.Count} transferência(s) retomada(s).", false);
        }
    }

    private async Task RecoverUnindexedPayloadPostsAsync()
    {
        if (Interlocked.CompareExchange(ref _unindexedPayloadScanStarted, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var settings = _store.Load();
            if (string.IsNullOrWhiteSpace(settings.AppsForumId))
            {
                return;
            }

            var threads = await RequireApi().GetForumThreadsAsync(settings.AppsForumId);
            var indexed = _store.LoadCachedFileThreadIds();
            var orphanThreadIds = threads
                .Where(thread => !thread.Name.StartsWith("storage-", StringComparison.Ordinal) && !RequireApi().IsSharedThread(thread.Id))
                .Select(thread => thread.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id) && !indexed.Contains(id))
                .ToHashSet(StringComparer.Ordinal);
            if (orphanThreadIds.Count == 0)
            {
                return;
            }

            var recoverable = await _transferManager.RecoverableAsync();
            var repairable = recoverable
                .Where(state =>
                    state.SyncFolderId == null &&
                    state.TransferType.Equals(TransferTypes.Upload, StringComparison.OrdinalIgnoreCase) &&
                    orphanThreadIds.Contains(state.ThreadId) &&
                    !_transferOperations.ContainsKey(state.TransferId) &&
                    File.Exists(state.SourcePath))
                .ToList();
            AppLogger.Info(
                "Registry.UnindexedPayloadPosts",
                $"Órfãos detectados: {orphanThreadIds.Count}; recuperáveis: {repairable.Count}.");
            if (repairable.Count > 0)
            {
                await NotifyInterruptedTransfersAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // App shutdown cancels the silent repair.
        }
        catch (Exception ex)
        {
            AppLogger.Error("Registry.UnindexedPayloadPosts", ex);
        }
    }

    private async Task<bool> OfferRetryEncryptedDownloadAsync(EncryptedPayloadIntegrityException integrity)
    {
        var shouldRetry = false;
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new Window
            {
                Owner = _owner,
                Title = "Organessônium",
                Width = 500,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var panel = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(24),
                Width = 452
            };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = integrity.EncryptionMode == EncryptionModes.Custom
                    ? "Não foi possível autenticar este arquivo. Verifique se a chave de criptografia correta está configurada."
                    : "Não foi possível verificar a integridade deste arquivo.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            });
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "O download foi tentado três vezes com dados novos. Nenhum conteúdo foi aberto.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
                Opacity = 0.72
            });

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0)
            };
            var closeButton = new System.Windows.Controls.Button
            {
                Content = "Fechar",
                MinWidth = 88,
                IsCancel = true
            };
            closeButton.Click += (_, _) => dialog.DialogResult = false;
            var retryButton = new System.Windows.Controls.Button
            {
                Content = "Tentar novamente",
                MinWidth = 136,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true
            };
            retryButton.Click += (_, _) =>
            {
                shouldRetry = true;
                dialog.DialogResult = true;
            };
            buttons.Children.Add(closeButton);
            buttons.Children.Add(retryButton);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            dialog.ShowDialog();
        });
        return shouldRetry;
    }

    private async Task<bool> OfferOpenVerifiedSizeMismatchAsync(VerifiedPayloadSizeMismatchException mismatch)
    {
        var shouldOpen = false;
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            mismatch.RecoveredFile.Refresh();
            if (!mismatch.RecoveredFile.Exists)
            {
                return;
            }

            var dialog = new Window
            {
                Owner = _owner,
                Title = "Organessônium",
                Width = 500,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var panel = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(24),
                Width = 452
            };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "A integridade criptográfica foi validada, mas os metadados do arquivo não correspondem ao conteúdo recuperado.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14
            });
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"Esperado: {mismatch.ExpectedLength:N0} bytes\nRecuperado: {mismatch.ActualLength:N0} bytes",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 12, 0, 0),
                Opacity = 0.72
            });

            var buttons = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0)
            };
            var closeButton = new System.Windows.Controls.Button
            {
                Content = "Fechar",
                MinWidth = 88,
                IsCancel = true
            };
            closeButton.Click += (_, _) => dialog.DialogResult = false;
            var openButton = new System.Windows.Controls.Button
            {
                Content = "Abrir mesmo assim",
                MinWidth = 148,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = true
            };
            openButton.Click += (_, _) =>
            {
                shouldOpen = true;
                dialog.DialogResult = true;
            };
            buttons.Children.Add(closeButton);
            buttons.Children.Add(openButton);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            dialog.ShowDialog();
        });

        if (!shouldOpen)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(mismatch.RecoveredFile.FullName) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Download.OpenVerifiedSizeMismatch", ex, mismatch.RecoveredFile.FullName);
            return false;
        }
    }

    private async Task ReportUploadItemProgressAsync(
        string opId,
        int fileIndex,
        int fileTotal,
        int itemPercent,
        string message,
        UiThrottle throttle,
        bool force,
        bool useBatchUi,
        string batchStatus = "active",
        UploadProgressTracker? progressTracker = null)
    {
        if (useBatchUi)
        {
            var now = Environment.TickCount64;
            if (!force && now - throttle.Last < 300)
            {
                return;
            }

            throttle.Last = now;
            await OpsUploadFileProgressAsync(opId, fileIndex, batchStatus, itemPercent, message);
            return;
        }

        await ReportUploadProgressAsync(
            opId,
            progressTracker?.Current ?? Math.Max(0, itemPercent),
            $"({fileIndex + 1}/{fileTotal}) {message}",
            throttle,
            force);
    }

    private async Task ReportUploadProgressAsync(string opId, int percent, string message, UiThrottle throttle, bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - throttle.Last < 300)
        {
            return;
        }

        throttle.Last = now;
        await OpsProgressAsync(opId, Math.Clamp(percent, 0, 100), message);
    }

    private async Task<AppEntry> CreateUploadFolderEntryAsync(
        AppRegistry registry,
        List<AppEntry> all,
        string name,
        string parentPath,
        ISet<string> serialFolderIds)
    {
        var folder = AppEntry.Folder(PathRules.CleanFileName(name), parentPath);
        folder.ParentFolderId = FolderIdForPath(all, parentPath);
        await registry.AddEntryAsync(folder, synchronizeSerials: false);
        ReplaceLocalEntry(all, folder);
        var visibleEntries = CurrentLocalEntries();
        ReplaceLocalEntry(visibleEntries, folder);
        await PublishLocalRegistryAsync(registry, visibleEntries, refreshStatistics: false, incremental: true, changedEntry: folder);
        TrackFolderSerial(serialFolderIds, folder.ParentFolderId);
        TrackFolderSerial(serialFolderIds, folder.FolderId);
        return folder;
    }

    private async Task<int> CollectFolderUploadTasksAsync(
        AppRegistry registry,
        List<AppEntry> all,
        DirectoryInfo folder,
        string folderVirtualPath,
        List<UploadTask> output,
        ISet<string> serialFolderIds,
        Action onFolderCreated)
    {
        var skipped = 0;
        async Task WalkAsync(DirectoryInfo dir, string virtualPath)
        {
            FileInfo[] files;
            try
            {
                files = dir.GetFiles();
            }
            catch
            {
                skipped++;
                return;
            }

            foreach (var file in files)
            {
                var relativeParent = Path.GetRelativePath(folder.FullName, file.DirectoryName ?? folder.FullName);
                var rel = relativeParent == "." ? "" : relativeParent.Replace(System.IO.Path.DirectorySeparatorChar, '/');
                var targetPath = string.IsNullOrWhiteSpace(rel) ? virtualPath : folderVirtualPath + "/" + rel;
                output.Add(new UploadTask(file, PathRules.Normalize(targetPath)));
            }

            DirectoryInfo[] dirs;
            try
            {
                dirs = dir.GetDirectories();
            }
            catch
            {
                skipped++;
                return;
            }

            foreach (var child in dirs)
            {
                if (FfmpegPreviewUpload.IsStagingDirectory(child.Name)) continue;
                var childFolder = await CreateUploadFolderEntryAsync(
                    registry,
                    all,
                    child.Name,
                    virtualPath,
                    serialFolderIds);
                onFolderCreated();
                await WalkAsync(child, PathRules.ChildPath(childFolder));
            }
        }

        await WalkAsync(folder, folderVirtualPath);
        return skipped;
    }

    private DirectoryInfo CreatePreviewTempDir(string threadId)
    {
        var safe = Regex.Replace(threadId ?? "item", "[^a-zA-Z0-9_-]", "");
        if (safe.Length > 16)
        {
            safe = safe[..16];
        }

        if (safe.Length < 3)
        {
            safe = "item";
        }

        return Directory.CreateTempSubdirectory("organessonium_preview_" + safe + "_");
    }

    private async Task<long> RegisteredFileSizeAsync(string threadId)
    {
        try
        {
            return FindLoadedEntry(threadId)?.SizeBytes ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private Task SendPreviewReadyAsync(string type, string name, string data, string? requestId = null)
    {
        return _bridge.CallAsync("_onPreviewReady", type, name, data, requestId);
    }

    private static string? PreviewType(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (MediaFileTypes.IsImage(name)) return "image";
        if (MediaFileTypes.IsVideo(name)) return "video";
        if (new[] { "mp3", "flac", "wav", "ogg", "aac", "m4a" }.Contains(ext)) return "audio";
        if (ext == "pdf") return "pdf";
        if (ext is "md" or "markdown") return "markdown";
        if (new[] { "odt", "ods", "odp", "rtf" }.Contains(ext)) return "document";
        if (new[] { "zip", "zipx", "jar", "war", "ear", "apk", "cbz", "epub", "xpi", "rar", "7z", "tar", "gz", "tgz", "bz2", "bzip2", "xz", "txz", "zst", "lzma", "cab", "iso", "arj", "lzh", "wim", "dmg", "deb", "rpm" }.Contains(ext)) return "archive";
        if (new[] { "txt", "json", "xml", "yaml", "yml", "log", "csv", "js", "ts", "java", "py", "html", "css", "sh", "bat", "ini", "cfg", "c", "cpp", "cs", "go", "rs", "rb" }.Contains(ext)) return "text";
        return null;
    }

    private static string GuessMime(string name)
    {
        if (MediaFileTypes.IsImage(name)) return MediaFileTypes.MimeType(name)!;
        return Path.GetExtension(name).TrimStart('.').ToLowerInvariant() switch
        {
            "png" => "image/png",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "svg" => "image/svg+xml",
            _ => "image/jpeg"
        };
    }

    private static string GuessFileMime(string name)
    {
        var type = PreviewType(name);
        return type switch
        {
            "image" => GuessMime(name),
            "video" or "audio" => MediaMime(name),
            "pdf" => "application/pdf",
            "markdown" => "text/markdown",
            "text" => "text/plain",
            "archive" => "application/octet-stream",
            _ => "application/octet-stream"
        };
    }

    private static string StripExtension(string name)
    {
        var stripped = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(stripped) ? name : stripped;
    }

    private static string PreviewFileName(string name, string extension)
    {
        var safeBase = PathRules.CleanFileName(StripExtension(name)).Replace('.', '_');
        if (string.IsNullOrWhiteSpace(safeBase))
        {
            safeBase = "preview";
        }

        return $"organessonium_preview_{safeBase}_{Guid.NewGuid():N}{extension}";
    }

    private static string VideoPreviewFileName(string name)
    {
        var safeBase = PathRules.CleanFileName(StripExtension(name)).Replace('.', '_');
        if (string.IsNullOrWhiteSpace(safeBase))
        {
            safeBase = "preview";
        }

        return $"organessonium_video_480p_{safeBase}_{Guid.NewGuid():N}.mp4";
    }

    private static string VideoPreviewCacheName(string name)
    {
        var safeBase = PathRules.CleanFileName(StripExtension(name)).Replace('.', '_');
        return (string.IsNullOrWhiteSpace(safeBase) ? "preview" : safeBase) + ".mp4";
    }

    private static string MediaMime(string name)
    {
        if (MediaFileTypes.IsVideo(name)) return MediaFileTypes.MimeType(name)!;
        return Path.GetExtension(name).TrimStart('.').ToLowerInvariant() switch
        {
            "mp4" or "m4v" => "video/mp4",
            "webm" => "video/webm",
            "mov" => "video/quicktime",
            "mkv" => "video/x-matroska",
            "avi" => "video/x-msvideo",
            "3gp" => "video/3gpp",
            "mp3" => "audio/mpeg",
            "flac" => "audio/flac",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "aac" => "audio/aac",
            "m4a" => "audio/mp4",
            _ => "application/octet-stream"
        };
    }

    private static Stream OpenPreviewRead(FileInfo file) =>
        EncryptedPreviewCache.IsEncrypted(file) ? EncryptedPreviewCache.OpenRead(file) : file.OpenRead();

    private static ZipArchive OpenPreviewZip(FileInfo file) => new(OpenPreviewRead(file), ZipArchiveMode.Read);

    private static string ReadPreviewText(FileInfo file)
    {
        using var reader = new StreamReader(OpenPreviewRead(file), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private string ExtractDocumentPreview(FileInfo file, string name)
    {
        try
        {
            return Path.GetExtension(name).TrimStart('.').ToLowerInvariant() switch
            {
                "docx" => ExtractDocxPreview(file),
                "odt" => ExtractOdtPreview(file),
                "ods" => ExtractOdsPreview(file),
                "odp" => ExtractOdpPreview(file),
                "xlsx" or "xlsm" => ExtractXlsxPreview(file, Path.GetExtension(name).TrimStart('.').ToUpperInvariant()),
                "pptx" => ExtractPptxPreview(file),
                "rtf" => StripRtf(ReadPreviewText(file)),
                "doc" => "Prévia indisponível para arquivos .doc legados. Converta para .docx ou baixe o arquivo para abrir no editor.",
                _ => ReadPreviewText(file)
            };
        }
        catch (Exception ex)
        {
            return "Não foi possível extrair a prévia deste documento.\n\n" + ex.Message;
        }
    }

    private static string ExtractDocxPreview(FileInfo file)
    {
        using var zip = OpenPreviewZip(file);
        var entry = zip.GetEntry("word/document.xml");
        if (entry == null)
        {
            return DocumentPreviewJson("DOCX", ["<p>Nenhum texto encontrado neste documento.</p>"]);
        }

        using var stream = entry.Open();
        var doc = ParseXml(stream);
        var styles = ReadDocxStyles(zip);
        var body = DirectChild(doc.DocumentElement, "w:body") ?? FirstDescendant(doc, "body");
        var pages = new List<string>();
        var blocks = new List<string>();
        var units = 0;
        if (body != null)
        {
            foreach (XmlNode child in body.ChildNodes)
            {
                if (IsXml(child, "p"))
                {
                    if (HasDescendant(child, "w:lastRenderedPageBreak") && blocks.Count > 0)
                    {
                        AddDocumentPage(pages, blocks);
                        units = 0;
                    }

                    var paragraph = RenderDocxParagraph(child, styles);
                    AppendDocumentBlock(pages, blocks, paragraph, ref units);
                }
                else if (IsXml(child, "tbl"))
                {
                    var table = RenderDocxTable(child, styles);
                    AppendDocumentBlock(pages, blocks, table, ref units);
                }
            }
        }
        else
        {
            foreach (XmlNode paragraph in doc.GetElementsByTagName("w:p"))
            {
                var block = RenderDocxParagraph(paragraph, styles);
                AppendDocumentBlock(pages, blocks, block, ref units);
            }
        }


        AddDocumentPage(pages, blocks);
        if (pages.Count == 0)
        {
            pages.Add("<p>Nenhum texto encontrado neste documento.</p>");
        }

        return DocumentPreviewJson("DOCX", pages);
    }

    private static Dictionary<string, DocxStyle> ReadDocxStyles(ZipArchive zip)
    {
        var output = new Dictionary<string, DocxStyle>(StringComparer.OrdinalIgnoreCase);
        var entry = zip.GetEntry("word/styles.xml");
        if (entry == null)
        {
            return output;
        }

        try
        {
            using var stream = entry.Open();
            var doc = ParseXml(stream);
            foreach (XmlNode style in doc.GetElementsByTagName("w:style"))
            {
                var id = Attr(style, "w:styleId", "styleId");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var pPr = DirectChild(style, "w:pPr");
                var rPr = DirectChild(style, "w:rPr");
                output[id] = new DocxStyle(
                    Alignment: ReadAlignment(pPr),
                    Bold: HasDirectChild(rPr, "w:b"),
                    Italic: HasDirectChild(rPr, "w:i"),
                    Underline: HasDirectChild(rPr, "w:u"),
                    FontSize: ReadFontSize(rPr));
            }
        }
        catch
        {
            // Styles are optional; document.xml still carries the text.
        }

        return output;
    }

    private static DocxParagraph RenderDocxParagraph(XmlNode paragraph, IReadOnlyDictionary<string, DocxStyle> styles)
    {
        var pPr = DirectChild(paragraph, "w:pPr");
        var styleId = Attr(DirectChild(pPr, "w:pStyle"), "w:val", "val");
        styles.TryGetValue(styleId ?? "", out var inherited);
        var text = new StringBuilder();
        var pageBreakAfter = false;

        foreach (XmlNode child in paragraph.ChildNodes)
        {
            if (child.Name == "w:r")
            {
                text.Append(RenderDocxRun(child, inherited, ref pageBreakAfter));
            }
            else if (child.Name == "w:hyperlink")
            {
                foreach (XmlNode run in child.ChildNodes)
                {
                    if (run.Name == "w:r")
                    {
                        text.Append(RenderDocxRun(run, inherited, ref pageBreakAfter));
                    }
                }
            }
        }

        var content = text.ToString();
        var plain = Regex.Replace(content, "<.*?>", "").Replace("&nbsp;", " ").Trim();
        var isEmpty = string.IsNullOrWhiteSpace(plain);
        var tag = ResolveDocxTag(styleId, pPr);
        var css = BuildDocxParagraphCss(pPr, inherited, isEmpty);
        var cls = isEmpty ? "docx-p docx-empty" : "docx-p";
        var html = isEmpty
            ? $"<{tag} class=\"{cls}\" style=\"{css}\">&nbsp;</{tag}>"
            : $"<{tag} class=\"{cls}\" style=\"{css}\">{content}</{tag}>";

        var units = isEmpty ? 1 : Math.Max(1, (plain.Length + 90) / 91);
        if (HasDirectChild(pPr, "w:sectPr"))
        {
            pageBreakAfter = true;
        }

        return new DocxParagraph(html, units, pageBreakAfter);
    }

    private static DocxParagraph RenderDocxTable(XmlNode table, IReadOnlyDictionary<string, DocxStyle> styles)
    {
        var html = new StringBuilder("<table class=\"docx-table\"><tbody>");
        var rowCount = 0;
        var textLength = 0;

        foreach (var row in Children(table, "tr"))
        {
            rowCount++;
            html.Append("<tr>");
            foreach (var cell in Children(row, "tc"))
            {
                var cellBlocks = new StringBuilder();
                foreach (XmlNode child in cell.ChildNodes)
                {
                    if (IsXml(child, "p"))
                    {
                        var paragraph = RenderDocxParagraph(child, styles);
                        cellBlocks.Append(paragraph.Html);
                    }
                    else if (IsXml(child, "tbl"))
                    {
                        cellBlocks.Append(RenderDocxTable(child, styles).Html);
                    }
                }

                var text = CellText(cell);
                textLength += text.Length;
                html.Append("<td>").Append(cellBlocks.Length == 0 ? Html(text) : cellBlocks.ToString()).Append("</td>");
            }
            html.Append("</tr>");
        }

        html.Append("</tbody></table>");
        var units = Math.Max(2, rowCount * 2 + textLength / 140);
        return new DocxParagraph(html.ToString(), units, false);
    }

    private static string RenderDocxRun(XmlNode run, DocxStyle? inherited, ref bool pageBreakAfter)
    {
        var rPr = DirectChild(run, "w:rPr");
        var fragments = new StringBuilder();
        foreach (XmlNode node in run.ChildNodes)
        {
            switch (node.Name)
            {
                case "w:t":
                    fragments.Append(Html(node.InnerText));
                    break;
                case "w:tab":
                    fragments.Append("<span class=\"docx-tab\"></span>");
                    break;
                case "w:br":
                    if (string.Equals(Attr(node, "w:type", "type"), "page", StringComparison.OrdinalIgnoreCase))
                    {
                        pageBreakAfter = true;
                    }
                    else
                    {
                        fragments.Append("<br>");
                    }
                    break;
            }
        }

        var content = fragments.ToString();
        if (content.Length == 0)
        {
            return "";
        }

        var css = BuildDocxRunCss(rPr, inherited);
        return css.Length == 0 ? content : $"<span style=\"{css}\">{content}</span>";
    }

    private static string BuildDocxParagraphCss(XmlNode? pPr, DocxStyle? inherited, bool isEmpty)
    {
        var css = new List<string>();
        var align = ReadAlignment(pPr) ?? inherited?.Alignment;
        if (!string.IsNullOrWhiteSpace(align))
        {
            css.Add("text-align:" + align);
        }

        var spacing = DirectChild(pPr, "w:spacing");
        var before = TwipsToPt(Attr(spacing, "w:before", "before"));
        var after = TwipsToPt(Attr(spacing, "w:after", "after"));
        css.Add($"margin-top:{Math.Max(0, before ?? 0):0.##}pt");
        css.Add($"margin-bottom:{Math.Max(isEmpty ? 0 : 6, after ?? 6):0.##}pt");

        var ind = DirectChild(pPr, "w:ind");
        var left = TwipsToPt(Attr(ind, "w:left", "left"));
        var firstLine = TwipsToPt(Attr(ind, "w:firstLine", "firstLine"));
        var hanging = TwipsToPt(Attr(ind, "w:hanging", "hanging"));
        if (left.HasValue) css.Add($"margin-left:{left.Value:0.##}pt");
        if (firstLine.HasValue) css.Add($"text-indent:{firstLine.Value:0.##}pt");
        if (hanging.HasValue) css.Add($"text-indent:-{hanging.Value:0.##}pt");

        if (isEmpty)
        {
            css.Add("min-height:1.35em");
        }

        return string.Join(';', css);
    }

    private static string BuildDocxRunCss(XmlNode? rPr, DocxStyle? inherited)
    {
        var css = new List<string>();
        if (HasDirectChild(rPr, "w:b") || inherited?.Bold == true) css.Add("font-weight:700");
        if (HasDirectChild(rPr, "w:i") || inherited?.Italic == true) css.Add("font-style:italic");
        if (HasDirectChild(rPr, "w:u") || inherited?.Underline == true) css.Add("text-decoration:underline");

        var size = ReadFontSize(rPr) ?? inherited?.FontSize;
        if (!string.IsNullOrWhiteSpace(size)) css.Add("font-size:" + size);

        var color = Attr(DirectChild(rPr, "w:color"), "w:val", "val");
        if (!string.IsNullOrWhiteSpace(color) && !color.Equals("auto", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(color, "^[0-9a-fA-F]{6}$"))
        {
            css.Add("color:#" + color);
        }

        var vert = Attr(DirectChild(rPr, "w:vertAlign"), "w:val", "val");
        if (vert == "superscript") css.Add("vertical-align:super;font-size:.75em");
        if (vert == "subscript") css.Add("vertical-align:sub;font-size:.75em");

        return string.Join(';', css);
    }

    private static string? ReadAlignment(XmlNode? pPr)
    {
        return Attr(DirectChild(pPr, "w:jc"), "w:val", "val") switch
        {
            "center" => "center",
            "right" => "right",
            "both" or "distribute" => "justify",
            _ => null
        };
    }

    private static string? ReadFontSize(XmlNode? rPr)
    {
        var raw = Attr(DirectChild(rPr, "w:sz"), "w:val", "val");
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var halfPoints))
        {
            return null;
        }

        return (halfPoints / 2.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "pt";
    }

    private static string ResolveDocxTag(string? styleId, XmlNode? pPr)
    {
        var style = styleId ?? "";
        if (style.Contains("Title", StringComparison.OrdinalIgnoreCase) || style.Contains("Titulo", StringComparison.OrdinalIgnoreCase))
        {
            return "h1";
        }

        var headingMatch = Regex.Match(style, @"Heading([1-6])|Ttulo([1-6])", RegexOptions.IgnoreCase);
        if (headingMatch.Success)
        {
            var level = headingMatch.Groups[1].Success ? headingMatch.Groups[1].Value : headingMatch.Groups[2].Value;
            return "h" + level;
        }

        return "p";
    }

    private static IEnumerable<XmlNode> Descendants(XmlNode node, string name)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (IsXml(child, name))
            {
                yield return child;
            }

            foreach (var nested in Descendants(child, name))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<XmlNode> Children(XmlNode? node, string name)
    {
        if (node == null)
        {
            yield break;
        }

        foreach (XmlNode child in node.ChildNodes)
        {
            if (IsXml(child, name))
            {
                yield return child;
            }
        }
    }

    private static XmlNode? FirstDescendant(XmlNode node, string name)
    {
        return Descendants(node, name).FirstOrDefault();
    }

    private static bool IsXml(XmlNode? node, string name)
    {
        if (node == null)
        {
            return false;
        }

        if (string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var local = name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : name;
        return string.Equals(node.LocalName, local, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDescendant(XmlNode node, string name)
    {
        return Descendants(node, name).Any();
    }

    private static XmlNode? DirectChild(XmlNode? node, string name)
    {
        if (node == null)
        {
            return null;
        }

        foreach (XmlNode child in node.ChildNodes)
        {
            if (IsXml(child, name))
            {
                return child;
            }
        }

        return null;
    }

    private static bool HasDirectChild(XmlNode? node, string name)
    {
        return DirectChild(node, name) != null;
    }

    private static string? Attr(XmlNode? node, params string[] names)
    {
        if (node?.Attributes == null)
        {
            return null;
        }

        foreach (var name in names)
        {
            var attr = node.Attributes[name];
            if (attr != null)
            {
                return attr.Value;
            }
        }

        foreach (XmlAttribute attr in node.Attributes)
        {
            foreach (var name in names)
            {
                var local = name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : name;
                if (string.Equals(attr.LocalName, local, StringComparison.OrdinalIgnoreCase))
                {
                    return attr.Value;
                }
            }
        }

        return null;
    }

    private static double? TwipsToPt(string? raw)
    {
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var twips))
        {
            return null;
        }

        return twips / 20.0;
    }

    private static string ExtractOdtPreview(FileInfo file)
    {
        using var zip = OpenPreviewZip(file);
        var doc = ReadZipXml(zip, "content.xml");
        if (doc == null)
        {
            return DocumentPreviewJson("ODT", ["<p>Nenhum texto encontrado neste documento.</p>"]);
        }

        var textBody = FirstDescendant(doc, "text");
        var blocks = new List<string>();
        if (textBody != null)
        {
            RenderOdtBlocks(textBody, blocks);
        }

        var pages = BlocksToPages(blocks, DocumentPageUnitLimit);
        return DocumentPreviewJson("ODT", pages);
    }

    private static void RenderOdtBlocks(XmlNode node, List<string> blocks)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (IsXml(child, "h"))
            {
                var level = Math.Clamp(ParseInt(Attr(child, "text:outline-level", "outline-level"), 1), 1, 6);
                var text = InlineHtml(ReadOdfText(child));
                if (text.Length > 0) blocks.Add($"<h{level}>{text}</h{level}>");
            }
            else if (IsXml(child, "p"))
            {
                blocks.Add("<p>" + InlineHtml(ReadOdfText(child)) + "</p>");
            }
            else if (IsXml(child, "list"))
            {
                blocks.Add(RenderOdfList(child));
            }
            else if (IsXml(child, "table"))
            {
                blocks.Add(RenderOdfTable(child, "odf-table"));
            }
            else if (child.HasChildNodes)
            {
                RenderOdtBlocks(child, blocks);
            }
        }
    }

    private static string ExtractOdsPreview(FileInfo file)
    {
        using var zip = OpenPreviewZip(file);
        var doc = ReadZipXml(zip, "content.xml");
        if (doc == null)
        {
            return DocumentPreviewJson("ODS", ["<p>Nenhum texto encontrado nesta planilha.</p>"]);
        }

        var pages = new List<string>();
        foreach (var table in Descendants(doc, "table").Where(IsSpreadsheetTable).Take(SpreadsheetMaxSheets))
        {
            pages.Add(RenderOdsSheet(table));
        }

        if (pages.Count == 0)
        {
            pages.Add("<p>Nenhum texto encontrado nesta planilha.</p>");
        }

        return DocumentPreviewJson("ODS", pages);
    }

    private static string RenderOdsSheet(XmlNode table)
    {
        var name = Attr(table, "table:name", "name") ?? "Planilha";
        var html = new StringBuilder();
        html.Append("<section class=\"sheet-preview\"><h2>").Append(Html(name)).Append("</h2>");
        html.Append(RenderOdfTable(table, "sheet-table"));
        html.Append("</section>");
        return html.ToString();
    }

    private static string ExtractOdpPreview(FileInfo file)
    {
        using var zip = OpenPreviewZip(file);
        var doc = ReadZipXml(zip, "content.xml");
        if (doc == null)
        {
            return DocumentPreviewJson("ODP", ["<p>Nenhum texto encontrado nesta apresentacao.</p>"]);
        }

        var pages = new List<string>();
        var index = 1;
        foreach (var slide in Descendants(doc, "page").Take(PresentationMaxSlides))
        {
            pages.Add(RenderOdfSlide(slide, index++));
        }

        if (pages.Count == 0)
        {
            pages.Add("<p>Nenhum texto encontrado nesta apresentacao.</p>");
        }

        return DocumentPreviewJson("ODP", pages);
    }

    private static string RenderOdfSlide(XmlNode slide, int index)
    {
        var name = Attr(slide, "draw:name", "name");
        var blocks = new List<string>();
        CollectOdfTextBlocks(slide, blocks);

        var html = new StringBuilder("<section class=\"slide-preview odf-slide\">");
        html.Append("<div class=\"slide-kicker\">Slide ").Append(index).Append("</div>");
        if (!string.IsNullOrWhiteSpace(name))
        {
            html.Append("<h2>").Append(Html(name)).Append("</h2>");
        }

        foreach (var block in blocks.Take(80))
        {
            html.Append(block);
        }

        if (blocks.Count == 0)
        {
            html.Append("<p>Nenhum texto encontrado neste slide.</p>");
        }

        html.Append("</section>");
        return html.ToString();
    }

    private static XmlDocument ParseXml(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        };
        var doc = new XmlDocument { XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        doc.Load(reader);
        return doc;
    }

    private static string ExtractPptxPreview(FileInfo file)
    {
        using var zip = OpenPreviewZip(file);
        var slidePaths = ReadPptxSlidePaths(zip);
        var pages = new List<string>();
        var index = 1;
        foreach (var path in slidePaths.Take(PresentationMaxSlides))
        {
            var doc = ReadZipXml(zip, path);
            if (doc != null)
            {
                pages.Add(RenderPptxSlide(doc, index));
            }

            index++;
        }

        if (pages.Count == 0)
        {
            pages.Add("<p>Nenhum texto encontrado nesta apresentacao.</p>");
        }

        return DocumentPreviewJson("PPTX", pages);
    }

    private static List<string> ReadPptxSlidePaths(ZipArchive zip)
    {
        var rels = ReadRelationships(zip, "ppt/presentation.xml");
        var doc = ReadZipXml(zip, "ppt/presentation.xml");
        var paths = new List<string>();
        if (doc != null)
        {
            foreach (var slideId in Descendants(doc, "sldId"))
            {
                var relId = Attr(slideId, "r:id", "id");
                if (relId != null && rels.TryGetValue(relId, out var target))
                {
                    paths.Add(target);
                }
            }
        }

        if (paths.Count == 0)
        {
            paths.AddRange(zip.Entries
                .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
                .Select(e => e.FullName));
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string RenderPptxSlide(XmlDocument doc, int index)
    {
        var blocks = new List<string>();
        foreach (var shape in Descendants(doc, "sp").Concat(Descendants(doc, "graphicFrame")).Take(140))
        {
            var paragraphs = new List<string>();
            foreach (var paragraph in Descendants(shape, "p"))
            {
                var text = ReadOpenXmlParagraphText(paragraph);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    paragraphs.Add(InlineHtml(text));
                }
            }

            if (paragraphs.Count > 0)
            {
                var cls = blocks.Count == 0 && paragraphs.Count == 1 && paragraphs[0].Length < 120
                    ? "slide-textbox slide-title"
                    : "slide-textbox";
                blocks.Add("<div class=\"" + cls + "\">" + string.Join("", paragraphs.Select(p => "<p>" + p + "</p>")) + "</div>");
            }
        }

        var html = new StringBuilder("<section class=\"slide-preview pptx-slide\">");
        html.Append("<div class=\"slide-kicker\">Slide ").Append(index).Append("</div>");
        foreach (var block in blocks)
        {
            html.Append(block);
        }

        if (blocks.Count == 0)
        {
            html.Append("<p>Nenhum texto encontrado neste slide.</p>");
        }

        html.Append("</section>");
        return html.ToString();
    }

    private static string ExtractXlsxPreview(FileInfo file, string format)
    {
        using var zip = OpenPreviewZip(file);
        var sharedStrings = ReadXlsxSharedStrings(zip);
        var sheets = ReadXlsxSheets(zip);
        var pages = new List<string>();

        foreach (var sheet in sheets.Take(SpreadsheetMaxSheets))
        {
            pages.Add(RenderXlsxSheet(zip, sheet, sharedStrings));
        }

        if (pages.Count == 0)
        {
            pages.Add("<p>Nenhum texto encontrado nesta planilha.</p>");
        }

        return DocumentPreviewJson(format, pages);
    }

    private static List<string> ReadXlsxSharedStrings(ZipArchive zip)
    {
        var doc = ReadZipXml(zip, "xl/sharedStrings.xml");
        var output = new List<string>();
        if (doc == null)
        {
            return output;
        }

        foreach (var item in Descendants(doc, "si"))
        {
            output.Add(ReadOpenXmlParagraphText(item));
        }

        return output;
    }

    private static List<XlsxSheet> ReadXlsxSheets(ZipArchive zip)
    {
        var doc = ReadZipXml(zip, "xl/workbook.xml");
        var rels = ReadRelationships(zip, "xl/workbook.xml");
        var output = new List<XlsxSheet>();
        if (doc != null)
        {
            foreach (var sheet in Descendants(doc, "sheet"))
            {
                var name = Attr(sheet, "name") ?? "Planilha";
                var relId = Attr(sheet, "r:id", "id");
                var sheetId = Attr(sheet, "sheetId");
                var path = relId != null && rels.TryGetValue(relId, out var target)
                    ? target
                    : "xl/worksheets/sheet" + (sheetId ?? (output.Count + 1).ToString(CultureInfo.InvariantCulture)) + ".xml";
                output.Add(new XlsxSheet(name, path));
            }
        }

        if (output.Count == 0)
        {
            output.AddRange(zip.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, NaturalStringComparer.Instance)
                .Select((e, i) => new XlsxSheet("Planilha " + (i + 1).ToString(CultureInfo.InvariantCulture), e.FullName)));
        }

        return output;
    }

    private static string RenderXlsxSheet(ZipArchive zip, XlsxSheet sheet, IReadOnlyList<string> sharedStrings)
    {
        var doc = ReadZipXml(zip, sheet.Path);
        if (doc == null)
        {
            return "<section class=\"sheet-preview\"><h2>" + Html(sheet.Name) + "</h2><p>Não foi possível ler esta planilha.</p></section>";
        }

        var rows = new List<Dictionary<int, string>>();
        var rowLabels = new List<string>();
        var maxColumn = 0;
        var totalRows = 0;

        foreach (var row in Descendants(doc, "row"))
        {
            totalRows++;
            if (rows.Count >= SpreadsheetMaxRows)
            {
                continue;
            }

            var cells = new Dictionary<int, string>();
            foreach (var cell in Children(row, "c"))
            {
                var col = ColumnIndexFromReference(Attr(cell, "r")) ?? cells.Count;
                if (col >= SpreadsheetMaxColumns)
                {
                    continue;
                }

                var value = ReadXlsxCellValue(cell, sharedStrings);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    cells[col] = value;
                    maxColumn = Math.Max(maxColumn, col);
                }
            }

            if (cells.Count > 0)
            {
                rows.Add(cells);
                rowLabels.Add(Attr(row, "r") ?? totalRows.ToString(CultureInfo.InvariantCulture));
            }
        }

        var html = new StringBuilder();
        html.Append("<section class=\"sheet-preview\"><h2>").Append(Html(sheet.Name)).Append("</h2>");
        if (rows.Count == 0)
        {
            html.Append("<p>Nenhum texto encontrado nesta planilha.</p></section>");
            return html.ToString();
        }

        html.Append("<div class=\"sheet-scroll\"><table class=\"sheet-table\"><thead><tr><th></th>");
        for (var col = 0; col <= maxColumn; col++)
        {
            html.Append("<th>").Append(ColumnName(col)).Append("</th>");
        }

        html.Append("</tr></thead><tbody>");
        for (var i = 0; i < rows.Count; i++)
        {
            html.Append("<tr><th>").Append(Html(rowLabels[i])).Append("</th>");
            for (var col = 0; col <= maxColumn; col++)
            {
                rows[i].TryGetValue(col, out var value);
                html.Append("<td>").Append(InlineHtml(value ?? "")).Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table></div>");
        if (totalRows > rows.Count)
        {
            html.Append("<p class=\"preview-note\">Mostrando as primeiras ")
                .Append(rows.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" linhas com conteúdo.</p>");
        }

        html.Append("</section>");
        return html.ToString();
    }

    private static string RenderArchivePreview(FileInfo file, string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        using var input = OpenPreviewRead(file);
        var model = new Dictionary<string, object?>
        {
            ["sizeBytes"] = input.Length,
            ["maxPreviewBytes"] = ArchivePreviewLimit,
            ["format"] = ext.ToUpperInvariant()
        };

        if (input.Length > ArchivePreviewLimit)
        {
            model["tooLarge"] = true;
            model["entries"] = Array.Empty<object>();
            return JsonSerializer.Serialize(model, JsonOptions.Web);
        }

        if (new[] { "zip", "jar", "war", "ear", "apk", "cbz", "epub", "xpi" }.Contains(ext))
        {
            ListZipArchive(file, model, 5000);
        }
        else
        {
            if (EncryptedPreviewCache.IsEncrypted(file))
            {
                var directory = Directory.CreateTempSubdirectory("organessonium_archive_");
                try
                {
                    var readable = new FileInfo(Path.Combine(directory.FullName, Path.GetFileName(name)));
                    using (var output = readable.Create()) input.CopyTo(output);
                    ListSevenZipArchive(readable, model, 5000);
                }
                finally { CleanDir(directory); }
            }
            else ListSevenZipArchive(file, model, 5000);
        }

        return JsonSerializer.Serialize(model, JsonOptions.Web);
    }

    private static string RenderArchiveTooLargePreview(long sizeBytes)
    {
        return JsonSerializer.Serialize(new
        {
            sizeBytes,
            maxPreviewBytes = ArchivePreviewLimit,
            tooLarge = true,
            entries = Array.Empty<object>()
        }, JsonOptions.Web);
    }

    private static void ListZipArchive(FileInfo file, Dictionary<string, object?> model, int maxEntries)
    {
        var entries = new List<Dictionary<string, object?>>();
        var total = 0;
        try
        {
            using var zip = OpenPreviewZip(file);
            foreach (var entry in zip.Entries)
            {
                total++;
                if (entries.Count < maxEntries)
                {
                    entries.Add(ArchiveEntry(entry.FullName, entry.FullName.EndsWith('/'), entry.Length));
                }
            }

            model["engine"] = "zip";
            model["total"] = total;
            model["shown"] = entries.Count;
            model["entries"] = entries;
        }
        catch (Exception ex)
        {
            model["error"] = ex.Message;
            model["entries"] = entries;
        }
    }

    private static void ListSevenZipArchive(FileInfo file, Dictionary<string, object?> model, int maxEntries)
    {
        var sevenZip = FindSevenZip();
        var entries = new List<Dictionary<string, object?>>();
        if (sevenZip == null)
        {
            model["unsupportedArchive"] = true;
            model["entries"] = entries;
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(sevenZip, $"l -slt -ba \"{file.FullName}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
            {
                model["error"] = "7-Zip não iniciou.";
                model["entries"] = entries;
                return;
            }

            var total = 0;
            Dictionary<string, string>? current = null;
            while (!process.StandardOutput.EndOfStream)
            {
                var line = process.StandardOutput.ReadLine() ?? "";
                if (line.StartsWith("----------", StringComparison.Ordinal))
                {
                    current = null;
                    continue;
                }

                if (line.Length == 0)
                {
                    total += CommitSevenZipEntry(current, entries, maxEntries);
                    current = null;
                    continue;
                }

                var eq = line.IndexOf(" = ", StringComparison.Ordinal);
                if (eq < 0)
                {
                    continue;
                }

                var key = line[..eq].Trim();
                var value = line[(eq + 3)..].Trim();
                if (key == "Path")
                {
                    total += CommitSevenZipEntry(current, entries, maxEntries);
                    current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                current ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                current[key] = value;
            }

            total += CommitSevenZipEntry(current, entries, maxEntries);
            process.WaitForExit(20_000);
            model["engine"] = "7zip";
            model["total"] = total;
            model["shown"] = entries.Count;
            model["entries"] = entries;
        }
        catch (Exception ex)
        {
            model["error"] = ex.Message;
            model["entries"] = entries;
        }
    }

    private static int CommitSevenZipEntry(Dictionary<string, string>? item, List<Dictionary<string, object?>> entries, int maxEntries)
    {
        if (item == null || !item.TryGetValue("Path", out var path) || string.IsNullOrWhiteSpace(path))
        {
            return 0;
        }

        var isDir = item.TryGetValue("Folder", out var folder) && folder == "+";
        var size = item.TryGetValue("Size", out var sizeText) && long.TryParse(sizeText, out var parsed) ? parsed : -1;
        if (entries.Count < maxEntries)
        {
            entries.Add(ArchiveEntry(path, isDir, size));
        }

        return 1;
    }

    private static Dictionary<string, object?> ArchiveEntry(string name, bool directory, long size)
    {
        return new Dictionary<string, object?>
        {
            ["name"] = name,
            ["directory"] = directory,
            ["size"] = size
        };
    }

    private static string? FindSevenZip()
    {
        var candidates = new List<string> { "7z.exe", "7z" };
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf)) candidates.Add(Path.Combine(pf, "7-Zip", "7z.exe"));
        if (!string.IsNullOrWhiteSpace(pfx86)) candidates.Add(Path.Combine(pfx86, "7-Zip", "7z.exe"));

        foreach (var candidate in candidates)
        {
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
            {
                return candidate;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo(candidate)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process != null && process.WaitForExit(2000))
                {
                    return candidate;
                }
            }
            catch
            {
                // Try next.
            }
        }

        return null;
    }

    private static List<string> TextPages(string text)
    {
        var lines = text.Split('\n').Select(Html).Select(line => "<p>" + line + "</p>").ToList();
        var pages = new List<string>();
        var blocks = new List<string>();
        foreach (var line in lines)
        {
            blocks.Add(line);
            if (blocks.Count >= 34)
            {
                AddDocumentPage(pages, blocks);
            }
        }

        AddDocumentPage(pages, blocks);
        if (pages.Count == 0)
        {
            pages.Add("<p>Nenhum texto encontrado neste documento.</p>");
        }

        return pages;
    }

    private static List<string> BlocksToPages(List<string> blocks, int maxUnits)
    {
        var pages = new List<string>();
        var current = new List<string>();
        var units = 0;
        foreach (var block in blocks)
        {
            var plainLength = Regex.Replace(block, "<.*?>", "").Length;
            var blockUnits = Math.Max(1, (plainLength + 100) / 101);
            current.Add(block);
            units += blockUnits;
            if (units >= maxUnits)
            {
                AddDocumentPage(pages, current);
                units = 0;
            }
        }

        AddDocumentPage(pages, current);
        if (pages.Count == 0)
        {
            pages.Add("<p>Nenhum texto encontrado neste documento.</p>");
        }

        return pages;
    }

    private static void AppendDocumentBlock(List<string> pages, List<string> blocks, DocxParagraph block, ref int units)
    {
        blocks.Add(block.Html);
        units += block.Units;
        if (block.PageBreakAfter || units >= DocumentPageUnitLimit)
        {
            AddDocumentPage(pages, blocks);
            units = 0;
        }
    }

    private static XmlDocument? ReadZipXml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(NormalizeZipPath(path));
        if (entry == null)
        {
            return null;
        }

        using var stream = entry.Open();
        return ParseXml(stream);
    }

    private static Dictionary<string, string> ReadRelationships(ZipArchive zip, string sourcePath)
    {
        var source = NormalizeZipPath(sourcePath);
        var slash = source.LastIndexOf('/');
        var dir = slash >= 0 ? source[..slash] : "";
        var file = slash >= 0 ? source[(slash + 1)..] : source;
        var relPath = string.IsNullOrEmpty(dir) ? "_rels/" + file + ".rels" : dir + "/_rels/" + file + ".rels";
        var doc = ReadZipXml(zip, relPath);
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc == null)
        {
            return output;
        }

        foreach (var rel in Descendants(doc, "Relationship"))
        {
            var id = Attr(rel, "Id");
            var target = Attr(rel, "Target");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            output[id] = CombineZipPath(source, target);
        }

        return output;
    }

    private static string CombineZipPath(string sourcePath, string target)
    {
        target = target.Replace('\\', '/');
        if (target.StartsWith("/", StringComparison.Ordinal))
        {
            return NormalizeZipPath(target.TrimStart('/'));
        }

        var source = NormalizeZipPath(sourcePath);
        var slash = source.LastIndexOf('/');
        var baseDir = slash >= 0 ? source[..slash] : "";
        return NormalizeZipPath(string.IsNullOrEmpty(baseDir) ? target : baseDir + "/" + target);
    }

    private static string NormalizeZipPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(part);
        }

        return string.Join('/', stack);
    }

    private static string RenderOdfList(XmlNode list)
    {
        var html = new StringBuilder("<ul>");
        foreach (var item in Children(list, "list-item"))
        {
            var itemBlocks = new List<string>();
            RenderOdtBlocks(item, itemBlocks);
            var content = itemBlocks.Count == 0 ? InlineHtml(ReadOdfText(item)) : string.Join("", itemBlocks);
            html.Append("<li>").Append(content).Append("</li>");
        }

        html.Append("</ul>");
        return html.ToString();
    }

    private static string RenderOdfTable(XmlNode table, string className)
    {
        var html = new StringBuilder("<div class=\"sheet-scroll\"><table class=\"")
            .Append(Html(className))
            .Append("\"><tbody>");
        var shownRows = 0;
        var totalRows = 0;

        foreach (var row in Children(table, "table-row"))
        {
            var repeatedRows = Math.Max(1, Math.Min(ParseInt(Attr(row, "table:number-rows-repeated", "number-rows-repeated"), 1), SpreadsheetMaxRows));
            for (var repeatRow = 0; repeatRow < repeatedRows; repeatRow++)
            {
                totalRows++;
                if (shownRows >= SpreadsheetMaxRows)
                {
                    continue;
                }

                var cells = new List<string>();
                foreach (var cell in row.ChildNodes.Cast<XmlNode>().Where(n => IsXml(n, "table-cell") || IsXml(n, "covered-table-cell")))
                {
                    if (IsXml(cell, "covered-table-cell"))
                    {
                        continue;
                    }

                    var repeatedCols = Math.Max(1, Math.Min(ParseInt(Attr(cell, "table:number-columns-repeated", "number-columns-repeated"), 1), SpreadsheetMaxColumns));
                    var value = InlineHtml(ReadOdfText(cell));
                    for (var i = 0; i < repeatedCols && cells.Count < SpreadsheetMaxColumns; i++)
                    {
                        cells.Add(value);
                    }
                }

                if (cells.Count == 0 || cells.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                shownRows++;
                html.Append("<tr>");
                foreach (var cell in cells)
                {
                    html.Append("<td>").Append(cell).Append("</td>");
                }

                html.Append("</tr>");
            }
        }

        html.Append("</tbody></table></div>");
        if (totalRows > shownRows)
        {
            html.Append("<p class=\"preview-note\">Mostrando as primeiras ")
                .Append(shownRows.ToString(CultureInfo.InvariantCulture))
                .Append(" linhas com conteúdo.</p>");
        }

        return html.ToString();
    }

    private static bool IsSpreadsheetTable(XmlNode table)
    {
        return Children(table, "table-row").Any();
    }

    private static void CollectOdfTextBlocks(XmlNode node, List<string> blocks)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (IsXml(child, "h"))
            {
                var text = InlineHtml(ReadOdfText(child));
                if (text.Length > 0)
                {
                    blocks.Add("<h2>" + text + "</h2>");
                }
            }
            else if (IsXml(child, "p"))
            {
                var text = InlineHtml(ReadOdfText(child));
                if (text.Length > 0)
                {
                    blocks.Add("<p>" + text + "</p>");
                }
            }
            else if (child.HasChildNodes)
            {
                CollectOdfTextBlocks(child, blocks);
            }
        }
    }

    private static string ReadOdfText(XmlNode node)
    {
        var text = new StringBuilder();
        AppendOdfText(node, text);
        return Regex.Replace(text.ToString(), "[ \t]+", " ").Trim();
    }

    private static void AppendOdfText(XmlNode node, StringBuilder output)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.NodeType == XmlNodeType.Text || child.NodeType == XmlNodeType.CDATA)
            {
                output.Append(child.Value);
                continue;
            }

            if (IsXml(child, "p") || IsXml(child, "h"))
            {
                if (output.Length > 0)
                {
                    output.Append('\n');
                }

                AppendOdfText(child, output);
                output.Append('\n');
                continue;
            }

            if (!child.HasChildNodes)
            {
                if (IsXml(child, "s"))
                {
                    output.Append(' ', Math.Max(1, ParseInt(Attr(child, "text:c", "c"), 1)));
                }
                else if (IsXml(child, "tab"))
                {
                    output.Append('\t');
                }
                else if (IsXml(child, "line-break"))
                {
                    output.Append('\n');
                }

                continue;
            }

            AppendOdfText(child, output);
        }
    }

    private static string ReadOpenXmlParagraphText(XmlNode node)
    {
        var text = new StringBuilder();
        foreach (XmlNode child in node.ChildNodes)
        {
            if (IsXml(child, "t"))
            {
                text.Append(child.InnerText);
            }
            else if (IsXml(child, "tab"))
            {
                text.Append('\t');
            }
            else if (IsXml(child, "br"))
            {
                text.Append('\n');
            }
            else if (child.HasChildNodes)
            {
                text.Append(ReadOpenXmlParagraphText(child));
            }
        }

        return Regex.Replace(text.ToString(), "[ \t]+", " ").Trim();
    }

    private static string ReadXlsxCellValue(XmlNode cell, IReadOnlyList<string> sharedStrings)
    {
        var type = Attr(cell, "t");
        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            return ReadOpenXmlParagraphText(cell);
        }

        var value = DirectChild(cell, "v")?.InnerText ?? "";
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
            index >= 0 && index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }

        if (string.Equals(type, "b", StringComparison.OrdinalIgnoreCase))
        {
            return value == "1" ? "TRUE" : "FALSE";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = ReadOpenXmlParagraphText(cell);
        }

        return value.Trim();
    }

    private static int? ColumnIndexFromReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var letters = new string(reference.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (letters.Length == 0)
        {
            return null;
        }

        var value = 0;
        foreach (var letter in letters)
        {
            value = value * 26 + (letter - 'A' + 1);
        }

        return value - 1;
    }

    private static string ColumnName(int index)
    {
        var value = index + 1;
        var output = "";
        while (value > 0)
        {
            value--;
            output = (char)('A' + value % 26) + output;
            value /= 26;
        }

        return output;
    }

    private static string InlineHtml(string text)
    {
        return Html(text ?? "")
            .Replace("\t", "<span class=\"docx-tab\"></span>")
            .Replace("\n", "<br>");
    }

    private static string CellText(XmlNode node)
    {
        return Regex.Replace(node.InnerText ?? "", "\\s+", " ").Trim();
    }

    private static int ParseInt(string? text, int fallback)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
    }

    private static string DocumentPreviewJson(string format, List<string> pages)
    {
        return JsonSerializer.Serialize(new { format, pages }, JsonOptions.Web);
    }

    private static void AddDocumentPage(List<string> pages, List<string> blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        pages.Add(string.Join("\n", blocks));
        blocks.Clear();
    }

    private static string XmlToText(string xml)
    {
        var text = Regex.Replace(xml, "(?i)</w:p>|</text:p>|</a:p>|</si>|</row>", "\n");
        text = Regex.Replace(text, "<[^>]+>", " ");
        return DecodeXmlEntities(text).Replace("\r", "").Trim();
    }

    private static string StripRtf(string rtf)
    {
        return Regex.Replace(
                Regex.Replace(
                    Regex.Replace(
                        Regex.Replace(rtf, @"\\par[d]?", "\n"),
                        @"\\'[0-9a-fA-F]{2}", ""),
                    @"\\[a-zA-Z]+-?\d* ?", ""),
                "[{}]", "")
            .Trim();
    }

    private static string DecodeXmlEntities(string text)
    {
        return text.Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&amp;", "&")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'");
    }

    private static string Html(string text)
    {
        return (text ?? "").Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    private static void CleanDir(DirectoryInfo dir)
    {
        try
        {
            if (dir.Exists)
            {
                dir.Delete(true);
            }
        }
        catch
        {
            // Best-effort cache cleanup.
        }
    }

    private static bool SamePath(FileSystemInfo left, FileSystemInfo right)
    {
        return string.Equals(
            left.FullName.TrimEnd('\\', '/'),
            right.FullName.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanUploadName(string name)
    {
        var cleaned = new string(name.Where(ch => !char.IsControl(ch) && (ch == ' ' || !char.IsSeparator(ch))).ToArray()).Trim();
        return cleaned.Length > 0 ? cleaned : "arquivo";
    }

    private Task ReportSetupConnectionErrorAsync(Exception ex)
    {
        var kind = ConnectionErrorClassifier.Classify(ex).Kind;
        return _bridge.CallAsync("_onSetupError", ClassifyConnectError(ex),
            kind is not (ConnectionErrorKind.InvalidToken or ConnectionErrorKind.InvalidChannelConfiguration));
    }

private static string ClassifyConnectError(Exception ex)
    {
        var issue = ConnectionErrorClassifier.Classify(ex);
        return issue.Kind == ConnectionErrorKind.InvalidToken
            ? "Token inválido ou expirado. Copie o token em: Developer Portal > Bot > Reset Token."
            : issue.Message;
    }

    private static bool LooksLikeDiscordId(string value)
    {
        return Regex.IsMatch(value ?? "", @"^\d{16,22}$");
    }

    private static bool IsTextChannel(int type)
    {
        return type is 0 or 5;
    }

    private static bool IsUploadPartDirectoryForSource(FileInfo source, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || source.Directory == null)
        {
            return false;
        }

        try
        {
            var directory = new DirectoryInfo(path);
            return directory.Name.StartsWith(".organessonium-upload-", StringComparison.OrdinalIgnoreCase) &&
                   directory.Parent != null &&
                   string.Equals(
                       directory.Parent.FullName.TrimEnd('\\', '/'),
                       source.Directory.FullName.TrimEnd('\\', '/'),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }


    private static void TryDeleteFile(FileInfo file)
    {
        try
        {
            if (file.Exists)
            {
                file.Delete();
            }
        }
        catch
        {
            // Each generated part is removed on a best-effort basis.
        }
    }

    private static bool IsResumableUploadFailure(Exception ex)
    {
        if (TransferMemoryPressureException.Find(ex) != null) return true;
        if (ex is DiscordApiException api)
        {
            return (int)api.StatusCode == 429 ||
                   (int)api.StatusCode >= 500 ||
                   api.DiscordCode == DiscordApiClient.MaxActiveThreadsCode;
        }

        return ex is HttpRequestException;
    }

    private static bool IsSerialChannel(int type)
    {
        return type is 0 or 5;
    }

    private static bool IsForumChannel(int type)
    {
        return type is 15 or 16;
    }

    private static bool SameDiscordChannelName(string actual, string expected)
    {
        return string.Equals(
            NormalizeDiscordChannelName(actual, ""),
            NormalizeDiscordChannelName(expected, ""),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDiscordChannelName(string value, string fallback)
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        text = text.TrimStart('#').ToLowerInvariant();
        text = Regex.Replace(text, @"\s+", "-");
        text = Regex.Replace(text, @"[^a-z0-9_-]", "");
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string ArgString(JsonElement[] args, int index, string fallback = "")
    {
        if (index >= args.Length)
        {
            return fallback;
        }

        return args[index].ValueKind switch
        {
            JsonValueKind.String => args[index].GetString() ?? fallback,
            JsonValueKind.Number => args[index].GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback
        };
    }

    private static int ArgInt(JsonElement[] args, int index, int fallback)
    {
        if (index >= args.Length)
        {
            return fallback;
        }

        return args[index].TryGetInt32(out var value) ? value : fallback;
    }

    private static bool ArgBool(JsonElement[] args, int index, bool fallback)
    {
        if (index >= args.Length)
        {
            return fallback;
        }

        return args[index].ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(args[index].GetString(), out var parsed) ? parsed : fallback,
            _ => fallback
        };
    }

    private static string NormalizeTheme(string value)
    {
        return value.Equals("light", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("dark", StringComparison.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : "system";
    }

    private static string NormalizeLanguage(string value)
    {
        if (value.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("pt", StringComparison.OrdinalIgnoreCase))
        {
            return "pt-BR";
        }

        return value.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : "system";
    }

    private static string NormalizeVideoPreviewQuality(string value)
    {
        return value.Equals("original", StringComparison.OrdinalIgnoreCase) ? "original" : "low";
    }

    private static string NewId()
    {
        return Guid.NewGuid().ToString("N")[..8];
    }

    private sealed record BridgeMessage(
        [property: JsonPropertyName("method")] string Method,
        [property: JsonPropertyName("args")] JsonElement[] Args);
    private sealed record ResolvedDiscordChannels(string NamesChannelId, string FoldersForumId, string AppsForumId, string SerialChannelId);
    private sealed record UploadTask(FileInfo File, string Path, FileReplacement? Replacement = null)
    {
        internal bool Resuming { get; init; }
        internal TransferState? PreparedTransfer { get; init; }
    }
    private sealed record UploadConflictResponse(string Action, bool ApplyToAll);
    private sealed record UploadConflictResolution(List<UploadTask> Tasks);
    private sealed record PreviewUploadIds(
        string PreviewMessageId,
        string VideoPreviewMessageId,
        IReadOnlyList<string> VideoPreviewChunkMessageIds,
        long VideoPreviewSizeBytes,
        IReadOnlyList<ChunkReference>? VideoPreviewChunkReferences = null);
    private sealed record VideoPreviewUploadResult(long SizeBytes, IReadOnlyList<string> ChunkMessageIds,
        IReadOnlyList<ChunkReference>? ChunkReferences = null);
    private sealed record DeletePlan(List<AppEntry> Files, List<AppEntry> Folders);
    private sealed record DeleteResult(int Total, int Deleted, int Failed);
    private sealed record DocxStyle(string? Alignment, bool Bold, bool Italic, bool Underline, string? FontSize);
    private sealed record DocxParagraph(string Html, int Units, bool PageBreakAfter);
    private sealed record XlsxSheet(string Name, string Path);

    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            return CompareNatural(x ?? "", y ?? "");
        }

        private static int CompareNatural(string x, string y)
        {
            var ix = 0;
            var iy = 0;
            while (ix < x.Length && iy < y.Length)
            {
                if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
                {
                    var sx = ix;
                    var sy = iy;
                    while (ix < x.Length && char.IsDigit(x[ix])) ix++;
                    while (iy < y.Length && char.IsDigit(y[iy])) iy++;

                    if (long.TryParse(x.AsSpan(sx, ix - sx), NumberStyles.Integer, CultureInfo.InvariantCulture, out var nx) &&
                        long.TryParse(y.AsSpan(sy, iy - sy), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ny) &&
                        nx != ny)
                    {
                        return nx.CompareTo(ny);
                    }
                }
                else
                {
                    var cmp = char.ToUpperInvariant(x[ix]).CompareTo(char.ToUpperInvariant(y[iy]));
                    if (cmp != 0)
                    {
                        return cmp;
                    }

                    ix++;
                    iy++;
                }
            }

            return x.Length.CompareTo(y.Length);
        }
    }

    private sealed class UiThrottle
    {
        public long Last;
    }

    private sealed class UploadProgressTracker
    {
        private readonly bool _isImage;
        private readonly bool _hasVideoPreview;
        private readonly object _sync = new();
        private int _current;

        public UploadProgressTracker(bool isImage, bool hasVideoPreview = false)
        {
            _isImage = isImage;
            _hasVideoPreview = hasVideoPreview;
        }

        public int Current
        {
            get
            {
                lock (_sync)
                {
                    return _current;
                }
            }
        }

        public int Preparing(int percent) => Current;

        public int Original(int percent)
        {
            return Report(0, _hasVideoPreview ? 80 : _isImage ? 98 : 99, percent);
        }

        public int ThumbnailGeneration(int percent)
        {
            return Current;
        }

        public int ThumbnailUpload(int percent)
        {
            return _isImage ? Report(98, 99, percent) : Current;
        }

        public int VideoPreviewGeneration(int percent) => _hasVideoPreview ? Report(80, 90, percent) : Current;

        public int VideoPreviewUpload(int percent) => _hasVideoPreview ? Report(90, 99, percent) : Current;

        public int Metadata(int percent) => Current;

        public int Done() => Report(100, 100, 100);

        private int Report(int start, int end, int percent)
        {
            var normalized = Math.Clamp(percent, 0, 100);
            var mapped = start + (int)Math.Round((end - start) * normalized / 100.0, MidpointRounding.AwayFromZero);
            lock (_sync)
            {
                _current = Math.Max(_current, Math.Clamp(mapped, 0, 100));
                return _current;
            }
        }
    }

    private sealed class SetupChannelsMissingException : Exception
    {
        public SetupChannelsMissingException(string message) : base(message)
        {
        }
    }
}
