using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> GeneratedMediaHiddenBrowserAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var ffmpeg = Environment.GetEnvironmentVariable("ORGANESSONIUM_FFMPEG_TEST_EXE");
        if (!File.Exists(ffmpeg)) return TestOutcome.Fail("FFmpeg não informado");
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-generated-media-ui-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? controller = null;
        try
        {
            var source = new FileInfo(Path.Combine(directory.FullName, "gameplay.mkv"));
            var info = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "color=c=red:s=1280x720:r=120",
                "-f", "lavfi", "-i", "sine=frequency=440", "-t", "2", "-c:v", "libx265", "-preset", "ultrafast",
                "-x265-params", "pools=1:frame-threads=1", "-c:a", "aac", source.FullName }) info.ArgumentList.Add(arg);
            using (var encoder = Process.Start(info)!)
            {
                var errors = encoder.StandardError.ReadToEndAsync();
                await encoder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); await errors;
                RequireSync(encoder.ExitCode == 0, "fixture HEVC inválida");
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var thumbnail = await FfmpegMediaService.ThumbnailAsync(source, timeout.Token);
            ThumbnailCache.Configure(directory.CreateSubdirectory("Previews"));
            ThumbnailCache.Store("media-test", thumbnail.Bytes);
            var crypto = CreateEncryptionService(out _);
            var context = new EncryptedPayloadContext("media-ui", crypto.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Video480p, 0, 1);
            using var server = new GroupedDiscordFixture();
            using var api = new DiscordApiClient("media-ui-" + Guid.NewGuid(), new HttpClient(server, false));
            await FfmpegPreviewUpload.UploadAsync(source, "480.mp4", "thread", new ForumUploader(api, "files"), crypto,
                context, true, false, null, null, timeout.Token);
            var preview = crypto.DecryptBytes(server.AttemptPayloads.Single().Single(), context);
            var mediaInfo = JsonSerializer.Serialize(new { src = "data:video/mp4;base64," + Convert.ToBase64String(preview),
                mime = "video/mp4", quality = "480p", qualities = new[] { "480p", "full" }, fileId = context.FileId });
            CryptographicOperations.ZeroMemory(preview);
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(directory.FullName, "WebView"));
            controller = await environment.CreateCoreWebView2ControllerAsync(host);
            controller.IsVisible = true; controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var web = directory.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                await using var output = File.Create(path); await input.CopyToAsync(output);
            }
            var view = controller.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            AppMemoryBudget.RegisterWebViewProcesses(environment.GetProcessInfos().Select(item => item.ProcessId));
            using var thumbnailResources = new PreviewResourceRegistry();
            using var thumbnailAdapter = new Organessonium.Bridge.WebView2PreviewAdapter(view, thumbnailResources);
            thumbnailAdapter.Attach();
            var thumbnailJson = JsonSerializer.Serialize(ThumbnailCache.Get("media-test"));
            await view.ExecuteScriptAsync("window.__organessoniumSettings={previewEnabled:true,theme:'dark',animationsEnabled:false,language:'pt-BR'};window.onBridgeReady();window.bridge.requestPreview=()=>{};window.__media={name:'gameplay.mkv',threadId:'media-test',fileId:'media-ui',type:'file',mimeType:'video/x-matroska',path:'/',parentFolderId:'root',sizeBytes:10000};window._onAppsLoaded([window.__media]);window._thumbs={'media-test':" + thumbnailJson + "};window._onThumb('media-test');");
            await Task.Delay(200);
            RequireSync(await view.ExecuteScriptAsync("(()=>{const i=document.querySelector('[data-thumb=\"media-test\"] img');return !!i&&i.complete&&i.naturalWidth===200&&i.naturalHeight===200})()") == "true",
                "a miniatura gerada não apareceu no card");
            await view.ExecuteScriptAsync("openPreview(window.__media);window._onPreviewReady('video','gameplay.mkv'," + JsonSerializer.Serialize(mediaInfo) + ");");
            var ready = false;
            for (var attempt = 0; attempt < 40 && !ready; attempt++)
            {
                await Task.Delay(100);
                ready = await view.ExecuteScriptAsync("(()=>{const v=document.querySelector('video.preview-media-player');return v?.readyState>=1&&v.videoHeight===480&&!v.error&&document.getElementById('preview-video-quality-button')?.textContent.includes('480p')})()") == "true";
            }
            RequireSync(ready && !IsWindowVisible(host), "o player não abriu a prévia 480p ou exibiu uma janela");
            await view.ExecuteScriptAsync("closePreview();window.__savedSettings=null;window.bridge.saveSettings=(...args)=>window.__savedSettings=args;openSettings();");
            RequireSync(await view.ExecuteScriptAsync("document.getElementById('setting-generate-video-previews').checked") == "true",
                "a opção de gerar prévias não está ativada por padrão");
            await view.ExecuteScriptAsync("document.getElementById('setting-generate-video-previews').click();");
            await Task.Delay(400);
            RequireSync(await view.ExecuteScriptAsync("window.__savedSettings?.[25]===false") == "true", "a opção de prévia não foi salva pela UI");
            await view.ExecuteScriptAsync("closeSettings();document.documentElement.style.setProperty('--accent','#ff0000');window.__cancelledFile=null;window.bridge.cancelUploadFile=(id,index)=>window.__cancelledFile={id,index};window._opsUploadBatch('phase',['movie.mkv','other.bin'],[100,100]);window._opsUploadFileProgress('phase',0,'active',80,'Gerando prévia');window._opsTransferMetrics('phase','upload',0,2,200,200,null);document.querySelector('#op-file-phase-0 .op-file-cancel').focus();");
            using var mousePoint = JsonDocument.Parse(await view.ExecuteScriptAsync("(()=>{const r=document.querySelector('#op-file-phase-0 .op-file-action').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()"));
            await view.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
                { type = "mouseMoved", x = mousePoint.RootElement.GetProperty("x").GetDouble(), y = mousePoint.RootElement.GetProperty("y").GetDouble() }));
            RequireSync(await view.ExecuteScriptAsync("document.getElementById('op-percent-phase').textContent==='40%'&&getComputedStyle(document.querySelector('#op-file-phase-0 .op-file-cancel')).opacity==='1'&&getComputedStyle(document.getElementById('ops-panel')).borderColor!=='rgb(255, 0, 0)'&&!getComputedStyle(document.getElementById('op-file-progress-phase-0')).backgroundImage.includes('255, 0, 0')") == "true",
                "barra total ignora a etapa de prévia, X não apareceu ou o progresso continua com destaque vermelho");
            await view.ExecuteScriptAsync("document.querySelector('#op-file-phase-0 .op-file-cancel').click();window._opsUploadFileProgress('phase',0,'done',100,'Concluído');window._opsUploadFileProgress('phase',1,'done',100,'Concluído');");
            RequireSync(await view.ExecuteScriptAsync("window.__cancelledFile?.id==='phase'&&window.__cancelledFile.index===0&&document.getElementById('op-percent-phase').textContent==='100%'") == "true",
                "X não cancelou apenas o arquivo escolhido ou a conclusão não chegou a 100%");
            return TestOutcome.Pass();
        }
        finally
        {
            controller?.Close(); DestroyWindow(host); ThumbnailCache.Remove("media-test");
            try { directory.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });

    private static async Task ProbeMediaBytesAsync(byte[] bytes, bool audio, string ffmpeg, CancellationToken token)
    {
        using var probe = StartMediaProbe(ffmpeg);
        var output = probe.StandardOutput.ReadToEndAsync(token);
        var errors = probe.StandardError.ReadToEndAsync(token);
        await probe.StandardInput.BaseStream.WriteAsync(bytes, token);
        probe.StandardInput.Close();
        await probe.WaitForExitAsync(token);
        await errors;
        using var json = JsonDocument.Parse(await output);
        CheckMediaProbe(json.RootElement, probe.ExitCode, audio);
    }

    private static Process StartMediaProbe(string ffmpeg)
    {
        var info = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-v", "error", "-count_packets", "-show_entries", "stream=codec_name,codec_type,width,height,nb_read_packets", "-of", "json", "-i", "pipe:0" })
            info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    private static void CheckMediaProbe(JsonElement json, int exit, bool audio)
    {
        var streams = json.GetProperty("streams").EnumerateArray().ToArray();
        RequireSync(exit == 0 && streams.Any(stream => stream.GetProperty("codec_name").GetString() == "h264" &&
            stream.GetProperty("width").GetInt32() <= 854 && stream.GetProperty("height").GetInt32() <= 480), "a prévia não contém H.264 480p válido");
        RequireSync(streams.Any(stream => stream.GetProperty("codec_type").GetString() == "audio") == audio, "a prévia perdeu/adicionou áudio");
    }

    private static async Task<TestOutcome> FfmpegLongPreviewStagedAsync()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("ORGANESSONIUM_FFMPEG_TEST_EXE");
        if (!File.Exists(ffmpeg)) return TestOutcome.Fail("FFmpeg não informado para a fixture");
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-long-preview-test-");
        try
        {
            var source = new FileInfo(Path.Combine(directory.FullName, "long.mkv"));
            var info = new ProcessStartInfo(ffmpeg!) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
                "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1800", "-c:v", "libx264", "-preset", "ultrafast",
                "-threads", "2", "-crf", "28", "-c:a", "aac", source.FullName }) info.ArgumentList.Add(arg);
            using (var fixture = Process.Start(info)!)
            {
                var errors = fixture.StandardError.ReadToEndAsync();
                await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
                await errors;
                RequireSync(fixture.ExitCode == 0, "a fixture longa não pôde ser criada");
            }
            Console.WriteLine("Real MKV fixture: 30 minutes; one encode followed by grouped upload.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            using var probe = StartMediaProbe(ffmpeg!);
            using var registeredProbe = AppMemoryBudget.RegisterMediaWorker(probe);
            using var stopProbe = timeout.Token.Register(() => { try { if (!probe.HasExited) probe.Kill(true); } catch (InvalidOperationException) { } });
            var probeOutput = probe.StandardOutput.ReadToEndAsync();
            var probeErrors = probe.StandardError.ReadToEndAsync();
            var crypto = CreateEncryptionService(out _);
            var context = new EncryptedPayloadContext("long-preview", crypto.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Video480p, 0, 1);
            var generationComplete = false;
            using var receiver = new StreamingPreviewReceiver(crypto, context, probe.StandardInput.BaseStream, () => generationComplete);
            using var api = new DiscordApiClient("long-preview-" + Guid.NewGuid(), new HttpClient(receiver, false));
            long sent = 0;
            var before = Directory.GetFiles(Path.GetTempPath(), "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).StartsWith("organessonium_video", StringComparison.Ordinal)).ToArray();
            var result = await FfmpegPreviewUpload.UploadAsync(source, "480.mp4", "thread", new ForumUploader(api, "files"),
                crypto, context, true, false, null, (current, _) => sent = current, timeout.Token, executable: ffmpeg,
                generationProgress: percent => generationComplete |= percent == 100,
                onPreviewSize: length =>
                {
                    var staged = directory.EnumerateDirectories(".organessonium-preview-*").Single();
                    RequireSync(staged.Attributes.HasFlag(FileAttributes.Hidden) && staged.GetFiles("preview.mp4").Single().Length == length,
                        "a prévia não foi gerada por inteiro na pasta oculta ao lado da origem");
                });
            probe.StandardInput.Close();
            await probe.WaitForExitAsync(timeout.Token);
            await probeErrors;
            using var json = JsonDocument.Parse(await probeOutput);
            CheckMediaProbe(json.RootElement, probe.ExitCode, true);
            RequireSync(json.RootElement.GetProperty("streams").EnumerateArray().Where(stream => stream.GetProperty("codec_type").GetString() == "video")
                .All(stream => int.Parse(stream.GetProperty("nb_read_packets").GetString()!) >= 53990), "o vídeo longo foi truncado");
            RequireSync(result.SizeBytes > VideoPreviewWorker.MaximumPreviewBytes && receiver.Bytes == result.SizeBytes && sent == result.SizeBytes,
                "a prévia longa foi limitada a 128 MiB ou os bytes enviados divergiram");
            RequireSync(receiver.Indices == result.Upload.ChunkReferences!.Count && receiver.MaximumAttachments == 10 &&
                receiver.MaximumPartBytes == DiscordLimits.AttachmentBytes,
                "o fluxo perdeu chunks ou não preservou batches de dez anexos");
            RequireSync(directory.EnumerateFiles().Count() == 1 && !directory.EnumerateDirectories(".organessonium-preview-*").Any() && before.SequenceEqual(Directory.GetFiles(Path.GetTempPath(), "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).StartsWith("organessonium_video", StringComparison.Ordinal))), "o gerador gravou mídia intermediária");
            RequireSync(AppMemoryBudget.PeakCommittedBytes <= AppMemoryBudget.LimitBytes, "o fluxo longo excedeu 1 GB");
            Console.WriteLine($"Long preview: {result.SizeBytes / 1024d / 1024d:F1} MiB; RAM peak: {AppMemoryBudget.PeakCommittedBytes / 1024d / 1024d:F1} MiB.");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private sealed class StreamingPreviewReceiver(EncryptionService crypto, EncryptedPayloadContext context, Stream destination, Func<bool> generated) : HttpMessageHandler
    {
        public long Bytes;
        public int Indices, MaximumAttachments, MaximumPartBytes;
        private int _nextMessage;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly SortedDictionary<int, byte[]> _pending = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequireSync(generated(), "o envio começou antes de a geração terminar");
            if (request.Method == HttpMethod.Patch) return Reply(new { id = "thread" });
            var multipart = (MultipartFormDataContent)request.Content!;
            var files = multipart.Where(part => part.Headers.ContentDisposition?.FileName != null).ToArray();
            var message = new DiscordMessageDto { Id = Interlocked.Increment(ref _nextMessage).ToString(), Content = "" };
            foreach (var file in files)
            {
                var encrypted = await file.ReadAsByteArrayAsync(token);
                var index = checked((int)BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(10, 4))) - 1;
                var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(encrypted.AsSpan(14, 4)));
                var plaintext = crypto.DecryptBytes(encrypted, context.WithChunkCount(count).WithChunkIndex(index));
                await _gate.WaitAsync(token);
                try
                {
                    MaximumPartBytes = Math.Max(MaximumPartBytes, encrypted.Length);
                    MaximumAttachments = Math.Max(MaximumAttachments, files.Length);
                    _pending.Add(index, plaintext);
                    while (_pending.Remove(Indices, out var next))
                    {
                        try { await destination.WriteAsync(next, token); Bytes += next.Length; }
                        finally { CryptographicOperations.ZeroMemory(next); }
                        Indices++;
                    }
                }
                finally { _gate.Release(); }
                message.Attachments.Add(new DiscordAttachmentDto { Id = "attachment-" + index,
                    FileName = file.Headers.ContentDisposition!.FileName!.Trim('"'), Size = encrypted.Length, Url = "https://fixture/" + index });
                CryptographicOperations.ZeroMemory(encrypted);
            }
            message.Attachments.Reverse();
            return Reply(message);
        }
        private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions.Web), System.Text.Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing)
        {
            if (disposing) { foreach (var bytes in _pending.Values) CryptographicOperations.ZeroMemory(bytes); _gate.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
