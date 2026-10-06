using System.IO;
using System.Windows;
using Organessonium.Services;

namespace Organessonium;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
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

        if (e.Args.Any(arg => arg.Equals("--audit-metadata", StringComparison.OrdinalIgnoreCase)))
        {
            RunMetadataAudit();
            return;
        }

        if (e.Args.Any(arg => arg.Equals("--repair-serials", StringComparison.OrdinalIgnoreCase)))
        {
            RunSerialRepair();
            return;
        }

        base.OnStartup(e);
        try { await StartDesktopAsync(_startBackground || e.Args.Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase)), _desktopStore, _initialIdentityPath); }
        catch (Exception ex)
        {
            _desktopStarted.TrySetException(ex);
            AppLogger.Error("App.UnhandledException", ex);
            if (!_startBackground && !e.Args.Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase)))
                MessageBox.Show("Não foi possível iniciar o Organessônium. Consulte os logs de erro.", "Organessônium");
            await ExitDesktopAsync();
        }
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

    private void RunMetadataAudit()
    {
        var store = new SettingsStore();
        var reportPath = Path.Combine(store.DataDirectory.FullName, "metadata-server-audit.txt");
        try
        {
            var settings = store.Load();
            if (string.IsNullOrWhiteSpace(settings.BotToken) ||
                string.IsNullOrWhiteSpace(settings.NamesChannelId) ||
                string.IsNullOrWhiteSpace(settings.FoldersForumId) ||
                string.IsNullOrWhiteSpace(settings.SerialChannelId))
            {
                File.WriteAllText(reportPath, "Audit unavailable: Discord channels are not configured.\n");
                Shutdown(2);
                return;
            }

            using var discord = new DiscordApiClient(settings.BotToken);
            var audit = new ServerMetadataAudit(discord, settings, Services.MetadataEncryption.MetadataCodec.ForStore(store));
            Task.Run(() => audit.WriteReportAsync(reportPath)).GetAwaiter().GetResult();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, "Audit failed: " + ex.GetType().Name + "\n");
            Shutdown(1);
        }
    }

    private void RunSerialRepair()
    {
        var store = new SettingsStore();
        var reportPath = Path.Combine(store.DataDirectory.FullName, "metadata-server-audit.txt");
        try
        {
            var settings = store.Load();
            using var discord = new DiscordApiClient(settings.BotToken);
            Task.Run(async () =>
            {
                var audit = new ServerMetadataAudit(discord, settings, Services.MetadataEncryption.MetadataCodec.ForStore(store));
                var before = await audit.WriteReportAsync(reportPath);
                if (before.UnreadableCount > 0 || before.MissingSerialReferences > 0)
                {
                    throw new InvalidOperationException("A auditoria encontrou metadados ou referências ausentes; os seriais não foram modificados.");
                }

                var registry = new AppRegistry(discord, store, settings);
                await registry.RefreshServerIndexAsync(rebuildSerials: true);
                var after = await audit.WriteReportAsync(reportPath);
                if (after.UnreadableCount > 0 || after.MissingSerialReferences > 0 || after.InvalidSerials > 0)
                {
                    throw new InvalidOperationException("A validação posterior encontrou problemas nos seriais; consulte o relatório local.");
                }
            }).GetAwaiter().GetResult();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            File.AppendAllText(reportPath, "\nRepair status: " + ex.GetType().Name + "\n");
            Shutdown(1);
        }
    }
}
