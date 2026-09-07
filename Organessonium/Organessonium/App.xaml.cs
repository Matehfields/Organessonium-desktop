using System.IO;
using System.Windows;
using Organessonium.Services;

namespace Organessonium;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("App.DispatcherUnhandledException", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLogger.Error("App.UnhandledException", ex);
            }
            else
            {
                AppLogger.Info("App.UnhandledException", args.ExceptionObject?.ToString() ?? "Unknown exception object.");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("App.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        if (e.Args.Any(arg => arg.Equals("--debug-names", StringComparison.OrdinalIgnoreCase)))
        {
            RunNamesDebug();
            return;
        }

        base.OnStartup(e);
    }

    private void RunNamesDebug()
    {
        var store = new SettingsStore();
        var settings = store.Load();
        var reportPath = Path.Combine(store.DataDirectory.FullName, "names-scan-report.txt");
        try
        {
            if (string.IsNullOrWhiteSpace(settings.BotToken) ||
                string.IsNullOrWhiteSpace(settings.NamesChannelId))
            {
                var details = $"botToken={(settings.BotToken.Length > 0 ? "OK" : "VAZIO")}, namesChannelId={(settings.NamesChannelId.Length > 0 ? "OK" : "VAZIO")}";
                File.WriteAllText(reportPath, "FALHA: bot nao configurado. " + details, System.Text.Encoding.UTF8);
                Shutdown(2);
                return;
            }

            using var discord = new DiscordApiClient(settings.BotToken);
            var scanner = new DebugNamesScanner(discord, store, settings);
            var report = scanner.ScanAsync().GetAwaiter().GetResult();
            File.WriteAllText(reportPath, report, System.Text.Encoding.UTF8);
            Shutdown(0);
        }
        catch (Exception ex)
        {
            AppLogger.Error("DebugNames.Failed", ex);
            try
            {
                File.WriteAllText(reportPath, "FALHA: " + ex, System.Text.Encoding.UTF8);
            }
            catch
            {
            }

            Shutdown(1);
        }
    }
}
