using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> MediaUploadTypesAsync()
    {
        using var h = new SharedHarness();
        var cases = new[] { ("HEIC", "image/heic"), ("HEIF", "image/heif"), ("AVIF", "image/avif"), ("JXL", "image/jxl"),
            ("TIFF", "image/tiff"), ("JFIF", "image/jpeg"), ("ICO", "image/vnd.microsoft.icon"), ("MOV", "video/quicktime"),
            ("MTS", "video/mp2t"), ("M2TS", "video/mp2t"), ("WMV", "video/x-ms-wmv"), ("OGV", "video/ogg"), ("3G2", "video/3gpp2") };
        var states = await Task.WhenAll(cases.Select(item => h.PrepareAsync("media." + item.Item1, [1, 2, 3])));
        var uploaded = await h.UploadAsync(states);
        var refreshed = await h.Registry.RefreshFolderAsync("root", "/");
        RequireSync(refreshed.Count == cases.Length && uploaded.SelectMany(entry => entry.ChunkReferences!).Select(reference => reference.AttachmentId).Distinct().Count() == cases.Length,
            "o upload misto perdeu arquivos ou compartilhou identificadores de anexos");
        foreach (var item in cases)
        {
            var entry = refreshed.Single(entry => entry.Name == "media." + item.Item1);
            RequireSync(entry.MimeType == item.Item2 && (item.Item2.StartsWith("image/") ? ThumbnailCache.IsImage(entry.Name) : ThumbnailCache.IsVideo(entry.Name)),
                "o metadata remoto não preservou a classificação de mídia");
        }
        return TestOutcome.Pass();
    }
}
