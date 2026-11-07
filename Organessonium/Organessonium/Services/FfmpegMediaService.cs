using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Organessonium.Services;

// CLI only. Thumbnail output stays in RAM; video output is consumed by the staging writer.
internal static class FfmpegMediaService
{
    internal static int PreviewChunkBytes => EncryptionService.MaximumPlaintextPayloadBytes;
    private const long WorkingBytes = 192L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly SemaphoreSlim ThumbnailGate = new(1, 1);
    internal static bool Available => EmbeddedMediaRuntime.Available && Environment.Is64BitOperatingSystem &&
        (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 ||
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));

    internal static async Task<IDisposable> EnterAsync(CancellationToken token, bool thumbnail = false)
    {
        var gate = thumbnail ? ThumbnailGate : Gate;
        await gate.WaitAsync(token).ConfigureAwait(false);
        return new GateLease(gate);
    }

    internal static async Task<RamMediaPreview> ThumbnailAsync(FileInfo source, CancellationToken token, string? executable = null)
    {
        using var gate = await EnterAsync(token, thumbnail: true).ConfigureAwait(false);
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
        Action<TimeSpan>? encodedTime = null, Action<int>? generationProgress = null)
    {
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (!thumbnail) admission.CancelAfter(TimeSpan.FromSeconds(30));
        IDisposable reservation;
        try
        {
            reservation = await RamUploadBudget.ReserveExternalBytesAsync(
                thumbnail ? VideoPreviewWorker.ThumbnailWorkingReservationBytes : WorkingBytes,
                thumbnail ? VideoPreviewWorker.MaximumThumbnailBytes : 0, admission.Token,
                priority: thumbnail).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("Memória indisponível para gerar a prévia. O arquivo original foi preservado.");
        }
        using (reservation)
        {
            var callerToken = token;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (thumbnail) deadline.CancelAfter(TimeSpan.FromSeconds(30));
            token = deadline.Token;
            var runtime = executable ?? await EmbeddedMediaRuntime.GetExecutableAsync(token).ConfigureAwait(false);
            var info = new ProcessStartInfo(runtime)
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
                errors = DrainProgressAsync(process.StandardError, encodedTime, generationProgress);
                await consume(process.StandardOutput.BaseStream, token).ConfigureAwait(false);
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new IOException($"O FFmpeg não pôde gerar a prévia (código {process.ExitCode}). O arquivo original foi preservado.");
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                if (!callerToken.IsCancellationRequested) throw new IOException("O codec excedeu o tempo de geração da miniatura.");
                throw new OperationCanceledException(callerToken);
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
        var args = new List<string> { "-hide_banner", "-loglevel", thumbnail ? "error" : "info", "-nostdin", "-nostats", "-progress", "pipe:2",
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
                "fps=30,scale=w='min(854,iw)':h='min(480,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1",
                "-r", "30", "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-threads", Math.Min(4, Environment.ProcessorCount).ToString(CultureInfo.InvariantCulture),
                "-pix_fmt", "yuv420p", "-b:v", "1000k", "-maxrate", "1000k", "-bufsize", "1000k",
                "-g", "60", "-keyint_min", "60", "-sc_threshold", "0",
                "-c:a", "aac", "-b:a", "96k", "-ac", "2", "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact",
                "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-f", "mp4", "pipe:1"]);
        }
        return args;
    }

    private static async Task DrainProgressAsync(StreamReader reader, Action<TimeSpan>? encodedTime, Action<int>? generationProgress)
    {
        double duration = 0;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            // Never retain stderr: codec errors can include source paths and embedded metadata.
            if (line.TrimStart().StartsWith("Duration:", StringComparison.Ordinal))
            {
                var match = Regex.Match(line, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
                if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var hours) &&
                    double.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var minutes) &&
                    double.TryParse(match.Groups[3].Value, CultureInfo.InvariantCulture, out var seconds))
                    duration = hours * 3600 + minutes * 60 + seconds;
            }
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
            {
                encodedTime?.Invoke(TimeSpan.FromMicroseconds(value));
                if (duration > 0) generationProgress?.Invoke((int)Math.Clamp(value / 10000d / duration, 0, 99));
            }
        }
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release(); }
    }
}
