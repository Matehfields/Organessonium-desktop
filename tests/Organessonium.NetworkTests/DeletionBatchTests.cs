using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> PermanentDeletionBatchHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        var entries = await h.UploadAsync(await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(index => h.PrepareAsync(index + ".bin", Encoding.UTF8.GetBytes("delete-" + index)))));
        using var transport = new DeletionBatchTransport(h.Server, entries.Take(8).Select(entry => entry.MetadataMessageId));
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(transport, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        await registry.RefreshMappingsAsync();
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1280, 800, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        AppController? controller = null;
        var phase = "browser initialization";
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observed = (_, args) =>
        {
            if (args.Exception is not OperationCanceledException)
                errors.Enqueue(args.Exception.GetType().Name + ": " + args.Exception.Message);
        };
        AppDomain.CurrentDomain.FirstChanceException += observed;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, h.Directory.CreateSubdirectory("browser").FullName);
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true; browser.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var web = h.Directory.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                using var output = File.Create(path); await input.CopyToAsync(output);
            }
            var view = browser.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var dummy = new WebView2();
            var bridge = new WebMessageBridge(dummy, script => view.ExecuteScriptAsync(script));
            controller = new AppController(new System.Windows.Window(), h.Store, bridge, _ => Task.CompletedTask);
            var session = new DiscordSession();
            typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
            typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
            controller.SetSession(session);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AppController).GetField("_registry", flags)!.SetValue(controller, registry);
            typeof(AppController).GetField("_loadedApps", flags)!.SetValue(controller, entries.ToList());
            await bridge.SetSettingsAsync(h.Settings);
            await bridge.NotifyReadyAsync();
            await bridge.CallAsync("_onLocalAppsChanged", entries);
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('#files-grid .file-card').length===10") == "true",
                "a biblioteca inicial não apareceu");
            var delete = typeof(AppController).GetMethod("DeleteItemsAsync", flags)!;
            transport.Enabled = true;
            phase = "four concurrent metadata deletes";
            var task = (Task)delete.Invoke(controller, [JsonSerializer.Serialize(entries.Take(8).Select(entry => entry.MetadataMessageId)), true])!;
            await transport.FourDeletes.Task.WaitAsync(TimeSpan.FromSeconds(15));
            RequireSync(transport.MaximumDeletes == 4 && !task.IsCompleted, "não houve quatro exclusões de metadados simultâneas");
            transport.ReleaseDeletes.TrySetResult();
            phase = "fresh reference proof";
            await transport.ProofStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            RequireSync(await view.ExecuteScriptAsync("document.querySelector('.op-item[data-op-type=delete] .op-percent').textContent==='80%'&&document.querySelector('.op-item[data-op-type=delete] .op-msg').textContent.includes('referências')") == "true",
                "a interface não mostrou a etapa de conferência antes de concluir");
            transport.ReleaseProof.TrySetResult();
            phase = "batch completion";
            await task.WaitAsync(TimeSpan.FromSeconds(30));
            RequireSync(transport.RootProofReads == 1 && transport.RootArchiveChanges <= 2,
                $"a exclusão repetiu a varredura/arquivamento por arquivo: leituras={transport.RootProofReads}, archives={transport.RootArchiveChanges}");
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('#files-grid .file-card').length===2&&document.querySelector('.op-item[data-op-type=delete]').dataset.opStatus==='done'&&document.querySelector('.op-item[data-op-type=delete] .op-percent').textContent==='100%'") == "true",
                "cards ou conclusão em 100% não foram atualizados");
            RequireSync(h.Store.LoadCachedEntriesForFolder("root").Count(entry => entry.Type == "file") == 2 &&
                h.Server.Messages(entries[0].ThreadId).Sum(message => message.Attachments.Count) == 2 &&
                !h.Server.DeletedChannels.Contains(entries[0].ThreadId), "a limpeza perdeu o cache, outro attachment ou o pool");
            var remaining = await h.Downloader.DownloadAndAssembleAsync(entries[9], h.Directory.CreateSubdirectory("remaining"), _ => { });
            RequireSync(await File.ReadAllTextAsync(remaining.FullName) == "delete-9", "o arquivo preservado deixou de ser baixável");

            // A failed remote proof must retain payloads and a durable intent, not silently delete bytes.
            transport.RejectProof = true;
            phase = "failed reference proof";
            await ((Task)delete.Invoke(controller, [JsonSerializer.Serialize(new[] { entries[8].MetadataMessageId }), true])!)
                .WaitAsync(TimeSpan.FromSeconds(20));
            RequireSync(h.Server.Messages(entries[8].ThreadId).Sum(message => message.Attachments.Count) == 2 &&
                await view.ExecuteScriptAsync("document.querySelectorAll('#files-grid .file-card').length===1&&document.getElementById('ops-list').textContent.includes('pendente')") == "true",
                "falha de conferência não preservou os payloads ou não apareceu no painel");
            transport.Enabled = false;
            phase = "durable cleanup recovery";
            await h.Storage.ReconcileAsync();
            RequireSync(h.Server.Messages(entries[9].ThreadId).Sum(message => message.Attachments.Count) == 1 &&
                h.Store.LoadCachedEntriesForFolder("root").Single(entry => entry.Type == "file").FileId == entries[9].FileId,
                "a recuperação do intent perdeu o arquivo restante");
            RequireSync(!IsWindowVisible(host), "o teste exibiu uma janela");
            return TestOutcome.Pass();
        }
        catch (Exception error)
        {
            var panel = browser != null ? await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('ops-list')?.textContent") : "no browser";
            throw new InvalidOperationException($"{phase}: {error.Message}; concurrency={transport.MaximumDeletes}; proofReads={transport.RootProofReads}; panel={panel}; observed={string.Join(" | ", errors.TakeLast(6))}", error);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= observed;
            transport.ReleaseDeletes.TrySetResult(); transport.ReleaseProof.TrySetResult();
            if (controller != null) await controller.DisposeAsync();
            browser?.Close(); DestroyWindow(host);
        }
    });

    private sealed class DeletionBatchTransport(HttpMessageHandler inner, IEnumerable<string> metadataIds) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        private readonly HashSet<string> _metadataIds = metadataIds.ToHashSet(StringComparer.Ordinal);
        private int _deletes;
        internal int MaximumDeletes;
        internal int RootProofReads;
        internal int RootArchiveChanges;
        internal bool Enabled;
        internal bool RejectProof;
        internal TaskCompletionSource FourDeletes { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDeletes { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ProofStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseProof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (Enabled && request.Method == HttpMethod.Delete && path.Contains("/channels/root-thread/messages/", StringComparison.Ordinal) &&
                _metadataIds.Contains(path.Split('/')[^1]))
            {
                var active = Interlocked.Increment(ref _deletes);
                int maximum;
                do { maximum = MaximumDeletes; }
                while (active > maximum && Interlocked.CompareExchange(ref MaximumDeletes, active, maximum) != maximum);
                if (active == 4) FourDeletes.TrySetResult();
                try { await ReleaseDeletes.Task.WaitAsync(token); return await _forward.SendAsync(request, token); }
                finally { Interlocked.Decrement(ref _deletes); }
            }
            if (Enabled && request.Method == HttpMethod.Get && path.EndsWith("/channels/root-thread/messages", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref RootProofReads);
                ProofStarted.TrySetResult();
                await ReleaseProof.Task.WaitAsync(token);
                if (RejectProof) return new(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            }
            if (Enabled && request.Method == HttpMethod.Patch && path.EndsWith("/channels/root-thread", StringComparison.Ordinal))
                Interlocked.Increment(ref RootArchiveChanges);
            return await _forward.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }
}
