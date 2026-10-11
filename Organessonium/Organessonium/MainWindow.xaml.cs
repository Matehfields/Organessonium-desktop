using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Services;
using ComTypes = System.Runtime.InteropServices.ComTypes;
using Windows.Graphics;

namespace Organessonium;


public partial class MainWindow : Window
{
    private const string LocalWebHost = "organessonium.local";
    private static readonly Lazy<DirectoryInfo> WebRootDirectory = new(ResolveWebRootDirectoryCore);
    private SettingsStore _store = null!;
    private WebMessageBridge? _bridge;
    private AppController? _controller;
    private WebView2PreviewAdapter? _previewAdapter;
    private string? _pendingSetupError;
    private bool _draggingFiles;
    private HwndSource? _windowSource;
    private string _lastDropSignature = "";
    private long _lastDropTick;
    private string _lastSystemTheme = WindowThemeService.InitialTheme();
    private readonly Windows.UI.ViewManagement.UISettings? _windowsColors = WindowsAccentService.TryCreate();
    private string _lastSystemAccent = "";
    private bool _recoveringWebView;
    private bool _isWebViewFullscreen;
    private WindowState _savedWindowState;
    private WindowStyle _savedWindowStyle;
    private ResizeMode _savedResizeMode;
    private bool _savedTopmost;
    private bool _shutdownInProgress;
    private AppWindowTitleBar? _nativeTitleBar;
    private bool _usesIntegratedTitleBar;
    private int _lastTitleBarLeftInset = -1;
    private int _lastTitleBarRightInset = -1;
    private int _lastTitleBarHeight = -1;
    private RectInt32[]? _lastNativeDragRectangles;
    private readonly Dictionary<IntPtr, NativeFileDropTarget> _oleDropTargets = new();

    private const int WmDropFiles = 0x0233;
    private const uint DragQueryFileCount = 0xFFFFFFFF;
    private const int SOk = 0;
    private const int DropEffectCopy = 1;
    private const short CfHdrop = 15;

    public MainWindow() : this(null) { }

    internal MainWindow(SettingsStore? store)
    {
        _store = store!;
        InitializeComponent();
        ContentRendered += OnContentRendered;
        Closing += OnClosing;
        Activated += (_, _) => _ = RefreshSystemThemeAsync();
        SystemEvents.UserPreferenceChanged += OnSystemUserPreferenceChanged;
        if (_windowsColors != null) _windowsColors.ColorValuesChanged += OnSystemAccentChanged;
        SourceInitialized += OnSourceInitialized;
        SizeChanged += (_, _) => _ = PublishNativeTitleBarMetricsAsync();
        StateChanged += (_, _) => _ = PublishNativeTitleBarMetricsAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowTheme(_lastSystemTheme);
        ConfigureIntegratedTitleBar(_lastSystemTheme);
        Dispatcher.BeginInvoke(new Action(ResetNativeTitleBarDragRegion));
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;

        var startupState = await Task.Run(() =>
        {
            var store = _store ?? new SettingsStore();
            return (Store: store, Settings: store.Load());
        });
        _store = startupState.Store;

        _bridge = new WebMessageBridge(WebView);
        _controller = new AppController(
            this,
            _store,
            _bridge,
            async _ => await Dispatcher.InvokeAsync(() => LoadLocalPage("index.html")),
            theme => Dispatcher.Invoke(() => ApplyWindowTheme(theme)),
            visible => Dispatcher.Invoke(() => SetStartupSplashVisible(visible)));

        var settings = startupState.Settings;
        ApplyWindowTheme(settings.Theme);
        await InitializeWebViewCoreAsync(settings);

        WireDropSupport();
        InstallShellDropSupport();

        ApplyWindowTheme(settings.Theme);
        if (settings.IsConfigured)
        {
            _ = ConnectConfiguredAsync();
        }
        else
        {
            LoadLocalPage("setup.html");
        }
    }

    private void ApplyWindowTheme(string? theme)
    {
        WindowThemeService.Apply(this, theme);
        ApplyHostChromeColors(theme);
    }

