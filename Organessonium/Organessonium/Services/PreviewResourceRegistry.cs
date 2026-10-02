using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace Organessonium.Services;

public sealed record PreviewResourceResponse(int Status, string Reason, string Headers, Stream? Content);

/// <summary>Opaque virtual HTTPS resources. No listener, port, socket or arbitrary path resolution.</summary>
public sealed class PreviewResourceRegistry : IDisposable
{
    // WebView2 requires available response content at deferral completion. Keep each media response small.
    public const int VideoResponseBytes = 512 * 1024;
    public const string Host = "media.organessonium.local";
    public const string UrlFilter = "https://" + Host + "/*";
    private readonly ConcurrentDictionary<string, Resource> _resources = new();
    internal long BufferedResponseBytes => _resources.Values.Sum(resource => resource.Streams.Keys.OfType<AvailableMemoryStream>().Sum(stream => stream.BufferedBytes));

    public string Register(FileInfo file, string mime) => Register(new Resource(file, null, null, mime));
    public string RegisterBytes(byte[] bytes, string mime) => Register(new Resource(null, bytes, null, mime));
    public string RegisterVideo(VideoStreamSession session, string mime) => Register(new Resource(null, null, session, mime));
    private string Register(Resource resource)
    {
        var id = Guid.NewGuid().ToString("N");
        _resources[id] = resource;
        return $"https://{Host}/preview/{id}";
    }
    public FileInfo? GetFileFromUrl(string? url) => Find(url)?.File;
    public VideoStreamSession? GetVideo(string? url) => Find(url)?.Video;

    public async Task<PreviewResourceResponse> OpenAsync(string url, string method, string? range)
    {
        var resource = Find(url);
        if (resource == null) return Empty(404, "Not Found");
        if (method is not ("GET" or "HEAD")) return Empty(405, "Method Not Allowed");
        var length = resource.Video?.Length ?? resource.File?.Length ?? resource.Bytes?.LongLength ?? 0;
        var parsed = ParseRange(range, length);
        if (parsed == null) return Empty(416, "Range Not Satisfiable", "Content-Range: bytes */" + length + "\r\n");
        var (start, end) = parsed.Value;
        var boundedVideo = resource.Video != null && method == "GET";
        if (boundedVideo) end = Math.Min(end, start + VideoResponseBytes - 1);
        var count = length == 0 ? 0 : end - start + 1;
        var partial = !string.IsNullOrEmpty(range) || boundedVideo;
        var mime = string.IsNullOrWhiteSpace(resource.Mime) || resource.Mime.Contains('\r') || resource.Mime.Contains('\n')
            ? "application/octet-stream" : resource.Mime;
        var headers = $"Content-Type: {mime}\r\nContent-Length: {count}\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: https://organessonium.local\r\n";
        if (partial) headers += $"Content-Range: bytes {start}-{end}/{length}\r\n";
        Stream? content = null;
        if (method == "GET" && count > 0)
        {
            if (resource.Video != null)
            {
                await resource.Responses.WaitAsync(resource.Video.Cancellation).ConfigureAwait(false);
                var transferred = false;
                try
                {
                    var bytes = new byte[checked((int)count)];
                    await resource.Video.ReadAsync(start, bytes, resource.Video.Cancellation).ConfigureAwait(false);
                    content = new AvailableMemoryStream(bytes, stream =>
                    {
                        resource.Streams.TryRemove(stream, out _);
                        resource.Responses.Release();
                    });
                    transferred = true;
                }
                finally { if (!transferred) resource.Responses.Release(); }
            }
            else if (resource.File != null)
                content = new VirtualFileStream(new DiskFile(resource.File), start, count);
            else if (resource.Bytes != null)
                content = new MemoryStream(resource.Bytes, checked((int)start), checked((int)count), false);
            if (content != null && !resource.Track(content))
            {
                content.Dispose();
                return Empty(410, "Gone");
            }
        }
        return new PreviewResourceResponse(partial ? 206 : 200, partial ? "Partial Content" : "OK", headers, content);
    }

    internal static (long Start, long End)? ParseRange(string? range, long length)
    {
        if (string.IsNullOrEmpty(range)) return (0, Math.Max(0, length - 1));
        if (length <= 0 || !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = range[6..].Split('-');
        if (parts.Length != 2) return null;
        if (parts[0].Length == 0)
        {
            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return null;
            return (Math.Max(0, length - suffix), length - 1);
        }
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start >= length) return null;
        var end = length - 1;
        if (parts[1].Length > 0 && !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out end)) return null;
        if (end < start) return null;
        return (start, Math.Min(end, length - 1));
    }

    private Resource? Find(string? url) => Token(url) is string id && _resources.TryGetValue(id, out var resource) ? resource : null;
    private static string? Token(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != Host || !uri.IsDefaultPort || uri.Query.Length > 0) return null;
        var parts = uri.AbsolutePath.Split('/');
        return parts.Length == 3 && parts[1] == "preview" && Guid.TryParseExact(parts[2], "N", out _) ? parts[2] : null;
    }
    private static PreviewResourceResponse Empty(int status, string reason, string extra = "") =>
        new(status, reason, extra + "Content-Length: 0\r\nCache-Control: no-store\r\n", null);

    public void RemoveUrl(string? url)
    {
        if (Token(url) is string id && _resources.TryRemove(id, out var resource)) resource.Dispose();
    }
    public void Dispose()
    {
        foreach (var pair in _resources.ToArray()) RemoveUrl($"https://{Host}/preview/{pair.Key}");
    }
    private sealed class Resource(FileInfo? file, byte[]? bytes, VideoStreamSession? video, string mime) : IDisposable
    {
        public FileInfo? File { get; } = file;
        public byte[]? Bytes { get; } = bytes;
        public VideoStreamSession? Video { get; } = video;
        public string Mime { get; } = mime;
        public ConcurrentDictionary<Stream, byte> Streams { get; } = new();
        public SemaphoreSlim Responses { get; } = new(4, 4);
        private readonly object _sync = new();
        private bool _closed;
        public bool Track(Stream stream)
        {
            lock (_sync)
            {
                if (_closed) return false;
                Streams[stream] = 0;
                return true;
            }
        }
        public void Dispose()
        {
            lock (_sync)
            {
                _closed = true;
                Video?.Dispose();
                foreach (var stream in Streams.Keys) stream.Dispose();
                Streams.Clear();
            }
        }
    }
    private sealed class DiskFile(FileInfo file) : IVirtualFile
    {
        public long Length => file.Length;
        public async ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, true);
            input.Position = offset;
            return await input.ReadAsync(destination, cancellationToken).ConfigureAwait(false);
        }
    }
}
