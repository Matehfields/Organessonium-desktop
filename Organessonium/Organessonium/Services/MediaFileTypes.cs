using System.IO;

namespace Organessonium.Services;

public static class MediaFileTypes
{
    public static string? MimeType(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" or ".dib" => "image/bmp",
        ".webp" => "image/webp",
        ".wbmp" => "image/vnd.wap.wbmp",
        ".tif" or ".tiff" => "image/tiff",
        ".heic" => "image/heic",
        ".heif" => "image/heif",
        ".avif" => "image/avif",
        ".jxl" => "image/jxl",
        ".ico" or ".cur" => "image/vnd.microsoft.icon",
        ".tga" => "image/x-tga",
        ".svg" => "image/svg+xml",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" or ".qt" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".avi" => "video/x-msvideo",
        ".3gp" => "video/3gpp",
        ".3g2" => "video/3gpp2",
        ".wmv" => "video/x-ms-wmv",
        ".flv" => "video/x-flv",
        ".mpg" or ".mpeg" or ".mpe" or ".m2v" or ".vob" => "video/mpeg",
        ".mts" or ".m2ts" => "video/mp2t",
        ".ogv" => "video/ogg",
        _ => null
    };

    public static bool IsImage(string name) => MimeType(name)?.StartsWith("image/", StringComparison.Ordinal) == true;
    public static bool IsVideo(string name) => MimeType(name)?.StartsWith("video/", StringComparison.Ordinal) == true;
}
