using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Organessonium.Services;
using Organessonium.Models;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> ThumbnailFormatsRamOnlyAsync()
    {
        var executable = Environment.GetEnvironmentVariable("ORGANESSONIUM_MEDIA_TEST_EXE");
        var ffmpeg = Environment.GetEnvironmentVariable("ORGANESSONIUM_FFMPEG_TEST_EXE");
        if (!File.Exists(executable) || !File.Exists(ffmpeg)) return TestOutcome.Fail("executáveis do teste de mídia não informados");
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-thumbnail-formats-test-");
        try
        {
            var residentCache = new byte[288 * 1024 * 1024];
            Array.Fill(residentCache, (byte)7);
            foreach (var extension in new[] { "png", "jpg", "webp", "avif", "mov", "mkv", "hevc.mkv" })
            {
                var source = new FileInfo(Path.Combine(directory.FullName, "source." + extension));
                var info = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
                var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=640x360:r=12" };
                if (extension == "hevc.mkv") arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                    "-t", "2", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "pools=1:frame-threads=1", "-c:a", "aac"]);
                else if (extension is "mkv" or "mov") arguments.AddRange(["-t", "2", "-c:v", "libx264", "-preset", "ultrafast"]);
                else if (extension == "avif") arguments.AddRange(["-frames:v", "1", "-c:v", "libaom-av1", "-cpu-used", "8", "-threads", "1", "-still-picture", "1"]);
                else arguments.AddRange(["-frames:v", "1"]);
                arguments.Add(source.FullName);
                foreach (var argument in arguments) info.ArgumentList.Add(argument);
                using (var encoder = Process.Start(info)!)
                {
                    var errors = encoder.StandardError.ReadToEndAsync();
                    await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                    await errors;
                    RequireSync(encoder.ExitCode == 0, "não foi possível criar a fixture " + extension);
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                using var thumbnail = ThumbnailCache.IsVideo(source.Name) || extension == "avif"
                    ? await FfmpegMediaService.ThumbnailAsync(source, timeout.Token, ffmpeg)
                    : await VideoPreviewWorker.GenerateAsync(source, false, false, null, timeout.Token, executable);
                using var image = new MemoryStream(thumbnail.Bytes);
                var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(image,
                    System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                var rgb = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var pixel = new byte[4];
                rgb.CopyPixels(new System.Windows.Int32Rect(100, 100, 1, 1), pixel, 4, 0);
                RequireSync(frame.PixelWidth == 200 && frame.PixelHeight == 200 && pixel[2] > 180 && pixel[1] < 70 && pixel[0] < 70,
                    extension + ": a miniatura contém ícone genérico em vez da imagem/frame");
                Console.WriteLine("Thumbnail format passed: " + extension);
                if (extension.EndsWith("mkv", StringComparison.Ordinal))
                {
                    var crypto = CreateEncryptionService(out _);
                    var context = new EncryptedPayloadContext("mkv-preview", crypto.CreateFileEncryption(EncryptionModes.Standard),
                        EncryptedPayloadRoles.Video480p, 0, 1);
                    using var server = new GroupedDiscordFixture();
                    using var api = new DiscordApiClient("mkv-" + Guid.NewGuid(), new HttpClient(server, false));
                    var uploaded = await FfmpegPreviewUpload.UploadAsync(source, "480.mp4", "thread", new ForumUploader(api, "files"),
                        crypto, context, true, false, null, null, timeout.Token, executable: ffmpeg);
                    var encrypted = server.AttemptPayloads.Single().Single();
                    var preview = crypto.DecryptBytes(encrypted, context);
                    RequireSync(preview.AsSpan(4, 4).SequenceEqual("ftyp"u8) && uploaded.Upload.ChunkReferences?.Count == 1,
                        "MKV não produziu e enviou uma prévia MP4 autenticada");
                    await ProbeMediaBytesAsync(preview, extension == "hevc.mkv", ffmpeg!, timeout.Token);
                    CryptographicOperations.ZeroMemory(preview);
                }
            }
            GC.KeepAlive(residentCache);
            RequireSync(directory.EnumerateFiles().Count() == 7, "o gerador criou arquivos intermediários");
            RequireSync(AppMemoryBudget.PeakCommittedBytes <= AppMemoryBudget.LimitBytes, "o gerador excedeu 1 GB");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private static async Task<TestOutcome> LargeVirtualMp4RangesAsync()
    {
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-virtual-large-test-");
        try
        {
            const long size = 35L * 1024 * 1024 * 1024;
            var path = Path.Combine(directory.FullName, "large.mp4");
            var ftyp = Mp4Box("ftyp", "isom0000"u8.ToArray());
            var offsets = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(offsets.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt64BigEndian(offsets.AsSpan(8), (ulong)(ftyp.Length + 16));
            var moov = Mp4Box("moov", Mp4Box("trak", Mp4Box("mdia", Mp4Box("minf", Mp4Box("stbl", Mp4Box("co64", offsets))))));
            var sample = "media-sample-kept-verbatim"u8.ToArray();
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, true))
            {
                RequireSync(MarkTestFileSparse(file.SafeFileHandle, 0x000900c4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero),
                    "não foi possível criar a fixture sparse sem gravar 35 GiB");
                file.SetLength(size);
                await file.WriteAsync(ftyp);
                var mdat = new byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
                "mdat"u8.CopyTo(mdat.AsSpan(4));
                BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)(size - ftyp.Length - moov.Length));
                await file.WriteAsync(mdat);
                await file.WriteAsync(sample);
                file.Position = size - moov.Length - sample.Length;
                await file.WriteAsync(sample);
                file.Position = size - moov.Length;
                await file.WriteAsync(moov);
            }
            using var prepared = await Mp4FastStart.PrepareAsync(new FileInfo(path));
            RequireSync(prepared.Applied && prepared.Length == size && prepared.File.FullName == path,
                "o MP4 grande foi copiado ou não recebeu o faststart virtual");
            foreach (var position in new[] { ftyp.Length + moov.Length + 16L, size - sample.Length })
            {
                await using var input = prepared.OpenRead();
                input.Position = position;
                var actual = new byte[sample.Length];
                await input.ReadExactlyAsync(actual);
                RequireSync(actual.SequenceEqual(sample), "o range virtual alterou a mídia do arquivo grande");
            }
            RequireSync(directory.EnumerateFiles("*", SearchOption.AllDirectories).Count() == 1,
                "a preparação criou uma cópia completa em disco");
            RequireSync(AppMemoryBudget.PeakCommittedBytes <= AppMemoryBudget.LimitBytes, "o MP4 grande ultrapassou 1 GB de RAM");
            Console.WriteLine($"Virtual MP4 source: 35 GiB; RAM peak: {AppMemoryBudget.PeakCommittedBytes / 1024d / 1024d:F1} MiB");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private static async Task<TestOutcome> MediaPreviewWorkerRamOnlyAsync()
    {
        var executable = Environment.GetEnvironmentVariable("ORGANESSONIUM_MEDIA_TEST_EXE");
        var ffmpeg = Environment.GetEnvironmentVariable("ORGANESSONIUM_FFMPEG_TEST_EXE");
        if (!File.Exists(executable) || !File.Exists(ffmpeg))
            return TestOutcome.Fail("informe ORGANESSONIUM_MEDIA_TEST_EXE e ORGANESSONIUM_FFMPEG_TEST_EXE para o teste real de mídia");
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-native-media-test-");
        string[] UploadTemps() => new[] { "organessonium-faststart-", "organessonium_image_preview_", "organessonium_video_preview_" }
            .SelectMany(prefix => Directory.GetDirectories(Path.GetTempPath(), prefix + "*")).Order().ToArray();
        var before = UploadTemps();
        try
        {
            var source = new FileInfo(Path.Combine(directory.FullName, "source.mp4"));
            var info = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                "testsrc2=size=640x360:rate=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100",
                "-t", "2", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-c:a", "aac", source.FullName })
                info.ArgumentList.Add(argument);
            using (var encoder = Process.Start(info)!)
            {
                var stderr = encoder.StandardError.ReadToEndAsync();
                await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                await stderr;
                RequireSync(encoder.ExitCode == 0, "a fixture de vídeo não pôde ser criada");
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var residentCache = new byte[128 * 1024 * 1024];
            Array.Fill(residentCache, (byte)7); // Existing app data must not be confused with the helper's native heap.
            using (var thumbnail = await VideoPreviewWorker.GenerateAsync(source, false, false, null, timeout.Token, executable))
                RequireSync(thumbnail.Length > 0 && thumbnail.Length < 1024 * 1024, "a miniatura RAM não foi produzida");
            using (var preview = await VideoPreviewWorker.GenerateAsync(source, true, false, null, timeout.Token, executable))
            {
                RequireSync(preview.Length > 16 && preview.Bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8), "a prévia RAM não é um MP4 válido");
                using var prepared = await Mp4FastStart.PrepareAsync(preview, timeout.Token);
                await using var input = prepared.OpenRead();
                RequireSync(input.Length == preview.Length, "a prévia virtual perdeu bytes");
                var probeInfo = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg!)!, "ffprobe.exe"))
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                };
                foreach (var argument in new[] { "-v", "error", "-show_entries", "stream=codec_type,width,height", "-of", "json", "-i", "pipe:0" })
                    probeInfo.ArgumentList.Add(argument);
                using var probe = Process.Start(probeInfo)!;
                var probeOutput = probe.StandardOutput.ReadToEndAsync();
                var probeErrors = probe.StandardError.ReadToEndAsync();
                await input.CopyToAsync(probe.StandardInput.BaseStream, timeout.Token);
                probe.StandardInput.Close();
                await probe.WaitForExitAsync(timeout.Token);
                await probeErrors;
                using var parsed = JsonDocument.Parse(await probeOutput);
                var streams = parsed.RootElement.GetProperty("streams").EnumerateArray().ToArray();
                RequireSync(probe.ExitCode == 0 && streams.Any(stream => stream.GetProperty("codec_type").GetString() == "audio") &&
                    streams.Any(stream => stream.GetProperty("codec_type").GetString() == "video" &&
                        stream.GetProperty("width").GetInt32() <= 854 && stream.GetProperty("height").GetInt32() <= 480),
                    "a prévia RAM não preservou áudio e vídeo 480p válidos");
            }
            var invalid = new FileInfo(Path.Combine(directory.FullName, "invalid.mp4"));
            await File.WriteAllBytesAsync(invalid.FullName, [1, 2, 3]);
            try { using var unexpected = await VideoPreviewWorker.GenerateAsync(invalid, true, false, null, timeout.Token, executable); return TestOutcome.Fail("mídia inválida foi aceita"); }
            catch (IOException) { } // Native failure exits the helper, not this parent process.
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            try { using var unexpected = await VideoPreviewWorker.GenerateAsync(source, true, false, null, cancelled.Token, executable); return TestOutcome.Fail("cancelamento do helper não foi respeitado"); }
            catch (OperationCanceledException) { }
            GC.KeepAlive(residentCache);
            RequireSync(before.SequenceEqual(UploadTemps()) && directory.EnumerateFiles().Count() == 2,
                "a geração de mídia escreveu temporários de upload");
            RequireSync(AppMemoryBudget.PeakCommittedBytes <= AppMemoryBudget.LimitBytes, "o helper ultrapassou o orçamento global de 1 GB");
            Console.WriteLine($"Native media RAM peak: {AppMemoryBudget.PeakCommittedBytes / 1024d / 1024d:F1} MiB");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MarkTestFileSparse(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
