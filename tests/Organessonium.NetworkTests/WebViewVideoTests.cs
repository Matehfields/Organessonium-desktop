using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> VideoPlayerControlsHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var root = Directory.CreateTempSubdirectory("organessonium-video-controls-");
        var hwnd = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1100, 700, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? controller = null;
        try
        {
            var assets = root.CreateSubdirectory("web");
            var assembly = typeof(PreviewResourceRegistry).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var target = Path.Combine(assets.FullName, name[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = assembly.GetManifestResourceStream(name)!;
                await using var output = File.Create(target);
                await input.CopyToAsync(output);
            }
            using var resources = new PreviewResourceRegistry();
            var environment = await CoreWebView2Environment.CreateAsync(null, root.CreateSubdirectory("profile").FullName,
                new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
            controller = await environment.CreateCoreWebView2ControllerAsync(hwnd);
            controller.IsVisible = true;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1100, 700);
            var core = controller.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping("organessonium.local", assets.FullName, CoreWebView2HostResourceAccessKind.Allow);
            await core.AddScriptToExecuteOnDocumentCreatedAsync("window._playerErrors=[];window.addEventListener('error',e=>_playerErrors.push(e.message||e.target.src),true);");
            using var adapter = new WebView2PreviewAdapter(core, resources);
            adapter.Attach();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) ready.TrySetResult();
                else ready.TrySetException(new IOException("Falha ao abrir a página local do player."));
            };
            core.Navigate("https://organessonium.local/index.html");
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            async Task CheckAsync(string script, string message, int seconds = 10)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
                var actual = "";
                while (!timeout.IsCancellationRequested)
                {
                    actual = await core.ExecuteScriptAsync(script);
                    if (actual == "true") return;
                    await Task.Delay(50);
                }
                var state = await core.ExecuteScriptAsync("(()=>{const v=document.querySelector('video');return {errors:_playerErrors,player:!!customElements.get('media-play-button')," +
                    "mounted:!!v,ready:v?.readyState,network:v?.networkState,hasSource:!!v?.getAttribute('src')," +
                    "mediaError:v?.error?.message,frame:!!document.querySelector('.vp-frame')};})()");
                throw new InvalidOperationException(message + " (" + actual + "; " + state + ")");
            }
            var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "video-controls-fixture.mp4"));
            await using var session = new VideoStreamSession(new FixtureVideoChunks(bytes), 0);
            await using var alternate = new VideoStreamSession(new FixtureVideoChunks(bytes), 0);
            var url = resources.RegisterVideo(session, "video/mp4");
            var alternateUrl = resources.RegisterVideo(alternate, "video/mp4");
            var data = JsonSerializer.Serialize(new { src = url, quality = "full", qualities = new[] { "full", "480p" },
                cacheUrl = PreviewResourceRegistry.CachedVideoUrl(url), streamBytes = bytes.Length });
            core.WebMessageReceived += (_, message) =>
            {
                using var command = JsonDocument.Parse(message.WebMessageAsJson);
                var method = command.RootElement.GetProperty("method").GetString();
                if (method == "videoPlayerStats")
                    _ = core.ExecuteScriptAsync("window._onVideoPlayerStats('fixture','full'," +
                        JsonSerializer.Serialize(session.Snapshot(), JsonOptions.Web) + ")");
                if (method == "requestVideoQuality")
                {
                    var qualitySource = JsonSerializer.Serialize(new object[] { "fixture", "480p", alternateUrl, "video/mp4",
                        PreviewResourceRegistry.CachedVideoUrl(alternateUrl), bytes.Length });
                    _ = core.ExecuteScriptAsync("window._onVideoQualityReady(..." + qualitySource + ")");
                }
            };
            await core.ExecuteScriptAsync("window.__organessoniumSettings={theme:'dark'};onBridgeReady();previewAppRef={threadId:'fixture',name:'Teste.mp4'};" +
                "previewOverlay.classList.remove('hidden');_onPreviewReady('video','Teste.mp4'," + JsonSerializer.Serialize(data) + ")");
            await CheckAsync("!!customElements.get('media-play-button') && document.querySelector('video')?.readyState>=2",
                "Video.js ou o H.264 do WebView2 não ficou pronto");
            const string normalLayout = "(()=>{const body=document.getElementById('preview-body');" +
                "const frame=body.querySelector('.vp-frame').getBoundingClientRect();" +
                "const play=body.querySelector('[data-video-play]').getBoundingClientRect();" +
                "return frame.width>=body.clientWidth*.75 && frame.height>150 && play.left>=frame.left && play.right<=frame.right;})()";
            await CheckAsync(normalLayout, "a coluna do player recolheu na janela normal");
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 600, 480);
            await CheckAsync("innerWidth<=600 && " + normalLayout, "o player perdeu a largura ao reduzir a janela");
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1100, 700);
            await core.ExecuteScriptAsync("window._videoKey = key => document.querySelector('.vp-frame').dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true}));_videoKey('k');");
            await CheckAsync("document.querySelector('video').currentTime>.25 && document.querySelector('.vp-buffer i')!==null", "reproduzir pelo Video.js ou exibir o buffer falhou");
            await core.ExecuteScriptAsync("_videoKey('k');");
            await CheckAsync("document.querySelector('video').paused && document.querySelector('.vp-spinner').hidden", "pausa intencional manteve o indicador");
            await core.ExecuteScriptAsync("_videoKey('5');");
            await CheckAsync("!document.querySelector('video').seeking && Math.abs(document.querySelector('video').currentTime-4)<.2", "seek proporcional falhou");
            await core.ExecuteScriptAsync("_videoKey('.');");
            await CheckAsync("!document.querySelector('video').seeking && document.querySelector('video').currentTime>4.02 && document.querySelector('video').currentTime<4.07",
                "avanço por timestamp no vídeo de 25 fps falhou");
            await core.ExecuteScriptAsync("_videoKey(',');");
            await CheckAsync("!document.querySelector('video').seeking && Math.abs(document.querySelector('video').currentTime-4)<.025", "retrocesso de quadro falhou");
            await core.ExecuteScriptAsync("_videoKey('>');_videoKey('m');_videoKey('i');");
            await CheckAsync("previewOverlay.classList.contains('video-docked') && document.querySelector('video').muted && document.querySelector('video').playbackRate===1.25",
                "mini player, silêncio ou velocidade falharam");
            await core.ExecuteScriptAsync("_videoKey('i');_videoKey('?');");
            await CheckAsync("!previewOverlay.classList.contains('video-docked') && document.querySelector('.vp-panel').textContent.includes('Atalhos do reprodutor')",
                "restaurar mini player ou abrir ajuda falhou");
            await core.ExecuteScriptAsync("_videoKey('Escape');_videoKey('f');");
            await CheckAsync("(()=>{const frame=document.querySelector('.vp-frame');const box=frame.getBoundingClientRect();" +
                "return frame.classList.contains('is-fullscreen') && box.left<=1 && box.top<=1 && " +
                "Math.abs(box.width-innerWidth)<2 && Math.abs(box.height-innerHeight)<2;})()", "a tela cheia deixou margens ou não abriu");
            await core.ExecuteScriptAsync("_videoKey('Escape');");
            await CheckAsync("!document.querySelector('.vp-frame').classList.contains('is-fullscreen')", "o player não saiu da tela cheia");
            await core.ExecuteScriptAsync("document.querySelector('video').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true}));" +
                "document.querySelector('.vp-panel [data-action=stats]').click();");
            await CheckAsync("!document.querySelector('.vp-stats').hidden && document.querySelector('.vp-stats dl').textContent.includes('256 × 144') && document.querySelector('.vp-stats dl').textContent.includes('H.264')", "estatísticas não exibiram os dados da mídia");
            await core.ExecuteScriptAsync("""
                (()=>{const input=document.querySelector('.vp-frame input[type=file]');const files=new DataTransfer();
                files.items.add(new File(['1\n00:00:01,000 --> 00:00:07,000\nLegenda de teste\n'],'teste.srt'));
                input.files=files.files; input.dispatchEvent(new Event('change'));})()
                """);
            await CheckAsync("Array.from(document.querySelector('video').textTracks).some(t=>t.mode==='showing' && t.cues?.[0]?.text==='Legenda de teste')",
                "legenda SRT não foi convertida ou exibida");
            await core.ExecuteScriptAsync("window._originalPlayer = document.querySelector('video');" +
                "document.querySelector('.vp-controls [data-action=settings]').click();" +
                "document.querySelector('.vp-panel [data-action=quality]').click();" +
                "document.querySelector('.vp-panel [data-action=\"quality:480p\"]').click();");
            await CheckAsync("(()=>{const v=document.querySelector('video');return v===_originalPlayer && v.dataset.quality==='480p' && !v.seeking && " +
                "v.readyState>=2 && Math.abs(v.currentTime-4)<.06 && v.paused && v.muted && v.playbackRate===1.25 && " +
                "Array.from(v.textTracks).some(t=>t.mode==='showing' && t.cues?.[0]?.text==='Legenda de teste');})()",
                "a troca de qualidade perdeu posição, estado ou legenda");
            await core.ExecuteScriptAsync("closePreview();");
            resources.RemoveUrl(url);
            resources.RemoveUrl(alternateUrl);
            await session.DisposeAsync();
            RequireSync(!IsWindowVisible(hwnd) && session.CachedBytes == 0, "o teste abriu janela ou reteve cache após fechar");
            return TestOutcome.Pass();
        }
        finally
        {
            controller?.Close();
            DestroyWindow(hwnd);
        }
    });

    // Uses the actual embedded player and WebView2 adapter, not a browser mock or a local server.
    private static async Task<TestOutcome> WebViewVirtualVideoPlaybackAsync()
    {
        var complete = new TaskCompletionSource<TestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var window = new Window { Title = "Organessônium: validação de streaming", Width = 1100, Height = 760 };
            var view = new WebView2();
            window.Content = view;
            var dispatcher = Dispatcher.CurrentDispatcher;
            window.Loaded += async (_, _) =>
            {
                var root = Directory.CreateTempSubdirectory("organessonium-webview-video-test-");
                try
                {
                    var fixture = Environment.GetEnvironmentVariable("ORGANESSONIUM_VIDEO_TEST_FILE");
                    if (string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture))
                    {
                        fixture = Path.Combine(root.FullName, "legacy.mp4");
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                        await File.WriteAllBytesAsync(fixture, await http.GetByteArrayAsync("https://media.w3.org/2010/05/sintel/trailer.mp4"));
                    }
                    var assembly = typeof(PreviewResourceRegistry).Assembly;
                    var webRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "web"));
                    foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal)))
                    {
                        var target = Path.Combine(webRoot.FullName, name[4..].Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var source = assembly.GetManifestResourceStream(name)!;
                        using var output = File.Create(target);
                        await source.CopyToAsync(output);
                    }
                    using var prepared = await Mp4FastStart.PrepareAsync(new FileInfo(fixture));
                    if (!prepared.Applied) throw new InvalidDataException("A fixture real precisa ter moov no final para validar o remux.");
                    var original = await File.ReadAllBytesAsync(fixture);
                    using var optimizedStream = new MemoryStream();
                    await using (var input = prepared.OpenRead()) await input.CopyToAsync(optimizedStream);
                    var optimized = optimizedStream.ToArray();
                    if (!Mp4MdatHash(original).SequenceEqual(Mp4MdatHash(optimized))) throw new InvalidDataException("A mídia real mudou durante faststart.");
                    if (Environment.GetEnvironmentVariable("ORGANESSONIUM_VIDEO_TEST_EXPORT_DIR") is string export && Directory.Exists(export))
                        await File.WriteAllBytesAsync(Path.Combine(export, "faststart-audio-video.mp4"), optimized);
                    using var registry = new PreviewResourceRegistry();
                    var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(root.FullName, "WebView"));
                    await view.EnsureCoreWebView2Async(env);
                    view.CoreWebView2.SetVirtualHostNameToFolderMapping("organessonium.local", webRoot.FullName, CoreWebView2HostResourceAccessKind.Allow);
                    using var adapter = new WebView2PreviewAdapter(view.CoreWebView2, registry);
                    adapter.Attach();
                    var navigation = new TaskCompletionSource();
                    view.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) navigation.TrySetResult(); else navigation.TrySetException(new IOException("Navegação de teste falhou.")); };
                    view.CoreWebView2.Navigate("https://organessonium.local/index.html");
                    await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await view.CoreWebView2.ExecuteScriptAsync("window.__organessoniumSettings={theme:'dark',videoPrefetchChunks:1};window.onBridgeReady();");

                    var oldDuration = 0d;
                    foreach (var bytes in new[] { original, optimized })
                    {
                        var provider = new FixtureVideoChunks(bytes);
                        await using var session = new VideoStreamSession(provider, 1);
                        await session.WarmUpAsync();
                        var url = registry.RegisterVideo(session, "video/mp4");
                        view.CoreWebView2.WebMessageReceived += (_, message) =>
                        {
                            using var parsed = JsonDocument.Parse(message.WebMessageAsJson);
                            if (parsed.RootElement.GetProperty("method").GetString() == "videoPlayback")
                            {
                                var args = parsed.RootElement.GetProperty("args");
                                session.SetPlaying(args[4].GetBoolean(), args[6].GetBoolean());
                            }
                        };
                        var info = JsonSerializer.Serialize(new { src = url, mime = "video/mp4", quality = "full", qualities = new[] { "full" }, fileId = "qa-file", resumeSeconds = 0 });
                        await view.CoreWebView2.ExecuteScriptAsync($"previewAppRef={{threadId:'qa-thread',fileId:'qa-file'}};previewOverlay.classList.remove('hidden');window._onPreviewReady('video','Teste de streaming.mp4',{JsonSerializer.Serialize(info)});");
                        await WaitForVideoScriptAsync(view, "(()=>{const v=document.querySelector('video');return !!v&&v.readyState>=1&&v.videoWidth>0&&!v.error})()", 30);
                        var duration = JsonSerializer.Deserialize<double>(await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('video').duration"));
                        if (oldDuration > 0 && Math.Abs(oldDuration - duration) > 0.01) throw new InvalidDataException("Duração mudou após faststart.");
                        oldDuration = duration;
                        // Not all chunks may be eagerly downloaded just to read metadata.
                        if (provider.Downloaded.Count >= provider.Index.Chunks.Count) throw new InvalidDataException("Abrir a prévia baixou o arquivo inteiro.");
                        await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('video').play().catch(()=>{});");
                        await WaitForVideoScriptAsync(view, "document.querySelector('video').currentTime>0.5", 20);
                        var seek = duration > 127 ? 120 : duration * 0.7;
                        await view.CoreWebView2.ExecuteScriptAsync($"document.querySelector('video').currentTime={seek.ToString(System.Globalization.CultureInfo.InvariantCulture)};");
                        await WaitForVideoScriptAsync(view, $"(()=>{{const v=document.querySelector('video');return !v.seeking&&!v.paused&&v.currentTime>{seek.ToString(System.Globalization.CultureInfo.InvariantCulture)}&&v.getVideoPlaybackQuality().totalVideoFrames>0}})()", 30);
                        await Task.Delay(1200);
                        await view.CoreWebView2.ExecuteScriptAsync("stopVideoPlayback();");
                        registry.RemoveUrl(url);
                        await session.DisposeAsync();
                        if (session.CachedBytes != 0) throw new InvalidDataException("RAM não foi liberada.");
                    }

                    // Resume is applied by the same JavaScript hook used by AppController.
                    await using (var session = new VideoStreamSession(new FixtureVideoChunks(optimized), 0))
                    {
                        var url = registry.RegisterVideo(session, "video/mp4");
                        var resume = oldDuration > 127 ? 120 : oldDuration * 0.7;
                        var info = JsonSerializer.Serialize(new { src = url, mime = "video/mp4", quality = "full", qualities = new[] { "full" }, fileId = "qa-file", resumeSeconds = resume, savedDuration = oldDuration });
                        await view.CoreWebView2.ExecuteScriptAsync($"window._onPreviewReady('video','Retomada.mp4',{JsonSerializer.Serialize(info)});");
                        await WaitForVideoScriptAsync(view, $"Math.abs(document.querySelector('video').currentTime-{resume.ToString(System.Globalization.CultureInfo.InvariantCulture)})<0.2&&!document.querySelector('video').seeking", 30);
                        await view.CoreWebView2.ExecuteScriptAsync("openSettings();");
                        await WaitForVideoScriptAsync(view, "document.getElementById('setting-video-prefetch').value==='1'", 5);
                        await view.CoreWebView2.ExecuteScriptAsync("const q=document.getElementById('setting-video-prefetch');q.value='0';q.dispatchEvent(new Event('input',{bubbles:true}));");
                        if (await view.CoreWebView2.ExecuteScriptAsync("settings.videoPrefetchChunks") != "0") throw new InvalidDataException("Configuração não chegou à interface.");
                        await view.CoreWebView2.ExecuteScriptAsync("closeSettings();document.querySelector('video').play().catch(()=>{});");
                        // Optional observation interval for a real screenshot without changing production behavior.
                        if (int.TryParse(Environment.GetEnvironmentVariable("ORGANESSONIUM_VIDEO_TEST_OBSERVE_SECONDS"), out var seconds))
                            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 45)));
                        await view.CoreWebView2.ExecuteScriptAsync("stopVideoPlayback();");
                        registry.RemoveUrl(url);
                    }
                    complete.TrySetResult(TestOutcome.Pass());
                }
                catch (Exception ex) { complete.TrySetResult(TestOutcome.Fail(ex.Message)); }
                finally
                {
                    view.Dispose();
                    window.Close();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    // The runtime can briefly retain profile files; no media is placed there by the core.
                    try { root.Delete(true); } catch { }
                }
            };
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await complete.Task.WaitAsync(TimeSpan.FromMinutes(4));
    }

    private static async Task WaitForVideoScriptAsync(WebView2 view, string condition, int seconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (await view.CoreWebView2.ExecuteScriptAsync(condition) != "true")
        {
            if (timeout.IsCancellationRequested)
            {
                var details = await view.CoreWebView2.ExecuteScriptAsync("(()=>{const v=document.querySelector('video');return JSON.stringify({ready:v?.readyState,time:v?.currentTime,duration:v?.duration,error:v?.error?.code})})()");
                throw new TimeoutException("Player não atingiu o estado esperado: " + details);
            }
            await Task.Delay(50);
        }
    }
    private static byte[] Mp4MdatHash(byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var position = 0; position < bytes.Length;)
        {
            var size32 = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            var header = size32 == 1 ? 16 : 8;
            var size = size32 == 0 ? bytes.Length - position : size32 == 1
                ? checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(position + 8))) : checked((int)size32);
            if (System.Text.Encoding.ASCII.GetString(bytes, position + 4, 4) == "mdat") hash.AppendData(bytes.AsSpan(position + header, size - header));
            position += size;
        }
        return hash.GetHashAndReset();
    }
    private sealed class FixtureVideoChunks : IVideoChunkProvider
    {
        private byte[]? _bytes;
        public VideoChunkIndex Index { get; }
        public System.Collections.Concurrent.ConcurrentDictionary<int, int> Downloaded { get; } = new();
        public FixtureVideoChunks(byte[] bytes)
        {
            _bytes = bytes;
            var lengths = new List<long>();
            // Intentionally variable old-style sizes, including a smaller final part.
            for (long start = 0; start < bytes.Length;) { var length = Math.Min(bytes.Length - start, lengths.Count % 2 == 0 ? 512 * 1024 : 768 * 1024); lengths.Add(length); start += length; }
            Index = new VideoChunkIndex(lengths);
        }
        public async Task<byte[]> DownloadAsync(int index, CancellationToken cancellationToken)
        {
            Downloaded.AddOrUpdate(index, 1, (_, count) => count + 1);
            await Task.Delay(80, cancellationToken);
            var chunk = Index.Chunks[index];
            return (_bytes ?? throw new ObjectDisposedException(nameof(FixtureVideoChunks))).AsSpan((int)chunk.Start, chunk.Length).ToArray();
        }
        public void Dispose() => _bytes = null;
    }
}
