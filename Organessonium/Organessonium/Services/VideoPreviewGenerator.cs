using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Organessonium.Services;

public static class VideoPreviewGenerator
{
    private static readonly SemaphoreSlim ImageDecoders = new(2, 2);

    public static async Task<RamMediaPreview> CreateThumbnailAsync(FileInfo source, CancellationToken token = default)
    {
        if (ThumbnailCache.IsImage(source.Name))
            try { return await CreateImageThumbnailAsync(source, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not TransferMemoryPressureException &&
                error is IOException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException) { }
        if (FfmpegMediaService.Available)
            try { return await FfmpegMediaService.ThumbnailAsync(source, token).ConfigureAwait(false); }
            catch (IOException error) when (error is not TransferMemoryPressureException && !token.IsCancellationRequested) { }
        return await VideoPreviewWorker.GenerateAsync(source, false, false, null, token).ConfigureAwait(false);
    }

    private static async Task<RamMediaPreview> CreateImageThumbnailAsync(FileInfo source, CancellationToken token)
    {
        await ImageDecoders.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var budget = await RamUploadBudget.ReserveExternalBytesAsync(64L * 1024 * 1024,
                VideoPreviewWorker.MaximumThumbnailBytes, token, priority: true).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var extension = source.Extension.ToLowerInvariant();
                if (extension is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff"))
                    throw new NotSupportedException("Esse formato usa o fallback de mídia.");
                using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
                var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                var frame = decoder.Frames[0];
                var orientation = ReadImageOrientation(frame);
                var pixels = checked((long)frame.PixelWidth * frame.PixelHeight);
                if (pixels > (extension is ".jpg" or ".jpeg" ? 64_000_000 : 8_000_000) || input.Length > 32L * 1024 * 1024)
                    throw new NotSupportedException("A imagem exige o decoder isolado.");
                var scale = Math.Min(1d, 512d / Math.Max(frame.PixelWidth, frame.PixelHeight));
                input.Position = 0;
                var image = new BitmapImage(); image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = Math.Max(1, (int)Math.Round(frame.PixelWidth * scale));
                image.DecodePixelHeight = Math.Max(1, (int)Math.Round(frame.PixelHeight * scale));
                image.StreamSource = input; image.EndInit(); image.Freeze();
                token.ThrowIfCancellationRequested();
                var transform = orientation switch
                {
                    2 => new Matrix(-1, 0, 0, 1, 0, 0), 3 => new Matrix(-1, 0, 0, -1, 0, 0),
                    4 => new Matrix(1, 0, 0, -1, 0, 0), 5 => new Matrix(0, 1, 1, 0, 0, 0),
                    6 => new Matrix(0, 1, -1, 0, 0, 0), 7 => new Matrix(0, -1, -1, 0, 0, 0),
                    8 => new Matrix(0, -1, 1, 0, 0, 0), _ => Matrix.Identity
                };
                BitmapSource oriented = image;
                if (!transform.IsIdentity)
                { oriented = new TransformedBitmap(image, new MatrixTransform(transform)); oriented.Freeze(); }
                return new RamMediaPreview(ThumbnailCache.CreateSquarePng(oriented));
            }, token).ConfigureAwait(false);
        }
        finally { ImageDecoders.Release(); }
    }

    private static int ReadImageOrientation(BitmapFrame frame)
    {
        if (frame.Metadata is not BitmapMetadata metadata) return 1;
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            try
            {
                var value = metadata.GetQuery(query);
                if (value != null && Convert.ToInt32(value) is >= 1 and <= 8) return Convert.ToInt32(value);
            }
            catch (Exception error) when (error is NotSupportedException or IOException or System.Runtime.InteropServices.COMException) { }
        return 1;
    }

    public static Task<RamMediaPreview> Create480pAsync(FileInfo source, bool hardwareAcceleration,
        Action<int>? onProgress = null, CancellationToken cancellationToken = default) =>
        VideoPreviewWorker.GenerateAsync(source, true, hardwareAcceleration, onProgress, cancellationToken);
}
