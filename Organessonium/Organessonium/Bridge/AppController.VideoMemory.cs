using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private readonly VideoPlaybackMemoryGuard _videoMemoryGuard;

    private async Task ReleaseVideoMemoryAsync()
    {
        var sessions = _videoSessions.ToArray();
        var requests = sessions.Select(pair => pair.Key[..pair.Key.LastIndexOf('|')]).Distinct().ToArray();
        var retired = new List<VideoStreamSession>();
        foreach (var pair in sessions)
        {
            if (_videoSessions.TryRemove(pair.Key, out var video)) { video.Dispose(); retired.Add(video); }
            if (_previewUrls.TryRemove(pair.Key, out var url)) _previewResources.RemoveUrl(url);
        }
        foreach (var request in requests) await _bridge.CallAsync("_onVideoMemoryPressure", request);
        foreach (var video in retired) await video.DisposeAsync();
        // Closed native responses can still hold a session object; its provider has released its pool.
        await Task.Run(UploadBufferMemoryReclaimer.Reclaim);
        foreach (var request in requests) await _bridge.CallAsync("_onVideoMemoryReleased", request);
    }
}
