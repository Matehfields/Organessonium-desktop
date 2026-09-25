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
        if (area is "App.DispatcherUnhandledException" or "App.UnhandledException" or
            "App.UnobservedTaskException")
        {
            Write("ERROR", area, details, exception);
        }
    }

    public static void Info(string area, string details)
    {
        // Logs are reserved for unexpected errors.
    }

    public static void Debug(string area, string details)
    {
        // Debug events are not persisted.
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
            if (!string.IsNullOrWhiteSpace(details))
            {
                builder.AppendLine("Details: " + details);
            }

            if (exception != null)
            {
                builder.AppendLine("Exception:");
                builder.AppendLine(exception.ToString());
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
