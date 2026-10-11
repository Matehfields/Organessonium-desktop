using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed class WebMessageBridge
{
    private readonly WebView2 _webView;

    public WebMessageBridge(WebView2 webView)
    {
        _webView = webView;
    }

    public Task SetSettingsAsync(AppSettings settings)
    {
        var jsonOptions = new JsonSerializerOptions(JsonOptions.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());
        var json = JsonSerializer.Serialize(settings, jsonOptions);
        return ExecuteAsync($"window.__organessoniumSettings={json};");
    }

    public Task SetSystemPreferencesAsync(string theme, string? accentColor = null)
    {
        var value = JsonSerializer.Serialize(theme, JsonOptions.Web);
        var accent = JsonSerializer.Serialize(accentColor ?? WindowsAccentService.Read(), JsonOptions.Web);
        return ExecuteAsync($"window.__organessoniumSystemPreferences={{...(window.__organessoniumSystemPreferences||{{}}),theme:{value},accentColor:{accent}}};");
    }

    public Task NotifyReadyAsync()
    {
        return ExecuteAsync("if(typeof window.onBridgeReady==='function')window.onBridgeReady();");
    }

    public Task CallAsync(string functionName, params object?[] args)
    {
        var name = JsonSerializer.Serialize(functionName, JsonOptions.Web);
        var jsonArgs = JsonSerializer.Serialize(args, JsonOptions.Web);
        return ExecuteAsync($"var f=window[{name}];if(typeof f==='function')f(...{jsonArgs});");
    }

    public Task SetThumbAsync(string threadId, string dataUrl)
    {
        var tid = JsonSerializer.Serialize(threadId, JsonOptions.Web);
        var data = JsonSerializer.Serialize(dataUrl, JsonOptions.Web);
        return ExecuteAsync($"window._thumbs=window._thumbs||{{}};window._thumbs[{tid}]={data};if(window._onThumb)window._onThumb({tid});");
    }

    private async Task ExecuteAsync(string script)
    {
        if (!_webView.Dispatcher.CheckAccess())
        {
            await await _webView.Dispatcher.InvokeAsync(() => ExecuteAsync(script));
            return;
        }

        try
        {
            if (_webView.CoreWebView2 == null)
            {
                return;
            }

            await _webView.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            AppLogger.Error("WebView.ExecuteScript", ex);
        }
    }
}