    private void ApplyHostChromeColors(string? theme)
    {
        var dark = WindowThemeService.IsDarkTheme(theme);
        var background = new SolidColorBrush(dark ? Color.FromRgb(16, 16, 16) : Color.FromRgb(248, 248, 248));
        RootHost.Background = background;
        StartupSplash.Background = background;
        SplashOmega.Foreground = new SolidColorBrush(dark ? Color.FromRgb(166, 166, 166) : Color.FromRgb(87, 96, 106));
        WebView.DefaultBackgroundColor = dark
            ? System.Drawing.Color.FromArgb(16, 16, 16)
            : System.Drawing.Color.FromArgb(248, 248, 248);
        if (_nativeTitleBar != null)
        {
            ApplyNativeTitleBarColors(_nativeTitleBar, dark);
        }
    }

    private void ConfigureIntegratedTitleBar(string? theme)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            !AppWindowTitleBar.IsCustomizationSupported())
        {
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));
            var titleBar = appWindow.TitleBar;
            ConfigureNativeTitleBar(titleBar, WindowThemeService.IsDarkTheme(theme));

            _nativeTitleBar = titleBar;
            _usesIntegratedTitleBar = true;
        }
        catch (Exception ex)
        {
            _nativeTitleBar = null;
            _usesIntegratedTitleBar = false;
            AppLogger.Debug("Window.TitleBar", "Native title bar integration unavailable: " + ex.Message);
        }
    }

    private static void ConfigureNativeTitleBar(AppWindowTitleBar titleBar, bool dark)
    {
        titleBar.ExtendsContentIntoTitleBar = true;
        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

        ApplyNativeTitleBarColors(titleBar, dark);
    }

    private static void ApplyNativeTitleBarColors(AppWindowTitleBar titleBar, bool dark)
    {
        var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonForegroundColor = dark
            ? Windows.UI.Color.FromArgb(255, 242, 242, 242)
            : Windows.UI.Color.FromArgb(255, 32, 33, 36);
        titleBar.ButtonInactiveForegroundColor = dark
            ? Windows.UI.Color.FromArgb(255, 166, 166, 166)
            : Windows.UI.Color.FromArgb(255, 95, 99, 104);
    }

    private async Task PublishNativeTitleBarMetricsAsync(bool force = false)
    {
        if (!_usesIntegratedTitleBar || _nativeTitleBar == null || _bridge == null)
        {
            return;
        }

        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var leftInset = ToTitleBarDip(_nativeTitleBar.LeftInset, dpiScale);
        var rightInset = ToTitleBarDip(_nativeTitleBar.RightInset, dpiScale);
        var height = ToTitleBarDip(_nativeTitleBar.Height, dpiScale);
        if (!force &&
            leftInset == _lastTitleBarLeftInset &&
            rightInset == _lastTitleBarRightInset &&
            height == _lastTitleBarHeight)
        {
            return;
        }

        _lastTitleBarLeftInset = leftInset;
        _lastTitleBarRightInset = rightInset;
        _lastTitleBarHeight = height;
        await _bridge.CallAsync("_onNativeTitleBarMetrics", new
        {
            enabled = true,
            leftInset,
            rightInset,
            height
        });
    }

    private void ResetNativeTitleBarDragRegion()
    {
        if (!_usesIntegratedTitleBar || _nativeTitleBar == null)
        {
            return;
        }

        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = Math.Max(0, ToTitleBarPixels(ActualWidth, dpiScale) - _nativeTitleBar.RightInset);
        var height = Math.Max(0, _nativeTitleBar.Height);
        ApplyNativeTitleBarDragRectangles(width > 0 && height > 0
            ? [new RectInt32(0, 0, width, height)]
            : Array.Empty<RectInt32>());
    }

    private void ApplyNativeTitleBarDragRectangles(RectInt32[] rectangles)
    {
        if (!_usesIntegratedTitleBar || _nativeTitleBar == null)
        {
            return;
        }

        if (_lastNativeDragRectangles != null &&
            _lastNativeDragRectangles.SequenceEqual(rectangles))
        {
            return;
        }

        _nativeTitleBar.SetDragRectangles(rectangles);
        _lastNativeDragRectangles = rectangles;
    }

    private void SetNativeTitleBarDragRegion(JsonElement message)
    {
        if (!_usesIntegratedTitleBar || _nativeTitleBar == null ||
            !message.TryGetProperty("args", out var args) ||
            args.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var values = args.EnumerateArray().ToArray();
        if (values.Length == 0 || values.Length % 4 != 0 ||
            !values.All(value => value.TryGetDouble(out _)))
        {
            return;
        }

        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var maximumWidth = ToTitleBarPixels(ActualWidth, dpiScale);
        var maximumHeight = Math.Min(ToTitleBarPixels(ActualHeight, dpiScale), _nativeTitleBar.Height);
        var rectangles = new List<RectInt32>(values.Length / 4);
        for (var index = 0; index < values.Length; index += 4)
        {
            var x = Math.Clamp(ToTitleBarPixels(values[index].GetDouble(), dpiScale), 0, maximumWidth);
            var y = Math.Clamp(ToTitleBarPixels(values[index + 1].GetDouble(), dpiScale), 0, maximumHeight);
            var width = Math.Clamp(ToTitleBarPixels(values[index + 2].GetDouble(), dpiScale), 0, maximumWidth - x);
            var height = Math.Clamp(ToTitleBarPixels(values[index + 3].GetDouble(), dpiScale), 0, maximumHeight - y);
            if (width > 0 && height > 0)
            {
                rectangles.Add(new RectInt32(x, y, width, height));
            }
        }

        ApplyNativeTitleBarDragRectangles(rectangles.ToArray());
    }

    private static int ToTitleBarDip(int pixels, double dpiScale)
    {
        return Math.Max(0, (int)Math.Round(pixels / Math.Max(1, dpiScale), MidpointRounding.AwayFromZero));
    }

    private static int ToTitleBarPixels(double dips, double dpiScale)
    {
        return Math.Max(0, (int)Math.Round(dips * Math.Max(1, dpiScale), MidpointRounding.AwayFromZero));
    }

    private void SetStartupSplashVisible(bool visible)
    {
        StartupSplash.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        WebView.Visibility = visible ? Visibility.Hidden : Visibility.Visible;
    }

    private async Task InitializeWebViewCoreAsync(Models.AppSettings settings)
    {
        var options = new CoreWebView2EnvironmentOptions();
        if (settings.HardwareAcceleration == false)
        {
            options.AdditionalBrowserArguments = "--disable-gpu --disable-gpu-compositing";
        }

        var environment = await CoreWebView2Environment.CreateAsync(null, _store.WebViewDataDirectory.FullName, options);
        await WebView.EnsureCoreWebView2Async(environment);
        environment.ProcessInfosChanged += (_, _) => AppMemoryBudget.RegisterWebViewProcesses(
            environment.GetProcessInfos().Select(info => info.ProcessId));
        AppMemoryBudget.RegisterWebViewProcesses(environment.GetProcessInfos().Select(info => info.ProcessId));
        WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            LocalWebHost,
            ResolveWebRootDirectory().FullName,
            CoreWebView2HostResourceAccessKind.Allow);
        WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
        WebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        WebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        WebView.CoreWebView2.ProcessFailed += OnWebViewProcessFailed;
        WebView.CoreWebView2.ContainsFullScreenElementChanged += OnContainsFullScreenElementChanged;
        _previewAdapter = new WebView2PreviewAdapter(WebView.CoreWebView2, _controller!.PreviewResources);
        _previewAdapter.Attach();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownInProgress)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _shutdownInProgress = true;
        if (Application.Current is App app && app.KeepBackgroundOnClose) Hide();
        SystemEvents.UserPreferenceChanged -= OnSystemUserPreferenceChanged;
        if (_windowsColors != null) _windowsColors.ColorValuesChanged -= OnSystemAccentChanged;
        RemoveShellDropSupport();
        try
        {
            _previewAdapter?.Dispose();
            if (_controller != null)
            {
                await _controller.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.Shutdown", ex);
        }
        finally
        {
            WebView.Dispose();
            Closing -= OnClosing;
            Close();
            if (Application.Current is App desktopApp) desktopApp.WindowReleased(this);
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_bridge == null)
        {
            SetStartupSplashVisible(false);
            return;
        }

        try
        {
            RefreshShellDropTargets();
            await _bridge.SetSystemPreferencesAsync(WindowThemeService.InitialTheme());
            await _bridge.SetSettingsAsync(_store.Load());
            await PublishNativeTitleBarMetricsAsync(force: true);
            await _bridge.NotifyReadyAsync();
            if (_pendingSetupError != null)
            {
                var error = _pendingSetupError;
                _pendingSetupError = null;
                await _bridge.CallAsync("_onSetupError", error);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("WebView.NavigationCompleted", ex);
        }
        finally
        {
            await Task.Delay(50);
            SetStartupSplashVisible(false);
        }
    }
    private void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_shutdownInProgress) return;
        AppLogger.Error(
            "WebView.ProcessFailed",
            new InvalidOperationException("WebView2 process failed: " + e.ProcessFailedKind));
        _ = RecoverWebViewAsync();
    }

    private void OnContainsFullScreenElementChanged(object? sender, object e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        SetWebViewFullscreen(WebView.CoreWebView2?.ContainsFullScreenElement == true);
    }

    public void SetBridgeFullscreen(bool enabled)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetBridgeFullscreen(enabled));
            return;
        }

        SetWebViewFullscreen(enabled);
    }
    private void SetWebViewFullscreen(bool enabled)
    {
        if (enabled == _isWebViewFullscreen)
        {
            return;
        }

        if (enabled)
        {
            _savedWindowState = WindowState;
            _savedWindowStyle = WindowStyle;
            _savedResizeMode = ResizeMode;
            _savedTopmost = Topmost;

            _isWebViewFullscreen = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
            return;
        }

        _isWebViewFullscreen = false;
        Topmost = _savedTopmost;
        WindowStyle = _savedWindowStyle;
        ResizeMode = _savedResizeMode;
        WindowState = _savedWindowState;
    }

    private async Task RecoverWebViewAsync()
    {
        if (!Dispatcher.CheckAccess())
        {
            await await Dispatcher.InvokeAsync(RecoverWebViewAsync);
            return;
        }

        if (_recoveringWebView || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _recoveringWebView = true;
        SetStartupSplashVisible(true);
        try
        {
            var oldWebView = WebView;
            _previewAdapter?.Dispose();
            UnwireCoreWebView(oldWebView);
            UnwireWebViewDropSupport(oldWebView);
            UninstallWebViewShellDropSupport(oldWebView);
            RootHost.Children.Remove(oldWebView);
            if (oldWebView is IDisposable disposable)
            {
                disposable.Dispose();
            }

            WebView = new WebView2 { Visibility = Visibility.Hidden };
            RootHost.Children.Add(WebView);
            _bridge = new WebMessageBridge(WebView);
            _controller?.AttachBridge(_bridge);

            WireWebViewDropSupport(WebView);
            InstallWebViewShellDropSupport(WebView);

            var settings = _store.Load();
            ApplyWindowTheme(settings.Theme);
            await InitializeWebViewCoreAsync(settings);
            ApplyWindowTheme(settings.Theme);
            if (settings.IsConfigured)
            {
                if (_controller?.HasSession == true)
                {
                    LoadLocalPage("index.html");
                }
                else
                {
                    _ = ConnectConfiguredAsync();
                }
            }
            else
            {
                LoadLocalPage("setup.html");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("WebView.Recover", ex);
        }
        finally
        {
            _recoveringWebView = false;
        }
    }

    private void UnwireCoreWebView(WebView2 webView)
    {
        try
        {
            if (webView.CoreWebView2 == null)
            {
                return;
            }

            webView.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            webView.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
            webView.CoreWebView2.ProcessFailed -= OnWebViewProcessFailed;
            webView.CoreWebView2.ContainsFullScreenElementChanged -= OnContainsFullScreenElementChanged;
            SetWebViewFullscreen(false);
        }
        catch
        {
            // A crashed WebView2 can throw while exposing CoreWebView2.
        }
    }

    private void OnSystemUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => _ = RefreshSystemThemeAsync()));
    }

    private async Task RefreshSystemThemeAsync(bool force = false)
    {
        var current = WindowThemeService.InitialTheme();
        var accent = WindowsAccentService.Read(_windowsColors);
        if (!force && string.Equals(current, _lastSystemTheme, StringComparison.OrdinalIgnoreCase) &&
            accent == _lastSystemAccent)
        {
            return;
        }

        _lastSystemTheme = current;
        _lastSystemAccent = accent;
        if (_store == null)
        {
            ApplyWindowTheme(current);
            return;
        }

        var settings = _store.Load();
        if (string.Equals(settings.Theme, "system", StringComparison.OrdinalIgnoreCase))
        {
            ApplyWindowTheme("system");
        }

        if (_bridge != null)
        {
            await _bridge.SetSystemPreferencesAsync(current, accent);
            await _bridge.CallAsync("_onSystemThemeChanged", current);
            await _bridge.CallAsync("_onSystemAccentChanged", accent);
        }
    }

    private void OnSystemAccentChanged(Windows.UI.ViewManagement.UISettings sender, object args)
    {
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Dispatcher.BeginInvoke(new Action(() => _ = RefreshSystemThemeAsync()));
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_controller == null)
        {
            return;
        }

        if (TryHandleHostMessage(e.WebMessageAsJson))
        {
            return;
        }

        await _controller.HandleJsonMessageAsync(e.WebMessageAsJson);
    }

    private bool TryHandleHostMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("method", out var methodElement))
            {
                return false;
            }

            switch (methodElement.GetString())
            {
                case "retryStartupConnect":
                    RetryStartupConnect();
                    return true;
                case "openSetup":
                    OpenStartupSetup();
                    return true;
                case "setTitleBarDragRegion":
                    SetNativeTitleBarDragRegion(document.RootElement);
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private void RetryStartupConnect()
    {
        var settings = _store.Load();
        ApplyWindowTheme(settings.Theme);
        if (!settings.IsConfigured)
        {
            OpenStartupSetup();
            return;
        }

        SetStartupSplashVisible(true);
        _ = ConnectConfiguredAsync();
    }

    private void OpenStartupSetup()
    {
        _pendingSetupError = null;
        SetStartupSplashVisible(true);
        LoadLocalPage("setup.html");
    }
    private async Task ConnectConfiguredAsync()
    {
        if (_controller == null || _bridge == null)
        {
            return;
        }

        var settings = _store.Load();
        NetworkConfig.Current.Apply(settings.Proxy);
        var session = new DiscordSession();
        try
        {
            await _bridge.CallAsync("_onStartupStatus", "Conectando ao bot...");
            await session.ConnectAsync(settings);
            _controller.SetSession(session);
            await _bridge.CallAsync("_onStartupStatus", "Bot conectado. Abrindo Organessônium...");
            await Dispatcher.InvokeAsync(() => LoadLocalPage("index.html"));
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConnectConfigured", ex);
            await session.DisposeAsync();
            var message = ResolveConnectError(ex);
            if (IsAuthError(ex))
            {
                settings.BotToken = "";
                _store.Save(settings);
                _pendingSetupError = message;
                await Dispatcher.InvokeAsync(() => LoadLocalPage("setup.html"));
            }
            else
            {
                WebView.CoreWebView2.NavigateToString(NetworkErrorHtml(message, settings.Theme));
            }
        }
    }

    private void LoadLocalPage(string fileName)
    {
        var safeName = fileName.Replace('\\', '/').TrimStart('/');
        var localFile = Path.Combine(ResolveWebRootDirectory().FullName, safeName.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(localFile))
        {
            throw new FileNotFoundException("Arquivo web não encontrado.", localFile);
        }

        WebView.CoreWebView2.Navigate($"https://{LocalWebHost}/{safeName}");
    }

    private static DirectoryInfo ResolveWebRootDirectory()
    {
        return WebRootDirectory.Value;
    }

    private static DirectoryInfo ResolveWebRootDirectoryCore()
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "web")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "web")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "web")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "web")),
            Path.Combine(AppContext.BaseDirectory, "web")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(Path.Combine(path, "index.html")) && File.Exists(Path.Combine(path, "setup.html")))
            {
                return new DirectoryInfo(path);
            }
        }

        return ExtractEmbeddedWebRoot();
    }

    private static DirectoryInfo ExtractEmbeddedWebRoot()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Select(name => name.Replace('\\', '/'))
            .Where(name => name.StartsWith("web/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (resources.Count == 0)
        {
            throw new DirectoryNotFoundException("Arquivos web embutidos não encontrados.");
        }

        var target = Path.Combine(
            Path.GetTempPath(),
            "Organessonium",
            "web-" + assembly.ManifestModule.ModuleVersionId.ToString("N"));
        var targetDir = Directory.CreateDirectory(target);
        var marker = Path.Combine(targetDir.FullName, ".extract-complete");
        if (File.Exists(marker))
        {
            return targetDir;
        }

        foreach (var file in targetDir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { file.Delete(); } catch { }
        }

        foreach (var dir in targetDir.EnumerateDirectories("*", SearchOption.AllDirectories).OrderByDescending(dir => dir.FullName.Length))
        {
            try { dir.Delete(true); } catch { }
        }

        foreach (var resource in resources)
        {
            var relative = resource["web/".Length..];
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            var output = Path.Combine(targetDir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? targetDir.FullName);
            using var input = assembly.GetManifestResourceStream(resource);
            if (input == null)
            {
                continue;
            }

            using var outputStream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read);
            input.CopyTo(outputStream);
        }

        File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        return targetDir;
    }

    private void WireDropSupport()
    {
        AllowDrop = true;
        RootHost.AllowDrop = true;

        PreviewDragOver += OnHostPreviewDragOver;
        PreviewDragLeave += OnHostPreviewDragLeave;
        PreviewDrop += OnHostPreviewDrop;
        RootHost.PreviewDragOver += OnHostPreviewDragOver;
        RootHost.PreviewDragLeave += OnHostPreviewDragLeave;
        RootHost.PreviewDrop += OnHostPreviewDrop;

        WireWebViewDropSupport(WebView);
    }

    private void WireWebViewDropSupport(WebView2 webView)
    {
        webView.AllowDrop = true;

        try
        {
            webView.AllowExternalDrop = false;
        }
        catch
        {
            // Older WebView2 runtimes may not expose this path consistently.
        }

        webView.PreviewDragOver += OnHostPreviewDragOver;
        webView.PreviewDragLeave += OnHostPreviewDragLeave;
        webView.PreviewDrop += OnHostPreviewDrop;
    }

    private void UnwireWebViewDropSupport(WebView2 webView)
    {
        webView.PreviewDragOver -= OnHostPreviewDragOver;
        webView.PreviewDragLeave -= OnHostPreviewDragLeave;
        webView.PreviewDrop -= OnHostPreviewDrop;
    }

    private void RefreshShellDropTargets()
    {
        if (_windowSource is { Handle: var windowHandle } && windowHandle != IntPtr.Zero)
        {
            DragAcceptFiles(windowHandle, true);
            RegisterOleDropTarget(windowHandle);
            RegisterChildDropTargets(windowHandle);
        }

        if (WebView.Handle != IntPtr.Zero)
        {
            DragAcceptFiles(WebView.Handle, true);
            RegisterOleDropTarget(WebView.Handle);
            RegisterChildDropTargets(WebView.Handle);
        }
    }

    private void OnHostPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!HasDropPaths(e.Data))
        {
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        if (!_draggingFiles)
        {
            _draggingFiles = true;
            _ = _bridge?.CallAsync("_onDragOver");
        }
    }

    private void OnHostPreviewDragLeave(object sender, DragEventArgs e)
    {
        if (!_draggingFiles)
        {
            return;
        }

        _draggingFiles = false;
        _ = _bridge?.CallAsync("_onDragLeave");
    }

    private void OnHostPreviewDrop(object sender, DragEventArgs e)
    {
        if (_controller == null || !HasDropPaths(e.Data))
        {
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _draggingFiles = false;
        _ = _bridge?.CallAsync("_onDragLeave");

        var files = ExtractDropPaths(e.Data);
        if (files.Count > 0)
        {
            StartDroppedUpload(files);
        }
    }

    private void InstallShellDropSupport()
    {
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        if (_windowSource != null)
        {
            _windowSource.AddHook(OnShellDropMessage);
            DragAcceptFiles(_windowSource.Handle, true);
            RegisterOleDropTarget(_windowSource.Handle);
            RegisterChildDropTargets(_windowSource.Handle);
        }

        InstallWebViewShellDropSupport(WebView);
    }

    private void InstallWebViewShellDropSupport(WebView2 webView)
    {
        webView.MessageHook += OnShellDropMessage;
        if (webView.Handle != IntPtr.Zero)
        {
            DragAcceptFiles(webView.Handle, true);
            RegisterOleDropTarget(webView.Handle);
            RegisterChildDropTargets(webView.Handle);
        }
    }

    private void UninstallWebViewShellDropSupport(WebView2 webView)
    {
        webView.MessageHook -= OnShellDropMessage;
        if (webView.Handle != IntPtr.Zero)
        {
            DragAcceptFiles(webView.Handle, false);
            UnregisterOleDropTarget(webView.Handle);
        }
    }

    private void RemoveShellDropSupport()
    {
        if (_windowSource != null)
        {
            DragAcceptFiles(_windowSource.Handle, false);
            UnregisterOleDropTarget(_windowSource.Handle);
            _windowSource.RemoveHook(OnShellDropMessage);
            _windowSource = null;
        }

        UninstallWebViewShellDropSupport(WebView);
        foreach (var handle in _oleDropTargets.Keys.ToArray())
        {
            UnregisterOleDropTarget(handle);
        }
    }

    private IntPtr OnShellDropMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmDropFiles)
        {
            return IntPtr.Zero;
        }

        var paths = QueryDroppedFiles(wParam);
        DragFinish(wParam);
        handled = true;
        _draggingFiles = false;
        _ = _bridge?.CallAsync("_onDragLeave");
        StartDroppedUpload(paths);
        return IntPtr.Zero;
    }

    private void StartDroppedUpload(IReadOnlyList<string> paths)
    {
        if (_controller == null || paths.Count == 0)
        {
            return;
        }

        var signature = string.Join("|", paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        var now = Environment.TickCount64;
        if (signature == _lastDropSignature && now - _lastDropTick < 1200)
        {
            return;
        }

        _lastDropSignature = signature;
        _lastDropTick = now;
        _ = _controller.UploadDroppedPathsAsync(paths);
    }

    private void RegisterOleDropTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _oleDropTargets.ContainsKey(hwnd))
        {
            return;
        }

        try
        {
            var target = new NativeFileDropTarget(
                paths => Dispatcher.BeginInvoke(new Action(() => StartDroppedUpload(paths))),
                active => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _draggingFiles = active;
                    _ = _bridge?.CallAsync(active ? "_onDragOver" : "_onDragLeave");
                })));
            var hr = RegisterDragDrop(hwnd, target);
            if (hr != SOk)
            {
                RevokeDragDrop(hwnd);
                hr = RegisterDragDrop(hwnd, target);
            }

            if (hr == SOk)
            {
                _oleDropTargets[hwnd] = target;
            }
            else
            {
                AppLogger.Info("DragDrop.RegisterOle", "RegisterDragDrop failed 0x" + hr.ToString("X") + " for " + hwnd.ToString("X"));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("DragDrop.RegisterOle", ex, hwnd.ToString("X"));
        }
    }

    private void UnregisterOleDropTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !_oleDropTargets.Remove(hwnd))
        {
            return;
        }

        try
        {
            RevokeDragDrop(hwnd);
        }
        catch
        {
            // Best-effort cleanup for windows that may already be gone.
        }
    }

    private static bool HasDropPaths(IDataObject data)
    {
        return data.GetDataPresent(DataFormats.FileDrop) ||
               data.GetDataPresent("FileNameW") ||
               data.GetDataPresent("FileName");
    }

    private static List<string> ExtractDropPaths(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] files)
        {
            return files.Where(File.Exists).Concat(files.Where(Directory.Exists)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        foreach (var format in new[] { "FileNameW", "FileName" })
        {
            if (data.GetData(format) is string[] names)
            {
                return names.Where(path => File.Exists(path) || Directory.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            if (data.GetData(format) is string name && (File.Exists(name) || Directory.Exists(name)))
            {
                return [name];
            }
        }

        return [];
    }

    private static List<string> QueryDroppedFiles(IntPtr dropHandle)
    {
        var count = DragQueryFile(dropHandle, DragQueryFileCount, null, 0);
        var files = new List<string>();
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFile(dropHandle, i, null, 0);
            if (length == 0)
            {
                continue;
            }

            var buffer = new StringBuilder((int)length + 1);
            DragQueryFile(dropHandle, i, buffer, buffer.Capacity);
            var path = buffer.ToString();
            if (File.Exists(path) || Directory.Exists(path))
            {
                files.Add(path);
            }
        }

        return files;
    }

    private static List<string> ExtractOleDropPaths(ComTypes.IDataObject dataObject)
    {
        var format = new ComTypes.FORMATETC
        {
            cfFormat = CfHdrop,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL
        };

        try
        {
            dataObject.GetData(ref format, out var medium);
            try
            {
                return medium.unionmember != IntPtr.Zero ? QueryDroppedFiles(medium.unionmember) : [];
            }
            finally
            {
                ReleaseStgMedium(ref medium);
            }
        }
        catch
        {
            return [];
        }
    }

    private void RegisterChildDropTargets(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        EnumChildWindows(hwnd, (child, _) =>
        {
            DragAcceptFiles(child, true);
            RegisterOleDropTarget(child);
            return true;
        }, IntPtr.Zero);
    }

    private static bool IsAuthError(Exception ex)
    {
        return ConnectionErrorClassifier.Classify(ex).Kind == ConnectionErrorKind.InvalidToken;
    }

    private static string ResolveConnectError(Exception ex)
    {
        return ConnectionErrorClassifier.Classify(ex).Message;
    }

    private static string NetworkErrorHtml(string message, string? theme)
    {
        var dark = WindowThemeService.IsDarkTheme(theme);
        var background = dark ? "#101010" : "#F8F8F8";
        var card = dark ? "#171717" : "#FFFFFF";
        var border = dark ? "#2B2B2B" : "#D8DEE4";
        var text = dark ? "#E8EAED" : "#1F2328";
        var muted = dark ? "#B8B8B8" : "#57606A";
        var codeBackground = dark ? "#101010" : "#F6F8FA";
        var button = dark ? "#2F2F2F" : "#F6F8FA";
        var buttonHover = dark ? "#3A3A3A" : "#EAEFF4";
        var safe = message.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        return $$$"""
            <!DOCTYPE html><html><head><meta charset="UTF-8"><style>
            *{margin:0;padding:0;box-sizing:border-box;font-family:'Segoe UI',sans-serif}
            body{background:{{{background}}};color:{{{text}}};min-height:100vh;display:flex;align-items:center;justify-content:center;padding:32px}
            .box{width:min(560px,100%);background:{{{card}}};border:1px solid {{{border}}};border-radius:12px;padding:28px;box-shadow:0 24px 60px rgba(0,0,0,.28)}
            h1{font-size:22px;margin-bottom:10px}p{color:{{{muted}}};line-height:1.55;font-size:14px;margin-bottom:14px}
            code{display:block;background:{{{codeBackground}}};border:1px solid {{{border}}};color:{{{text}}};border-radius:8px;padding:12px;white-space:normal;font-size:12px;line-height:1.45}
            .actions{display:flex;gap:10px;flex-wrap:wrap;margin-top:18px}.actions button{border:1px solid {{{border}}};background:{{{button}}};color:{{{text}}};border-radius:8px;padding:10px 14px;font-size:13px;font-weight:600;cursor:pointer}.actions button:hover{background:{{{buttonHover}}}}.actions .primary{background:#1A73E8;border-color:#1A73E8;color:white}.actions .primary:hover{background:#1765CC}
            </style></head><body><main class="box"><h1>Não foi possível iniciar o bot</h1>
            <p>O app não conseguiu conectar ao Discord. Verifique sua conexão ou ajuste as configurações do bot.</p><code>{{{safe}}}</code>
            <div class="actions"><button class="primary" type="button" onclick="sendHost('retryStartupConnect')">Tentar novamente</button><button type="button" onclick="sendHost('openSetup')">Configurar conexão</button></div></main>
            <script>function sendHost(method){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage({method:method,args:[]});}</script></body></html>
            """;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [ComVisible(true)]
    private sealed class NativeFileDropTarget : IDropTarget
    {
        private readonly Action<IReadOnlyList<string>> _onDrop;
        private readonly Action<bool> _onDragState;

        public NativeFileDropTarget(Action<IReadOnlyList<string>> onDrop, Action<bool> onDragState)
        {
            _onDrop = onDrop;
            _onDragState = onDragState;
        }

        public int DragEnter(ComTypes.IDataObject dataObject, int keyState, NativePoint point, ref int effect)
        {
            effect = DropEffectCopy;
            _onDragState(true);
            return SOk;
        }

        public int DragOver(int keyState, NativePoint point, ref int effect)
        {
            effect = DropEffectCopy;
            return SOk;
        }

        public int DragLeave()
        {
            _onDragState(false);
            return SOk;
        }

        public int Drop(ComTypes.IDataObject dataObject, int keyState, NativePoint point, ref int effect)
        {
            effect = DropEffectCopy;
            _onDragState(false);
            var paths = ExtractOleDropPaths(dataObject);
            if (paths.Count > 0)
            {
                _onDrop(paths);
            }

            return SOk;
        }
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("00000122-0000-0000-C000-000000000046")]
    private interface IDropTarget
    {
        [PreserveSig]
        int DragEnter(
            [In] ComTypes.IDataObject dataObject,
            int keyState,
            NativePoint point,
            ref int effect);

        [PreserveSig]
        int DragOver(
            int keyState,
            NativePoint point,
            ref int effect);

        [PreserveSig]
        int DragLeave();

        [PreserveSig]
        int Drop(
            [In] ComTypes.IDataObject dataObject,
            int keyState,
            NativePoint point,
            ref int effect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, int cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget dropTarget);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
}


