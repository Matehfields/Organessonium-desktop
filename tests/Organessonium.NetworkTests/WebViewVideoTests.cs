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
                    var optimized = await File.ReadAllBytesAsync(prepared.File.FullName);
                    if (!Mp4MdatHash(original).SequenceEqual(Mp4MdatHash(optimized))) throw new InvalidDataException("A mídia real mudou durante faststart.");
                    if (Environment.GetEnvironmentVariable("ORGANESSONIUM_VIDEO_TEST_EXPORT_DIR") is string export && Directory.Exists(export))
                        File.Copy(prepared.File.FullName, Path.Combine(export, "faststart-audio-video.mp4"), true);
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
