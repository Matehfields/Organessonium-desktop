using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> LoggingPersistsOnlyErrorsAsync()
    {
        var temporary = Directory.CreateTempSubdirectory("organessonium-error-logging-");
        try
        {
            AppLogger.Configure(temporary);
            AppLogger.Info("App.Status", "information");
            AppLogger.Debug("Upload.Progress", "debug");
            AppLogger.SyncEvent("Sync.Completed", "items=1");
            AppLogger.SyncEvent("Sync.Deferred", "waiting", warning: true);
            var logs = Path.Combine(temporary.FullName, "Logs");
            if (Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories).Length != 0)
                return Task.FromResult(TestOutcome.Fail("INFO/DEBUG/WARN criou arquivo de log"));
            const string privateValue = "private-log-fixture-21428";
            AppLogger.Error("App.DispatcherUnhandledException", new InvalidOperationException(privateValue, new IOException(privateValue)), privateValue);
            var files = Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories);
            var content = files.Length == 1 ? File.ReadAllText(files[0]) : "";
            var valid = files.Length == 1 && content.Contains("Level: ERROR") &&
                content.Contains("System.InvalidOperationException") && content.Contains("System.IO.IOException") &&
                !content.Contains(privateValue, StringComparison.Ordinal);
            AppLogger.Error("VideoPreviewUpload", new IOException("sensitive-filename-and-metadata"));
            var mediaLogs = Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
            valid &= mediaLogs.Any(text => text.Contains("Area: VideoPreviewUpload") && text.Contains("IOException")) &&
                !mediaLogs.Any(text => text.Contains("sensitive-filename-and-metadata"));
            return Task.FromResult(valid ? TestOutcome.Pass() : TestOutcome.Fail("erro não foi preservado no log"));
        }
        finally { temporary.Delete(true); }
    }
}
