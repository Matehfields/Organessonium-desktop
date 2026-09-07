using System.IO;
using Windows.Media.Editing;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Organessonium.Services;

public static class VideoPreviewGenerator
{
    public static async Task<FileInfo?> CreateThumbnailAsync(
        FileInfo source,
        DirectoryInfo destination,
        string? outputFileName = null,
        CancellationToken cancellationToken = default)
    {
        destination.Create();
        var output = new FileInfo(Path.Combine(destination.FullName, outputFileName ?? "preview_200x200.jpg"));
        var sourceFile = await StorageFile.GetFileFromPathAsync(source.FullName);

        try
        {
            var clip = await MediaClip.CreateFromFileAsync(sourceFile).AsTask(cancellationToken);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            var (thumbWidth, thumbHeight) = await ThumbnailSizeAsync(sourceFile);
            using var thumbnail = await composition.GetThumbnailAsync(
                TimeSpan.Zero,
                thumbWidth,
                thumbHeight,
                VideoFramePrecision.NearestFrame).AsTask(cancellationToken);
            if (await WriteThumbnailStreamAsync(thumbnail, output, cancellationToken))
            {
                return output;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("VideoThumbnail.MediaComposition", ex, source.FullName);
        }

        try
        {
            using var shellThumbnail = await sourceFile.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                512,
                ThumbnailOptions.ResizeThumbnail).AsTask(cancellationToken);
            if (await WriteThumbnailStreamAsync(shellThumbnail, output, cancellationToken))
            {
                return output;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("VideoThumbnail.ShellThumbnail", ex, source.FullName);
        }

        return null;
    }

    private static async Task<bool> WriteThumbnailStreamAsync(
        IRandomAccessStreamWithContentType thumbnail,
        FileInfo output,
        CancellationToken cancellationToken)
    {
        if (thumbnail.Size == 0)
        {
            return false;
        }

        if (thumbnail.Size > int.MaxValue)
        {
            return false;
        }

        var sourceBytes = new byte[(int)thumbnail.Size];
        using (var reader = new DataReader(thumbnail.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)thumbnail.Size).AsTask(cancellationToken);
            reader.ReadBytes(sourceBytes);
        }

        if (sourceBytes.Length == 0)
        {
            return false;
        }

        var square = ThumbnailCache.CreateSquareJpeg(sourceBytes);
        output.Directory?.Create();
        await File.WriteAllBytesAsync(output.FullName, square, cancellationToken);
        output.Refresh();
        return output.Exists && output.Length > 0;
    }

    public static async Task<FileInfo> Create480pAsync(
        FileInfo source,
        DirectoryInfo destination,
        bool hardwareAcceleration,
        Action<int>? onProgress = null,
        CancellationToken cancellationToken = default,
        string? outputFileName = null)
    {
        destination.Create();
        var output = new FileInfo(Path.Combine(destination.FullName, outputFileName ?? "preview_480p.mp4"));
        var sourceFile = await StorageFile.GetFileFromPathAsync(source.FullName);
        var targetFolder = await StorageFolder.GetFolderFromPathAsync(destination.FullName);
        var targetFile = await targetFolder.CreateFileAsync(output.Name, CreationCollisionOption.ReplaceExisting);

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Wvga);
        if (profile.Video != null)
        {
            var properties = await sourceFile.Properties.GetVideoPropertiesAsync();
            var sourceWidth = Math.Max(1u, properties.Width);
            var sourceHeight = Math.Max(1u, properties.Height);
            var scale = Math.Min(1.0, Math.Min(854.0 / sourceWidth, 480.0 / sourceHeight));
            profile.Video.Width = EvenDimension(sourceWidth * scale);
            profile.Video.Height = EvenDimension(sourceHeight * scale);
            profile.Video.Bitrate = 1_000_000;
        }

        if (profile.Audio != null)
        {
            profile.Audio.Bitrate = 96_000;
        }

        var transcoder = new MediaTranscoder
        {
            AlwaysReencode = true,
            HardwareAccelerationEnabled = hardwareAcceleration
        };
        var prepared = await transcoder.PrepareFileTranscodeAsync(sourceFile, targetFile, profile);
        if (!prepared.CanTranscode)
        {
            throw new InvalidOperationException("O Windows não conseguiu gerar a prévia 480p: " + prepared.FailureReason);
        }

        var progress = new Progress<double>(value => onProgress?.Invoke(Math.Clamp((int)Math.Round(value), 0, 100)));
        await prepared.TranscodeAsync().AsTask(cancellationToken, progress);
        output.Refresh();
        if (!output.Exists || output.Length == 0)
        {
            throw new InvalidOperationException("A prévia 480p foi gerada vazia.");
        }

        onProgress?.Invoke(100);
        return output;
    }

    private static uint EvenDimension(double value)
    {
        var rounded = Math.Max(2u, (uint)Math.Round(value));
        return rounded % 2 == 0 ? rounded : rounded - 1;
    }

    private static async Task<(int Width, int Height)> ThumbnailSizeAsync(StorageFile sourceFile)
    {
        try
        {
            var properties = await sourceFile.Properties.GetVideoPropertiesAsync();
            var width = Math.Max(1u, properties.Width);
            var height = Math.Max(1u, properties.Height);
            var scale = Math.Min(1.0, 1024.0 / Math.Max(width, height));
            return (
                Math.Max(2, (int)Math.Round(width * scale)),
                Math.Max(2, (int)Math.Round(height * scale)));
        }
        catch
        {
            return (512, 288);
        }
    }
}
