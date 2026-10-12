using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace Organessonium.Services;

// CLI only: no libav linking, no media files or complete preview buffers on disk.
internal static class FfmpegMediaService
{
    internal const int PreviewChunkBytes = 2 * 1024 * 1024;
    private const long WorkingBytes = 192L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "media", "ffmpeg.exe");
    internal static bool Available => File.Exists(Executable) && Environment.Is64BitOperatingSystem &&
        (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 ||
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));

    internal static async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        return new GateLease();
    }

    internal static async Task<RamMediaPreview> ThumbnailAsync(FileInfo source, CancellationToken token, string? executable = null)
    {
        using var gate = await EnterAsync(token).ConfigureAwait(false);
        using var sourceLock = OpenSourceLock(source);
        using var output = new MemoryStream();
        await ReadOutputAsync(source, false, true, async (stream, cancellation) =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > VideoPreviewWorker.MaximumThumbnailBytes)
                        throw new IOException("A miniatura excedeu o limite de memória.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
                }
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }, token, executable).ConfigureAwait(false);
        if (output.Length == 0) throw new IOException("Não foi possível extrair a miniatura da mídia.");
        var bytes = output.ToArray();
        CryptographicOperations.ZeroMemory(output.GetBuffer());
        return new RamMediaPreview(bytes);
    }

    internal static FileStream OpenSourceLock(FileInfo source) => new(source.FullName, FileMode.Open,
        FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);

    internal static async Task ReadOutputAsync(FileInfo source, bool hardwareAcceleration, bool thumbnail,
        Func<Stream, CancellationToken, Task> consume, CancellationToken token, string? executable = null,
        Action<TimeSpan>? encodedTime = null)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(token);
        admission.CancelAfter(TimeSpan.FromSeconds(30));
        IDisposable reservation;
        try
        {
            reservation = await RamUploadBudget.ReserveExternalBytesAsync(
                thumbnail ? VideoPreviewWorker.ThumbnailWorkingReservationBytes : WorkingBytes,
                thumbnail ? VideoPreviewWorker.MaximumThumbnailBytes : 0, admission.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("Memória indisponível para gerar a prévia. O arquivo original foi preservado.");
        }
        using (reservation)
        {
            var info = new ProcessStartInfo(executable ?? Executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in Arguments(source, hardwareAcceleration, thumbnail)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("Não foi possível iniciar o FFmpeg.");
            IDisposable? registration = null;
            Task? errors = null;
            using var cancel = token.Register(() => Stop(process));
            try
            {
                registration = AppMemoryBudget.RegisterMediaWorker(process);
                errors = DrainProgressAsync(process.StandardError, encodedTime);
                await consume(process.StandardOutput.BaseStream, token).ConfigureAwait(false);
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new IOException($"O FFmpeg não pôde gerar a prévia (código {process.ExitCode}). O arquivo original foi preservado.");
            }
            catch (Exception) when (token.IsCancellationRequested) { throw new OperationCanceledException(token); }
            catch (EndOfStreamException ex)
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                if (errors != null) await errors.ConfigureAwait(false);
                throw new IOException(process.ExitCode == 0
                    ? "O tamanho da prévia mudou entre as duas gerações. " + ex.Message
                    : $"O FFmpeg encerrou a geração da prévia com erro (código {process.ExitCode}).", ex);
            }
            finally
            {
                Stop(process);
                if (errors != null)
                {
                    try { await errors.ConfigureAwait(false); } catch (IOException) { }
                }
                registration?.Dispose();
            }
        }
    }

    private static IEnumerable<string> Arguments(FileInfo source, bool hardwareAcceleration, bool thumbnail)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-nostats", "-progress", "pipe:2",
            "-threads", "2", "-filter_threads", "1" };
        if (hardwareAcceleration && !thumbnail) args.AddRange(["-hwaccel", "auto"]);
        args.AddRange(["-i", source.FullName, "-map", "0:v:0", "-sn", "-dn", "-map_metadata", "-1"]);
        if (thumbnail)
        {
            args.AddRange(["-vf", "scale=200:200:force_original_aspect_ratio=increase,crop=200:200,setsar=1",
                "-frames:v", "1", "-threads", "1", "-c:v", ThumbnailCache.IsImage(source.Name) ? "png" : "mjpeg",
                "-f", "image2pipe", "pipe:1"]);
        }
        else
        {
            args.AddRange(["-map", "0:a:0?", "-vf",
                "scale=w='min(854,iw)':h='min(480,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1",
                "-r", "30", "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-threads", "1",
                "-pix_fmt", "yuv420p", "-b:v", "1000k", "-maxrate", "1000k", "-bufsize", "1000k",
                "-g", "60", "-keyint_min", "60", "-sc_threshold", "0",
                "-c:a", "aac", "-b:a", "96k", "-ac", "2", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact",
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1"]);
        }
        return args;
    }

    private static async Task DrainProgressAsync(StreamReader reader, Action<TimeSpan>? encodedTime)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            // Never retain stderr: codec errors can include source paths and embedded metadata.
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
                encodedTime?.Invoke(TimeSpan.FromMicroseconds(value));
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private sealed class GateLease : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) Gate.Release(); }
    }
}
