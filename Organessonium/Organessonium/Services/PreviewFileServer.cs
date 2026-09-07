using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace Organessonium.Services;

public sealed class PreviewFileServer : IDisposable
{
    private readonly ConcurrentDictionary<string, ServedFile> _files = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public string Register(FileInfo file, string mime)
    {
        EnsureStarted();
        var token = Guid.NewGuid().ToString("N");
        _files[token] = new ServedFile(file, null, mime, null, null);
        return $"http://127.0.0.1:{Port}/preview/{token}/{Uri.EscapeDataString(file.Name)}";
    }

    public string RegisterBytes(byte[] data, string mime)
    {
        EnsureStarted();
        var token = Guid.NewGuid().ToString("N");
        _files[token] = new ServedFile(null, data, mime, null, null);
        return $"http://127.0.0.1:{Port}/preview/{token}";
    }

    public string RegisterProgressive(ProgressiveDownload download, string mime)
    {
        EnsureStarted();
        var token = Guid.NewGuid().ToString("N");
        _files[token] = new ServedFile(download.File, null, mime, download, download.Length);
        return $"http://127.0.0.1:{Port}/preview/{token}/{Uri.EscapeDataString(download.File.Name)}";
    }

    public FileInfo? GetFileFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var token = ExtractToken(url);
        if (token == null || !_files.TryGetValue(token, out var served))
        {
            return null;
        }

        if (served.Progressive != null && !served.Progressive.IsComplete)
        {
            return null;
        }

        return served.File;
    }

    public void RemoveUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        var token = ExtractToken(url);
        if (token != null && _files.TryRemove(token, out var served))
        {
            served.Progressive?.Dispose();
        }
    }
    public int Port { get; private set; }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        foreach (var served in _files.Values)
        {
            try { served.Progressive?.Dispose(); } catch { }
        }

        _files.Clear();
        try { _listener?.Stop(); } catch { }
        _listener?.Close();
        _cts?.Dispose();
    }
    private void EnsureStarted()
    {
        if (_listener != null)
        {
            return;
        }

        Port = FindFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(context), cancellationToken);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await Task.Delay(150, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["Accept-Ranges"] = "bytes";

            var token = ExtractToken(context.Request.Url?.AbsolutePath);
            if (token == null ||
                !_files.TryGetValue(token, out var served) ||
                (served.File?.Exists != true && served.Bytes == null))
            {
                context.Response.StatusCode = 404;
                return;
            }

            var length = served.Length ?? served.File?.Length ?? served.Bytes?.LongLength ?? 0;
            var start = 0L;
            var end = Math.Max(0, length - 1);
            var status = 200;
            var range = context.Request.Headers["Range"];
            if (!string.IsNullOrWhiteSpace(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && length > 0)
            {
                var parsed = ParseRange(range, length);
                if (parsed == null)
                {
                    context.Response.StatusCode = 416;
                    context.Response.Headers["Content-Range"] = "bytes */" + length;
                    return;
                }

                start = parsed.Value.Start;
                end = parsed.Value.End;
                status = 206;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{length}";
            }

            var contentLength = length == 0 ? 0 : end - start + 1;
            context.Response.StatusCode = status;
            context.Response.ContentType = string.IsNullOrWhiteSpace(served.Mime) ? "application/octet-stream" : served.Mime;
            context.Response.ContentLength64 = contentLength;
            if (context.Request.HttpMethod.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (served.Progressive != null)
            {
                await served.Progressive.EnsureAvailableAsync(end);
            }

            if (served.File != null)
            {
                await using var input = new FileStream(served.File.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, true);
                input.Seek(start, SeekOrigin.Begin);
                await CopyBytesAsync(input, context.Response.OutputStream, contentLength);
            }
            else if (served.Bytes != null && contentLength > 0)
            {
                await CopyBytesAsync(served.Bytes, context.Response.OutputStream, start, contentLength);
            }
        }
        catch
        {
            try { context.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private static async Task CopyBytesAsync(Stream input, Stream output, long bytes)
    {
        var buffer = new byte[64 * 1024];
        var remaining = bytes;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read <= 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    private static async Task CopyBytesAsync(byte[] input, Stream output, long start, long bytes)
    {
        var offset = start;
        var remaining = bytes;
        while (remaining > 0)
        {
            var take = (int)Math.Min(64 * 1024, remaining);
            await output.WriteAsync(input.AsMemory((int)offset, take));
            offset += take;
            remaining -= take;
        }
    }

    private static (long Start, long End)? ParseRange(string range, long length)
    {
        try
        {
            var spec = range["bytes=".Length..].Trim();
            var dash = spec.IndexOf('-');
            if (dash < 0)
            {
                return null;
            }

            var startText = spec[..dash].Trim();
            var endText = spec[(dash + 1)..].Trim();
            long start;
            long end;
            if (startText.Length == 0)
            {
                var suffix = long.Parse(endText);
                if (suffix <= 0)
                {
                    return null;
                }

                start = Math.Max(0, length - suffix);
                end = length - 1;
            }
            else
            {
                start = long.Parse(startText);
                end = endText.Length == 0 ? length - 1 : long.Parse(endText);
            }

            if (start < 0 || start >= length || end < start)
            {
                return null;
            }

            return (start, Math.Min(end, length - 1));
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractToken(string? urlOrPath)
    {
        if (string.IsNullOrWhiteSpace(urlOrPath))
        {
            return null;
        }

        var path = Uri.TryCreate(urlOrPath, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : urlOrPath;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("preview", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return parts.Length > 0 ? parts[^1] : null;
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record ServedFile(FileInfo? File, byte[]? Bytes, string Mime, ProgressiveDownload? Progressive, long? Length);
}


