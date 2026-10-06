using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> ForcedUploadKeepsSixteenWorkersAndReleasesBuffersAsync()
    {
        var settings = new AppSettings { UploadThreads = 4, ForceUploadParallelism = true };
        using var gate = new AdaptiveUploadGate(settings.EffectiveUploadThreads, adaptive: !settings.ForceUploadParallelism);
        using var memory = new UploadMemoryCoordinator(settings.EffectiveUploadThreads, forceParallelism: true);
        var encryption = CreateEncryptionService(out _);
        var context = new EncryptedPayloadContext("forced-upload", encryption.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Original, 0, 33);
        using var handler = new ForcedUploadHandler(encryption, context);
        using var discord = new DiscordApiClient("forced-upload-" + Guid.NewGuid().ToString("N"), new HttpClient(handler));
        var uploader = new ForumUploader(discord, "files");
        var buffers = new List<byte[]>();
        Task<PreparedUploadPart> PrepareAsync(int index)
        {
            if (!memory.TryRentBuffers(1024, out var lease) || lease == null)
                throw new InvalidOperationException("O modo fixo não disponibilizou o buffer do worker.");
            try
            {
                var plaintext = Enumerable.Repeat((byte)(index + 1), 1024).ToArray();
                var ciphertext = encryption.EncryptBytes(plaintext, context.WithChunkIndex(index));
                ciphertext.CopyTo(lease.Ciphertext.Span);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(ciphertext);
                MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)lease.Ciphertext, out var buffer);
                lock (buffers) buffers.Add(buffer.Array!);
                lease.ReleasePlaintext();
                return Task.FromResult(new PreparedUploadPart($"chunk-{index + 1}.bin", 1024 + EncryptionService.EnvelopeOverheadBytes, 1024, lease));
            }
            catch { lease.Dispose(); throw; }
        }
        if (!memory.SelectPlan(1024).UseMemory || memory.SelectPlan(1L << 40).MemorySafeWorkers != 16)
            return TestOutcome.Fail("o modo fixo ainda depende do tamanho do arquivo ou do plano adaptativo");
        var result = await uploader.UploadResumableGeneratedAsync("arquivo.bin", 33, PrepareAsync, "thread", "batch",
            new Dictionary<int, string> { [0] = "already-uploaded" },
            maxParallelParts: UploadConcurrencyPlanner.PartsPerFile(settings.EffectiveUploadThreads, 8, true),
            encryptedPayload: context, adaptiveUploadGate: gate);
        if (handler.MaximumConcurrent != 16 || gate.CurrentLimit != 16 || handler.UploadedIndices.Count != 32 ||
            result.ChunkMessageIds[0] != "already-uploaded" || result.ChunkMessageIds.Any(string.IsNullOrWhiteSpace) ||
            !handler.Retried || buffers.Any(buffer => buffer.Any(value => value != 0)))
            return TestOutcome.Fail($"upload fixo inválido: concorrência={handler.MaximumConcurrent}, limite={gate.CurrentLimit}");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try
        {
            await uploader.UploadResumableGeneratedAsync("cancelado.bin", 33, PrepareAsync, "thread", "batch",
                new Dictionary<int, string> { [0] = "already-uploaded" }, cancellationToken: cancellation.Token,
                maxParallelParts: 16, encryptedPayload: context, adaptiveUploadGate: gate);
            return TestOutcome.Fail("o upload não foi cancelado");
        }
        catch (OperationCanceledException) { }
        return buffers.All(buffer => buffer.All(value => value == 0))
            ? TestOutcome.Pass() : TestOutcome.Fail("o cancelamento reteve conteúdo nos buffers");
    }

    private sealed class ForcedUploadHandler(EncryptionService encryption, EncryptedPayloadContext context) : HttpMessageHandler
    {
        private int _active, _retry;
        public int MaximumConcurrent { get; private set; }
        public bool Retried => _retry > 1;
        public HashSet<int> UploadedIndices { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not MultipartFormDataContent multipart)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"thread\"}") };
            var file = multipart.First(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "files[0]");
            var name = file.Headers.ContentDisposition!.FileName!.Trim('"');
            var index = int.Parse(name.Split('-')[1].Split('.')[0]) - 1;
            if (index == 1 && Interlocked.Increment(ref _retry) == 1)
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent("{\"retry_after\":0.01,\"global\":false}", Encoding.UTF8, "application/json") };
            lock (UploadedIndices) MaximumConcurrent = Math.Max(MaximumConcurrent, ++_active);
            try
            {
                var payload = await file.ReadAsByteArrayAsync(cancellationToken);
                var plaintext = encryption.DecryptBytes(payload, context.WithChunkIndex(index));
                if (plaintext.Length != 1024 || plaintext.Any(value => value != (byte)(index + 1)))
                    throw new InvalidDataException("O attachment não preservou o chunk criptografado.");
                CryptographicOperations.ZeroMemory(payload); CryptographicOperations.ZeroMemory(plaintext);
                await Task.Delay(600, cancellationToken);
                lock (UploadedIndices) UploadedIndices.Add(index);
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { id = "message-" + index, content = "", attachments = Array.Empty<object>() })) };
            }
            finally { lock (UploadedIndices) _active--; }
        }
    }

    private static async Task<TestOutcome> WebViewForcedUploadSettingsAsync()
    {
        var result = new TaskCompletionSource<TestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var window = new Window { Width = 1280, Height = 800, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            var view = new WebView2(); window.Content = view;
            var dispatcher = Dispatcher.CurrentDispatcher;
            window.Loaded += async (_, _) =>
            {
                var root = Directory.CreateTempSubdirectory("organessonium-force16-ui-");
                try
                {
                    var store = new SettingsStore(Directory.CreateDirectory(Path.Combine(root.FullName, "Data")));
                    store.Save(new AppSettings { UploadThreads = 4, Theme = "dark", Language = "pt-BR", AnimationsEnabled = false });
                    var web = Directory.CreateDirectory(Path.Combine(root.FullName, "web"));
                    var assembly = typeof(AppRegistry).Assembly;
                    foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
                    {
                        var target = Path.Combine(web.FullName, name[4..].Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var source = assembly.GetManifestResourceStream(name)!; using var output = File.Create(target);
                        await source.CopyToAsync(output);
                    }
                    await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, Path.Combine(root.FullName, "WebView")));
                    view.CoreWebView2.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
                    var bridge = new WebMessageBridge(view);
                    var navigation = new TaskCompletionSource(); var saved = new TaskCompletionSource();
                    view.CoreWebView2.NavigationCompleted += (_, e) =>
                    { if (e.IsSuccess) navigation.TrySetResult(); else navigation.TrySetException(new IOException("Navegação falhou.")); };
                    // Persist the actual bridge cache in an isolated database, without changing the user's data-directory pointer.
                    view.CoreWebView2.WebMessageReceived += async (_, e) =>
                    {
                        try
                        {
                            using var message = JsonDocument.Parse(e.WebMessageAsJson);
                            if (message.RootElement.GetProperty("method").GetString() != "saveSettings") return;
                            var cache = JsonSerializer.Deserialize<string>(await view.CoreWebView2.ExecuteScriptAsync("window.bridge.getSettings()"))!;
                            var jsonOptions = new JsonSerializerOptions(JsonOptions.Web);
                            jsonOptions.Converters.Add(new JsonStringEnumConverter());
                            var updated = JsonSerializer.Deserialize<AppSettings>(cache, jsonOptions)!;
                            if (updated.ForceUploadParallelism != message.RootElement.GetProperty("args")[22].GetBoolean())
                                throw new InvalidDataException("O argumento de salvamento divergiu do cache da ponte.");
                            store.Save(updated); saved.TrySetResult();
                        }
                        catch (Exception ex) { saved.TrySetException(ex); }
                    };
                    view.CoreWebView2.Navigate("https://organessonium.local/index.html");
                    await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await bridge.SetSettingsAsync(store.Load());
                    await view.CoreWebView2.ExecuteScriptAsync("window.onBridgeReady();openSettings();");
                    await WaitForLibraryScriptAsync(view, "document.getElementById('settings-panel').classList.contains('open')");
                    await RequireLibraryScriptAsync(view, "document.getElementById('setting-force-upload').getAttribute('aria-pressed')==='false'&&document.getElementById('upload-thr-val').textContent==='4'", "O padrão adaptativo não apareceu.");
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('setting-force-upload').click()");
                    await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    if (!store.Load().ForceUploadParallelism || !store.Load().Clone().ForceUploadParallelism || store.Load().UploadThreads != 4)
                        throw new InvalidDataException("A opção não persistiu ou substituiu o valor adaptativo.");
                    navigation = new TaskCompletionSource(); view.CoreWebView2.Reload();
                    await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await bridge.SetSettingsAsync(store.Load());
                    await view.CoreWebView2.ExecuteScriptAsync("window.onBridgeReady();openSettings();");
                    await WaitForLibraryScriptAsync(view, "document.getElementById('settings-panel').classList.contains('open')");
                    await RequireLibraryScriptAsync(view, "document.getElementById('setting-force-upload').classList.contains('active')&&document.getElementById('setting-force-upload').getAttribute('aria-pressed')==='true'&&document.getElementById('setting-upload-threads').disabled&&document.getElementById('upload-thr-val').textContent==='16'", "O modo fixo não ficou visível após reabrir.");
                    var screenshot = Path.Combine(Path.GetTempPath(), "organessonium-force16-" + Guid.NewGuid().ToString("N") + ".png");
                    using (var output = File.Create(screenshot)) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                    Console.WriteLine("Upload settings screenshot: " + screenshot);
                    window.Width = 680; await Task.Delay(250);
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('setting-force-upload').scrollIntoView({block:'center'})");
                    await RequireLibraryScriptAsync(view, "(()=>{const r=document.getElementById('setting-force-upload').getBoundingClientRect();return r.width>80&&r.height>20&&r.left>=0&&r.right<=innerWidth&&r.top>=0&&r.bottom<=innerHeight})()", "O botão ficou fora da tela menor.");
                    saved = new TaskCompletionSource();
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('setting-force-upload').click()");
                    await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await RequireLibraryScriptAsync(view, "document.getElementById('setting-force-upload').getAttribute('aria-pressed')==='false'&&!document.getElementById('setting-upload-threads').disabled&&document.getElementById('upload-thr-val').textContent==='4'", "Desativar não restaurou o valor anterior.");
                    if (store.Load().ForceUploadParallelism || store.Load().EffectiveUploadThreads != 4)
                        throw new InvalidDataException("O modo adaptativo não foi restaurado no banco.");
                    result.TrySetResult(TestOutcome.Pass());
                }
                catch (Exception ex) { result.TrySetResult(TestOutcome.Fail(ex.ToString())); }
                finally
                {
                    view.Dispose(); window.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    SqliteConnection.ClearAllPools(); try { root.Delete(true); } catch { }
                }
            };
            window.Show(); Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return await result.Task.WaitAsync(TimeSpan.FromMinutes(1));
    }
}
