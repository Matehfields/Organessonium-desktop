using System.Diagnostics;
using System.IO;
using System.Text;

namespace Organessonium.Services;

public static class AppLogger
{
    private static readonly object Lock = new();
    private static DirectoryInfo? LogDirectory;

    public static void Configure(DirectoryInfo dataDirectory)
    {
        LogDirectory = Directory.CreateDirectory(Path.Combine(dataDirectory.FullName, "Logs"));
    }

    public static void Error(string area, Exception exception, string? details = null)
    {
        if (Diagnostics.DevelopmentDiagnostics.Error(area, exception)) return;
        if (area is "App.DispatcherUnhandledException" or "App.UnhandledException" or
            "App.UnobservedTaskException" or "Video.Stream" or "Video.FastStart" or "Video.Progress")
        {
            Write("ERROR", area, details, exception);
        }
        else if (area is "PreviewUpload" or "VideoPreviewUpload")
        {
            // Do not persist codec stderr, file names, paths or decrypted metadata.
            Write("ERROR", area, null, new IOException($"Media failure: {exception.GetType().Name}; HResult=0x{exception.HResult:X8}"));
        }
    }

    public static void Info(string area, string details)
    {
        Diagnostics.DevelopmentDiagnostics.Signal("log.info", area);
        // Logs are reserved for unexpected errors.
    }

    public static void Debug(string area, string details)
    {
        Diagnostics.DevelopmentDiagnostics.Signal("log.debug", area);
        // Debug events are not persisted.
    }

    internal static void SyncEvent(string area, string summary, bool warning = false)
    {
        Diagnostics.DevelopmentDiagnostics.Signal(warning ? "log.warning" : "log.info", area);
        // Synchronization status is shown in the UI; only errors are persisted.
    }

    private static void Write(string level, string area, string? details, Exception? exception, string? subdirectory = null)
    {
        try
        {
            var dir = LogDirectory ?? Directory.CreateDirectory(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OrganessoniumData",
                    "Logs"));
            if (!string.IsNullOrWhiteSpace(subdirectory))
            {
                dir = Directory.CreateDirectory(Path.Combine(dir.FullName, subdirectory));
            }
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
            var path = Path.Combine(dir.FullName, stamp + ".log");
            var builder = new StringBuilder();
            builder.AppendLine("Organessônium");
            builder.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            builder.AppendLine("Level: " + level);
            builder.AppendLine("Area: " + area);
            if (exception != null)
            {
                builder.AppendLine("Exception:");
                var depth = 0;
                for (Exception? current = exception; current != null && depth++ < 4; current = current.InnerException)
                {
                    builder.AppendLine(current.GetType().FullName + $" (HResult=0x{current.HResult:X8})");
                    foreach (var frame in new StackTrace(current, false).GetFrames().Take(16))
                    {
                        var method = frame.GetMethod();
                        builder.AppendLine("  at " + method?.DeclaringType?.FullName + "." + method?.Name +
                            " (IL " + frame.GetILOffset() + ")");
                    }
                }
            }

            lock (Lock)
            {
                File.WriteAllText(path, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never interrupt the app.
        }
    }
}
