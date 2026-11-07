using System.Windows;
using Organessonium.Services;
using Organessonium.Services.Sync;

namespace Organessonium;

public partial class App
{
    private SingleInstanceHost? _instance;
    private TrayManager? _tray;
    private MainWindow? _mainWindow;
    private bool _exiting;
    private SettingsStore? _desktopStore;
    private string? _initialIdentityPath;
    private bool _startBackground;
    private readonly TaskCompletionSource _desktopStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task DesktopStarted => _desktopStarted.Task;

    public App() { }
    internal App(SettingsStore store, string identityPath, bool background)
    {
        _desktopStore = store;
        _initialIdentityPath = identityPath;
        _startBackground = background;
    }
    internal SyncBackgroundHost? SyncHost { get; private set; }
    internal bool Exiting => _exiting;
    private Task<SettingsStore>? _desktopStoreInitialization;
    internal Task<SettingsStore> GetDesktopStoreAsync() => _desktopStore != null
        ? Task.FromResult(_desktopStore)
        : _desktopStoreInitialization ??= Task.Run(() => new SettingsStore());

    internal async Task StartDesktopAsync(bool background, SettingsStore? store = null, string? identityPath = null)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _desktopStore = store;
        _instance = new SingleInstanceHost(identityPath);
        if (!_instance.IsPrimary)
        {
            try { await _instance.NotifyPrimaryAsync(!background); }
            finally { await _instance.DisposeAsync(); _instance = null; Shutdown(); }
            return;
        }
        _instance.OpenRequested += () => Dispatcher.BeginInvoke(OpenMainWindow);
        _instance.Listen();
        if (!background) OpenMainWindow();
        _desktopStore ??= await GetDesktopStoreAsync();
        SyncHost = await Task.Run(() => new SyncBackgroundHost(_desktopStore, identityPath));
        SyncHost.Changed += UpdateTray;
        SyncHost.Notification += message => Dispatcher.BeginInvoke(() =>
        { if (_mainWindow == null || !_mainWindow.IsVisible) _tray?.Notify(message); });
        await SyncHost.StartAsync();
        if (background) EnsureTray();
        _mainWindow?.AttachSyncHost(SyncHost);
        AppLogger.SyncEvent("Sync.Startup", background ? "background; no window" : "foreground");
        _desktopStarted.TrySetResult();
    }

    internal void OpenMainWindow()
    {
        if (_exiting) return;
        if (_mainWindow != null)
        {
            _tray?.Dispose();
            _tray = null;
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            return;
        }
        var window = new MainWindow(_desktopStore);
        _mainWindow = window;
        MainWindow = window;
        window.Closed += (_, _) => WindowReleased(window);
        window.Show();
        AppLogger.SyncEvent("Sync.Window", "opened");
        _tray?.Dispose();
        _tray = null;
    }

    internal bool KeepBackgroundOnClose => !_exiting && (_desktopStore?.Load().CloseToTray ?? false);

    internal void WindowHidden(MainWindow window)
    {
        if (ReferenceEquals(_mainWindow, window)) EnsureTray();
    }

    internal void WindowReleased(MainWindow window)
    {
        if (!ReferenceEquals(_mainWindow, window)) return;
        _mainWindow = null;
        if (KeepBackgroundOnClose) EnsureTray();
        else _ = ExitDesktopAsync();
        AppLogger.SyncEvent("Sync.Window", "closed; WebView disposed");
    }

    private void EnsureTray()
    {
        _tray ??= new TrayManager(Dispatcher, OpenMainWindow, () => SyncHost?.RequestAll(true),
            paused => SyncHost?.Pause(paused), () => _ = ExitDesktopAsync());
        UpdateTray();
    }

    private void UpdateTray()
    {
        var statuses = SyncHost?.Engine?.Statuses ?? [];
        var label = statuses.Any(s => s.State == "attention") ? "Decisões pendentes" :
            statuses.Any(s => s.State is "scanning" or "syncing") ? "Sincronizando..." :
            statuses.Any(s => s.State == "offline") ? "Aguardando conexão" : "Tudo sincronizado";
        _tray?.Update(SyncHost?.Engine?.Paused ?? false, label);
    }

    internal async Task ExitDesktopAsync()
    {
        if (_exiting) return;
        if (_mainWindow != null && !await _mainWindow.ConfirmExitDesktopAsync()) return;
        if (_exiting) return;
        _exiting = true;
        AppLogger.SyncEvent("Sync.Exit", "cancelling workers");
        _tray?.Dispose();
        _tray = null;
        SyncHost?.Engine?.Pause(true);
        if (_mainWindow != null) _mainWindow.Close();
        if (SyncHost != null) await SyncHost.DisposeAsync();
        SyncHost = null;
        if (_instance != null) await _instance.DisposeAsync();
        _instance = null;
        // MainWindow completes asynchronous disposal before the application exits.
        if (_mainWindow != null)
        {
            var closed = new TaskCompletionSource();
            _mainWindow.Closed += (_, _) => closed.TrySetResult();
            if (_mainWindow.IsLoaded) await closed.Task;
        }
        Shutdown();
    }
}
