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

public sealed class AppController : IAsyncDisposable
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
    private readonly Window _owner;
    private readonly SettingsStore _store;
    private WebMessageBridge _bridge;
    private readonly Func<DiscordSession, Task> _onConnected;
    private readonly Action<string> _onThemeChanged;
    private readonly ConcurrentDictionary<string, DirectoryInfo> _previewDirs = new();
    private readonly ConcurrentDictionary<string, string> _previewUrls = new();
    private readonly ConcurrentDictionary<string, byte> _thumbnailLoads = new();
    private readonly object _thumbnailRequestSync = new();
    private readonly SemaphoreSlim _thumbnailDeliveryGate = new(1, 1);
    private CancellationTokenSource? _thumbnailRequestCts;
    private string _thumbnailRequestKey = "";
    private readonly ConcurrentDictionary<string, TaskCompletionSource<UploadConflictResponse>> _uploadConflictResponses = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transferOperations = new();
    private readonly SemaphoreSlim _registryLoadGate = new(1, 1);
    private readonly TransferManager _transferManager;
    private readonly EncryptionService _encryption;
    private readonly PreviewFileServer _previewServer = new();
    private IReadOnlyList<AppEntry> _loadedApps = [];
    private DiscordSession? _session;
    private string _currentPath = "/";

    public AppController(
        Window owner,
        SettingsStore store,
        WebMessageBridge bridge,
        Func<DiscordSession, Task> onConnected,
        Action<string>? onThemeChanged = null)
    {
        _owner = owner;
        _store = store;
        _bridge = bridge;
        _onConnected = onConnected;
        _onThemeChanged = onThemeChanged ?? (_ => { });
        _transferManager = new TransferManager(new TransferStateRepository(store.TransfersDirectory));
        _encryption = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(store));
    }

    public void SetSession(DiscordSession session)
    {
        _session = session;
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

        foreach (var dir in _previewDirs.Values)
        {
            CleanDir(dir);
        }

        _previewServer.Dispose();
        if (_session != null)
        {
            await _session.DisposeAsync();
        }
    }

    private Task DispatchAsync(string method, JsonElement[] args)
    {
        switch (method)
        {
            case "connect": return ConnectAsync(args);
            case "logout": _ = RunAsync(LogoutAsync); return Task.CompletedTask;
            case "openGuide": return OpenGuideAsync();
            case "setCurrentPath": _currentPath = PathRules.Normalize(ArgString(args, 0)); return Task.CompletedTask;
            case "loadApps": _ = RunAsync(LoadAppsAsync); return Task.CompletedTask;
            case "loadVisibleThumbnails": _ = RunAsync(() => LoadVisibleThumbnailsAsync(ArgString(args, 0))); return Task.CompletedTask;
            case "requestPreview": _ = RunAsync(() => RequestPreviewAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "requestVideoQuality": _ = RequestVideoQualitySafeAsync(ArgString(args, 0), ArgString(args, 1), ArgString(args, 2)); return Task.CompletedTask;
            case "closePreview": ClosePreview(ArgString(args, 0)); return Task.CompletedTask;
            case "clearPreviewCache": _ = RunAsync(ClearPreviewCacheAsync); return Task.CompletedTask;
            case "getPreviewCacheSize": _ = RunAsync(SendPreviewCacheSizeAsync); return Task.CompletedTask;
            case "openPreviewMedia": _ = RunAsync(() => OpenPreviewMediaAsync(ArgString(args, 0))); return Task.CompletedTask;
            case "requestPdfPages": return Task.CompletedTask;
            case "createFolder": _ = RunAsync(() => CreateFolderAsync(ArgString(args, 0))); return Task.CompletedTask;
            case "moveItems": _ = RunAsync(() => MoveItemsAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "moveItem": _ = RunAsync(() => MoveItemsAsync(JsonSerializer.Serialize(new[] { ArgString(args, 0) }), ArgString(args, 1))); return Task.CompletedTask;
            case "updateFolderColor": _ = RunAsync(() => UpdateFolderColorAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "renameItem": _ = RunAsync(() => RenameItemAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "startUpload": return StartUploadDialogAsync();
            case "startFolderUpload": return StartFolderUploadDialogAsync();
            case "resolveUploadConflict": ResolveUploadConflict(args); return Task.CompletedTask;
            case "cancelTransfers": CancelTransfers(ArgString(args, 0)); return Task.CompletedTask;
            case "resumeTransfers": _ = RunAsync(ResumeInterruptedTransfersAsync); return Task.CompletedTask;
            case "failInterruptedTransfers": _ = RunAsync(FailInterruptedTransfersAsync); return Task.CompletedTask;
            case "startDownload": return StartDownloadAsync(ArgString(args, 0), ArgString(args, 1));
            case "deleteApp": _ = RunAsync(() => DeleteAppAsync(ArgString(args, 0), ArgString(args, 1))); return Task.CompletedTask;
            case "deleteItems": _ = RunAsync(() => DeleteItemsAsync(ArgString(args, 0))); return Task.CompletedTask;
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
            case "chooseTempPath": return ChoosePathAsync("temp", "Pasta temporária");
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
        var token = ArgString(args, 0).Trim();
        var names = ArgString(args, 1, "names").Trim();
        var forum = ArgString(args, 2, "files").Trim();
        var allowCreate = ArgBool(args, 3, false);
        var proxyJson = ArgString(args, 4);

        if (string.IsNullOrWhiteSpace(token)) { await _bridge.CallAsync("_onSetupError", "Insira o token do bot."); return; }
        if (string.IsNullOrWhiteSpace(names)) { names = "names"; }
        if (string.IsNullOrWhiteSpace(forum)) { forum = "files"; }

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
                await _bridge.CallAsync("_onSetupError", "Configuração de proxy inválida.");
                return;
            }
        }

        if (settings.Proxy.Mode == ProxyMode.Manual && !settings.Proxy.IsValidManual(out var proxyError))
        {
            await _bridge.CallAsync("_onSetupError", "Proxy manual inválido: " + proxyError);
            return;
        }

        NetworkConfig.Current.Apply(settings.Proxy);
        _store.Save(settings);

        await _bridge.CallAsync("_onSetupConnecting", "Procurando canais no servidor...");
        if (LooksLikeDiscordId(names) && LooksLikeDiscordId(forum))
        {
            settings.NamesChannelId = names;
            settings.AppsForumId = forum;
        }
        else
        {
            try
            {
                var resolved = await ResolveDiscordChannelsAsync(token, names, forum, allowCreate);
                settings.NamesChannelId = resolved.NamesChannelId;
                settings.AppsForumId = resolved.AppsForumId;
            }
            catch (SetupChannelsMissingException ex)
            {
                await _bridge.CallAsync("_onSetupChannelsMissing", ex.Message);
                return;
            }
            catch (Exception ex)
            {
                await _bridge.CallAsync("_onSetupError", ClassifyConnectError(ex));
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
            await _onConnected(session);
            await NotifyInterruptedTransfersAsync();
        }
        catch (Exception ex)
        {
            await session.DisposeAsync();
            await _bridge.CallAsync("_onSetupError", ClassifyConnectError(ex));
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

    private async Task LogoutAsync()
    {
        _loadedApps = [];
        var session = _session;
        _session = null;
        if (session != null)
        {
            await session.DisposeAsync();
        }

        var settings = _store.Load();
        settings.BotToken = "";
        settings.NamesChannelId = "";
        settings.AppsForumId = "";
        _store.Save(settings);
        await _bridge.SetSettingsAsync(settings);
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
        string forumInput,
        bool allowCreate)
    {
        var namesName = NormalizeDiscordChannelName(namesInput, "names");
        var forumName = NormalizeDiscordChannelName(forumInput, "files");
        using var api = new DiscordApiClient(token);
        var guilds = await api.GetCurrentUserGuildsAsync();
        if (guilds.Count == 0)
        {
            throw new SetupChannelsMissingException("O bot não está em nenhum servidor. Adicione o bot a um servidor e tente novamente.");
        }

        var completeMatches = new List<(DiscordGuildDto Guild, DiscordChannelDto Names, DiscordChannelDto Forum)>();
        var wrongTypes = new List<string>();
        foreach (var guild in guilds)
        {
            var channels = await api.GetGuildChannelsAsync(guild.Id);
            var namesChannels = channels
                .Where(c => SameDiscordChannelName(c.Name, namesName) && IsTextChannel(c.Type))
                .ToList();
            var forumChannels = channels
                .Where(c => SameDiscordChannelName(c.Name, forumName) && IsForumChannel(c.Type))
                .ToList();

            if (namesChannels.Count > 1 || forumChannels.Count > 1)
            {
                throw new SetupChannelsMissingException(
                    "Encontrei mais de um canal com o mesmo nome no servidor \"" + guild.Name +
                    "\". Informe nomes diferentes para evitar usar o canal errado.");
            }

            if (channels.Any(c => SameDiscordChannelName(c.Name, namesName) && !IsTextChannel(c.Type)))
            {
                wrongTypes.Add("\"" + namesName + "\" no servidor \"" + guild.Name + "\" não é canal de texto.");
            }

            if (channels.Any(c => SameDiscordChannelName(c.Name, forumName) && !IsForumChannel(c.Type)))
            {
                wrongTypes.Add("\"" + forumName + "\" no servidor \"" + guild.Name + "\" não é fórum.");
            }

            if (namesChannels.Count == 1 && forumChannels.Count == 1)
            {
                completeMatches.Add((guild, namesChannels[0], forumChannels[0]));
            }
        }

        if (completeMatches.Count == 1)
        {
            return new ResolvedDiscordChannels(completeMatches[0].Names.Id, completeMatches[0].Forum.Id);
        }

        if (completeMatches.Count > 1)
        {
            throw new SetupChannelsMissingException(
                "Encontrei canais \"" + namesName + "\" e \"" + forumName +
                "\" em mais de um servidor. Use nomes diferentes para evitar usar o servidor errado.");
        }

        if (!allowCreate)
        {
            var message = "Não encontrei os canais \"" + namesName + "\" e \"" + forumName +
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
            (SameDiscordChannelName(c.Name, forumName) && !IsForumChannel(c.Type)));
        if (existingWrongType != null)
        {
            throw new SetupChannelsMissingException(
                "Ja existe um canal chamado \"" + existingWrongType.Name +
                "\" com tipo diferente do necessário. Informe outro nome para evitar configurar o canal errado.");
        }

        var namesExisting = targetChannels.Where(c => SameDiscordChannelName(c.Name, namesName) && IsTextChannel(c.Type)).ToList();
        var forumExisting = targetChannels.Where(c => SameDiscordChannelName(c.Name, forumName) && IsForumChannel(c.Type)).ToList();
        if (namesExisting.Count > 1 || forumExisting.Count > 1)
        {
            throw new SetupChannelsMissingException(
                "Encontrei canais duplicados no servidor. Informe nomes diferentes para evitar usar o canal errado.");
        }

        var namesChannel = namesExisting.Count == 1
            ? namesExisting[0]
            : await api.CreateGuildChannelAsync(targetGuild.Id, namesName, 0);
        var forumChannel = forumExisting.Count == 1
            ? forumExisting[0]
            : await api.CreateGuildChannelAsync(targetGuild.Id, forumName, 15);
        return new ResolvedDiscordChannels(namesChannel.Id, forumChannel.Id);
    }

    private async Task LoadAppsAsync()
    {
        await _registryLoadGate.WaitAsync();
        try
        {
            var cached = _store.LoadCachedEntries();
            _loadedApps = cached;

            var hasCache = cached.Count > 0;
            await _bridge.CallAsync("_onRegistryLoadStarted", hasCache);
            if (hasCache)
            {
                await _bridge.CallAsync("_onCachedAppsLoaded", cached);
            }

            await _bridge.CallAsync("_onRegistryLoadProgress", 0);

            var registry = Registry();
            var apps = await registry.LoadAsync(
                onProgress: count => _bridge.CallAsync("_onRegistryLoadProgress", count),
                onBatch: async batch =>
                {
                    MergeLoadedApps(batch);
                    await _bridge.CallAsync("_onAppsBatch", batch);
                });

            _loadedApps = apps;
            await _bridge.CallAsync("_onAppsLoaded", apps);
        }
        finally
        {
            _registryLoadGate.Release();
        }
    }
    private void MergeLoadedApps(IReadOnlyList<AppEntry> batch)
    {
        var byId = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in _loadedApps)
        {
            byId[EntryCacheKey(app)] = app;
        }

        foreach (var app in batch)
        {
            byId[EntryCacheKey(app)] = app;
        }

        _loadedApps = byId.Values.ToList();
    }

    private static string EntryCacheKey(AppEntry app)
    {
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
    private async Task LoadVisibleThumbnailsAsync(string visibleIdsJson)
    {
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

        var path = _currentPath;
        var requestKey = path + "|" + string.Join(",", visibleIds.Order(StringComparer.Ordinal));
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
            var apps = (_loadedApps.Count > 0 ? _loadedApps : await Registry().LoadAsync())
                .Where(app => visibleIds.Contains(app.ThreadId))
                .ToList();
            var downloader = Downloader();
            using var http = NetworkConfig.Current.CreateClient();
            await Parallel.ForEachAsync(
                apps,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Clamp(settings.DownloadThreads, 1, 4)
                },
                async (app, token) =>
                {
                    if (path != _currentPath || !IsThumbnailCandidate(app, path))
                    {
                        return;
                    }

                    var ownsLoad = false;
                    for (var attempt = 0; attempt < 5 && !ownsLoad; attempt++)
                    {
                        ownsLoad = _thumbnailLoads.TryAdd(app.ThreadId, 0);
                        if (!ownsLoad)
                        {
                            await Task.Delay(40, token);
                        }
                    }
                    if (!ownsLoad)
                    {
                        return;
                    }

                    try
                    {
                        var cached = ThumbnailCache.Get(app.ThreadId);
                        if (cached != null)
                        {
                            await DeliverThumbnailAsync(app.ThreadId, cached, path, visibleIds, token);
                            return;
                        }

                        var previewBytes = !string.IsNullOrWhiteSpace(app.PreviewMessageId)
                            ? await downloader.DownloadMessageAttachmentBytesAsync(
                                app.ThreadId,
                                app.PreviewMessageId,
                                cancellationToken: token,
                                encryption: app.Encryption == null
                                    ? null
                                    : new EncryptedPayloadContext(
                                        app.FileId,
                                        app.Encryption,
                                        EncryptedPayloadRoles.Preview,
                                        0))
                            : await downloader.DownloadMarkedPreviewBytesAsync(
                                app.ThreadId,
                                "PREVIEW|",
                                cancellationToken: token) ??
                              await downloader.DownloadPreviewBytesAsync(
                                  app.ThreadId,
                                  "image-200",
                                  cancellationToken: token);
                        string? thumb;
                        if (previewBytes is { Length: > 0 })
                        {
                            thumb = ThumbnailCache.Store(app.ThreadId, previewBytes);
                        }
                        else
                        {
                            if (app.Encryption != null)
                            {
                                return;
                            }

                            if (!ThumbnailCache.IsImage(app.Name) || app.SizeBytes > 10L * 1024 * 1024)
                            {
                                return;
                            }

                            var url = await downloader.GetFirstAttachmentUrlAsync(app.ThreadId, token);
                            if (string.IsNullOrWhiteSpace(url))
                            {
                                return;
                            }

                            var data = await http.GetByteArrayAsync(url, token);
                            thumb = ThumbnailCache.GenerateAndStore(app.ThreadId, data);
                        }

                        if (thumb != null)
                        {
                            await DeliverThumbnailAsync(app.ThreadId, thumb, path, visibleIds, token);
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Thumbnail.Load", ex, app.ThreadId);
                    }
                    finally
                    {
                        _thumbnailLoads.TryRemove(app.ThreadId, out _);
                    }
                });
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
    private static HashSet<string> ParseVisibleThumbnailIds(string visibleIdsJson)
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
                .ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch
        {
            return [];
        }
    }
    private static bool IsThumbnailCandidate(AppEntry app, string path)
    {
        return app.Type == "file" &&
               PathRules.Normalize(app.Path) == path &&
               (ThumbnailCache.IsImage(app.Name) || ThumbnailCache.IsVideo(app.Name));
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
        var entry = FindLoadedEntry(threadId);

        DirectoryInfo? tempDir = null;
        try
        {
            if (type == "archive" && await RegisteredFileSizeAsync(threadId) > ArchivePreviewLimit)
            {
                await SendPreviewReadyAsync("archive", name, RenderArchiveTooLargePreview(await RegisteredFileSizeAsync(threadId)));
                return;
            }

            tempDir = CreatePreviewTempDir(threadId);
            if (type == "video" && ShouldUseVideoPreview(entry, settings))
            {
                var preview = await GetOrDownloadVideoPreview480Async(threadId, name, entry, tempDir);
                if (preview != null)
                {
                    var previewUrl = _previewServer.Register(preview, "video/mp4");
                    TrackPreviewUrl(threadId, "480p", previewUrl);
                    await SendPreviewReadyAsync(
                        "video",
                        name,
                        JsonSerializer.Serialize(
                            new { src = previewUrl, mime = "video/mp4", quality = "480p", qualities = new[] { "480p", "full" } },
                            JsonOptions.Web));
                    CleanDir(tempDir);
                    tempDir = null;
                    return;
                }
            }

            if (type == "video" && entry is { ChunkMessageIds.Count: > 0 })
            {
                var streamUrl = await GetOrCreateProgressiveVideoUrlAsync(entry, tempDir);
                if (!string.IsNullOrWhiteSpace(streamUrl))
                {
                    var qualities = HasVideoPreview(entry)
                        ? new[] { "480p", "full" }
                        : new[] { "full" };
                    await SendPreviewReadyAsync(
                        "video",
                        name,
                        JsonSerializer.Serialize(
                            new { src = streamUrl, mime = MediaMime(name), quality = "full", qualities },
                            JsonOptions.Web));
                    tempDir = null;
                    return;
                }
            }

            var file = type == "markdown"
                ? entry != null
                    ? await Downloader().DownloadAndAssembleAsync(
                        entry,
                        tempDir,
                        _ => { },
                        onDetailedProgress: progress => ReportPreviewProgress(progress, "generic"))
                    : await Downloader().DownloadAndAssembleAsync(
                        threadId,
                        tempDir,
                        _ => { },
                        onDetailedProgress: progress => ReportPreviewProgress(progress, "generic"))
                : await GetOrDownloadFullPreviewFileAsync(threadId, name, tempDir, "generic");

            switch (type)
            {
                case "image":
                    var imageUrl = _previewServer.Register(file, GuessMime(name));
                    TrackPreviewUrl(threadId, "default", imageUrl);
                    await SendPreviewReadyAsync("image", name, imageUrl);
                    break;
                case "pdf":
                    var pdfUrl = _previewServer.Register(file, "application/pdf");
                    TrackPreviewUrl(threadId, "default", pdfUrl);
                    await SendPreviewReadyAsync("pdf", name, JsonSerializer.Serialize(new { threadId, directUrl = pdfUrl, totalPages = 0, batchSize = 0 }, JsonOptions.Web));
                    break;
                case "markdown":
                    await SendPreviewReadyAsync("markdown", name, await File.ReadAllTextAsync(file.FullName, Encoding.UTF8));
                    break;
                case "text":
                    await SendPreviewReadyAsync("text", name, await File.ReadAllTextAsync(file.FullName));
                    break;
                case "document":
                    await SendPreviewReadyAsync("document", name, ExtractDocumentPreview(file, name));
                    break;
                case "archive":
                    await SendPreviewReadyAsync("archive", name, RenderArchivePreview(file, name));
                    break;
                case "video":
                case "audio":
                    var url = _previewServer.Register(file, MediaMime(name));
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
        foreach (var key in _previewUrls.Keys.Where(key => key.StartsWith(threadId + "|", StringComparison.Ordinal)).ToArray())
        {
            if (_previewUrls.TryRemove(key, out var url))
            {
                _previewServer.RemoveUrl(url);
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
            file = _previewServer.GetFileFromUrl(url);
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
            var fullUrl = _previewServer.Register(file, GuessFileMime(entry.Name));
            TrackPreviewUrl(threadId, "full", fullUrl);
        }

        Process.Start(new ProcessStartInfo(file.FullName) { UseShellExecute = true });
    }

    private async Task RequestVideoQualityAsync(string threadId, string name, string quality)
    {
        quality = quality.Equals("480p", StringComparison.OrdinalIgnoreCase) ? "480p" : "full";
        var existing = GetPreviewUrl(threadId, quality);
        if (existing != null)
        {
            await _bridge.CallAsync("_onVideoQualityReady", threadId, quality, existing, quality == "480p" ? "video/mp4" : MediaMime(name));
            return;
        }

        if (quality == "480p")
        {
            var entry = FindLoadedEntry(threadId);
            var dir480 = _previewDirs.GetOrAdd(threadId, _ => CreatePreviewTempDir(threadId));
            var preview = await GetOrDownloadVideoPreview480Async(threadId, name, entry, dir480);
            if (preview == null)
            {
                await _bridge.CallAsync("_onPreviewError", "A prévia 480p não está disponível para este vídeo.");
                return;
            }

            var previewUrl = _previewServer.Register(preview, "video/mp4");
            TrackPreviewUrl(threadId, "480p", previewUrl);
            await _bridge.CallAsync("_onVideoQualityReady", threadId, "480p", previewUrl, "video/mp4");
            return;
        }

        var entryForFull = FindLoadedEntry(threadId);
        var dir = _previewDirs.GetOrAdd(threadId, _ => CreatePreviewTempDir(threadId));
        if (entryForFull is { ChunkMessageIds.Count: > 0 } && ThumbnailCache.IsVideo(entryForFull.Name))
        {
            var streamUrl = await GetOrCreateProgressiveVideoUrlAsync(entryForFull, dir);
            if (!string.IsNullOrWhiteSpace(streamUrl))
            {
                await _bridge.CallAsync("_onVideoQualityReady", threadId, "full", streamUrl, MediaMime(name));
                return;
            }
        }

        var file = await GetOrDownloadFullPreviewFileAsync(threadId, name, dir, "full");
        var url = _previewServer.Register(file, MediaMime(name));
        TrackPreviewUrl(threadId, "full", url);
        await _bridge.CallAsync("_onVideoQualityReady", threadId, "full", url, MediaMime(name));
    }

    private async Task RequestVideoQualitySafeAsync(string threadId, string name, string quality)
    {
        try
        {
            await RequestVideoQualityAsync(threadId, name, quality);
        }
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
        return _loadedApps.FirstOrDefault(app => app.ThreadId == threadId);
    }

    private static bool ShouldUseVideoPreview(AppEntry? entry, AppSettings settings)
    {
        return entry != null &&
               !settings.VideoPreviewQuality.Equals("original", StringComparison.OrdinalIgnoreCase) &&
               ThumbnailCache.IsVideo(entry.Name);
    }

    private static bool HasVideoPreview(AppEntry? entry)
    {
        return entry?.VideoPreviewChunkMessageIds.Count > 0 || !string.IsNullOrWhiteSpace(entry?.VideoPreviewMessageId);
    }


    private async Task<string?> GetOrCreateProgressiveVideoUrlAsync(AppEntry entry, DirectoryInfo tempDir)
    {
        if (entry.Encryption != null)
        {
            return null;
        }

        var existing = GetPreviewUrl(entry.ThreadId, "full");
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        var parts = await Downloader().ResolveProgressivePartsAsync(entry);
        if (parts.Count == 0)
        {
            return null;
        }

        tempDir.Create();
        _previewDirs[entry.ThreadId] = tempDir;
        var fileName = "stream_" + PathRules.CleanFileName(string.IsNullOrWhiteSpace(entry.Name) ? "video.mp4" : entry.Name);
        var target = new FileInfo(System.IO.Path.Combine(tempDir.FullName, fileName));
        var progressive = new ProgressiveDownload(
            parts,
            target,
            entry.SizeBytes,
            progress => ReportPreviewProgress(progress, "full"));
        var url = _previewServer.RegisterProgressive(progressive, MediaMime(entry.Name));
        TrackPreviewUrl(entry.ThreadId, "full", url);
        return url;
    }
    private async Task<FileInfo> GetOrDownloadFullPreviewFileAsync(
        string threadId,
        string name,
        DirectoryInfo tempDir,
        string phase)
    {
        var cached = _store.GetCachedPreviewFile(threadId, "full", name);
        if (cached != null)
        {
            await _bridge.CallAsync("_onPreviewProgress", 100, "cache", cached.Length, cached.Length);
            return cached;
        }

        var entry = FindLoadedEntry(threadId);
        var downloaded = entry != null
            ? await Downloader().DownloadAndAssembleAsync(
                entry,
                tempDir,
                _ => { },
                onDetailedProgress: progress => ReportPreviewProgress(progress, phase))
            : await Downloader().DownloadAndAssembleAsync(
                threadId,
                tempDir,
                _ => { },
                onDetailedProgress: progress => ReportPreviewProgress(progress, phase));
        var stored = await CopyToPreviewCacheAsync(downloaded, threadId, "full", name);
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
            await _bridge.CallAsync("_onPreviewProgress", 100, "cache", cached.Length, cached.Length);
            return cached;
        }

        var preview = entry?.VideoPreviewChunkMessageIds.Count > 0
            ? await Downloader().DownloadVideoPreview480Async(
                entry,
                tempDir,
                progress => ReportPreviewProgress(progress, "video480"))
            : LooksLikeDiscordId(entry?.VideoPreviewMessageId ?? "")
                ? await Downloader().DownloadMessageAttachmentAsync(
                    threadId,
                    entry!.VideoPreviewMessageId,
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
            : await Downloader().DownloadMarkedPreviewFileAsync(
                threadId,
                "VIDEO_PREVIEW_480P|",
                tempDir,
                progress => ReportPreviewProgress(progress, "video480")) ??
              await Downloader().DownloadPreviewAndAssembleAsync(
                threadId,
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

    private async Task<FileInfo> CopyToPreviewCacheAsync(FileInfo source, string threadId, string slot, string name)
    {
        var target = _store.GetPreviewCacheFile(threadId, slot, name);
        target.Directory?.Create();
        if (!SamePath(source, target))
        {
            await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            await using var output = new FileStream(target.FullName, FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, true);
            await input.CopyToAsync(output);
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

        var clean = PathRules.CleanFileName(name.Trim());
        var registry = Registry();
        var all = await registry.LoadAsync();
        var folder = AppEntry.Folder(clean, _currentPath);
        folder.ParentFolderId = FolderIdForPath(all, _currentPath);
        await registry.AddEntryAsync(folder);
        await _bridge.CallAsync("_onStatus", "OK Pasta criada.", false);
        await LoadAppsAsync();
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
        var all = await registry.LoadAsync();
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

            await OpsUpdateAsync(opId, "done", $"OK {moved} item(ns) movido(s).");
            await _bridge.CallAsync("_onStatus", $"OK {moved} item(ns) movido(s).", false);
            await LoadAppsAsync();
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
        entry.ParentFolderId = FolderIdForPath(all.Where(candidate => !ReferenceEquals(candidate, entry)), newPath);
        if (entry.Type == "folder")
        {
            var oldFolderPath = PathRules.ChildPath(entry);
            var newFolderPath = PathRules.Normalize(newPath == "/" ? "/" + entry.Name : newPath + "/" + entry.Name);
            foreach (var child in all.Where(child => child.Type != "folder").ToList())
            {
                var childPath = PathRules.Normalize(child.Path);
                if (childPath == oldFolderPath || childPath.StartsWith(oldFolderPath + "/", StringComparison.Ordinal))
                {
                    await registry.DeleteEntryAsync(child);
                    child.Path = MovedChildPath(oldFolderPath, newFolderPath, childPath);
                    child.MetadataMessageId = "";
                    await registry.AddEntryAsync(child);
                }
            }
        }

        await registry.DeleteEntryAsync(entry);
        entry.Path = newPath;
        entry.MetadataMessageId = "";
        await registry.AddEntryAsync(entry);
    }

    private async Task UpdateFolderColorAsync(string metadataMessageId, string color)
    {
        var safeColor = Regex.IsMatch(color ?? "", "^#[0-9a-fA-F]{6}$") ? color : "#C4C7C5";

        var registry = Registry();
        var all = await registry.LoadAsync();
        var entry = all.FirstOrDefault(a => a.MetadataMessageId == metadataMessageId && a.Type == "folder");
        if (entry == null)
        {
            await ErrorAsync("Pasta não encontrada.");
            return;
        }

        await registry.DeleteEntryAsync(entry);
        entry.FolderColor = (safeColor ?? "#C4C7C5").ToUpperInvariant();
        entry.MetadataMessageId = "";
        await registry.AddEntryAsync(entry);
        await _bridge.CallAsync("_onStatus", "OK Cor da pasta atualizada.", false);
        await LoadAppsAsync();
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
        var all = await registry.LoadAsync();
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
            var children = all
                .Where(child => !ReferenceEquals(child, entry) && child.MetadataMessageId != entry.MetadataMessageId && IsInsideFolder(child, oldFolderPath))
                .OrderBy(child => PathRules.Normalize(child.Path).Count(ch => ch == '/'))
                .ToList();

            foreach (var child in children)
            {
                var childPath = PathRules.Normalize(child.Path);
                await registry.DeleteEntryAsync(child);
                child.Path = MovedChildPath(oldFolderPath, newFolderPath, childPath);
                child.MetadataMessageId = "";
                await registry.AddEntryAsync(child);
            }
        }

        await registry.DeleteEntryAsync(entry);
        entry.Name = clean;
        entry.MetadataMessageId = "";
        await registry.AddEntryAsync(entry);
        await _bridge.CallAsync("_onStatus", "OK Item renomeado.", false);
        await LoadAppsAsync();
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
        var basePath = _currentPath;
        var tasks = new List<UploadTask>();
        var folderCount = 0;
        var skipped = 0;
        var registry = Registry();
        var all = await registry.LoadAsync();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                var rootFolder = await CreateUploadFolderEntryAsync(registry, all, dir.Name, basePath);
                folderCount++;
                skipped += await CollectFolderUploadTasksAsync(registry, all, dir, PathRules.ChildPath(rootFolder), tasks, () => folderCount++);
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
                await LoadAppsAsync();
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
        if (resolved.OverwriteEntries.Count > 0)
        {
            await _bridge.CallAsync("_onStatus", "Substituindo arquivo(s) existente(s)...", true);
            await DeleteUploadConflictEntriesAsync(registry, resolved.OverwriteEntries);
        }

        if (tasks.Count == 0)
        {
            await LoadAppsAsync();
            await _bridge.CallAsync("_onStatus", "OK Upload cancelado.", false);
            return;
        }

        var opId = NewId();
        await OpsUploadBatchAsync(opId, tasks.Select(task => task.File.Name).ToArray());
        if (skipped > 0)
        {
            await OpsProgressAsync(opId, 0, $"{skipped} item(ns) sem acesso foram ignorados");
        }

        var cancellationToken = BeginTransferOperation(opId);
        try
        {
            await RunUploadQueueAsync(opId, tasks, all, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await OpsUpdateAsync(opId, "cancelled", "Cancelado");
            await _bridge.CallAsync("_onStatus", "Upload cancelado.", false);
        }
        finally
        {
            EndTransferOperation(opId);
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
            return new UploadConflictResolution(input.ToList(), []);
        }

        var tasks = new List<UploadTask>();
        var acceptedByKey = new Dictionary<string, List<UploadTask>>(StringComparer.OrdinalIgnoreCase);
        var overwriteEntries = new List<AppEntry>();
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
                if (existing.Count > 0)
                {
                    overwriteEntries.AddRange(existing);
                    existingByKey.Remove(key);
                }

                if (batchConflicts.Count > 0)
                {
                    tasks.RemoveAll(candidate => batchConflicts.Any(conflict => ReferenceEquals(conflict, candidate)));
                    batchConflicts.Clear();
                }

                Accept(key, task);
            }
            else if (action == "keep")
            {
                Accept(key, task);
            }
        }

        var uniqueOverwriteEntries = overwriteEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.MetadataMessageId))
            .GroupBy(entry => entry.MetadataMessageId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        return new UploadConflictResolution(tasks, uniqueOverwriteEntries);
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

    private async Task DeleteUploadConflictEntriesAsync(AppRegistry registry, IReadOnlyList<AppEntry> entries)
    {
        foreach (var entry in entries)
        {
            try
            {
                ClosePreview(entry.ThreadId);
                await DeleteStoredEntryAsync(registry, entry);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UploadConflictDelete] " + ex.Message);
                AppLogger.Error("UploadConflictDelete", ex, entry.Name);
            }
        }
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
        CancellationToken cancellationToken)
    {
        var settings = _store.Load();
        var uploader = new ForumUploader(RequireApi(), settings.AppsForumId);
        var registry = Registry();
        var entrySnapshot = allEntries.ToList();
        var uploadedEntries = new ConcurrentDictionary<int, AppEntry>();
        var done = 0;
        var failed = 0;
        var active = 0;
        var nextIndex = -1;
        var stoppedByApi = 0;
        var fileParallelism = Math.Min(Math.Clamp(settings.MaxParallelUploads, 1, 8), tasks.Count);
        using var uploadGate = new SemaphoreSlim(Math.Clamp(settings.UploadThreads, 1, 16));
        var totalBytes = tasks.Sum(task => Math.Max(0, task.File.Length));
        long uploadedBytes = 0;
        await OpsTransferMetricsAsync(opId, "upload", 0, tasks.Count, 0, totalBytes);

        var workers = Enumerable.Range(0, fileParallelism).Select(async _ =>
        {
            while (Volatile.Read(ref stoppedByApi) == 0)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= tasks.Count)
                {
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var task = tasks[index];
                var activeNow = Interlocked.Increment(ref active);
                await OpsUploadFileProgressAsync(opId, index, "active", 0, "Preparando...");
                await OpsProgressAsync(opId, Percent(Volatile.Read(ref done) + Volatile.Read(ref failed), tasks.Count), $"{activeNow} upload(s) ativo(s)");
                try
                {
                    var uploaded = await UploadSingleAsync(
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
                        cancellationToken,
                        uploadGate: uploadGate,
                        useBatchUi: true,
                        onBytesUploaded: async bytes =>
                        {
                            var currentBytes = Interlocked.Add(ref uploadedBytes, Math.Max(0, bytes));
                            await OpsTransferMetricsAsync(opId, "upload", Volatile.Read(ref done), tasks.Count, currentBytes, totalBytes);
                        });
                    uploadedEntries[index] = uploaded;
                    var completed = Interlocked.Increment(ref done);
                    await OpsUploadFileProgressAsync(opId, index, "done", 100, "Concluído");
                    await OpsTransferMetricsAsync(opId, "upload", completed, tasks.Count, Interlocked.Read(ref uploadedBytes), totalBytes);
                    await OpsProgressAsync(opId, Percent(completed + Volatile.Read(ref failed), tasks.Count), $"{completed}/{tasks.Count} concluídos");
                }
                catch (OperationCanceledException)
                {
                    await OpsUploadFileProgressAsync(opId, index, "cancelled", -1, "Cancelado");
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Upload] " + ex.Message);
                    AppLogger.Error("UploadSingle", ex, task.File.FullName);
                    var failedNow = Interlocked.Increment(ref failed);
                    var message = FriendlyUploadError(ex);
                    await OpsUploadFileProgressAsync(opId, index, "error", -1, message);
                    await OpsProgressAsync(opId, Percent(Volatile.Read(ref done) + failedNow, tasks.Count), $"{failedNow} upload(s) falharam");
                    if (IsBlockingUploadApiError(ex))
                    {
                        Volatile.Write(ref stoppedByApi, 1);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        });

        await Task.WhenAll(workers);
        foreach (var pair in uploadedEntries.OrderBy(pair => pair.Key))
        {
            allEntries.Add(pair.Value);
        }

        var status = done == tasks.Count ? "done" : "error";
        var summary = Volatile.Read(ref stoppedByApi) != 0
            ? $"{done}/{tasks.Count} concluídos - Discord recusou novos uploads agora"
            : failed > 0
                ? $"{done}/{tasks.Count} concluídos, {failed} falharam"
                : $"{done}/{tasks.Count} concluídos";
        await OpsUpdateAsync(opId, status, summary);
        await LoadAppsAsync();
    }

    private static bool IsBlockingUploadApiError(Exception ex)
    {
        return ex is DiscordApiException api && api.DiscordCode == DiscordApiClient.MaxActiveThreadsCode;
    }

    private static string FriendlyUploadError(Exception ex)
    {
        if (ex is DiscordApiException api)
        {
            if (api.DiscordCode == DiscordApiClient.MaxActiveThreadsCode)
            {
                return "limite de threads ativas no fórum do Discord";
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
        SemaphoreSlim? uploadGate = null,
        bool useBatchUi = false,
        Func<long, Task>? onBytesUploaded = null)
    {
        var name = CleanUploadName(existingTransferState?.FileName ?? file.Name);
        var preserveLegacyTransfer = existingTransferState?.Encryption == null &&
                                     existingTransferState?.UploadedChunks.Count > 0;
        var parts = await ArchiveSplitter.SplitAsync(
            file,
            cancellationToken,
            preserveLegacyTransfer ? null : EncryptionService.MaximumPlaintextPayloadBytes);
        var threadId = "";
        TransferState? transferState = existingTransferState;
        EncryptedPayloadStaging? encryptedStaging = null;
        try
        {
            var parentFolderId = string.IsNullOrWhiteSpace(transferState?.TargetFolderId)
                ? FolderIdForPath(allEntries, targetPath)
                : transferState.TargetFolderId;
            if (transferState == null)
            {
                transferState = await _transferManager.CreateUploadAsync(
                    file,
                    targetPath,
                    parentFolderId,
                    GuessFileMime(name),
                    parts.Count > 0 ? parts.Max(part => part.Length) : 0,
                    parts.Count,
                    cancellationToken);
                transferState.FileId = Guid.NewGuid().ToString("N");
                transferState.Encryption = _encryption.CreateFileEncryption(settings.EncryptionMode);
            }
            else
            {
                transferState.Status = TransferStatuses.Recovering;
                transferState.SourcePath = file.FullName;
                transferState.FileName = name;
                transferState.MimeType = string.IsNullOrWhiteSpace(transferState.MimeType) ? GuessFileMime(name) : transferState.MimeType;
                transferState.SizeBytes = Math.Max(0, file.Length);
                transferState.TargetPath = PathRules.Normalize(string.IsNullOrWhiteSpace(transferState.TargetPath) ? targetPath : transferState.TargetPath);
                transferState.TargetFolderId = parentFolderId;
                transferState.TotalChunks = parts.Count;
                transferState.ChunkSizeBytes = parts.Count > 0 ? parts.Max(part => part.Length) : 0;
                transferState.PendingChunks = Enumerable.Range(0, parts.Count)
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

            transferState.FileName = name;
            await _transferManager.SaveAsync(transferState, cancellationToken);
            targetPath = PathRules.Normalize(string.IsNullOrWhiteSpace(transferState.TargetPath) ? targetPath : transferState.TargetPath);
            if (transferState.Encryption != null)
            {
                encryptedStaging = await _encryption.StageFilesAsync(
                    parts,
                    transferState.FileId,
                    transferState.Encryption,
                    EncryptedPayloadRoles.Original,
                    cancellationToken);
            }
            var uploadParts = encryptedStaging?.Files ?? parts;

            async Task ReportRateLimitWaitAsync(TimeSpan remaining)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                await ReportUploadItemProgressAsync(
                    opId,
                    fileIndex,
                    fileTotal,
                    -1,
                    $"Limite do Discord - retomando em {seconds}s",
                    throttle,
                    true,
                    useBatchUi,
                    "waiting");
            }

            var uploadedMap = transferState.UploadedChunks
                .Where(chunk => !string.IsNullOrWhiteSpace(chunk.MessageId))
                .ToDictionary(chunk => chunk.Index, chunk => chunk.MessageId);
            using var checkpointGate = new SemaphoreSlim(1, 1);
            var upload = await uploader.UploadResumableAsync(
                name,
                uploadParts,
                transferState.ThreadId,
                transferState.BatchId,
                uploadedMap,
                uploadedParts =>
                {
                    var partPercent = Percent(uploadedParts, uploadParts.Count);
                    var message = uploadParts.Count > 1
                        ? $"Parte {Math.Max(1, uploadedParts)}/{uploadParts.Count}"
                        : "Enviando arquivo";
                    _ = ReportUploadItemProgressAsync(
                        opId,
                        fileIndex,
                        fileTotal,
                        partPercent,
                        message,
                        throttle,
                        uploadedParts >= parts.Count,
                        useBatchUi);
                },
                async chunk =>
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
                        if (onBytesUploaded != null)
                        {
                            await onBytesUploaded(chunk.SizeBytes);
                        }
                    }
                    finally
                    {
                        checkpointGate.Release();
                    }
                },
                cancellationToken,
                ReportRateLimitWaitAsync,
                settings.UploadThreads,
                uploadGate,
                transferState.Encryption == null
                    ? null
                    : new EncryptedPayloadContext(
                        transferState.FileId,
                        transferState.Encryption,
                        EncryptedPayloadRoles.Original,
                        0));
            threadId = upload.ThreadId;

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
                transferState.Encryption == null
                    ? null
                    : new EncryptedPayloadContext(
                        transferState.FileId,
                        transferState.Encryption,
                        EncryptedPayloadRoles.Preview,
                        0));

            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            var entry = AppEntry.File(name, threadId, now, file.Length, targetPath);
            entry.FileId = transferState.FileId;
            entry.Encryption = transferState.Encryption?.Clone();
            entry.ParentFolderId = parentFolderId;
            entry.BatchId = upload.BatchId;
            entry.ChunkCount = upload.ChunkCount;
            entry.ChunkMessageIds = upload.ChunkMessageIds.ToList();
            entry.MimeType = GuessFileMime(name);
            entry.PreviewMessageId = previewIds.PreviewMessageId;
            entry.VideoPreviewMessageId = previewIds.VideoPreviewMessageId;
            entry.VideoPreviewChunkMessageIds = previewIds.VideoPreviewChunkMessageIds.ToList();
            entry.VideoPreviewSizeBytes = previewIds.VideoPreviewSizeBytes;
            await registry.AddEntryAsync(entry, cancellationToken, ReportRateLimitWaitAsync);
            transferState.FileId = entry.FileId;
            transferState.MetadataMessageId = entry.MetadataMessageId;
            await _transferManager.CompleteAsync(transferState, settings.KeepTransferHistory, cancellationToken);
            try
            {
                await uploader.ArchiveThreadAsync(threadId, cancellationToken, ReportRateLimitWaitAsync);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error("UploadArchiveThread", ex, threadId);
            }

            return entry;
        }
        catch (OperationCanceledException)
        {
            if (!string.IsNullOrWhiteSpace(threadId))
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
                await _transferManager.FailAsync(transferState, ex, CancellationToken.None);
            }

            throw;
        }
        finally
        {
            encryptedStaging?.Dispose();
            ArchiveSplitter.CleanupParts(parts, file);
        }
    }

    private async Task<PreviewUploadIds> TryUploadGeneratedPreviewAsync(
        string opId,
        int fileIndex,
        int fileTotal,
        FileInfo file,
        string name,
        string threadId,
        ForumUploader uploader,
        AppSettings settings,
        UiThrottle throttle,
        Func<TimeSpan, Task> onRateLimitWait,
        CancellationToken cancellationToken,
        TransferState? transferState = null,
        bool useBatchUi = false,
        EncryptedPayloadContext? previewEncryption = null)
    {
        DirectoryInfo? tempDir = null;
        var previewMessageId = "";
        var videoPreviewMessageId = "";
        IReadOnlyList<string> videoPreviewChunkMessageIds = [];
        long videoPreviewSizeBytes = 0;
        try
        {
            if (ThumbnailCache.IsImage(name))
            {
                await ReportUploadItemProgressAsync(
                    opId,
                    fileIndex,
                    fileTotal,
                    0,
                    "Gerando miniatura 200x200",
                    throttle,
                    true,
                    useBatchUi);
                var png = ThumbnailCache.CreateSquarePng(await File.ReadAllBytesAsync(file.FullName, cancellationToken));
                ThumbnailCache.Store(threadId, png);
                tempDir = Directory.CreateTempSubdirectory("organessonium_image_preview_");
                var generated = new FileInfo(Path.Combine(tempDir.FullName, PreviewFileName(name, ".png")));
                await File.WriteAllBytesAsync(generated.FullName, png, cancellationToken);
                using (var encryptedPreview = previewEncryption == null
                    ? null
                    : await _encryption.StageFilesAsync(
                        [generated],
                        previewEncryption.FileId,
                        previewEncryption.Encryption,
                        EncryptedPayloadRoles.Preview,
                        cancellationToken))
                {
                    previewMessageId = await uploader.UploadPreviewFileToThreadAsync(
                        threadId,
                        previewEncryption == null
                            ? "PREVIEW|" + StripExtension(name)
                            : EncryptedPayloadProtocol.Message(previewEncryption, 1),
                        encryptedPreview?.Files[0] ?? generated,
                        cancellationToken,
                        onRateLimitWait: onRateLimitWait);
                }
                if (transferState != null)
                {
                    transferState.ThumbnailMessageId = previewMessageId;
                    await _transferManager.SaveAsync(transferState, cancellationToken);
                }

                return new PreviewUploadIds(previewMessageId, "", [], 0);
            }

            if (PreviewType(name) != "video")
            {
                return new PreviewUploadIds("", "", [], 0);
            }

            tempDir = Directory.CreateTempSubdirectory("organessonium_video_preview_");
            previewMessageId = await TryCreateAndUploadVideoThumbnailAsync(
                opId,
                fileIndex,
                fileTotal,
                file,
                name,
                threadId,
                tempDir,
                uploader,
                throttle,
                onRateLimitWait,
                cancellationToken,
                useBatchUi,
                previewEncryption);
            if (transferState != null && !string.IsNullOrWhiteSpace(previewMessageId))
            {
                transferState.ThumbnailMessageId = previewMessageId;
                await _transferManager.SaveAsync(transferState, cancellationToken);
            }

            if (!settings.VideoPreviewQuality.Equals("original", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var generated = await VideoPreviewGenerator.Create480pAsync(
                        file,
                        tempDir,
                        settings.HardwareAcceleration,
                        progress => _ = ReportUploadItemProgressAsync(
                            opId,
                            fileIndex,
                            fileTotal,
                            progress,
                            $"Gerando prévia 480p - {progress}%",
                            throttle,
                            progress is 0 or 100,
                            useBatchUi),
                        cancellationToken,
                        outputFileName: VideoPreviewFileName(name));
                    await ReportUploadItemProgressAsync(
                        opId,
                        fileIndex,
                        fileTotal,
                        -1,
                        "Enviando preview 480p",
                        throttle,
                        true,
                        useBatchUi);
                    var videoPreview = await UploadVideoPreview480Async(
                        opId,
                        fileIndex,
                        fileTotal,
                        threadId,
                        name,
                        generated,
                        uploader,
                        throttle,
                        onRateLimitWait,
                        cancellationToken,
                        useBatchUi,
                        previewEncryption == null
                            ? null
                            : previewEncryption with { Role = EncryptedPayloadRoles.Video480p });
                    videoPreviewChunkMessageIds = videoPreview.ChunkMessageIds;
                    videoPreviewMessageId = videoPreviewChunkMessageIds.FirstOrDefault() ?? "";
                    videoPreviewSizeBytes = videoPreview.SizeBytes;
                    if (transferState != null)
                    {
                        transferState.VideoPreview480pChunks = videoPreviewChunkMessageIds.ToList();
                        transferState.VideoPreview480pSizeBytes = videoPreviewSizeBytes;
                        await _transferManager.SaveAsync(transferState, cancellationToken);
                    }

                    if (string.IsNullOrWhiteSpace(previewMessageId))
                    {
                        previewMessageId = await TryCreateAndUploadVideoThumbnailAsync(
                            opId,
                            fileIndex,
                            fileTotal,
                            generated,
                            name,
                            threadId,
                            tempDir,
                            uploader,
                            throttle,
                            onRateLimitWait,
                            cancellationToken,
                            useBatchUi,
                            previewEncryption);
                        if (transferState != null && !string.IsNullOrWhiteSpace(previewMessageId))
                        {
                            transferState.ThumbnailMessageId = previewMessageId;
                            await _transferManager.SaveAsync(transferState, cancellationToken);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[VideoPreviewUpload] " + ex.Message);
                    AppLogger.Error("VideoPreviewUpload", ex, file.FullName);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[PreviewUpload] " + ex.Message);
            AppLogger.Error("PreviewUpload", ex, file.FullName);
        }
        finally
        {
            if (tempDir != null)
            {
                CleanDir(tempDir);
            }
        }

        return new PreviewUploadIds(previewMessageId, videoPreviewMessageId, videoPreviewChunkMessageIds, videoPreviewSizeBytes);
    }

    private async Task<VideoPreviewUploadResult> UploadVideoPreview480Async(
        string opId,
        int fileIndex,
        int fileTotal,
        string threadId,
        string originalName,
        FileInfo generated,
        ForumUploader uploader,
        UiThrottle throttle,
        Func<TimeSpan, Task> onRateLimitWait,
        CancellationToken cancellationToken,
        bool useBatchUi,
        EncryptedPayloadContext? videoEncryption)
    {
        var previewParts = await ArchiveSplitter.SplitAsync(
            generated,
            cancellationToken,
            videoEncryption == null ? null : EncryptionService.MaximumPlaintextPayloadBytes);
        EncryptedPayloadStaging? encryptedPreview = null;
        try
        {
            if (videoEncryption != null)
            {
                encryptedPreview = await _encryption.StageFilesAsync(
                    previewParts,
                    videoEncryption.FileId,
                    videoEncryption.Encryption,
                    EncryptedPayloadRoles.Video480p,
                    cancellationToken);
            }
            var payloadParts = encryptedPreview?.Files ?? previewParts;
            if (payloadParts.Count == 1)
            {
                var messageId = await uploader.UploadPreviewFileToThreadAsync(
                    threadId,
                    videoEncryption == null
                        ? "VIDEO_PREVIEW_480P|" + StripExtension(originalName)
                        : EncryptedPayloadProtocol.Message(videoEncryption, 1),
                    payloadParts[0],
                    cancellationToken,
                    onRateLimitWait: onRateLimitWait);
                return new VideoPreviewUploadResult(generated.Length, [messageId]);
            }

            var chunks = await uploader.UploadPreviewToThreadAsync(
                threadId,
                "video-480",
                payloadParts,
                uploadedParts =>
                {
                    var percent = Percent(uploadedParts, payloadParts.Count);
                    var msg = $"Enviando preview 480p - parte {Math.Max(1, uploadedParts)}/{payloadParts.Count}";
                    _ = ReportUploadItemProgressAsync(
                        opId,
                        fileIndex,
                        fileTotal,
                        percent,
                        msg,
                        throttle,
                        uploadedParts >= previewParts.Count,
                        useBatchUi);
                },
                cancellationToken,
                onRateLimitWait: onRateLimitWait,
                encryptedPayload: videoEncryption);
            return new VideoPreviewUploadResult(generated.Length, chunks);
        }
        finally
        {
            encryptedPreview?.Dispose();
            ArchiveSplitter.CleanupParts(previewParts, generated);
        }
    }

    private async Task<string> TryCreateAndUploadVideoThumbnailAsync(
        string opId,
        int fileIndex,
        int fileTotal,
        FileInfo source,
        string originalName,
        string threadId,
        DirectoryInfo tempDir,
        ForumUploader uploader,
        UiThrottle throttle,
        Func<TimeSpan, Task> onRateLimitWait,
        CancellationToken cancellationToken,
        bool useBatchUi,
        EncryptedPayloadContext? previewEncryption)
    {
        try
        {
            await ReportUploadItemProgressAsync(
                opId,
                fileIndex,
                fileTotal,
                0,
                "Gerando miniatura 200x200",
                throttle,
                true,
                useBatchUi);
            var thumbnail = await VideoPreviewGenerator.CreateThumbnailAsync(
                source,
                tempDir,
                PreviewFileName(originalName, ".jpg"),
                cancellationToken);
            if (thumbnail == null)
            {
                return "";
            }

            ThumbnailCache.Store(threadId, await File.ReadAllBytesAsync(thumbnail.FullName, cancellationToken));
            using var encryptedThumbnail = previewEncryption == null
                ? null
                : await _encryption.StageFilesAsync(
                    [thumbnail],
                    previewEncryption.FileId,
                    previewEncryption.Encryption,
                    EncryptedPayloadRoles.Preview,
                    cancellationToken);
            var messageId = await uploader.UploadPreviewFileToThreadAsync(
                threadId,
                previewEncryption == null
                    ? "PREVIEW|" + StripExtension(originalName)
                    : EncryptedPayloadProtocol.Message(previewEncryption, 1),
                encryptedThumbnail?.Files[0] ?? thumbnail,
                cancellationToken,
                onRateLimitWait: onRateLimitWait);
            await ReportUploadItemProgressAsync(
                opId,
                fileIndex,
                fileTotal,
                100,
                "Miniatura de vídeo enviada",
                throttle,
                true,
                useBatchUi);
            return messageId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[VideoThumbnailUpload] " + ex.Message);
            AppLogger.Error("VideoThumbnailUpload", ex, source.FullName);
            return "";
        }
    }

    private async Task StartDownloadAsync(string threadId, string name, DirectoryInfo? selectedDestination = null)
    {
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
        await OpsAddAsync(opId, "download", name);
        var cancellationToken = BeginTransferOperation(opId);
        _ = RunAsync(async () =>
        {
            TransferState? transferState = null;
            long totalDownloadBytes = 0;
            try
            {
                await OpsUpdateAsync(opId, "active", "Preparando download...");
                var entry = (_loadedApps.Count > 0 ? _loadedApps : await Registry().LoadAsync(cancellationToken))
                    .FirstOrDefault(app => app.ThreadId == threadId);
                totalDownloadBytes = Math.Max(0, entry?.SizeBytes ?? 0);
                await OpsTransferMetricsAsync(opId, "download", 0, 1, 0, totalDownloadBytes);
                if (entry != null)
                {
                    transferState = await _transferManager.CreateDownloadAsync(entry, dest, cancellationToken);
                    await Downloader().DownloadAndAssembleAsync(
                        entry,
                        dest,
                        transferState,
                        _transferManager,
                        msg => _ = OpsUpdateAsync(opId, "active", msg),
                        cancellationToken,
                        progress =>
                        {
                            Interlocked.Exchange(ref totalDownloadBytes, Math.Max(0, progress.TotalBytes));
                            _ = OpsProgressAsync(opId, progress.Percent, progress.Message);
                            _ = OpsTransferMetricsAsync(opId, "download", 0, 1, progress.DownloadedBytes, progress.TotalBytes);
                        });
                    await _transferManager.CompleteAsync(transferState, _store.Load().KeepTransferHistory, cancellationToken);
                }
                else
                {
                    await Downloader().DownloadAndAssembleAsync(threadId, dest, msg => _ = OpsUpdateAsync(opId, "active", msg), cancellationToken);
                }

                var completedBytes = Interlocked.Read(ref totalDownloadBytes);
                await OpsTransferMetricsAsync(opId, "download", 1, 1, completedBytes, completedBytes);
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
                await OpsTransferMetricsAsync(opId, "download", 1, 1, completedBytes, completedBytes);
                await OpsUpdateAsync(opId, "done", opened ? "Concluído com aviso; arquivo aberto." : "Concluído com aviso.");
            }
            catch (Exception ex)
            {
                if (transferState != null)
                {
                    await _transferManager.FailAsync(transferState, ex, CancellationToken.None);
                }

                await OpsUpdateAsync(opId, "error", ex.Message);
                if (ex is EncryptedPayloadIntegrityException integrity &&
                    await OfferRetryEncryptedDownloadAsync(integrity))
                {
                    await StartDownloadAsync(threadId, name, dest);
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
        var baseDir = await ResolveDownloadDirectoryAsync("Salvar pasta em...");
        if (baseDir == null)
        {
            return;
        }

        _ = RunAsync(async () =>
        {
            var all = await Registry().LoadAsync();
            var entry = all.FirstOrDefault(a => a.Type == "folder" && a.Name == folderName && a.ThreadId == folderThreadId);
            var folderPath = entry != null ? PathRules.ChildPath(entry) : PathRules.Normalize(_currentPath == "/" ? "/" + folderName : _currentPath + "/" + folderName);
            var toDownload = all.Where(a => a.Type != "folder" && (PathRules.Normalize(a.Path) == folderPath || PathRules.Normalize(a.Path).StartsWith(folderPath + "/", StringComparison.Ordinal))).ToList();
            if (toDownload.Count == 0)
            {
                await ErrorAsync("A pasta está vazia.");
                return;
            }

            var opId = NewId();
            await OpsAddAsync(opId, "download", $"{folderName} ({toDownload.Count} arquivo(s))");
            var cancellationToken = BeginTransferOperation(opId);
            var done = 0;
            var failed = 0;
            var totalBytes = toDownload.Sum(app => Math.Max(0, app.SizeBytes));
            long downloadedBytes = 0;
            await OpsTransferMetricsAsync(opId, "download", 0, toDownload.Count, 0, totalBytes);
            try
            {
                for (var i = 0; i < toDownload.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var app = toDownload[i];
                    var rel = PathRules.Normalize(app.Path)[folderPath.Length..].TrimStart('/');
                    var destDir = string.IsNullOrWhiteSpace(rel)
                        ? new DirectoryInfo(System.IO.Path.Combine(baseDir.FullName, folderName))
                        : new DirectoryInfo(System.IO.Path.Combine(baseDir.FullName, folderName, rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    Directory.CreateDirectory(destDir.FullName);
                    await OpsUpdateAsync(opId, "active", $"({i + 1}/{toDownload.Count}) {app.Name}");
                    TransferState? transferState = null;
                    long currentFileBytes = 0;
                    long currentFileTotal = Math.Max(0, app.SizeBytes);
                    var completedBeforeFile = Interlocked.Read(ref downloadedBytes);
                    try
                    {
                        transferState = await _transferManager.CreateDownloadAsync(app, destDir, cancellationToken);
                        await Downloader().DownloadAndAssembleAsync(
                            app,
                            destDir,
                            transferState,
                            _transferManager,
                            _ => { },
                            cancellationToken,
                            progress =>
                            {
                                Interlocked.Exchange(ref currentFileBytes, Math.Max(0, progress.DownloadedBytes));
                                Interlocked.Exchange(ref currentFileTotal, Math.Max(0, progress.TotalBytes));
                                var aggregate = completedBeforeFile + Math.Max(0, progress.DownloadedBytes);
                                var overallPercent = totalBytes > 0
                                    ? Math.Clamp((int)Math.Round(aggregate * 100.0 / totalBytes), 0, 100)
                                    : Percent(done, toDownload.Count);
                                _ = OpsProgressAsync(opId, overallPercent, $"({i + 1}/{toDownload.Count}) {progress.Message}");
                                _ = OpsTransferMetricsAsync(opId, "download", done, toDownload.Count, aggregate, totalBytes);
                            });
                        await _transferManager.CompleteAsync(transferState, _store.Load().KeepTransferHistory, cancellationToken);
                        var measuredTotal = Math.Max(Math.Max(0, app.SizeBytes), Math.Max(Interlocked.Read(ref currentFileTotal), Interlocked.Read(ref currentFileBytes)));
                        Interlocked.Exchange(ref downloadedBytes, completedBeforeFile + measuredTotal);
                        done++;
                        await OpsTransferMetricsAsync(opId, "download", done, toDownload.Count, Interlocked.Read(ref downloadedBytes), totalBytes);
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
                        done++;
                        await OpsTransferMetricsAsync(opId, "download", done, toDownload.Count, Interlocked.Read(ref downloadedBytes), totalBytes);
                    }
                    catch (Exception ex)
                    {
                        if (transferState != null)
                        {
                            await _transferManager.FailAsync(transferState, ex, CancellationToken.None);
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

    private async Task DeleteAppAsync(string threadId, string metadataMessageId)
    {
        var registry = Registry();
        var all = await registry.LoadAsync();
        var entry = all.FirstOrDefault(a => a.MetadataMessageId == metadataMessageId);
        if (entry != null)
        {
            var result = await DeleteEntriesWithProgressAsync(registry, all, [entry]);
            if (result.Failed > 0)
            {
                await ErrorAsync($"{result.Deleted}/{result.Total} apagados; {result.Failed} falharam.");
                await LoadAppsAsync();
                return;
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(threadId) && !threadId.StartsWith("folder_", StringComparison.Ordinal))
            {
                await Downloader().DeleteThreadAsync(threadId);
                ThumbnailCache.Remove(threadId);
            }

            await registry.DeleteEntryAsync(new AppEntry { MetadataMessageId = metadataMessageId });
        }

        await _bridge.CallAsync("_onStatus", "OK Item apagado.", false);
        await LoadAppsAsync();
    }

    private async Task DeleteItemsAsync(string idsJson)
    {
        var ids = JsonSerializer.Deserialize<string[]>(idsJson, JsonOptions.Web) ?? [];
        if (ids.Length == 0)
        {
            await ErrorAsync("Nenhum item selecionado.");
            return;
        }

        var registry = Registry();
        var all = await registry.LoadAsync();
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
        if (result.Failed == 0)
        {
            await _bridge.CallAsync("_onStatus", $"OK {result.Deleted} item(ns) apagado(s).", false);
        }
        else
        {
            await ErrorAsync($"{result.Deleted}/{result.Total} apagados; {result.Failed} falharam.");
        }

        await LoadAppsAsync();
    }

    private async Task<DeleteResult> DeleteEntriesWithProgressAsync(AppRegistry registry, List<AppEntry> all, IReadOnlyList<AppEntry> selected)
    {
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
        for (var i = 0; i < plan.Files.Count; i++)
        {
            var file = plan.Files[i];
            await OpsProgressAsync(opId, Percent(i, progressTotal), DeleteProgressMessage("arquivo", i, fileTotal, file.Name));
            try
            {
                await DeleteStoredEntryAsync(registry, file);
                deletedFiles++;
            }
            catch (Exception ex)
            {
                failed++;
                Debug.WriteLine("[Delete] " + ex.Message);
            }

            await OpsProgressAsync(opId, Percent(i + 1, progressTotal), DeleteProgressMessage("arquivo", i + 1, fileTotal, file.Name));
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
                await registry.DeleteEntryAsync(folder);
                deletedFolders++;
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
        await OpsUpdateAsync(opId, status, failed == 0
            ? $"{visibleDeleted}/{visibleTotal} {visibleKind} apagado(s)"
            : $"{visibleDeleted}/{visibleTotal} {visibleKind} apagado(s), {failed} falharam");
        return new DeleteResult(visibleTotal, visibleDeleted, failed);
    }

    private async Task DeleteEntryAsync(AppRegistry registry, List<AppEntry> all, AppEntry entry)
    {
        var isFolder = entry.Type == "folder" || entry.ThreadId.StartsWith("folder_", StringComparison.Ordinal);
        if (isFolder)
        {
            var folderPath = PathRules.ChildPath(entry);
            var children = all
                .Where(a => a != entry)
                .Where(a => PathRules.Normalize(a.Path) == folderPath || PathRules.Normalize(a.Path).StartsWith(folderPath + "/", StringComparison.Ordinal))
                .ToList();
            foreach (var child in children)
            {
                await DeleteStoredEntryAsync(registry, child);
            }

            await registry.DeleteEntryAsync(entry);
        }
        else
        {
            await DeleteStoredEntryAsync(registry, entry);
        }
    }

    private async Task DeleteStoredEntryAsync(AppRegistry registry, AppEntry entry)
    {
        var isFolder = entry.Type == "folder" || entry.ThreadId.StartsWith("folder_", StringComparison.Ordinal);
        if (!isFolder && !string.IsNullOrWhiteSpace(entry.ThreadId))
        {
            await Downloader().DeleteThreadAsync(entry.ThreadId);
            ThumbnailCache.Remove(entry.ThreadId);
        }

        await registry.DeleteEntryAsync(entry);
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
                var folderPath = PathRules.ChildPath(entry);
                foreach (var child in all.Where(child => child != entry && IsInsideFolder(child, folderPath)))
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

    private static string? FolderIdForPath(IEnumerable<AppEntry> entries, string path)
    {
        var normalized = PathRules.Normalize(path);
        if (normalized == "/")
        {
            return null;
        }

        return entries
            .Where(entry => IsFolderEntry(entry))
            .FirstOrDefault(entry => PathRules.ChildPath(entry).Equals(normalized, StringComparison.OrdinalIgnoreCase))
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
            var encryption = _encryption.CreateFileEncryption(settings.EncryptionMode);
            var fileId = Guid.NewGuid().ToString("N");
            var parts = await ArchiveSplitter.SplitAsync(
                file,
                cancellationToken: default,
                maxPartBytes: EncryptionService.MaximumPlaintextPayloadBytes);
            try
            {
                using var encryptedParts = await _encryption.StageFilesAsync(
                    parts,
                    fileId,
                    encryption,
                    EncryptedPayloadRoles.Original);
                var encryptedPayload = new EncryptedPayloadContext(fileId, encryption, EncryptedPayloadRoles.Original, 0);
                var uploader = new ForumUploader(RequireApi(), settings.AppsForumId);
                var canReuseThread = !string.IsNullOrWhiteSpace(threadId) && !threadId.StartsWith("folder_", StringComparison.Ordinal);
                ForumUploadResult uploadResult;
                try
                {
                    uploadResult = canReuseThread
                        ? await uploader.UploadToThreadAsync(threadId, safeName, encryptedParts.Files, encryptedPayload: encryptedPayload)
                        : await uploader.UploadResumableAsync(
                            safeName,
                            encryptedParts.Files,
                            "",
                            "",
                            new Dictionary<int, string>(),
                            cancellationToken: default,
                            encryptedPayload: encryptedPayload);
                }
                catch (DiscordApiException ex) when (canReuseThread && ((int)ex.StatusCode == 403 || (int)ex.StatusCode == 404))
                {
                    uploadResult = await uploader.UploadResumableAsync(
                        safeName,
                        encryptedParts.Files,
                        "",
                        "",
                        new Dictionary<int, string>(),
                        cancellationToken: default,
                        encryptedPayload: encryptedPayload);
                }
                var newThreadId = uploadResult.ThreadId;
                var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                var updated = AppEntry.File(safeName, newThreadId, now, file.Length, path);
                updated.FileId = fileId;
                updated.Encryption = encryption;
                updated.BatchId = uploadResult.BatchId;
                updated.ChunkCount = uploadResult.ChunkCount;
                updated.ChunkMessageIds = uploadResult.ChunkMessageIds.ToList();
var registry = Registry();
                var all = await registry.LoadAsync();
                updated.ParentFolderId = FolderIdForPath(all, path);
                var newMetadataMessageId = await registry.AddEntryAsync(updated);

                if (!string.IsNullOrWhiteSpace(metadataMessageId))
                {
                    await registry.DeleteEntryAsync(new AppEntry { MetadataMessageId = metadataMessageId });
                }

                if (!string.IsNullOrWhiteSpace(threadId) && threadId != newThreadId)
                {
                    try { await Downloader().DeleteThreadAsync(threadId); } catch { }
                    ThumbnailCache.Remove(threadId);
                }

                updated.MetadataMessageId = newMetadataMessageId;
                try
                {
                    await uploader.ArchiveThreadAsync(newThreadId);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("MarkdownArchiveThread", ex, newThreadId);
                }

                await _bridge.CallAsync("_onMarkdownSaved", tabId, updated);
            }
            finally
            {
                ArchiveSplitter.CleanupParts(parts, file);
            }
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
            using var staged = await _encryption.StageFilesAsync(
                [file],
                fileId,
                encryption,
                EncryptedPayloadRoles.MarkdownImage);
            message = await api.SendFileMessageAsync(
                threadId,
                EncryptedPayloadProtocol.Message(context, 1),
                staged.Files[0]);
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
            var all = await Registry().LoadAsync();
            var image = all.FirstOrDefault(app => app.ThreadId == threadId);
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

        var url = _previewServer.RegisterBytes(bytes, reference.MimeType);
        await _bridge.CallAsync("_onEncryptedMarkdownImageReady", tabId, value, url);
    }

    private async Task ResolveEncryptedDriveMarkdownImageAsync(string tabId, string imageThreadId)
    {
        var image = (_loadedApps.Count > 0 ? _loadedApps : await Registry().LoadAsync())
            .FirstOrDefault(app => app.ThreadId == imageThreadId && app.Encryption != null);
        if (image == null)
        {
            throw new InvalidOperationException("Não foi possível localizar a imagem criptografada.");
        }

        var tempDir = Directory.CreateTempSubdirectory("organessonium_markdown_image_");
        _previewDirs["markdown-image-" + NewId()] = tempDir;
        var file = await Downloader().DownloadAndAssembleAsync(image, tempDir, _ => { });
        var url = _previewServer.Register(file, string.IsNullOrWhiteSpace(image.MimeType) ? GuessFileMime(image.Name) : image.MimeType);
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
        settings.DownloadPath = ArgString(args, 3);
        settings.TempPath = ArgString(args, 4);
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
        if (settings.Proxy.Mode == ProxyMode.Manual && !settings.Proxy.IsValidManual(out var _))
        {
            settings.Proxy.Mode = ProxyMode.Automatic;
        }

        NetworkConfig.Current.Apply(settings.Proxy);
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
    private async Task<DirectoryInfo?> ResolveDownloadDirectoryAsync(string title)
    {
        var settings = _store.Load();
        if (!string.IsNullOrWhiteSpace(settings.DownloadPath))
        {
            var dir = Directory.CreateDirectory(settings.DownloadPath);
            return dir;
        }

        DirectoryInfo? selected = null;
        await _owner.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog { Title = title };
            if (dialog.ShowDialog(_owner) == true)
            {
                selected = Directory.CreateDirectory(dialog.FolderName);
            }
        });
        return selected;
    }

    private AppRegistry Registry()
    {
        return new AppRegistry(RequireApi(), _store, _store.Load());
    }

    private ForumDownloader Downloader()
    {
        return new ForumDownloader(RequireApi(), _store.Load(), _encryption);
    }

    private DiscordApiClient RequireApi()
    {
        return _session?.RequireApi() ?? throw new InvalidOperationException("Discord ainda não conectado.");
    }

    private async Task RunAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            AppLogger.Error("RunAsync", ex);
            await ErrorAsync(ex.Message);
        }
    }

    private async Task ErrorAsync(string message)
    {
        await _bridge.CallAsync("_onError", message);
    }

    private async Task OpsAddAsync(string id, string type, string name)
    {
        await _bridge.CallAsync("_opsAdd", id, type, name);
    }

    private async Task OpsUploadBatchAsync(string id, IReadOnlyList<string> names)
    {
        await _bridge.CallAsync("_opsUploadBatch", id, names);
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
        long totalBytes)
    {
        await _bridge.CallAsync(
            "_opsTransferMetrics",
            id,
            direction,
            Math.Max(0, completedItems),
            Math.Max(1, totalItems),
            Math.Max(0, transferredBytes),
            Math.Max(0, totalBytes));
    }

    private CancellationToken BeginTransferOperation(string opId)
    {
        var cts = new CancellationTokenSource();
        _transferOperations[opId] = cts;
        return cts.Token;
    }

    private void EndTransferOperation(string opId)
    {
        if (_transferOperations.TryRemove(opId, out var cts))
        {
            cts.Dispose();
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
        var states = await _transferManager.RecoverableAsync();
        if (states.Count == 0)
        {
            return;
        }

        var settings = _store.Load();
        if (settings.AutoResumeTransfers)
        {
            _ = RunAsync(ResumeInterruptedTransfersAsync);
            return;
        }

        await _bridge.CallAsync("_onInterruptedTransfers", states.Count);
    }

    private async Task FailInterruptedTransfersAsync()
    {
        var states = await _transferManager.RecoverableAsync();
        foreach (var state in states)
        {
            await _transferManager.FailAsync(state, new InvalidOperationException("Marcada como falha pelo usuário."), CancellationToken.None);
        }

        await _bridge.CallAsync("_onStatus", "Transferências interrompidas marcadas como falha.", false);
    }

    private async Task ResumeInterruptedTransfersAsync()
    {
        var states = await _transferManager.RecoverableAsync();
        if (states.Count == 0)
        {
            await _bridge.CallAsync("_onStatus", "Nenhuma transferência interrompida.", false);
            return;
        }

        await _bridge.CallAsync("_onStatus", $"Retomando {states.Count} transferência(s)...", true);
        var settings = _store.Load();
        var registry = Registry();
        var all = await registry.LoadAsync();
        var uploader = new ForumUploader(RequireApi(), settings.AppsForumId);
        var throttle = new UiThrottle();
        var resumed = 0;

        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var opId = state.TransferId;
            await OpsAddAsync(opId, state.TransferType, state.FileName);
            var cancellationToken = BeginTransferOperation(opId);
            try
            {
                state.Status = TransferStatuses.Recovering;
                await _transferManager.SaveAsync(state, cancellationToken);

                if (state.TransferType.Equals(TransferTypes.Upload, StringComparison.OrdinalIgnoreCase))
                {
                    var source = new FileInfo(state.SourcePath);
                    if (!source.Exists)
                    {
                        throw new FileNotFoundException("Arquivo de origem não existe mais.", state.SourcePath);
                    }

                    var uploaded = await UploadSingleAsync(
                        opId,
                        i,
                        states.Count,
                        source,
                        state.TargetPath,
                        all,
                        uploader,
                        registry,
                        settings,
                        throttle,
                        cancellationToken,
                        state);
                    all.Add(uploaded);
                    resumed++;
                    await OpsUpdateAsync(opId, "done", "Concluído");
                    continue;
                }

                if (state.TransferType.Equals(TransferTypes.Download, StringComparison.OrdinalIgnoreCase))
                {
                    var entry = all.FirstOrDefault(app =>
                        (!string.IsNullOrWhiteSpace(state.FileId) && app.FileId == state.FileId) ||
                        (!string.IsNullOrWhiteSpace(state.ThreadId) && app.ThreadId == state.ThreadId));
                    if (entry == null)
                    {
                        throw new InvalidOperationException("Arquivo não encontrado na listagem atual para retomar download.");
                    }

                    var destination = new DirectoryInfo(string.IsNullOrWhiteSpace(state.DestinationPath)
                        ? (settings.DownloadPath.Trim().Length > 0 ? settings.DownloadPath : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                        : state.DestinationPath);
                    await Downloader().DownloadAndAssembleAsync(
                        entry,
                        destination,
                        state,
                        _transferManager,
                        msg => _ = OpsUpdateAsync(opId, "active", msg),
                        cancellationToken,
                        progress => _ = OpsProgressAsync(opId, progress.Percent, progress.Message));
                    await _transferManager.CompleteAsync(state, settings.KeepTransferHistory, cancellationToken);
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
                await _transferManager.FailAsync(state, ex, CancellationToken.None);
                await OpsUpdateAsync(opId, "error", ex.Message);
            }
            finally
            {
                EndTransferOperation(opId);
            }
        }

        await LoadAppsAsync();
        await _bridge.CallAsync("_onStatus", $"{resumed}/{states.Count} transferência(s) retomada(s).", false);
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
        string batchStatus = "active")
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
            Percent(fileIndex, fileTotal),
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
        string parentPath)
    {
        var folder = AppEntry.Folder(PathRules.CleanFileName(name), parentPath);
        folder.ParentFolderId = FolderIdForPath(all, parentPath);
        await registry.AddEntryAsync(folder);
        all.Add(folder);
        return folder;
    }

    private async Task<int> CollectFolderUploadTasksAsync(
        AppRegistry registry,
        List<AppEntry> all,
        DirectoryInfo folder,
        string folderVirtualPath,
        List<UploadTask> output,
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
                var childFolder = await CreateUploadFolderEntryAsync(registry, all, child.Name, virtualPath);
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
            return (await Registry().LoadAsync()).FirstOrDefault(a => a.ThreadId == threadId)?.SizeBytes ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    private Task SendPreviewReadyAsync(string type, string name, string data)
    {
        return _bridge.CallAsync("_onPreviewReady", type, name, data);
    }

    private static string? PreviewType(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        if (new[] { "jpg", "jpeg", "png", "gif", "bmp", "webp", "tiff", "tif", "svg" }.Contains(ext)) return "image";
        if (new[] { "mp4", "mkv", "avi", "mov", "webm", "m4v", "3gp" }.Contains(ext)) return "video";
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
                "rtf" => StripRtf(File.ReadAllText(file.FullName, Encoding.UTF8)),
                "doc" => "Prévia indisponível para arquivos .doc legados. Converta para .docx ou baixe o arquivo para abrir no editor.",
                _ => File.ReadAllText(file.FullName, Encoding.UTF8)
            };
        }
        catch (Exception ex)
        {
            return "Não foi possível extrair a prévia deste documento.\n\n" + ex.Message;
        }
    }

    private static string ExtractDocxPreview(FileInfo file)
    {
        using var zip = ZipFile.OpenRead(file.FullName);
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
        using var zip = ZipFile.OpenRead(file.FullName);
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
        using var zip = ZipFile.OpenRead(file.FullName);
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
        using var zip = ZipFile.OpenRead(file.FullName);
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
        using var zip = ZipFile.OpenRead(file.FullName);
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
        using var zip = ZipFile.OpenRead(file.FullName);
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
        var model = new Dictionary<string, object?>
        {
            ["sizeBytes"] = file.Length,
            ["maxPreviewBytes"] = ArchivePreviewLimit,
            ["format"] = ext.ToUpperInvariant()
        };

        if (file.Length > ArchivePreviewLimit)
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
            ListSevenZipArchive(file, model, 5000);
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
            using var zip = ZipFile.OpenRead(file.FullName);
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
    private sealed record ResolvedDiscordChannels(string NamesChannelId, string AppsForumId);
    private sealed record UploadTask(FileInfo File, string Path);
    private sealed record UploadConflictResponse(string Action, bool ApplyToAll);
    private sealed record UploadConflictResolution(List<UploadTask> Tasks, List<AppEntry> OverwriteEntries);
    private sealed record PreviewUploadIds(
        string PreviewMessageId,
        string VideoPreviewMessageId,
        IReadOnlyList<string> VideoPreviewChunkMessageIds,
        long VideoPreviewSizeBytes);
    private sealed record VideoPreviewUploadResult(long SizeBytes, IReadOnlyList<string> ChunkMessageIds);
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

    private sealed class SetupChannelsMissingException : Exception
    {
        public SetupChannelsMissingException(string message) : base(message)
        {
        }
    }
}






