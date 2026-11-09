using System.Net.Http;
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
    private static Task<TestOutcome> QueuedFolderCreationHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        h.Settings.Language = "pt-BR";
        h.Settings.SearchWorkers = 1;
        h.Store.Save(h.Settings);
        const int files = 215;
        for (var index = 0; index < files; index++)
            h.Server.SeedLegacyMetadata(JsonSerializer.Serialize(new FileMetadata
            {
                FileId = "fixture-" + index, ThreadId = "payload-" + index, Name = $"file-{index:D4}.bin",
                ParentFolderId = "root", MimeType = "application/octet-stream", SizeBytes = 1024,
                UploadedAt = TransferState.Now(), Chunks = ["chunk-" + index]
            }, JsonOptions.Web));
        var initial = await h.Registry.RefreshFolderAsync("root", "/");
        var coldPages = h.Server.MessagePages.GetValueOrDefault("root-thread");
        RequireSync(initial.Count == files && coldPages >= 3, "a preparação não criou um índice com várias páginas");
        h.Store.SaveLibraryStatistics(new LibraryStatistics(files, 0, files * 1024));

        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1280, 800, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        AppController? controller = null;
        using var responseGate = new FolderPostResponseGate(h.Server);
        using var firstApi = new DiscordApiClient(h.Settings.BotToken, new HttpClient(responseGate, false));
        using var reopenedApi = new DiscordApiClient(h.Settings.BotToken, new HttpClient(h.Server, false));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, h.Directory.CreateSubdirectory("browser").FullName);
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true;
            browser.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var web = h.Directory.CreateSubdirectory("web");
            var assembly = typeof(AppController).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            var view = browser.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
            using var dummy = new WebView2();
            var bridge = new WebMessageBridge(dummy, script => view.ExecuteScriptAsync(script));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            AppController OpenController(SettingsStore store, DiscordApiClient api)
            {
                var app = new AppController(new System.Windows.Window(), store, bridge, _ => Task.CompletedTask);
                var session = new DiscordSession();
                typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
                typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
                app.SetSession(session);
                return app;
            }
            view.WebMessageReceived += async (_, e) =>
            {
                using var message = JsonDocument.Parse(e.WebMessageAsJson);
                var method = message.RootElement.GetProperty("method").GetString();
                if (controller != null && method is "createFolder" or "setCurrentPath")
                    await controller.HandleJsonMessageAsync(e.WebMessageAsJson);
            };
            async Task OpenPageAsync(string query = "")
            {
                var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Navigated(object? _, CoreWebView2NavigationCompletedEventArgs e)
                {
                    view.NavigationCompleted -= Navigated;
                    if (e.IsSuccess) navigation.TrySetResult();
                    else navigation.TrySetException(new IOException("Falha ao abrir a biblioteca de teste."));
                }
                view.NavigationCompleted += Navigated;
                view.Navigate("https://organessonium.local/index.html" + query);
                await navigation.Task.WaitAsync(timeout.Token);
                await bridge.SetSettingsAsync(h.Settings);
                await bridge.NotifyReadyAsync();
            }
            async Task WaitForAsync(string expression)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                wait.CancelAfter(TimeSpan.FromSeconds(12));
                while (await view.ExecuteScriptAsync(expression) != "true")
                {
                    if (wait.IsCancellationRequested) throw new TimeoutException(expression);
                    await Task.Delay(40, timeout.Token);
                }
            }
            Task CreateWithMenuAsync(string name) => view.ExecuteScriptAsync(
                "$('btn-sidebar-new').click();$('sidebar-new-folder').click();$('folder-name-input').value=" +
                JsonSerializer.Serialize(name) + ";$('folder-confirm').click()");

            controller = OpenController(h.Store, firstApi);
            typeof(AppController).GetField("_loadedApps", flags)!.SetValue(controller, initial);
            await OpenPageAsync();
            await bridge.CallAsync("_onAppsLoaded", initial);
            await CreateWithMenuAsync("Pasta em teste");
            await responseGate.Created.Task.WaitAsync(timeout.Token);
            var warmPages = h.Server.MessagePages.GetValueOrDefault("root-thread") - coldPages;
            await WaitForAsync("allApps.some(entry=>entry.name==='Pasta em teste'&&entry.pendingCreation)&&[...document.querySelectorAll('.folder-card')].some(card=>card.textContent.includes('Pasta em teste'))");
            RequireSync(warmPages == 1, $"a criação consultou {warmPages} páginas de metadados com serial válido");
            await view.ExecuteScriptAsync("[...document.querySelectorAll('.folder-card')].find(card=>card.textContent.includes('Pasta em teste')).click()");
            await WaitForAsync("currentPath==='/Pasta em teste'&&folderLoadingPath===''");
            await CreateWithMenuAsync("Subpasta em teste");
            await WaitForAsync("pendingLibraryChanges.length===2&&[...document.querySelectorAll('.folder-card')].some(card=>card.textContent.includes('Subpasta em teste'))");

            const string key = "libraryMutations:names:folders:files:serial";
            var reopenedStore = new SettingsStore(h.Store.DataDirectory);
            var pending = reopenedStore.LoadLibraryMutations(key);
            var parentId = pending[0].Changes[0].Source.FolderId;
            var childId = pending[1].Changes[0].Source.FolderId;
            RequireSync(pending[0].FolderCreation is { Attempted: true, ThreadId: null } && pending[1].Changes[0].Source.ParentFolderId == parentId,
                "a fila não salvou o post em andamento e o vínculo da subpasta");
            await controller.DisposeAsync();
            controller = null;
            await responseGate.Interrupted.Task.WaitAsync(timeout.Token);

            controller = OpenController(reopenedStore, reopenedApi);
            await OpenPageAsync("?restart=1");
            await controller.HandleJsonMessageAsync("""{"method":"loadApps","args":[]}""");
            await WaitForAsync("pendingLibraryChanges.length===0&&allApps.some(entry=>entry.name==='Pasta em teste'&&entry.metadataMessageId&&!entry.pendingCreation)");
            await view.ExecuteScriptAsync("[...document.querySelectorAll('.folder-card')].find(card=>card.textContent.includes('Pasta em teste')).click()");
            await WaitForAsync("currentPath==='/Pasta em teste'&&[...document.querySelectorAll('.folder-card')].some(card=>card.textContent.includes('Subpasta em teste'))");
            var mappings = reopenedStore.LoadFolderIndexMappings();
            var parent = reopenedStore.LoadCachedEntriesForFolder("root").Single(entry => entry.Name == "Pasta em teste");
            var child = reopenedStore.LoadCachedEntriesForFolder(parentId).Single(entry => entry.Name == "Subpasta em teste");
            var restored = new AppRegistry(reopenedApi, reopenedStore, h.Settings);
            var statistics = await restored.GetLibraryStatisticsAsync("root", timeout.Token);
            RequireSync(parent.FolderId == parentId && child.FolderId == childId && mappings[parentId].ThreadId == responseGate.ThreadId &&
                mappings[parentId].SerialMessageId.Length > 0 && mappings[childId].SerialMessageId.Length > 0 &&
                reopenedStore.LoadLibraryMutations(key).Count == 0 &&
                h.Server.PublishedTitles.Count(title => title == pending[0].FolderCreation!.PostTitle) == 1 &&
                statistics is { FileCount: files, FolderCount: 2, TotalBytes: files * 1024 },
                "a retomada duplicou o índice, perdeu o vínculo ou publicou totais incorretos");
            Console.WriteLine($"FOLDER_CREATION_RESULT warmIndexPages={warmPages} coldIndexPages={coldPages} visibleBeforeAck=True recoveredPost=True childRetained=True");
            return TestOutcome.Pass();
        }
        finally
        {
            if (controller != null) await controller.DisposeAsync();
            browser?.Close();
            DestroyWindow(host);
        }
    });

    private sealed class FolderPostResponseGate(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        internal TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string ThreadId { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await _forward.SendAsync(request, token);
            if (request.Method != HttpMethod.Post ||
                !request.RequestUri!.AbsolutePath.EndsWith("/channels/folders/threads", StringComparison.Ordinal)) return response;
            using (response)
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                ThreadId = body.RootElement.GetProperty("id").GetString()!;
                Created.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { Interrupted.TrySetResult(); }
            }
            throw new OperationCanceledException(token);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _forward.Dispose();
            base.Dispose(disposing);
        }
    }
}
