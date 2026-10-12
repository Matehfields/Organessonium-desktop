using System.IO;

namespace Organessonium.Services;

public static class VideoPreviewGenerator
{
    public static async Task<RamMediaPreview> CreateThumbnailAsync(FileInfo source, CancellationToken token = default)
    {
        if (ThumbnailCache.IsVideo(source.Name) && FfmpegMediaService.Available)
            return await FfmpegMediaService.ThumbnailAsync(source, token).ConfigureAwait(false);
        try { return await VideoPreviewWorker.GenerateAsync(source, false, false, null, token).ConfigureAwait(false); }
        catch (IOException) when (FfmpegMediaService.Available && !token.IsCancellationRequested)
        {
            return await FfmpegMediaService.ThumbnailAsync(source, token).ConfigureAwait(false);
        }
    }

    public static Task<RamMediaPreview> Create480pAsync(FileInfo source, bool hardwareAcceleration,
        Action<int>? onProgress = null, CancellationToken cancellationToken = default) =>
        VideoPreviewWorker.GenerateAsync(source, true, hardwareAcceleration, onProgress, cancellationToken);
}
