using System.IO;

namespace Organessonium.Services;

public static class VideoPreviewGenerator
{
    public static Task<RamMediaPreview> CreateThumbnailAsync(FileInfo source, CancellationToken token = default) =>
        VideoPreviewWorker.GenerateAsync(source, false, false, null, token);

    public static Task<RamMediaPreview> Create480pAsync(FileInfo source, bool hardwareAcceleration,
        Action<int>? onProgress = null, CancellationToken cancellationToken = default) =>
        VideoPreviewWorker.GenerateAsync(source, true, hardwareAcceleration, onProgress, cancellationToken);
}
