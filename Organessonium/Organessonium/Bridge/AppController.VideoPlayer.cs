using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private Task SendVideoQualityReadyAsync(string id, string name, string quality, string url) =>
        _bridge.CallAsync("_onVideoQualityReady", id, quality, url, quality == "480p" ? "video/mp4" : MediaMime(name),
            PreviewResourceRegistry.CachedVideoUrl(url), _previewResources.GetVideo(url)?.Length ?? 0);

    private Task SendVideoPlayerStatsAsync(string id, string quality)
    {
        if (quality is not ("full" or "480p") || !_videoSessions.TryGetValue(id + "|" + quality, out var session))
            return Task.CompletedTask;
        return _bridge.CallAsync("_onVideoPlayerStats", id, quality, session.Snapshot());
    }

    private void SaveVideoPreferences(string value)
    {
        if (value.Length > 1024) return;
        try
        {
            if (JsonSerializer.Deserialize<VideoPlayerPreferences>(value, JsonOptions.Web) is { } preferences)
                _store.SaveVideoPlayerPreferences(preferences);
        }
        catch (JsonException) { }
    }

    private Task PrepareVideoFrameDownloadAsync(string id, string url)
    {
        if (!_videoOpenRequests.ContainsKey(id) || _owner is not MainWindow window ||
            !window.AuthorizeVideoFrameDownload(url))
            return Task.CompletedTask;
        return _bridge.CallAsync("_onVideoFrameDownloadReady", id, url);
    }
}
