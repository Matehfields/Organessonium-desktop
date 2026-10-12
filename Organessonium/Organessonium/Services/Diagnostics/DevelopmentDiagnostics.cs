using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;

namespace Organessonium.Services.Diagnostics;

// Development-only observer. No business service depends on a diagnostic result.
internal static class DevelopmentDiagnostics
{
    internal const string DisableVariable = "ORGANESSONIUM_DIAGNOSTICS";
    internal static DiagnosticSession? Current { get; private set; }
    private static readonly AsyncLocal<DiagnosticOperation?> Operation = new();
    private static readonly HashSet<string> HttpSegments = new(StringComparer.Ordinal)
        { "api", "v10", "channels", "messages", "threads", "guilds", "users", "@me", "attachments", "active", "archived", "public" };
    internal static long OperationId => Operation.Value?.Id ?? 0;

    internal static DiagnosticSession? Start(string[] args, DirectoryInfo? directory = null, DiagnosticOptions? options = null)
    {
        if (Current != null || args.FirstOrDefault() == VideoPreviewWorker.Argument ||
            Environment.GetEnvironmentVariable(DisableVariable) == "0") return null;
        try
        {
            directory ??= new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrganessoniumData", "Logs", "Diagnostics"));
            var session = new DiagnosticSession(directory, options ?? new DiagnosticOptions());
            Current = session;
            session.Attach();
            return session;
        }
        catch { return null; } // Diagnostics cannot prevent startup.
    }

    internal static void Release(DiagnosticSession session)
    {
        if (ReferenceEquals(Current, session)) Current = null;
    }

    internal static bool Error(string area, Exception exception)
    {
        var session = Current;
        if (session?.Healthy != true) return false;
        return session.Exception("log.error", area, exception);
    }

    internal static void Signal(string kind, string area) => Current?.Signal(kind, area);
    internal static void AttachDispatcher(Dispatcher dispatcher) => Current?.AttachDispatcher(dispatcher);
    internal static DiagnosticOperation? Begin(string kind, string? label = null)
    {
        if (Current is not { Healthy: true } session) return null;
        var previous = Operation.Value;
        var resolved = kind == "bridge" ? DiagnosticLabels.Bridge(label) : label ?? previous?.Label ?? kind;
        var operation = session.Begin(kind, resolved, previous,
            value => Operation.Value = value);
        Operation.Value = operation;
        return operation;
    }

    internal static DiagnosticOperation? Http(HttpRequestMessage request)
    {
        if (Current?.Healthy != true) return null;
        // Neither major Discord IDs, query strings, CDN signatures nor headers enter a log.
        var path = request.RequestUri?.IsAbsoluteUri == true
            ? request.RequestUri.AbsolutePath : request.RequestUri?.OriginalString.Split('?')[0] ?? "";
        var route = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment == "@me" ? "me" : HttpSegments.Contains(segment) ? segment : "id"));
        return Begin("http", request.Method.Method + "." + route.Replace('/', '.'));
    }

    internal static object Status() => new
    {
        enabled = Current?.Healthy == true,
        sessionId = Current?.Id ?? "",
        directory = Current?.Directory.FullName ?? ""
    };
}

internal sealed record DiagnosticOptions
{
    internal int QueueCapacity { get; init; } = 512;
    internal long FileBytes { get; init; } = 4 * 1024 * 1024;
    internal int FilesPerSession { get; init; } = 4;
    internal int RetainedSessions { get; init; } = 4;
    internal int SampleMilliseconds { get; init; } = 2000;
    internal int UiStallMilliseconds { get; init; } = 5000;
    internal int BrowserStallMilliseconds { get; init; } = 12000;
    internal int BrowserHeartbeatMilliseconds { get; init; } = 2000;
}

internal sealed class DiagnosticOperation : IDisposable
{
    private readonly DiagnosticSession _session;
    private readonly DiagnosticOperation? _parent;
    private readonly Action<DiagnosticOperation?> _restore;
    private int _disposed;
    private int? _statusCode;
    internal long Id { get; }
    internal string Label { get; }
    internal long Started { get; } = Stopwatch.GetTimestamp();

    internal DiagnosticOperation(DiagnosticSession session, long id, string label,
        DiagnosticOperation? parent, Action<DiagnosticOperation?> restore)
    { _session = session; Id = id; Label = label; _parent = parent; _restore = restore; }

    internal void HttpStatus(int code) => _statusCode = code;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _session.End(this, _statusCode);
        _restore(_parent);
    }
}
