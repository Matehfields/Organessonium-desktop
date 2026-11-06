using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> UploadPreparationOverlapsSendingAsync()
    {
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-upload-pipeline-");
        const int count = 31;
        const int chunkBytes = 64 * 1024;
        try
        {
            var source = new FileInfo(Path.Combine(directory.FullName, "source.bin"));
            await File.WriteAllBytesAsync(source.FullName, Enumerable.Repeat((byte)37, count * chunkBytes).ToArray());
            var crypto = CreateEncryptionService(out _);
            var context = new EncryptedPayloadContext("overlap", crypto.CreateFileEncryption(EncryptionModes.Standard),
                EncryptedPayloadRoles.Original, 0, count);
            using var memory = new UploadMemoryCoordinator(1);
            var retainedBuffers = new List<byte[]>();
            foreach (var cancel in new[] { false, true })
            {
                using var token = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var gate = new AdaptiveUploadGate(1);
                using var server = new UploadBudgetHandler { HoldRequests = true };
                using var api = new DiscordApiClient("pipeline-test-" + Guid.NewGuid(), new HttpClient(server, false));
                var prepared = 0;
                var upload = new ForumUploader(api, "files").UploadResumableGeneratedAsync("source.bin", count,
                    async index =>
                    {
                        await Task.Delay(5, token.Token); // Slow storage while the existing HTTP request is still active.
                        var part = await memory.PreparePartAsync(crypto, source, source.Length, chunkBytes,
                            context.WithChunkIndex(index), token.Token);
                        using var stream = (MemoryStream)part.OpenRead();
                        RequireSync(stream.TryGetBuffer(out var buffer), "buffer RAM não disponível");
                        lock (retainedBuffers) retainedBuffers.Add(buffer.Array!);
                        Interlocked.Increment(ref prepared);
                        return part;
                    }, "thread", "", new Dictionary<int, string>(), cancellationToken: token.Token,
                    maxParallelParts: 1, adaptiveUploadGate: gate, encryptedPayload: context,
                    useAttachmentReferences: true, batchPreparationGate: memory.BatchPreparationGate,
                    maxPreparedPartBytes: chunkBytes + EncryptionService.EnvelopeOverheadBytes);
                await server.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var timeout = Stopwatch.StartNew();
                while (Volatile.Read(ref prepared) < 20 && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
                RequireSync(server.Active == 1 && Volatile.Read(ref prepared) == 20,
                    "o sucessor não foi preparado durante o envio ou a fila leu além do limite");
                if (cancel)
                {
                    token.Cancel();
                    try { await upload; return TestOutcome.Fail("a fila não respeitou o cancelamento"); }
                    catch (OperationCanceledException) { }
                }
                else
                {
                    server.Release.TrySetResult();
                    var result = await upload;
                    RequireSync(result.ChunkReferences?.Count == count, "a sobreposição perdeu chunks");
                }
                RequireSync(retainedBuffers.All(buffer => buffer.All(value => value == 0)),
                    "buffers de envio ou do sucessor não foram limpos");
            }
            RequireSync(directory.EnumerateFiles("*", SearchOption.AllDirectories).Count() == 1,
                "a fila criou chunks cifrados no disco");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private static Task<TestOutcome> MemoryBudgetHiddenBrowserAsync() => OnHiddenDispatcherAsync(async () =>
    {
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-budget-browser-");
        var window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new InvalidOperationException("Hidden test host unavailable.");
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, directory.FullName);
            controller = await environment.CreateCoreWebView2ControllerAsync(window);
            controller.IsVisible = false;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var web = directory.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, name[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(name)!;
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            controller.CoreWebView2.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName,
                CoreWebView2HostResourceAccessKind.Allow);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            controller.CoreWebView2.NavigationCompleted += (_, _) => loaded.TrySetResult();
            controller.CoreWebView2.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var view = controller.CoreWebView2;
            await view.ExecuteScriptAsync("window.__organessoniumSettings={uploadThreads:4,forceUploadParallelism:false,language:'pt-BR',animationsEnabled:false};window.onBridgeReady();openSettings();");
            await Task.Delay(150);
            async Task Check(string script, string message) => RequireSync(await view.ExecuteScriptAsync(script) == "true", message);
            await Check("document.getElementById('setting-force-upload').textContent.includes('1 GB')", "o limite de RAM não apareceu no botão");
            await view.ExecuteScriptAsync("document.getElementById('setting-force-upload').click()");
            await Check("document.getElementById('setting-force-upload').getAttribute('aria-pressed')==='true'&&document.getElementById('setting-upload-threads').disabled&&document.getElementById('upload-thr-val').textContent==='16'", "o botão não mostrou 16 workers");
            await view.ExecuteScriptAsync("document.getElementById('setting-force-upload').click();closeSettings();window._opsAdd('budget','upload','source.bin');window._opsUploadBatch('budget',['source.bin']);window._opsTransferMetrics('budget','upload',0,1,0,1000000,0,0);");
            await Task.Delay(1100);
            await view.ExecuteScriptAsync("window._opsTransferMetrics('budget','upload',0,1,100000,1000000,2,400000)");
            await Check("document.getElementById('op-budget').dataset.metricSpeed!=='0 B/s'&&!document.getElementById('op-eta-budget').classList.contains('hidden')", "a UI não mostrou velocidade e estimativa durante o envio");
            await view.ExecuteScriptAsync("window._opsUploadFileProgress('budget',0,'active',50,'100 KB / 976 KB enviados');");
            await Check("document.getElementById('op-budget').dataset.progress==='50'&&document.getElementById('op-file-detail-budget-0').textContent.includes('KB')", "o progresso do lote não ponderou a etapa individual ou perdeu os bytes transmitidos");
            await view.ExecuteScriptAsync("window._opsTransferMetrics('budget','upload',0,1,1000000,1000000,3,1000000);window._opsUploadFileProgress('budget',0,'active',99,'Concluindo');");
            await Check("document.getElementById('op-percent-budget').textContent==='99%'&&document.getElementById('op-file-detail-budget-0').textContent==='Concluindo'&&document.getElementById('op-eta-budget').classList.contains('hidden')", "a espera do ACK não mostrou Concluindo em 99%");
            await view.ExecuteScriptAsync("window._opsUploadBatch('image',['foto.jpg']);window._opsTransferMetrics('image','upload',0,1,1000000,1000000,1,1000000);window._opsUploadFileProgress('image',0,'active',98,'Concluindo');");
            await Check("document.getElementById('op-percent-image').textContent==='98%'", "a imagem não reservou o envio da miniatura");
            await view.ExecuteScriptAsync("window._opsUploadFileProgress('image',0,'active',99,'Concluindo');window._opsUploadFileProgress('budget',0,'done',100,'Concluído');window._opsUploadFileProgress('budget',0,'active',30,'30 KB enviados');");
            await Check("document.getElementById('op-percent-image').textContent==='99%'&&document.getElementById('op-budget').textContent.includes('Concluído')&&!document.getElementById('op-budget').textContent.includes('30 KB')", "uma atualização atrasada substituiu a conclusão");
            await view.ExecuteScriptAsync("window._opsAdd('bytes-download','download','source.bin');window._opsProgress('bytes-download',64,'640 MB / 1 GB baixados');window._onNonfatalError('A prévia falhou. O original foi preservado.');");
            await Check("document.getElementById('op-msg-bytes-download').textContent.includes('640 MB')&&!document.getElementById('confirm-overlay').classList.contains('hidden')&&document.getElementById('confirm-message').textContent.includes('original foi preservado')&&document.getElementById('confirm-cancel').classList.contains('hidden')", "o download em bytes ou o aviso de falha não apareceram na interface");
            await view.ExecuteScriptAsync("document.getElementById('confirm-ok').click();");
            await view.ExecuteScriptAsync("window._opsUploadBatch('finished',['a.bin','b.bin']);window._opsTransferMetrics('finished','upload',0,2,990000,1000000,2,990000);window._opsUploadFileProgress('finished',0,'done',100,'Concluído');window._opsUploadFileProgress('finished',1,'active',99,'Concluindo');");
            await Check("document.getElementById('op-percent-finished').textContent==='99%'", "o lote chegou a 100% antes de concluir todos os arquivos");
            await view.ExecuteScriptAsync("window._opsUploadFileProgress('finished',1,'done',100,'Concluído');window._opsTransferMetrics('finished','upload',2,2,990000,1000000,2,990000);");
            await Check("document.getElementById('op-percent-finished').textContent==='100%'&&document.getElementById('op-file-percent-finished-1').textContent==='100%'", "o lote concluído ficou abaixo de 100% com métricas atrasadas");
            await view.ExecuteScriptAsync("window._opsUploadBatch('eta-wall',Array(100).fill('image.jpg'));window._opsTransferMetrics('eta-wall','upload',0,100,0,100000000,0,0)");
            await Task.Delay(1100);
            await view.ExecuteScriptAsync("window._opsTransferMetrics('eta-wall','upload',10,100,90000000,100000000,2,400000000)");
            await Check("parseInt(document.getElementById('op-eta-eta-wall').textContent.match(/(\\d+) s/)[1],10)>=8", "a ETA usou somente a transmissão e ignorou os arquivos ainda não concluídos");
            await view.ExecuteScriptAsync("window._opsUpdate('eta-wall','paused','Pausado')");
            await Check("document.getElementById('op-eta-eta-wall').classList.contains('hidden')", "a transferência pausada continuou exibindo estimativa");
            AppMemoryBudget.RegisterWebViewProcesses(environment.GetProcessInfos().Select(info => info.ProcessId));
            RequireSync(!IsWindowVisible(window), "o teste exibiu uma janela");
            RequireSync(AppMemoryBudget.PeakCommittedBytes <= AppMemoryBudget.LimitBytes, "o job ultrapassou 1 GB");
            return TestOutcome.Pass();
        }
        finally
        {
            controller?.Close(); DestroyWindow(window);
            try { directory.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });

    private static async Task<TestOutcome> RamOnlyLargeUploadAndMetricsAsync()
    {
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-ram-upload-");
        try
        {
            var source = new FileInfo(Path.Combine(directory.FullName, "source.bin"));
            await using (var file = source.Create()) file.SetLength(1024L * 1024 * 1024);
            var count = ArchiveSplitter.GetPartCount(source.Length, EncryptionService.MaximumPlaintextPayloadBytes);
            var crypto = CreateEncryptionService(out _);
            var context = new EncryptedPayloadContext("ram-large", crypto.CreateFileEncryption(EncryptionModes.Standard),
                EncryptedPayloadRoles.Original, 0, count);
            long logical = 0, wire = 0;
            var updatesBeforeConfirmation = 0;
            var confirmed = new List<int>();
            var bytes = new UploadByteProgress(source.Length, EncryptionService.MaximumPlaintextPayloadBytes, true,
                (data, sent) => { Interlocked.Add(ref logical, data); Interlocked.Add(ref wire, sent); Interlocked.Increment(ref updatesBeforeConfirmation); });
            using var server = new UploadBudgetHandler();
            using var api = new DiscordApiClient("budget-test-" + Guid.NewGuid(), new HttpClient(server, false));
            using var memory = new UploadMemoryCoordinator(16, forceParallelism: true);
            var result = await new ForumUploader(api, "files").UploadResumableGeneratedAsync("source.bin", count,
                index => memory.PreparePartAsync(crypto, source, source.Length, EncryptionService.MaximumPlaintextPayloadBytes,
                    context.WithChunkIndex(index), default, deferBuffers: true), "", "", new Dictionary<int, string>(),
                uploadedParts: done => confirmed.Add(done), encryptedPayload: context, maxParallelParts: 16,
                useAttachmentReferences: true, batchPreparationGate: memory.BatchPreparationGate,
                streamPreparedParts: true,
                onPartTransferProgress: bytes.Report, onBatchUploaded: batch =>
                {
                    RequireSync(updatesBeforeConfirmation > 0, "os bytes só foram reportados depois do ACK");
                    foreach (var chunk in batch.Chunks) bytes.Confirm(chunk.Index);
                    return Task.CompletedTask;
                });
            RequireSync(result.ChunkReferences?.Count == count && logical == source.Length,
                "contagem lógica incorreta no arquivo de 1 GiB");
            RequireSync(wire == source.Length + count * EncryptionService.EnvelopeOverheadBytes,
                "contagem de bytes enviados incorreta");
            RequireSync(confirmed.SequenceEqual(confirmed.Order()) && confirmed.Last() == count, "partes confirmadas regrediram");
            RequireSync(directory.EnumerateFiles("*", SearchOption.AllDirectories).Count() == 1,
                "o upload criou ciphertext no disco");
            RequireSync(AppMemoryBudget.PeakCommittedBytes <= AppMemoryBudget.LimitBytes, "pico acima de 1 GB");
            Console.WriteLine($"RAM upload peak: {AppMemoryBudget.PeakCommittedBytes / 1024d / 1024d:F1} MiB; source: 1 GiB");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private static Task<TestOutcome> LiveUploadWorkersAndSlowStorageAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-live-upload-");
        using var gate = new AdaptiveUploadGate(1);
        using var registered = UploadConcurrencyRegistry.Register(gate);
        using var server = new UploadBudgetHandler { HoldRequests = true };
        using var api = new DiscordApiClient("live-test-" + Guid.NewGuid(), new HttpClient(server, false));
        var crypto = CreateEncryptionService(out _);
        var context = new EncryptedPayloadContext("live", crypto.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Original, 0, 41);
        var files = new List<FileInfo>();
        var ticks = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) => ticks++, Dispatcher.CurrentDispatcher);
        try
        {
            for (var index = 0; index < 41; index++)
            {
                var file = new FileInfo(Path.Combine(directory.FullName, $"chunk-{index}.bin"));
                await File.WriteAllBytesAsync(file.FullName, crypto.EncryptBytes(new byte[1024], context.WithChunkIndex(index)));
                files.Add(file);
            }
            timer.Start();
            var upload = new ForumUploader(api, "files").UploadResumableGeneratedAsync("live.bin", 41, index =>
            {
                Thread.Sleep(40); // Simulates blocking storage metadata/open on the preparation worker.
                return Task.FromResult(new PreparedUploadPart(files[index], 1024));
            }, "thread", "", new Dictionary<int, string>(), maxParallelParts: 1, adaptiveUploadGate: gate,
                encryptedPayload: context, useAttachmentReferences: true);
            await server.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            UploadConcurrencyRegistry.Apply(true, 1);
            var timeout = Stopwatch.StartNew();
            while (server.Active < 3 && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            RequireSync(server.Active >= 3, "ativar o botão não aumentou a concorrência do upload atual");
            RequireSync(ticks > 15, "armazenamento lento bloqueou o dispatcher");
            UploadConcurrencyRegistry.Apply(false, 1);
            RequireSync(gate.CurrentLimit == 1, "desativar o botão não restaurou o limite atual");
            server.Release.TrySetResult();
            var result = await upload.WaitAsync(TimeSpan.FromSeconds(10));
            RequireSync(result.ChunkCount == 41, "reconfigurar workers perdeu partes");
            return TestOutcome.Pass();
        }
        finally { timer.Stop(); server.Release.TrySetResult(); directory.Delete(true); }
    });

    private sealed class UploadBudgetHandler : HttpMessageHandler
    {
        private int _message, _active;
        internal int Active => Volatile.Read(ref _active);
        internal bool HoldRequests;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post) return Reply(new { id = "thread" });
            Interlocked.Increment(ref _active);
            try
            {
                var message = Interlocked.Increment(ref _message).ToString();
                var multipart = (MultipartFormDataContent)request.Content!;
                var attachments = new List<object>();
                foreach (var part in multipart.Where(part => part.Headers.ContentDisposition?.FileName != null))
                {
                    var name = part.Headers.ContentDisposition!.FileName!.Trim('"');
                    var size = part.Headers.ContentLength!.Value;
                    await part.CopyToAsync(Stream.Null, token);
                    attachments.Add(new { id = message + "-" + attachments.Count, filename = name, size, url = "https://cdn.invalid/test" });
                }
                Started.TrySetResult();
                if (HoldRequests) await Release.Task.WaitAsync(token);
                var data = new { id = message, attachments };
                return request.RequestUri!.AbsolutePath.EndsWith("/threads", StringComparison.Ordinal)
                    ? Reply(new { id = "thread", message = data }) : Reply(data);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }

    private static Task<TestOutcome> OnHiddenDispatcherAsync(Func<Task<TestOutcome>> work)
    {
        var completed = new TaskCompletionSource<TestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { completed.TrySetResult(await work()); }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completed.Task;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}
