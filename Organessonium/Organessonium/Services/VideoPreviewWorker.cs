using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Organessonium.Services;

// Native codecs run in a separate hidden process. Output crosses an anonymous RAM pipe.
public static class VideoPreviewWorker
{
    public const string Argument = "--media-preview-worker";
    internal const int MaximumPreviewBytes = 128 * 1024 * 1024;
    internal const long WorkingReservationBytes = 320L * 1024 * 1024;
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);

    internal static async Task<RamMediaPreview> GenerateAsync(FileInfo source, bool video,
        bool hardwareAcceleration, Action<int>? progress, CancellationToken token, string? executable = null)
    {
        await WorkerGate.WaitAsync(token).ConfigureAwait(false);
        IDisposable? reservation = null;
        IDisposable? processRegistration = null;
        Process? worker = null;
        Task<string>? errors = null;
        try
        {
            using var admission = CancellationTokenSource.CreateLinkedTokenSource(token);
            admission.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                reservation = await RamUploadBudget.ReserveExternalBytesAsync(WorkingReservationBytes, MaximumPreviewBytes, admission.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException("Memória indisponível para gerar a prévia. O arquivo original foi preservado.");
            }
            var info = new ProcessStartInfo(executable ?? Environment.ProcessPath ?? throw new InvalidOperationException("Executável do app indisponível."))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            info.ArgumentList.Add(Argument);
            info.ArgumentList.Add(video ? "480p" : "thumbnail");
            info.ArgumentList.Add(source.FullName);
            info.ArgumentList.Add(hardwareAcceleration ? "true" : "false");
            worker = Process.Start(info) ?? throw new IOException("Não foi possível iniciar o gerador de prévias.");
            processRegistration = AppMemoryBudget.RegisterMediaWorker(worker);
            errors = ReadProgressAsync(worker.StandardError, progress, token);
            var header = new byte[4];
            try { await worker.StandardOutput.BaseStream.ReadExactlyAsync(header, token).ConfigureAwait(false); }
            catch (EndOfStreamException)
            {
                await worker.WaitForExitAsync(token).ConfigureAwait(false);
                var reason = await errors.ConfigureAwait(false);
                throw new IOException(reason == "size-limit"
                    ? "A prévia 480p não cabe no orçamento de RAM. O arquivo original foi preservado."
                    : "O gerador de prévias falhou. O arquivo original foi preservado.");
            }
            var size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size <= 0 || size > MaximumPreviewBytes) throw new InvalidDataException("Saída inválida do gerador de prévias.");
            var bytes = new byte[size];
            try
            {
                await worker.StandardOutput.BaseStream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                await worker.WaitForExitAsync(token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                if (worker.ExitCode != 0) throw new IOException("O gerador de prévias terminou com erro.");
                return new RamMediaPreview(bytes);
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
        finally
        {
            if (worker != null)
            {
                try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) when (worker.HasExited) { }
                if (errors != null)
                {
                    try { await errors.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
                }
                worker.Dispose();
            }
            processRegistration?.Dispose();
            reservation?.Dispose();
            WorkerGate.Release();
        }
    }

    private static async Task<string> ReadProgressAsync(StreamReader reader, Action<int>? progress, CancellationToken token)
    {
        var reason = "";
        while (await reader.ReadLineAsync(token).ConfigureAwait(false) is { } line)
        {
            if (int.TryParse(line, out var value)) progress?.Invoke(Math.Clamp(value, 0, 100));
            else if (line == "size-limit") reason = line;
        }
        return reason;
    }

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length != 4 || arguments[0] != Argument) return 2;
        try
        {
            var source = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(arguments[2]));
            using var output = new BoundedPreviewStream();
            using var target = output.AsRandomAccessStream();
            if (arguments[1] == "thumbnail")
            {
                byte[]? image = null;
                if (!ThumbnailCache.IsImage(source.Name))
                {
                    try
                    {
                        var clip = await MediaClip.CreateFromFileAsync(source);
                        var composition = new MediaComposition();
                        composition.Clips.Add(clip);
                        using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.Zero, 512, 288, VideoFramePrecision.NearestFrame);
                        image = await ThumbnailBytesAsync(thumbnail);
                    }
                    catch (Exception) { } // The Shell fallback remains available for unsupported media.
                }
                if (image == null)
                {
                    using var thumbnail = await source.GetThumbnailAsync(ThumbnailMode.SingleItem, 512, ThumbnailOptions.ResizeThumbnail);
                    image = await ThumbnailBytesAsync(thumbnail);
                }
                if (image == null) throw new IOException("Miniatura indisponível.");
                var square = ThumbnailCache.IsImage(source.Name) ? ThumbnailCache.CreateSquarePng(image) : ThumbnailCache.CreateSquareJpeg(image);
                await output.WriteAsync(square);
                CryptographicOperations.ZeroMemory(image);
                CryptographicOperations.ZeroMemory(square);
            }
            else if (arguments[1] == "480p")
            {
                var properties = await source.Properties.GetVideoPropertiesAsync();
                // Flat MP4 must remain seekable until the encoder finishes its index. Never spill to disk.
                if (properties.Duration.TotalSeconds * 1_096_000 / 8 > MaximumPreviewBytes - 1024 * 1024)
                    throw new PreviewMemoryLimitException();
                var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Wvga);
                if (profile.Video != null)
                {
                    var scale = Math.Min(1.0, Math.Min(854.0 / Math.Max(1u, properties.Width), 480.0 / Math.Max(1u, properties.Height)));
                    profile.Video.Width = EvenDimension(properties.Width * scale);
                    profile.Video.Height = EvenDimension(properties.Height * scale);
                    profile.Video.Bitrate = 1_000_000;
                }
                if (profile.Audio != null) profile.Audio.Bitrate = 96_000;
                using var input = await source.OpenReadAsync();
                var transcoder = new MediaTranscoder { AlwaysReencode = true, HardwareAccelerationEnabled = arguments[3] == "true" };
                var prepared = await transcoder.PrepareStreamTranscodeAsync(input, target, profile);
                if (!prepared.CanTranscode) throw new IOException("Formato não suportado pelo encoder Windows.");
                await prepared.TranscodeAsync().AsTask(default, new Progress<double>(value => Console.Error.WriteLine(Math.Clamp((int)value, 0, 100))));
            }
            else return 2;
            if (output.Length == 0) throw new IOException("Prévia vazia.");
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, checked((int)output.Length));
            using var pipe = Console.OpenStandardOutput();
            await pipe.WriteAsync(header);
            await pipe.WriteAsync(output.GetBuffer().AsMemory(0, checked((int)output.Length)));
            await pipe.FlushAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is PreviewMemoryLimitException ? "size-limit" : ex.GetType().Name);
            return 1;
        }
    }

    private static uint EvenDimension(double value) { var dimension = Math.Max(2u, (uint)Math.Round(value)); return dimension % 2 == 0 ? dimension : dimension - 1; }
    private static async Task<byte[]?> ThumbnailBytesAsync(IRandomAccessStreamWithContentType image)
    {
        if (image.Size is 0 or > 8 * 1024 * 1024) return null;
        var bytes = new byte[checked((int)image.Size)];
        using var reader = new DataReader(image.GetInputStreamAt(0));
        await reader.LoadAsync((uint)image.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
    private sealed class PreviewMemoryLimitException : IOException { }
    private sealed class BoundedPreviewStream() : MemoryStream(1024 * 1024)
    {
        private void Check(long length) { if (length > MaximumPreviewBytes) throw new PreviewMemoryLimitException(); }
        public override void SetLength(long value) { Check(value); base.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { Check(checked(Position + count)); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(checked(Position + buffer.Length)); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) { Check(checked(Position + buffer.Length)); return base.WriteAsync(buffer, token); }
        protected override void Dispose(bool disposing) { if (disposing) CryptographicOperations.ZeroMemory(GetBuffer()); base.Dispose(disposing); }
    }
}

public sealed class RamMediaPreview(byte[] bytes) : IDisposable
{
    public byte[] Bytes { get; } = bytes;
    public long Length => Bytes.LongLength;
    public Stream OpenRead() => new MemoryStream(Bytes, writable: false);
    public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
}
