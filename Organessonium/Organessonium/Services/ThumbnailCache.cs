using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace Organessonium.Services;

public static class ThumbnailCache
{
    private static readonly ConcurrentDictionary<string, string> Memory = new();
    private static DirectoryInfo? CacheDirectory;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "png", "gif", "bmp", "webp", "wbmp", "tiff", "tif"
    };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "webm", "mkv", "avi", "3gp"
    };

    public static void Configure(DirectoryInfo previewDirectory)
    {
        CacheDirectory = Directory.CreateDirectory(previewDirectory.FullName);
    }

    public static bool IsImage(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        return ext.Length > 0 && ImageExtensions.Contains(ext);
    }

    public static bool IsVideo(string fileName)
    {
        var ext = Path.GetExtension(fileName).TrimStart('.');
        return ext.Length > 0 && VideoExtensions.Contains(ext);
    }

    public static bool Has(string threadId)
    {
        return Get(threadId) != null;
    }

    public static string? Get(string threadId)
    {
        if (Memory.TryGetValue(threadId, out var value))
        {
            return value;
        }

        var file = ThumbnailFile(threadId) ?? LegacyThumbnailFile(threadId);
        if (file?.Exists != true || file.Length == 0)
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(file.FullName);
            var dataUrl = "data:" + DetectImageMime(bytes) + ";base64," + Convert.ToBase64String(bytes);
            Memory[threadId] = dataUrl;
            return dataUrl;
        }
        catch
        {
            return null;
        }
    }

    public static void Remove(string threadId)
    {
        Memory.TryRemove(threadId, out _);
        var file = ThumbnailFile(threadId);
        if (file?.Exists == true)
        {
            TryDelete(file);
        }

        var legacy = LegacyThumbnailFile(threadId);
        if (legacy?.Exists == true)
        {
            TryDelete(legacy);
        }
    }

    public static void Clear()
    {
        Memory.Clear();
    }

    public static string? GenerateAndStore(string threadId, byte[] bytes)
    {
        try
        {
            return Store(threadId, CreateSquarePng(bytes));
        }
        catch
        {
            return null;
        }
    }

    public static byte[] CreateSquareJpeg(byte[] bytes)
    {
        return CreateSquareEncoded(bytes, new JpegBitmapEncoder { QualityLevel = 88 });
    }

    public static byte[] CreateSquarePng(byte[] bytes)
    {
        return CreateSquareEncoded(bytes, new PngBitmapEncoder());
    }

    private static byte[] CreateSquareEncoded(byte[] bytes, BitmapEncoder encoder)
    {
        using var input = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var side = Math.Max(1, Math.Min(frame.PixelWidth, frame.PixelHeight));
        var crop = new CroppedBitmap(
            frame,
            new System.Windows.Int32Rect(
                Math.Max(0, (frame.PixelWidth - side) / 2),
                Math.Max(0, (frame.PixelHeight - side) / 2),
                side,
                side));
        crop.Freeze();

        BitmapSource source = crop;
        if (side != 200)
        {
            var scale = 200.0 / side;
            var transformed = new TransformedBitmap(crop, new System.Windows.Media.ScaleTransform(scale, scale));
            transformed.Freeze();
            source = transformed;
        }

        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    public static string? Store(string threadId, byte[] imageBytes)
    {
        try
        {
            var file = ThumbnailFile(threadId, createDirectory: true);
            if (file != null)
            {
                File.WriteAllBytes(file.FullName, imageBytes);
            }

            var dataUrl = "data:" + DetectImageMime(imageBytes) + ";base64," + Convert.ToBase64String(imageBytes);
            Memory[threadId] = dataUrl;
            return dataUrl;
        }
        catch
        {
            return null;
        }
    }

    private static FileInfo? ThumbnailFile(string threadId, bool createDirectory = false)
    {
        var dir = CacheDirectory;
        if (dir == null)
        {
            return null;
        }

        if (createDirectory)
        {
            dir.Create();
        }

        var safe = string.IsNullOrWhiteSpace(threadId) ? "item" : threadId;
        safe = string.Concat(safe.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "item";
        }

        return new FileInfo(Path.Combine(dir.FullName, "thumb_" + safe + ".preview"));
    }

    private static FileInfo? LegacyThumbnailFile(string threadId)
    {
        var dir = CacheDirectory;
        if (dir == null)
        {
            return null;
        }

        var safe = string.IsNullOrWhiteSpace(threadId) ? "item" : threadId;
        safe = string.Concat(safe.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "item";
        }

        return new FileInfo(Path.Combine(dir.FullName, "thumb_" + safe + ".jpg"));
    }

    private static string DetectImageMime(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x4E &&
            bytes[3] == 0x47)
        {
            return "image/png";
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xD8 &&
            bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (bytes.Length >= 6 &&
            bytes[0] == 0x47 &&
            bytes[1] == 0x49 &&
            bytes[2] == 0x46)
        {
            return "image/gif";
        }

        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 &&
            bytes[1] == 0x49 &&
            bytes[2] == 0x46 &&
            bytes[3] == 0x46 &&
            bytes[8] == 0x57 &&
            bytes[9] == 0x45 &&
            bytes[10] == 0x42 &&
            bytes[11] == 0x50)
        {
            return "image/webp";
        }

        return "image/png";
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
            // Cache cleanup should not block file operations.
        }
    }
}
