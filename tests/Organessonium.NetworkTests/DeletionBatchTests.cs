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
        var waitingCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanupObserver = registry.SharedStorage.ObserveCleanup(update =>
        { if (update.Waiting) waitingCleanup.TrySetResult(); return false; }, api.PayloadLifetime);
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
            var observeMaintenance = false;
            long maintenanceScriptBytes = 0;
            var maintenancePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var bridge = new WebMessageBridge(dummy, async script =>
            {
                var publication = observeMaintenance && (script.Contains("_onFolderIndexReconciled") || script.Contains("_onAppsLoaded"));
                if (publication) maintenanceScriptBytes += Encoding.UTF8.GetByteCount(script);
                await view.ExecuteScriptAsync(script);
                if (publication && script.Contains("Found during maintenance.bin", StringComparison.Ordinal)) maintenancePublished.TrySetResult();
            });
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
            var journalThread = (await registry.GetStorageJournalIndexAsync(default)).ThreadId;
            var journalReadsBefore = h.Server.MessagePages.GetValueOrDefault(journalThread);
            transport.Enabled = true;
            phase = "four concurrent metadata deletes";
            var task = (Task)delete.Invoke(controller, [JsonSerializer.Serialize(entries.Take(8).Select(entry => entry.MetadataMessageId)), true])!;
            await transport.FourDeletes.Task.WaitAsync(TimeSpan.FromSeconds(15));
            RequireSync(transport.MaximumDeletes == 4 && !task.IsCompleted, "não houve quatro exclusões de metadados simultâneas");
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('#files-grid .file-card').length===2&&pendingLibraryChanges.length===8") == "true",
                "a biblioteca esperou pela exclusão remota para remover os cards");
            var browsing = Enumerable.Range(0, 2305).Select(index => new AppEntry { Type = "file", Name = $"photo-{index:D4}.jpg",
                FileId = "browse-" + index, ThreadId = "browse-thread-" + index, MetadataMessageId = "browse-meta-" + index,
                ParentFolderId = "browse", Path = "/Browse", SizeBytes = 1024 }).ToArray();
            var navigated = entries.Concat(browsing).ToList();
            typeof(AppController).GetField("_loadedApps", flags)!.SetValue(controller, navigated);
            typeof(AppController).GetField("_currentPath", flags)!.SetValue(controller, "/Browse");
            await view.ExecuteScriptAsync("navigate('/Browse',false,'browse')");
            await bridge.CallAsync("_onAppsLoaded", navigated);
            await view.ExecuteScriptAsync("window.__browsingCard=document.querySelector('.cards-grid .file-card');window.__browsingCard.click()");
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===100") == "true",
                "a pasta navegada criou mais de cem cards inicialmente");
            transport.ReleaseDeletes.TrySetResult();
            phase = "fresh reference proof";
            await transport.ProofStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            RequireSync(await view.ExecuteScriptAsync("pendingLibraryChanges.length===0&&!document.querySelector('.op-item[data-op-type=delete],.op-item[data-op-type=cleanup]')") == "true",
                "a remoção do índice aguardou a limpeza física ou mostrou a manutenção silenciosa");
            RequireSync(h.Server.Messages(entries[0].ThreadId).Sum(message => message.Attachments.Count) == 10,
                "a limpeza física ocorreu antes da conferência bloqueada");
            transport.ReleaseProof.TrySetResult();
            phase = "batch completion";
            for (var attempt = 0; attempt < 150 && h.Server.Messages(entries[0].ThreadId).Sum(message => message.Attachments.Count) != 2; attempt++)
                await Task.Delay(50);
            RequireSync(h.Server.Messages(entries[0].ThreadId).Sum(message => message.Attachments.Count) == 2 &&
                await view.ExecuteScriptAsync("!document.querySelector('.op-item[data-op-type=cleanup]')") == "true",
                "a limpeza física não concluiu silenciosamente");
            RequireSync(h.Server.MessagePages.GetValueOrDefault(journalThread) - journalReadsBefore == 2,
                "a conferência de arquivos releu o journal técnico como uma pasta da biblioteca");
            RequireSync(await view.ExecuteScriptAsync("window.__browsingCard===document.querySelector('.cards-grid .file-card')&&window.__browsingCard.classList.contains('selected')&&document.querySelectorAll('.cards-grid .file-card').length===100") == "true",
                "uma exclusão em outra pasta apagou/recriou os cards ou perdeu a seleção durante a navegação");
            RequireSync(((IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!).Count(entry => entry.ParentFolderId == "browse") == 2305,
                "a publicação da exclusão sobrescreveu a coleção carregada após a navegação");
            await view.ExecuteScriptAsync("document.getElementById('main-area').scrollTop=document.getElementById('main-area').scrollHeight");
            for (var attempt = 0; attempt < 30 && await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===200") != "true"; attempt++)
                await Task.Delay(100);
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===200&&window.__browsingCard===document.querySelector('.cards-grid .file-card')") == "true",
                "a rolagem não acrescentou cem cards preservando os anteriores");
            await view.ExecuteScriptAsync("setView('list')");
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.file-list-files .file-row').length===200") == "true",
                "a lista ignorou o limite já carregado");
            await view.ExecuteScriptAsync("setView('grid');document.getElementById('search-input').value='photo-2304';document.getElementById('search-input').dispatchEvent(new Event('input'))");
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===1&&document.querySelector('.cards-grid .file-card').textContent.includes('2304')") == "true",
                "a busca considerou somente os primeiros cem arquivos");
            await view.ExecuteScriptAsync("document.getElementById('search-input').value='';document.getElementById('search-input').dispatchEvent(new Event('input'));clearSelection();selectAllCurrentView()");
            RequireSync(await view.ExecuteScriptAsync("selectedKeys.size===2305&&document.querySelectorAll('.cards-grid .file-card').length===100") == "true",
                "a seleção de todos perdeu itens ainda não renderizados ou a busca não reiniciou a página");
            await view.ExecuteScriptAsync("clearSelection();navigate('/',false,'root')");
            typeof(AppController).GetField("_currentPath", flags)!.SetValue(controller, "/");
            RequireSync(transport.RootProofReads == 1 && transport.RootArchiveChanges <= 2,
                $"a exclusão repetiu a varredura/arquivamento por arquivo: leituras={transport.RootProofReads}, archives={transport.RootArchiveChanges}");
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('#files-grid .file-card').length===2&&pendingLibraryChanges.length===0") == "true",
                "a remoção dos cards não se manteve após a confirmação remota");
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
            await waitingCleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
            RequireSync(h.Server.Messages(entries[8].ThreadId).Sum(message => message.Attachments.Count) == 2 &&
                await view.ExecuteScriptAsync("document.querySelectorAll('#files-grid .file-card').length===1&&!document.querySelector('.op-item[data-op-type=cleanup]')") == "true",
                "falha de conferência não preservou os payloads ou exibiu uma notificação");
            phase = "close and reopen with pending cleanup";
            await controller.DisposeAsync(); controller = null;
            api.Dispose();
            using var reopenedTransport = new DeletionBatchTransport(h.Server, []);
            using var reopenedApi = new DiscordApiClient(h.Settings.BotToken, new HttpClient(reopenedTransport, false));
            var reopenedStore = new SettingsStore(h.Store.DataDirectory);
            var reopenedRegistry = new AppRegistry(reopenedApi, reopenedStore, reopenedStore.Load());
            using var observeRestart = reopenedRegistry.SharedStorage.ObserveCleanup(update =>
            { if (update.Pending?.Count > 0) reopenedTransport.Enabled = true; return false; }, reopenedApi.PayloadLifetime);
            var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => reloaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html?restart=1");
            await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            controller = new AppController(new System.Windows.Window(), reopenedStore, bridge, _ => Task.CompletedTask);
            var reopenedSession = new DiscordSession();
            typeof(DiscordSession).GetProperty("Api")!.SetValue(reopenedSession, reopenedApi);
            typeof(DiscordSession).GetProperty("Settings")!.SetValue(reopenedSession, reopenedStore.Load());
            controller.SetSession(reopenedSession);
            typeof(AppController).GetField("_registry", flags)!.SetValue(controller, reopenedRegistry);
            await bridge.SetSettingsAsync(reopenedStore.Load()); await bridge.NotifyReadyAsync();
            await ((Task)typeof(AppController).GetMethod("LoadAppsAsync", flags)!.Invoke(controller, [])!).WaitAsync(TimeSpan.FromSeconds(15));
            await reopenedTransport.ProofStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            RequireSync(await view.ExecuteScriptAsync("!document.querySelector('.op-item[data-op-type=cleanup]')") == "true",
                "reabrir o app mostrou a limpeza pendente no painel");
            reopenedTransport.ReleaseProof.TrySetResult();
            for (var attempt = 0; attempt < 150 && h.Server.Messages(entries[9].ThreadId).Sum(message => message.Attachments.Count) != 1; attempt++)
                await Task.Delay(50);
            RequireSync(h.Server.Messages(entries[9].ThreadId).Sum(message => message.Attachments.Count) == 1,
                "a operação pendente não retomou após reabrir o app");
            RequireSync(h.Server.Messages(entries[9].ThreadId).Sum(message => message.Attachments.Count) == 1 &&
                h.Store.LoadCachedEntriesForFolder("root").Single(entry => entry.Type == "file").FileId == entries[9].FileId,
                "a recuperação do intent perdeu o arquivo restante");
            phase = "folder creation UI";
            await ((Task)typeof(AppController).GetMethod("CreateFolderAsync", flags)!.Invoke(controller, ["Created after cleanup"])!)
                .WaitAsync(TimeSpan.FromSeconds(10));
            const string createdFolderVisible = "Array.from(document.querySelectorAll('#files-grid .folder-card')).some(card=>card.textContent.includes('Created after cleanup'))&&document.querySelectorAll('.cards-grid .file-card').length===1";
            for (var attempt = 0; attempt < 100 && await view.ExecuteScriptAsync(createdFolderVisible) != "true"; attempt++)
                await Task.Delay(50);
            RequireSync(await view.ExecuteScriptAsync(createdFolderVisible) == "true",
                "a pasta criada não apareceu na interface ou substituiu o arquivo preservado");
            phase = "large library reconciliation without hidden rendering";
            var archivedFiles = Enumerable.Range(0, 6653).Select(index => new AppEntry
            { Type = "file", Name = "archive-" + index + ".bin", FileId = "archive-" + index,
                ThreadId = "archive-payload-" + index, MetadataMessageId = "archive-metadata-" + index,
                ParentFolderId = "archive", Path = "/Archive", SizeBytes = 1024 }).ToArray();
            var largeLibrary = reopenedStore.LoadCachedEntriesForFolder("root").Concat(archivedFiles).ToList();
            typeof(AppController).GetField("_loadedApps", flags)!.SetValue(controller, largeLibrary);
            await bridge.CallAsync("_onAppsLoaded", largeLibrary);
            var external = AppEntry.File("Found during maintenance.bin", "external-thread", TransferState.Now(), 3, "/");
            external.ChunkCount = 1; external.ChunkMessageIds = ["external-message"];
            await new MetadataService(Organessonium.Services.MetadataEncryption.MetadataCodec.ForStore(reopenedStore), reopenedApi.DownloadAttachmentBytesAsync)
                .SendEntryAsync(reopenedApi, "root-thread", external);
            await view.ExecuteScriptAsync("openSettings();selectSettingsCategory('protection');window.__maintenanceMutations=0;window.__maintenanceObserver=new MutationObserver(records=>window.__maintenanceMutations+=records.length);window.__maintenanceObserver.observe($('main-area'),{subtree:true,childList:true,attributes:true});");
            observeMaintenance = true;
            typeof(AppController).GetMethod("QueueRegistryMaintenance", flags)!.Invoke(controller, [reopenedRegistry]);
            await maintenancePublished.Task.WaitAsync(TimeSpan.FromSeconds(20));
            RequireSync(maintenanceScriptBytes < 64 * 1024 && await view.ExecuteScriptAsync("isSettingsOpen()&&window.__maintenanceMutations===0&&allApps.some(entry=>entry.name==='Found during maintenance.bin')&&allApps.some(entry=>entry.fileId==='archive-6652')") == "true",
                "a manutenção reenviou toda a biblioteca, alterou cards ocultos ou perdeu os demais arquivos");
            await view.ExecuteScriptAsync("window.__maintenanceObserver.disconnect();$('settings-tab-downloads').click();$('setting-threads').value=6;$('setting-threads').dispatchEvent(new Event('input'));$('btn-back').click()");
            RequireSync(await view.ExecuteScriptAsync("!isSettingsOpen()&&$('thr-val').textContent==='6'&&document.querySelector('#files-grid').textContent.includes('Found during maintenance.bin')&&!document.querySelector('.op-item[data-op-type=cleanup]')") == "true",
                "a interface deixou de responder durante a manutenção ou não aplicou os dados ao retornar");
            Console.WriteLine("MAINTENANCE_UI_RESULT libraryFiles=6653 fullLibraryBytes=" + Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(largeLibrary, JsonOptions.Web)) + " publicationBytes=" + maintenanceScriptBytes + " hiddenMutations=0");
            RequireSync(!IsWindowVisible(host), "o teste exibiu uma janela");
            return TestOutcome.Pass();
        }
        catch (Exception error)
        {
            string panel;
            try { panel = browser != null ? await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('ops-list')?.textContent") : "no browser"; }
            catch (Exception diagnostic) { panel = "diagnostic unavailable: " + diagnostic.Message; }
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
