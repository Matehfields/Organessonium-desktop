using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace Organessonium.Services.Diagnostics;

internal sealed class DiagnosticWebView : IDisposable
{
    private readonly CoreWebView2 _view;
    private readonly DiagnosticSession _session;
    private readonly long _id;
    private readonly string _scriptId;
    private DiagnosticWebView(CoreWebView2 view, DiagnosticSession session, string scriptId)
    {
        _view = view; _session = session; _scriptId = scriptId;
        _id = session.AddBrowser();
        view.WebMessageReceived += OnMessage;
        view.ProcessFailed += OnFailure;
        view.NavigationStarting += OnStarting;
        view.NavigationCompleted += OnCompleted;
    }

    internal static async Task<DiagnosticWebView?> AttachAsync(CoreWebView2 view)
    {
        if (DevelopmentDiagnostics.Current is not { Healthy: true } session) return null;
        try
        {
            var script = $$"""
                (()=>{
                    if(window.__organessoniumDiagnosticObserver)return;
                    window.__organessoniumDiagnosticObserver=true;
                    const emit=(kind,type='',line=0,column=0)=>{
                        if(location.hostname!=='organessonium.local')return;
                        chrome.webview.postMessage({method:'developmentDiagnostic',kind,type,line,column,visible:!document.hidden});
                    };
                    addEventListener('error',e=>emit('javascript.error',e.error?.name||'Error',e.lineno||0,e.colno||0));
                    addEventListener('unhandledrejection',e=>emit('javascript.rejection',e.reason?.name||'Error'));
                    addEventListener('visibilitychange',()=>emit('heartbeat'));
                    setInterval(()=>emit('heartbeat'),{{session.Options.BrowserHeartbeatMilliseconds}});
                    emit('heartbeat');
                })();
                """;
            var id = await view.AddScriptToExecuteOnDocumentCreatedAsync(script);
            return new DiagnosticWebView(view, session, id);
        }
        catch (Exception exception) { session.Exception("observer.attachFailed", "webview", exception); return null; }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (args.Source.Split('?')[0] is not ("https://organessonium.local/index.html" or
                "https://organessonium.local/setup.html")) return;
            var text = args.WebMessageAsJson;
            if (text.Length > 512) return;
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (!root.TryGetProperty("method", out var method) || method.GetString() != "developmentDiagnostic") return;
            var kind = root.GetProperty("kind").GetString();
            if (kind == "heartbeat")
            { _session.BrowserHeartbeat(_id, root.GetProperty("visible").GetBoolean()); return; }
            if (kind is not ("javascript.error" or "javascript.rejection")) return;
            var type = root.GetProperty("type").GetString();
            type = type is "Error" or "TypeError" or "RangeError" or "ReferenceError" or "SyntaxError" or "URIError"
                ? type : "Error";
            _session.Record(kind, "webview", new { id = _id, type,
                line = root.GetProperty("line").GetInt32(), column = root.GetProperty("column").GetInt32() });
        }
        catch { } // Malformed observations cannot interfere with bridge dispatch.
    }

    private void OnFailure(object? sender, CoreWebView2ProcessFailedEventArgs args)
    {
        try { _session.Record("webview.processFailed", "webview", new { id = _id, kind = args.ProcessFailedKind.ToString(),
            reason = args.Reason.ToString(), exitCode = args.ExitCode }); }
        catch { }
    }
    private void OnStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    { _session.BrowserNavigation(_id, started: true); _session.Record("webview.navigationStarting", "webview", new { id = _id }); }
    private void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        try
        {
            if (!args.IsSuccess) _session.BrowserNavigation(_id, started: false);
            _session.Record("webview.navigationCompleted", "webview", new { id = _id, success = args.IsSuccess,
                status = args.WebErrorStatus.ToString() });
        }
        catch { }
    }

    public void Dispose()
    {
        _session.RemoveBrowser(_id);
        try
        {
            _view.WebMessageReceived -= OnMessage;
            _view.ProcessFailed -= OnFailure;
            _view.NavigationStarting -= OnStarting;
            _view.NavigationCompleted -= OnCompleted;
            _view.RemoveScriptToExecuteOnDocumentCreated(_scriptId);
        }
        catch { }
    }
}
