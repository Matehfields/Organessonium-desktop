using Microsoft.Web.WebView2.Core;
using Organessonium.Services;

namespace Organessonium.Bridge;

/// <summary>Only the final adapter knows WebView2. The virtual host is intercepted, never resolved over TCP.</summary>
public sealed class WebView2PreviewAdapter(CoreWebView2 core, PreviewResourceRegistry resources) : IDisposable
{
    private bool _disposed;
    public void Attach()
    {
        core.AddWebResourceRequestedFilter(PreviewResourceRegistry.UrlFilter, CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnRequested;
    }
    private async void OnRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var url = args.Request.Uri;
            var method = args.Request.Method;
            var range = args.Request.Headers.Contains("Range") ? args.Request.Headers.GetHeader("Range") : null;
            var response = await Task.Run(() => resources.OpenAsync(url, method, range));
            if (_disposed) { response.Content?.Dispose(); return; }
            args.Response = core.Environment.CreateWebResourceResponse(response.Content, response.Status, response.Reason, response.Headers);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) args.Response = core.Environment.CreateWebResourceResponse(null, 410, "Gone", "Content-Length: 0\r\n");
        }
        catch (Exception)
        {
            if (_disposed) return;
            AppLogger.Error("Video.Stream", new System.IO.IOException("Não foi possível fornecer a faixa de bytes solicitada."));
            args.Response = core.Environment.CreateWebResourceResponse(null, 503, "Service Unavailable", "Content-Length: 0\r\nCache-Control: no-store\r\n");
        }
        finally
        {
            try { deferral.Dispose(); }
            catch (Exception) when (_disposed) { /* The runtime has already revoked the pending request. */ }
        }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        core.WebResourceRequested -= OnRequested;
        core.RemoveWebResourceRequestedFilter(PreviewResourceRegistry.UrlFilter, CoreWebView2WebResourceContext.All);
    }
}
