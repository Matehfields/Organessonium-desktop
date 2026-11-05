using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> DownloadOfflineRecoveryHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        var bytes = Enumerable.Range(0, 49152).Select(i => (byte)(i % 251)).ToArray();
        var entry = (await h.UploadAsync([await h.PrepareAsync("network-download.bin", bytes)])).Single();
        using var handler = new DownloadOutageHandler(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(handler, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        MainWindow? window = null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, h.Directory.CreateSubdirectory("browser").FullName);
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true; browser.Bounds = new System.Drawing.Rectangle(0, 0, 1200, 800);
            var web = h.Directory.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                await using var output = File.Create(path); await input.CopyToAsync(output);
            }
            var view = browser.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html"); await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var control = new WebView2();
            var bridge = new WebMessageBridge(control, script => view.ExecuteScriptAsync(script));
            window = new MainWindow(h.Store);
            typeof(MainWindow).GetField("_bridge", flags)!.SetValue(window, bridge);
            await using var controller = new AppController(window, h.Store, bridge, _ => Task.CompletedTask,
                downloadClientFactory: () => new HttpClient(handler, false));
            SetFixtureSession(controller, api, h.Settings, registry);
            await bridge.SetSettingsAsync(h.Settings);
            await view.ExecuteScriptAsync("""
                window.onBridgeReady();window.__waiting=0;window.__networkOp='';window.__opsUpdate=window._opsUpdate;
                window._opsUpdate=(id,status,message)=>{window.__opsUpdate(id,status,message);if(message==='Aguardando rede...'){window.__waiting++;window.__networkOp=id}};
                """);
            handler.Enabled = true;
            var destination = h.Directory.CreateSubdirectory("download");
            await (Task)typeof(AppController).GetMethod("StartDownloadAsync", flags)!.Invoke(controller,
                [entry.ThreadId, entry.Name, destination, entry])!;
            async Task WaitUi(string script, TimeSpan timeout)
            {
                using var limit = new CancellationTokenSource(timeout);
                while (await view.ExecuteScriptAsync(script) != "true") await Task.Delay(50, limit.Token);
            }
            const string waiting = "window.__waiting>0&&document.getElementById('op-msg-'+window.__networkOp)?.textContent==='Aguardando rede...'&&document.getElementById('op-'+window.__networkOp)?.dataset.opStatus==='active'";
            await WaitUi(waiting, TimeSpan.FromSeconds(10));
            var transport = new HttpRequestException("Synthetic TLS EOF", new IOException("Synthetic connection closed"));
            await window.NotifyUnexpectedErrorAsync(new AggregateException(transport));
            await (Task)typeof(AppController).GetMethod("RunAsync", flags)!.Invoke(controller,
                [(Func<Task>)(() => Task.FromException(transport)), true])!;
            RequireSync(await view.ExecuteScriptAsync("document.getElementById('confirm-overlay').classList.contains('hidden')&&nonfatalMessages.length===0") == "true",
                "a falha temporária de rede abriu um diálogo global");
            await window.NotifyUnexpectedErrorAsync(new UnauthorizedAccessException("Synthetic permission failure"));
            RequireSync(await view.ExecuteScriptAsync("!document.getElementById('confirm-overlay').classList.contains('hidden')") == "true",
                "o filtro de rede também ocultou um erro de permissão");
            await view.ExecuteScriptAsync("document.getElementById('confirm-ok').click()");
            Console.WriteLine("NETWORK_RECOVERY waiting visible; TLS/REST failures preserved; checking idle timeout");
            await handler.IdleTimedOut.Task.WaitAsync(TimeSpan.FromSeconds(40));
            await WaitUi(waiting, TimeSpan.FromSeconds(5));
            RequireSync(await view.ExecuteScriptAsync("document.getElementById('confirm-overlay').classList.contains('hidden')") == "true",
                "o timeout do corpo abriu um diálogo em vez de aguardar rede");
            handler.Resume.TrySetResult();
            await WaitUi("document.getElementById('op-'+window.__networkOp)?.dataset.opStatus==='done'", TimeSpan.FromSeconds(15));
            RequireSync(handler.MetadataFailures == 1 && handler.PayloadFailures == 1 && handler.StalledBodies == 1 &&
                (await File.ReadAllBytesAsync(Path.Combine(destination.FullName, entry.Name))).SequenceEqual(bytes) &&
                h.Transfers.Repository.FailedDirectory.GetFiles().Length == 0,
                "o download não se recuperou das quedas de rede com bytes íntegros");
            RequireSync(!window.IsVisible && !IsWindowVisible(host), "o teste exibiu uma janela");
            Console.WriteLine("NETWORK_RECOVERY verified bytes; no network dialogs; normal errors preserved");
            return TestOutcome.Pass();
        }
        finally
        {
            handler.Resume.TrySetResult();
            if (window != null)
            {
                window.Closing -= typeof(MainWindow).GetMethod("OnClosing", flags, null,
                    [typeof(object), typeof(System.ComponentModel.CancelEventArgs)], null)!.CreateDelegate<System.ComponentModel.CancelEventHandler>(window);
                window.Close();
            }
            browser?.Close(); DestroyWindow(host);
        }
    });

    private sealed class DownloadOutageHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _next = new(inner, false);
        internal bool Enabled;
        internal int MetadataFailures, PayloadFailures, StalledBodies;
        internal readonly TaskCompletionSource IdleTimedOut = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!Enabled) return await _next.SendAsync(request, token);
            if (!request.RequestUri!.AbsolutePath.StartsWith("/attachments/", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get && Interlocked.CompareExchange(ref MetadataFailures, 1, 0) == 0)
                    throw new HttpRequestException("Synthetic TLS EOF", new IOException("Synthetic connection closed"));
                return await _next.SendAsync(request, token);
            }
            if (Interlocked.CompareExchange(ref PayloadFailures, 1, 0) == 0)
                throw new HttpRequestException("Synthetic TLS EOF", new IOException("Synthetic connection closed"));
            if (Interlocked.CompareExchange(ref StalledBodies, 1, 0) == 0)
            {
                var response = await _next.SendAsync(request, token);
                var length = response.Content.Headers.ContentLength;
                response.Content.Dispose(); response.Content = new StreamContent(new StalledDownloadStream(IdleTimedOut));
                response.Content.Headers.ContentLength = length;
                return response;
            }
            await Resume.Task.WaitAsync(token);
            return await _next.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _next.Dispose(); base.Dispose(disposing); }
    }

    private sealed class StalledDownloadStream(TaskCompletionSource timedOut) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
            catch (OperationCanceledException) { timedOut.TrySetResult(); throw; }
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
