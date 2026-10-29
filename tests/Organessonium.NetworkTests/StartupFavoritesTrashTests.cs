using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> StartupFavoritesTrashHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var root = Directory.CreateTempSubdirectory("organessonium-startup-trash-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        AppController? controller = null;
        using var server = new TrashDiscordFixture();
        using var gate = new StartupNamesGate(server);
        var phase = "published library";
        try
        {
            using var api = new DiscordApiClient("isolated-startup-token", new HttpClient(gate, false));
            var store = new SettingsStore(root.CreateSubdirectory("data"));
            var settings = new AppSettings { NamesChannelId = "names", FoldersForumId = "folders", AppsForumId = "files", SerialChannelId = "serial", PreviewEnabled = false, Language = "pt-BR", Theme = "dark", SearchWorkers = 1 };
            store.Save(settings);
            // Published library on another installation, cold local cache on this one.
            using (var publisherApi = new DiscordApiClient("isolated-publisher", new HttpClient(server, false)))
            {
                var publisherStore = new SettingsStore(root.CreateSubdirectory("publisher"));
                publisherStore.Save(settings);
                await new AppRegistry(publisherApi, publisherStore, settings).RefreshServerIndexAsync(true);
                await publisherApi.DeleteMessageAsync("serial", publisherStore.LoadFolderIndexMappings()["photos"].SerialMessageId);
            }
            var environment = await CoreWebView2Environment.CreateAsync(null, root.CreateSubdirectory("browser").FullName);
            phase = "cold startup";
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true; browser.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var web = root.CreateSubdirectory("web");
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
            using var dummy = new WebView2();
            var bridge = new WebMessageBridge(dummy, script => view.ExecuteScriptAsync(script));
            controller = new AppController(new System.Windows.Window(), store, bridge, _ => Task.CompletedTask);
            var session = new DiscordSession();
            typeof(DiscordSession).GetProperty(nameof(DiscordSession.Api))!.SetValue(session, api);
            var presencePending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var presenceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.StartPresenceInBackground(async token =>
            {
                presencePending.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { presenceCancelled.TrySetResult(); }
            });
            await presencePending.Task.WaitAsync(TimeSpan.FromSeconds(2));
            controller.SetSession(session);
            var registry = new AppRegistry(api, store, settings);
            typeof(AppController).GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, registry);
            view.WebMessageReceived += async (_, args) =>
            {
                using var json = JsonDocument.Parse(args.WebMessageAsJson);
                var method = json.RootElement.GetProperty("method").GetString();
                if (method is "loadApps" or "setCurrentPath" or "emptyTrash") await controller.HandleJsonMessageAsync(args.WebMessageAsJson);
            };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await bridge.SetSettingsAsync(settings);
            await bridge.NotifyReadyAsync();
            async Task Wait(string script, string message)
            {
                for (var n = 0; n < 150; n++)
                {
                    if (await view.ExecuteScriptAsync(script) == "true") return;
                    await Task.Delay(50);
                }
                var state = await view.ExecuteScriptAsync("JSON.stringify({cards:document.querySelectorAll('#files-grid .file-card').length,count:document.getElementById('server-item-count').textContent,total:libraryTotalBytes,files:libraryFileCount,folders:libraryFolderCount,status:document.getElementById('status-text').textContent})");
                throw new InvalidOperationException(message + " :: " + state);
            }
            await Wait("document.getElementById('empty-state').classList.contains('folder-loading')", "a primeira abertura mostrou pasta vazia em vez do loading");
            RequireSync(!presenceCancelled.Task.IsCompleted, "a presença não permaneceu independente da interface");
            gate.Release.TrySetResult();
            await Wait("document.querySelectorAll('#files-grid .file-card').length===3&&document.getElementById('server-item-count').textContent==='6'", "a biblioteca/totais não apareceram com a presença ainda pendente");
            await Wait("libraryTotalBytes===8492&&libraryFileCount===3&&libraryFolderCount===3", "os totais reais enviados pelo C# não chegaram à interface");
            RequireSync(!presenceCancelled.Task.IsCompleted, "a biblioteca esperou a presença terminar");
            RequireSync(gate.IndexReads.GetValueOrDefault("photos-thread") > 0 &&
                gate.IndexReads.GetValueOrDefault("archive-thread") == 0 && gate.IndexReads.GetValueOrDefault("other-thread") == 0,
                "os totais não recuperaram somente o índice sem serial");

            // Reproduce a mapped Favorites serial that used to reinsert its own stale folder.
            var folder = (await registry.LoadCachedFolderAsync("root", "/")).Single(entry => entry.FolderId == "photos");
            phase = "add favorite folder";
            await registry.SetFavoriteAsync(folder, true);
            var favoriteMessage = (await api.GetMessagesAsync("serial")).Single(message => SerialMessageEnvelope.TryParse(message.Content, out var envelope) && envelope.FolderId == "favorites");
            store.UpsertFolderIndexMapping(server.AddFavoritesIndex(favoriteMessage.Id));
            registry = new AppRegistry(api, store, settings);
            phase = "remove mapped favorite";
            await registry.SetFavoriteAsync(folder, false);
            var reopened = new AppRegistry(api, store, settings);
            phase = "reload favorites";
            var favorites = await reopened.LoadFavoritesAsync(forceRefresh: true);
            RequireSync(favorites.All(entry => entry.FolderId != folder.FolderId), "a pasta removida reapareceu nos favoritos ao reabrir");
            await bridge.CallAsync("_onSpecialItemsLoaded", "favorites", favorites);
            await view.ExecuteScriptAsync("showFavoritesWorkspace()");
            await Wait("!Array.from(document.querySelectorAll('#files-grid .folder-card')).some(card=>card.textContent.includes('Fotos'))", "a UI conservou o favorito removido");

            // Empty all trash, not just the current filter/selection; preserve files outside it.
            typeof(AppController).GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, reopened);
            phase = "move contents to trash";
            var file = (await reopened.LoadCachedFolderAsync("root", "/")).Single(entry => entry.FileId == "file-a");
            await reopened.MoveToTrashAsync(file);
            phase = "move folder to trash";
            await reopened.MoveToTrashAsync(folder);
            await view.ExecuteScriptAsync("showTrashWorkspace()");
            phase = "empty trash UI";
            await Wait("document.querySelectorAll('#files-grid .file-card').length===2&&!document.getElementById('btn-empty-trash').classList.contains('hidden')", "o botão de esvaziar não apareceu na lixeira");
            await view.ExecuteScriptAsync("document.getElementById('btn-empty-trash').click()");
            await Wait("!document.getElementById('confirm-overlay').classList.contains('hidden')", "esvaziar não pediu confirmação");
            await view.ExecuteScriptAsync("document.getElementById('confirm-cancel').click()");
            RequireSync(server.DeletedChannels.Count == 0, "cancelar a confirmação apagou dados");
            await view.ExecuteScriptAsync("document.getElementById('btn-empty-trash').click();document.getElementById('confirm-ok').click()");
            await Wait("!trashEmptying&&document.querySelectorAll('#files-grid .file-card').length===0&&!document.getElementById('empty-state').classList.contains('folder-loading')", "esvaziar não atualizou a lixeira na interface");
            await Wait("document.querySelector('.op-item[data-op-type=cleanup]')?.dataset.opStatus==='done'", "a limpeza física da lixeira não concluiu em segundo plano");
            RequireSync(server.Entry("trash-thread", "file-a") == null && server.DeletedChannels.Contains("payload-a") && server.DeletedChannels.Contains("payload-b") &&
                !server.DeletedChannels.Contains("payload-c") && server.Entry("other-thread", "file-c") != null,
                "esvaziar não removeu os descendentes ou atingiu conteúdo fora da lixeira");
            await controller.DisposeAsync(); controller = null;
            RequireSync(presenceCancelled.Task.IsCompleted && !IsWindowVisible(host), "sair deixou a presença ativa ou o teste abriu uma janela");
            return TestOutcome.Pass();
        }
        catch (Exception ex) { throw new InvalidOperationException(phase + ": " + ex.Message, ex); }
        finally
        {
            gate.Release.TrySetResult();
            if (controller != null) await controller.DisposeAsync();
            browser?.Close(); DestroyWindow(host); SqliteConnection.ClearAllPools();
            try { root.Delete(true); } catch (IOException) { }
        }
    });

    private sealed class StartupNamesGate(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, int> IndexReads { get; } = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal))
                IndexReads.AddOrUpdate(request.RequestUri.AbsolutePath.Split('/')[^2], 1, (_, count) => count + 1);
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/channels/names/messages", StringComparison.Ordinal))
                await Release.Task.WaitAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
