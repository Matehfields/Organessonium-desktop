using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> SerialFreshnessAndMetadataCacheAsync()
    {
        using var h = new SharedHarness();
        var initial = (await h.UploadAsync([await h.PrepareAsync("version.bin", new byte[48])])).Single();
        var otherStore = new SettingsStore(h.Directory.CreateSubdirectory("other-device")); otherStore.Save(h.Settings);
        var other = new AppRegistry(h.Api, otherStore, h.Settings); await other.RefreshMappingsAsync();
        RequireSync((await other.GetLibraryStatisticsAsync("root"))?.TotalBytes == 48, "total inicial inválido");
        var current = (await h.UploadAsync([await h.PrepareAsync("version.bin", new byte[96], initial.FileId)], publish: false)).Single();
        FileVersioning.PreservePrevious(initial, current, TransferState.Now());
        await h.Registry.UpdateEntryAsync(current, expectedPayload: MetadataService.CreateFileMetadata(initial));
        await other.RefreshMappingsAsync();
        var statistics = await other.GetLibraryStatisticsAsync("root");
        RequireSync(statistics is { TotalBytes: 144, FileCount: 1 } && otherStore.SerialCacheDirectory.GetFiles("*.bin").Length == 1,
            $"o serial antigo continuou em memória/disco ou o total ignorou uma versão: bytes={statistics?.TotalBytes}, files={statistics?.FileCount}, caches={otherStore.SerialCacheDirectory.GetFiles("*.bin").Length}");
        var cold = (await other.LoadCachedFolderAsync("root", "/")).Single();
        var missing = h.Server.MissingMessageReads;
        var full = await other.LoadFileDetailsAsync(cold);
        var rootThread = otherStore.LoadFolderIndexMappings()["root"].ThreadId;
        var reads = h.Server.MessageReads.GetValueOrDefault((rootThread, full.MetadataMessageId));
        await other.LoadFileDetailsAsync(cold);
        RequireSync(full.LogicalChunkCount == 1 && h.Server.MissingMessageReads == missing &&
            h.Server.MessageReads.GetValueOrDefault((rootThread, full.MetadataMessageId)) == reads,
            "a hidratação consultou names indevidamente ou não reutilizou o metadata autenticado do cache");
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => other.SynchronizeFolderSerialsAsync(["root"])));
        RequireSync(otherStore.SerialCacheDirectory.GetFiles("*.bin").Length == 1 && otherStore.SerialCacheDirectory.GetFiles("*.tmp").Length == 0 &&
            (await other.GetLibraryStatisticsAsync("root"))?.TotalBytes == 144,
            "gravações concorrentes do serial colidiram ou deixaram temporários");
        return TestOutcome.Pass();
    }

    private static Task<TestOutcome> VideoBulkLookupAndImmediatePlayerAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        const int chunkBytes = 1024;
        var bytes = Enumerable.Range(0, chunkBytes * 120 + 437).Select(index => (byte)(index % 251)).ToArray();
        var source = new FileInfo(Path.Combine(h.Directory.FullName, "video.mov")); await File.WriteAllBytesAsync(source.FullName, bytes);
        var checksums = await FileChecksums.CalculateAsync(source.OpenRead, bytes.Length, chunkBytes, default);
        var encryption = h.Encryption.CreateFileEncryption("standard");
        var context = new EncryptedPayloadContext("video-start", encryption, EncryptedPayloadRoles.Original, 0, 121) { Checksums = checksums };
        using var memory = new UploadMemoryCoordinator(4);
        var result = await new ForumUploader(h.Api, "files").UploadResumableGeneratedAsync(source.Name, 121,
            index => memory.PreparePartAsync(h.Encryption, source, source.Length, chunkBytes, context.WithChunkIndex(index), default, deferBuffers: true),
            "", "", new Dictionary<int, string>(), encryptedPayload: context, useAttachmentReferences: true, streamPreparedParts: true,
            maxPreparedPartBytes: chunkBytes + EncryptionService.EnvelopeOverheadBytes);
        var entry = AppEntry.File(source.Name, result.ThreadId, TransferState.Now(), source.Length, "/");
        entry.FileId = context.FileId; entry.Encryption = encryption; entry.Checksums = checksums;
        entry.ChunkReferences = result.ChunkReferences!.ToList();
        h.Server.MessagePages.Clear(); h.Server.MessageReads.Clear();
        using (var provider = await h.Downloader.OpenVideoChunksAsync(entry, false))
        {
            RequireSync(h.Server.MessagePages.GetValueOrDefault(entry.ThreadId) == 1 &&
                !h.Server.MessageReads.Keys.Any(key => key.Thread == entry.ThreadId) &&
                (await provider.DownloadAsync(120, default)).SequenceEqual(bytes.Skip(chunkBytes * 120)),
                "a abertura não agrupou as mensagens ou perdeu os tamanhos/IDs exatos dos chunks");
        }
        using var web = new WebView2();
        await using var controller = new AppController(new System.Windows.Window(), h.Store, new WebMessageBridge(web), _ => Task.CompletedTask);
        SetFixtureSession(controller, h.Api, h.Settings, h.Registry);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var started = (Task<string>)typeof(AppController).GetMethod("GetOrCreateVideoStreamUrlAsync", flags)!.Invoke(controller, [entry, false, CancellationToken.None])!;
        var url = await started.WaitAsync(TimeSpan.FromSeconds(3));
        RequireSync(url.StartsWith("https://media.organessonium.local/preview/", StringComparison.Ordinal),
            "o player aguardou o download inicial antes de receber a URL progressiva");
        return TestOutcome.Pass();
    });

    private static void SetFixtureSession(AppController controller, DiscordApiClient api, AppSettings settings, AppRegistry registry)
    {
        var session = new DiscordSession();
        typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, settings);
        controller.SetSession(session);
        typeof(AppController).GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, registry);
    }

    private static Task<TestOutcome> ResumedTransfersAndNetworkHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        h.Settings.AutoResumeTransfers = true; h.Store.Save(h.Settings);
        var downloadBytes = Enumerable.Range(0, 32768).Select(index => (byte)(index % 251)).ToArray();
        var original = await h.PrepareAsync("saved-download.bin", downloadBytes);
        var entry = (await h.UploadAsync([original])).Single();
        // A legacy plaintext part is migratable only with independently verifiable integrity.
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(downloadBytes));
        entry.Checksums = new PayloadChecksums { ChunkSizeBytes = downloadBytes.Length, Chunks = [digest], Sha256 = digest };
        await h.Transfers.CompleteAsync(original, false);
        var destination = h.Directory.CreateSubdirectory("download");
        var download = await h.Transfers.CreateDownloadAsync(entry, destination);
        var part = new FileInfo(Path.Combine(h.Directory.FullName, "verified-download-part.bin"));
        await File.WriteAllBytesAsync(part.FullName, downloadBytes);
        await h.Transfers.MarkDownloadChunkAsync(download, 0, part.FullName, part.Length);
        var acknowledged = await h.PrepareAsync("acknowledged.bin", new byte[63]);
        await h.UploadAsync([acknowledged], publish: false);
        var pending = await h.PrepareAsync("pending.bin", new byte[65536]);
        var existingMessages = h.Server.Messages(acknowledged.ThreadId).Count;
        using var timeout = new ResumeTimeoutHandler(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(timeout, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, h.Directory.CreateSubdirectory("browser").FullName);
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true; browser.Bounds = new System.Drawing.Rectangle(0, 0, 1200, 800);
            var resources = h.Directory.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(resources.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            var view = browser.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", resources.FullName, CoreWebView2HostResourceAccessKind.Allow);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var web = new WebView2();
            var bridge = new WebMessageBridge(web, script => view.ExecuteScriptAsync(script));
            await using var controller = new AppController(new System.Windows.Window(), h.Store, bridge, _ => Task.CompletedTask);
            SetFixtureSession(controller, api, h.Settings, registry);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AppController).GetField("_currentPath", flags)!.SetValue(controller, "/Elsewhere");
            await bridge.SetSettingsAsync(h.Settings);
            await view.ExecuteScriptAsync("""
                window.onBridgeReady();window.__errors=[];window.addEventListener('error',e=>window.__errors.push(e.message));
                window.__metrics=[];window.__realMetrics=window._opsTransferMetrics;
                window._opsTransferMetrics=(...args)=>{window.__realMetrics(...args);const item=document.getElementById('op-'+args[0]);window.__metrics.push({args,ready:item?.dataset.metricReady,speed:item?.dataset.metricSpeed,batch:item?.classList.contains('op-batch')})};
                """);
            var resumed = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.WebMessageReceived += (_, args) =>
            {
                using var message = JsonDocument.Parse(args.WebMessageAsJson);
                if (message.RootElement.GetProperty("method").GetString() == "resumeTransfers")
                {
                    var method = typeof(AppController).GetMethod("ResumeInterruptedTransfersAsync", flags, null, Type.EmptyTypes, null)!;
                    resumed.TrySetResult((Task)method.Invoke(controller, null)!);
                }
            };
            await (Task)typeof(AppController).GetMethod("NotifyInterruptedTransfersAsync", flags)!.Invoke(controller, null)!;
            RequireSync(await view.ExecuteScriptAsync("!document.getElementById('confirm-overlay').classList.contains('hidden')") == "true" &&
                !resumed.Task.IsCompleted, "a retomada começou sem confirmação ou a pergunta não apareceu");
            await view.ExecuteScriptAsync("document.getElementById('confirm-ok').click()");
            await (await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5))).WaitAsync(TimeSpan.FromSeconds(30));
            RequireSync((await h.Transfers.RecoverableAsync()).Count == 0 && h.Transfers.Repository.FailedDirectory.GetFiles().Length == 0 &&
                (await File.ReadAllBytesAsync(Path.Combine(destination.FullName, entry.Name))).SequenceEqual(downloadBytes),
                "a retomada não concluiu o upload/download salvo fora da pasta atual");
            var root = await registry.RefreshFolderAsync("root", "/");
            RequireSync(root.Any(item => item.FileId == pending.FileId) && root.Any(item => item.FileId == acknowledged.FileId) &&
                h.Server.Messages(acknowledged.ThreadId).Count == existingMessages && timeout.PayloadTimeouts == 1 &&
                (string)typeof(AppController).GetField("_currentPath", flags)!.GetValue(controller)! == "/Elsewhere",
                "a retomada duplicou payload confirmado, perdeu a navegação ou falhou após timeout");
            RequireSync(await view.ExecuteScriptAsync("window.__errors.length===0&&window.__metrics.some(m=>m.args[1]==='upload'&&m.batch&&m.ready==='true'&&m.args[2]===2)&&window.__metrics.some(m=>m.args[1]==='download'&&m.ready==='true'&&m.args[2]===1)") == "true",
                "a retomada não usou os cards e as métricas normais da interface");
            var downloader = new ForumDownloader(api, h.Settings, h.Encryption, () => new HttpClient(timeout, false), new StorageReadContext(registry.SharedStorage));
            await bridge.CallAsync("_opsAdd", "download-format", "download", "formatted.bin");
            await view.ExecuteScriptAsync("""
                window.__byteTexts=[];window.__updateBeforeBytes=window._opsUpdate;
                window._opsUpdate=(id,status,msg)=>{window.__updateBeforeBytes(id,status,msg);if(id==='download-format'&&msg.endsWith(' baixados'))window.__byteTexts.push(document.getElementById('op-msg-'+id).textContent)};
                """);
            var byteCallbacks = new System.Collections.Concurrent.ConcurrentQueue<Task>();
            var formatter = typeof(AppController).GetMethod("FormatTransferBytes", BindingFlags.Static | BindingFlags.NonPublic)!;
            string FormatDownloadBytes(long bytes) => (string)formatter.Invoke(null, [bytes])!;
            var recovered = await downloader.DownloadAndAssembleAsync(entry, h.Directory.CreateSubdirectory("timeout-download"),
                message => byteCallbacks.Enqueue(bridge.CallAsync("_opsUpdate", "download-format", "active", message)),
                onDetailedProgress: progress => byteCallbacks.Enqueue(bridge.CallAsync("_opsProgress", "download-format", progress.Percent,
                    progress.Assembling ? progress.Message : $"{FormatDownloadBytes(progress.DownloadedBytes)} / {FormatDownloadBytes(progress.TotalBytes)} baixados")));
            await Task.WhenAll(byteCallbacks);
            RequireSync(await view.ExecuteScriptAsync("window._opsUpdate=window.__updateBeforeBytes;window.__byteTexts.length>=2&&window.__byteTexts.every(text=>/^(?:\\d+ B|\\d+\\.\\d (?:KB|MB|GB|TB)) \\/ (?:\\d+ B|\\d+\\.\\d (?:KB|MB|GB|TB)) baixados$/.test(text))") == "true",
                "o painel alternou separador ou quantidade de casas decimais entre callbacks de download");
            RequireSync(timeout.DownloadTimeouts == 1 && (await File.ReadAllBytesAsync(recovered.FullName)).SequenceEqual(downloadBytes),
                "o timeout de download foi confundido com cancelamento ou corrompeu os bytes");
            using (var incremental = new DirectDownloadFixture(lengths: Enumerable.Repeat(1024, 120).ToArray()))
            {
                var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                incremental.BeforeMessage = (index, token) => index < 10 ? Task.CompletedTask : tail.Task.WaitAsync(token);
                var callbacks = new System.Collections.Concurrent.ConcurrentQueue<Task>();
                await bridge.CallAsync("_opsAdd", "incremental-ui", "download", incremental.Entry.Name);
                var running = incremental.Run(progress: progress => callbacks.Enqueue(bridge.CallAsync("_opsProgress",
                    "incremental-ui", progress.Percent, progress.Assembling ? progress.Message :
                    $"{FormatDownloadBytes(progress.DownloadedBytes)} / {FormatDownloadBytes(progress.TotalBytes)} baixados")));
                try
                {
                    await WaitForVideoConditionAsync(() => incremental.Reads.Count == 10);
                    await Task.WhenAll(callbacks.ToArray());
                    RequireSync(!running.IsCompleted && await view.ExecuteScriptAsync(
                        "document.getElementById('op-incremental-ui').dataset.opStatus==='active'&&document.getElementById('op-msg-incremental-ui').textContent.endsWith(' baixados')&&parseFloat(document.getElementById('op-progress-incremental-ui').style.width)>0") == "true",
                        "o painel não mostrou o download em andamento enquanto as referências restantes estavam pendentes");
                }
                finally { tail.TrySetResult(); await running; }
                await Task.WhenAll(callbacks);
            }
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try
            {
                await downloader.DownloadAndAssembleAsync(entry, h.Directory.CreateSubdirectory("cancelled-download"), _ => { }, cancelled.Token);
                return TestOutcome.Fail("o cancelamento explícito foi ignorado");
            }
            catch (OperationCanceledException) { }
            var unavailable = AppEntry.File("unavailable.bin", "unavailable-thread", TransferState.Now(), 32, "/");
            unavailable.FileId = "unavailable-download";
            unavailable.Encryption = h.Encryption.CreateFileEncryption(EncryptionModes.Standard);
            unavailable.ChunkMessageIds = ["99999999"]; unavailable.ChunkCount = 1;
            await (Task)typeof(AppController).GetMethod("StartDownloadAsync", flags)!.Invoke(controller,
                [unavailable.ThreadId, unavailable.Name, h.Directory.CreateSubdirectory("unavailable-download"), unavailable])!;
            var errorVisible = false;
            for (var attempt = 0; attempt < 100 && !errorVisible; attempt++)
            {
                errorVisible = await view.ExecuteScriptAsync("!!document.querySelector('.op-item[data-op-type=download][data-op-status=error]')") == "true";
                if (!errorVisible) await Task.Delay(50);
            }
            RequireSync(errorVisible && (await h.Transfers.RecoverableAsync()).Any(saved =>
                saved.FileId == unavailable.FileId && saved.Status == TransferStatuses.Paused) &&
                h.Transfers.Repository.FailedDirectory.GetFiles().Length == 0,
                "a falha do download não apareceu no painel ou descartou sua retomada");
            RequireSync(!IsWindowVisible(host), "o teste exibiu uma janela");
            return TestOutcome.Pass();
        }
        finally { browser?.Close(); DestroyWindow(host); }
    });

    private sealed class ResumeTimeoutHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _next = new(inner, false);
        internal int PayloadTimeouts;
        internal int DownloadTimeouts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/attachments/", StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref DownloadTimeouts, 1, 0) == 0)
                throw new TaskCanceledException("Synthetic download timeout", new TimeoutException());
            if (request.Method == HttpMethod.Post && request.Content is MultipartFormDataContent multipart &&
                multipart.Any(part => (part.Headers.ContentDisposition?.FileNameStar ?? part.Headers.ContentDisposition?.FileName?.Trim('"')) is string name && name.EndsWith(".bin") && name != "serial.bin") &&
                Interlocked.CompareExchange(ref PayloadTimeouts, 1, 0) == 0)
                throw new TaskCanceledException("Synthetic upload timeout", new TimeoutException());
            return await _next.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _next.Dispose(); base.Dispose(disposing); }
    }
}
