using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    // The UI, bridge, controller, SQLite and OSER writer are real. Only the remote transport is simulated.
    private static async Task<TestOutcome> WebViewTrashLifecycleAsync()
    {
        var complete = new TaskCompletionSource<TestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            AcceptanceRect secondary = default;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new AcceptanceMonitor { Size = Marshal.SizeOf<AcceptanceMonitor>() };
                if (GetAcceptanceMonitor(monitor, ref info) && (info.Flags & 1) == 0) { secondary = info.Work; return false; }
                return true;
            }, IntPtr.Zero);
            if (secondary.Right <= secondary.Left)
            {
                complete.TrySetResult(TestOutcome.Fail("Monitor secundário indisponível para o teste visível."));
                return;
            }
            var window = new Window
            {
                Title = "Organessônium - Teste isolado da biblioteca", ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = secondary.Left + 20, Top = secondary.Top + 20,
                Width = Math.Min(1280, secondary.Right - secondary.Left - 40), Height = Math.Min(800, secondary.Bottom - secondary.Top - 60)
            };
            window.SourceInitialized += (_, _) => SetWindowPos(new System.Windows.Interop.WindowInteropHelper(window).Handle, IntPtr.Zero,
                secondary.Left + 20, secondary.Top + 20, (int)window.Width, (int)window.Height, 0x0014);
            var view = new WebView2();
            window.Content = view;
            var dispatcher = Dispatcher.CurrentDispatcher;
            window.Loaded += async (_, _) =>
            {
                var root = Directory.CreateTempSubdirectory("organessonium-trash-test-");
                AppController? controller = null;
                try
                {
                    using var handler = new TrashDiscordFixture(video: true);
                    DiscordApiClient CreateDiscord() => new("trash-test-token", new HttpClient(handler, disposeHandler: false));
                    using var firstDiscord = CreateDiscord();
                    var discord = firstDiscord;
                    var store = new SettingsStore(Directory.CreateDirectory(Path.Combine(root.FullName, "Data")));
                    var settings = new AppSettings { NamesChannelId = "names", FoldersForumId = "folders", AppsForumId = "files", SerialChannelId = "serial", SearchWorkers = 1, Theme = "dark", PreviewEnabled = false };
                    store.Save(settings);
                    var registry = new AppRegistry(discord, store, settings);
                    await registry.RefreshServerIndexAsync(true);
                    var webRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "web"));
                    var assembly = typeof(AppController).Assembly;
                    foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal)))
                    {
                        var target = Path.Combine(webRoot.FullName, name[4..].Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var source = assembly.GetManifestResourceStream(name)!;
                        using var output = File.Create(target);
                        await source.CopyToAsync(output);
                    }
                    await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, Path.Combine(root.FullName, "WebView")));
                    view.CoreWebView2.SetVirtualHostNameToFolderMapping("organessonium.local", webRoot.FullName, CoreWebView2HostResourceAccessKind.Allow);
                    var bridge = new WebMessageBridge(view);
                    AppController CreateController(SettingsStore data, DiscordApiClient api)
                    {
                        var app = new AppController(window, data, bridge, _ => Task.CompletedTask);
                        var session = new DiscordSession();
                        typeof(DiscordSession).GetProperty(nameof(DiscordSession.Api))!.SetValue(session, api);
                        app.SetSession(session);
                        return app;
                    }
                    controller = CreateController(store, discord);
                    view.CoreWebView2.WebMessageReceived += async (_, e) =>
                    {
                        using var message = JsonDocument.Parse(e.WebMessageAsJson);
                        var method = message.RootElement.GetProperty("method").GetString();
                        if (method is "refreshServerIndex" or "refreshCurrentFolder" or "setCurrentPath" or "deleteItems" or
                            "restoreItems" or "restoreTrash" or "moveItems" or "requestFileInformation" or "loadFavorites" or "searchLibrary")
                            if (controller != null) await controller.HandleJsonMessageAsync(e.WebMessageAsJson);
                    };
                    async Task OpenLibraryAsync(bool reopened = false)
                    {
                        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        void Navigated(object? _, CoreWebView2NavigationCompletedEventArgs e)
                        {
                            view.CoreWebView2.NavigationCompleted -= Navigated;
                            if (e.IsSuccess) navigation.TrySetResult();
                            else navigation.TrySetException(new IOException("WebView test navigation: " + e.WebErrorStatus + " HTTP " + e.HttpStatusCode));
                        }
                        view.CoreWebView2.NavigationCompleted += Navigated;
                        view.CoreWebView2.Navigate("https://organessonium.local/index.html" + (reopened ? "?restart=1" : ""));
                        await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                        await bridge.SetSettingsAsync(settings);
                        await view.CoreWebView2.ExecuteScriptAsync("window.onBridgeReady();window.__actions=0;window.__refreshes=0;window.__testError='';const status=window._onStatus;window._onStatus=(s,l)=>{status(s,l);if(String(s).startsWith('OK'))window.__actions++};const refreshed=window._onServerIndexRefreshed;window._onServerIndexRefreshed=(r)=>{refreshed(r);window.__refreshes++};window._onError=(e)=>{window.__testError=String(e)};");
                        if (reopened)
                            await controller!.HandleJsonMessageAsync("""{"method":"loadApps","args":[]}""");
                        else
                        {
                            await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('main-area').dispatchEvent(new KeyboardEvent('keydown',{key:'F5',bubbles:true}));");
                            await WaitForLibraryScriptAsync(view, "window.__refreshes===1");
                        }
                    }
                    await OpenLibraryAsync();

                    async Task ClickSidebarAsync(string name, int files, int folders)
                    {
                        await view.CoreWebView2.ExecuteScriptAsync($"document.getElementById('sidebar-{name}').click()");
                        await WaitForLibraryScriptAsync(view, $"activeLibraryView==='{name}'&&folderLoadingPath===''&&document.querySelectorAll('#files-grid .file-card:not(.folder-card)').length==={files}&&document.querySelectorAll('#files-grid .folder-card').length==={folders}");
                    }
                    async Task DeleteVisibleAsync(string name, bool folder, bool shift, int? expected)
                    {
                        await view.CoreWebView2.ExecuteScriptAsync($"clearSelection();window.__testError='';var card=[...document.querySelectorAll('#files-grid .file-card')].find(c=>c.textContent.includes('{name}'));card.focus();card.dispatchEvent(new KeyboardEvent('keydown',{{key:' ',bubbles:true}}));card.dispatchEvent(new KeyboardEvent('keydown',{{key:'Delete',shiftKey:{shift.ToString().ToLowerInvariant()},bubbles:true}}));");
                        await RequireLibraryScriptAsync(view, "!document.getElementById('confirm-overlay').classList.contains('hidden')", "Delete não abriu a confirmação visível.");
                        await RequireLibraryScriptAsync(view, $"document.getElementById('confirm-ok').textContent==='{(shift ? "Apagar permanentemente" : "Mover para a lixeira")}'", "Delete e Shift+Delete usam a mesma ação.");
                        await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('confirm-ok').click()");
                        if (expected >= 0) await WaitForLibraryScriptAsync(view, $"window.__actions==={expected}");
                        else if (expected != null) await WaitForLibraryScriptAsync(view, "window.__testError.length>0");
                    }
                    async Task CheckTrashSerialAsync(int children, string? stableId = null)
                    {
                        var mapping = store.LoadFolderIndexMappings()[AppRegistry.TrashFolderId];
                        var remote = DecodePublishedSerial(mapping.SerialMessageId);
                        var reopened = new SerialIndexService(discord, store, settings);
                        var cached = await reopened.LoadCachedFolderAsync(AppRegistry.TrashFolderId);
                        if (remote.Kind != FolderSerialKind.Folder || remote.FolderId != "trash" || remote.Children.Count != children || cached?.Children.Count != children || store.LoadCachedEntriesForFolder("trash").Count != children)
                            throw new InvalidDataException("A lixeira não acompanha o serial remoto, persistente e SQLite.");
                        if (stableId != null && stableId != mapping.SerialMessageId)
                            throw new InvalidDataException("A lixeira trocou o ID da mensagem em vez de substituir o .bin.");
                        if (mapping.SerialSha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(handler.Serials[mapping.SerialMessageId])).ToLowerInvariant())
                            throw new InvalidDataException("O mapping da lixeira não acompanha o SHA-256 do serial.");
                    }
                    FolderSerial DecodePublishedSerial(string messageId)
                    {
                        var transport = new SerialMetadataTransport(MetadataCodec.ForStore(store));
                        return new FolderSerialCodec().DecodeFolder(transport.Decode(handler.Serials[messageId]));
                    }
                    async Task WaitForPhysicalDeletionAsync(string threadId)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        while (!handler.DeletedChannels.Contains(threadId)) await Task.Delay(50, timeout.Token);
                    }

                    await ClickSidebarAsync("favorites", 1, 0);
                    handler.RejectTrashWrites = true;
                    await DeleteVisibleAsync("Favorito.png", false, false, -1);
                    if (handler.Entry("root-thread", "file-a") == null || handler.DeletedChannels.Count != 0)
                        throw new InvalidDataException("Falha no destino removeu o arquivo original.");
                    await RequireLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .file-card').length===1", "Falha no envio ocultou o favorito.");
                    handler.RejectTrashWrites = false;
                    var postGate = handler.TrashPostGate = new RemoteWriteGate();
                    var resumedPost = new RemoteWriteGate();
                    var deleteGate = handler.RootDeleteGate = new RemoteWriteGate();
                    await DeleteVisibleAsync("Favorito.png", false, false, null);
                    try
                    {
                        await postGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        await WaitForLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .file-card').length===0&&pendingLibraryChanges.length===1");
                        var reopenedStore = new SettingsStore(store.DataDirectory);
                        var queued = reopenedStore.LoadLibraryMutations("libraryMutations:names:folders:files:serial");
                        if (queued.Count != 1 || queued[0].Relocations?.Single().FilePayload?.Chunks?.Single() != "payload-part" ||
                            handler.Entry("root-thread", "file-a") == null)
                            throw new InvalidDataException("O payload da mudança local não foi persistido antes da rede.");
                        await controller.DisposeAsync();
                        controller = null;
                        await postGate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                        handler.TrashPostGate = resumedPost;
                        discord = CreateDiscord();
                        controller = CreateController(reopenedStore, discord);
                        await OpenLibraryAsync(reopened: true);
                        await resumedPost.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        await ClickSidebarAsync("favorites", 0, 0);
                        await RequireLibraryScriptAsync(view, "pendingLibraryChanges.length===1", "Reabrir perdeu a mudança pendente da interface.");
                        resumedPost.Released.TrySetResult();
                        await deleteGate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                        await CheckTrashSerialAsync(1);
                        var rootSerialId = store.LoadFolderIndexMappings()["root"].SerialMessageId;
                        if (DecodePublishedSerial(rootSerialId).Children.Any(child => child.Id == "file-a") ||
                            handler.Entry("root-thread", "file-a") == null)
                            throw new InvalidDataException("O serial não foi atualizado antes da remoção da mensagem antiga.");
                    }
                    finally { postGate.Released.TrySetResult(); resumedPost.Released.TrySetResult(); deleteGate.Released.TrySetResult(); }
                    await WaitForLibraryScriptAsync(view, "window.__actions===1&&pendingLibraryChanges.length===0");
                    handler.TrashPostGate = null; handler.RootDeleteGate = null;
                    await RequireLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .file-card').length===0", "O item enviado à lixeira continua nos favoritos.");
                    if (handler.DeletedChannels.Count != 0 || handler.Entry("root-thread", "file-a") != null)
                        throw new InvalidDataException("Delete apagou payloads ou deixou metadata na origem.");
                    var trashed = handler.Entry("trash-thread", "file-a") as FileMetadata;
                    if (trashed?.Trash?.OriginalParentFolderId != "root" || !DateTimeOffset.TryParse(trashed.Trash.DeletedAt, out _) ||
                        trashed.Chunks?.Single() != "payload-part" || trashed.Encryption?.Salt != "AAAAAAAAAAAAAAAAAAAAAA" ||
                        trashed.Preview?.ThumbnailMessageId != "thumb-part" || trashed.Checksums?.Chunks.Single() != new string('1', 64))
                        throw new InvalidDataException("O envio à lixeira perdeu os metadados completos.");
                    await ClickSidebarAsync("trash", 1, 0);
                    await CheckTrashSerialAsync(1);
                    var trashId = store.LoadFolderIndexMappings()["trash"].SerialMessageId;
                    await RequireLibraryScriptAsync(view, "document.getElementById('current-folder-name').textContent==='Lixeira'&&document.getElementById('sidebar-trash').getAttribute('aria-current')==='page'", "A aba Lixeira não apareceu na interface.");
                    var screenshot = Path.Combine(Path.GetTempPath(), "organessonium-trash-ui-" + Guid.NewGuid().ToString("N") + ".png");
                    using (var output = File.Create(screenshot)) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                    Console.WriteLine("Trash UI screenshot: " + screenshot);
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#files-grid .file-card').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:300,clientY:240}));document.getElementById('ctx-restore').click()");
                    await WaitForLibraryScriptAsync(view, "window.__actions===2");
                    if (handler.Entry("root-thread", "file-a") is not FileMetadata { Trash: null, Favorite: true } || handler.DeletedChannels.Count != 0)
                        throw new InvalidDataException("Restaurar não preservou o arquivo e seu favorito.");
                    await CheckTrashSerialAsync(0, trashId);
                    await ClickSidebarAsync("favorites", 1, 0);
                    await ClickSidebarAsync("files", 1, 2);
                    await DeleteVisibleAsync("Favorito.png", false, true, 3);
                    await WaitForPhysicalDeletionAsync("payload-a");
                    if (!handler.DeletedChannels.SequenceEqual(new[] { "payload-a" }))
                        throw new InvalidDataException("Shift+Delete não apagou somente o payload selecionado.");
                    await CheckTrashSerialAsync(0, trashId);
                    await ClickSidebarAsync("favorites", 0, 0);
                    await ClickSidebarAsync("files", 0, 2);
                    await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('#files-grid .folder-card')].find(c=>c.textContent.includes('Fotos')).click()");
                    await WaitForLibraryScriptAsync(view, "currentPath==='/Fotos'&&folderLoadingPath===''&&document.querySelectorAll('#files-grid .file-card').length===1");
                    await view.CoreWebView2.ExecuteScriptAsync("openFileDetails(allApps.find(entry=>entry.fileId==='file-b'),false)");
                    await WaitForLibraryScriptAsync(view, "[...$('file-details-content').querySelectorAll('div')].some(row=>row.querySelector('dt')?.textContent===t('detailsSize')&&row.querySelector('dd')?.textContent===fmtSize(100))&&[...$('file-details-content').querySelectorAll('div')].some(row=>row.querySelector('dt')?.textContent===t('detailsStorage')&&row.querySelector('dd')?.textContent===fmtSize(150))");
                    await WaitForLibraryScriptAsync(view, "$('storage-display').textContent===fmtSize(350)");
                    await view.CoreWebView2.ExecuteScriptAsync("closeFileDetails();moveIdsToPath([allApps.find(entry=>entry.fileId==='file-b').metadataMessageId],'/Arquivo')");
                    await WaitForLibraryScriptAsync(view, "window.__actions===4");
                    if (handler.Entry("archive-thread", "file-b") is not FileMetadata { ParentFolderId: "archive" } ||
                        handler.Entry("photos-thread", "file-b") != null)
                        throw new InvalidDataException("Mover não publicou o arquivo no destino.");
                    await view.CoreWebView2.ExecuteScriptAsync("moveIdsToPath([allApps.find(entry=>entry.fileId==='file-b').metadataMessageId],'/Fotos')");
                    await WaitForLibraryScriptAsync(view, "window.__actions===5");
                    await ClickSidebarAsync("files", 0, 2);
                    await DeleteVisibleAsync("Fotos", true, false, 6);
                    if (handler.Entry("photos-thread", "file-b") == null || handler.DeletedChannels.Count != 1)
                        throw new InvalidDataException("Mover uma pasta apagou seus filhos.");
                    await view.CoreWebView2.ExecuteScriptAsync("$('search-input').value='inside-photos';$('search-input').dispatchEvent(new Event('input'))");
                    await WaitForLibraryScriptAsync(view, "!$('library-search-popup').hidden&&$('library-search-status').textContent===t('searchEmpty')");
                    await view.CoreWebView2.ExecuteScriptAsync("LibrarySearch.clear()");
                    await ClickSidebarAsync("trash", 0, 1);
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#files-grid .folder-card').dispatchEvent(new KeyboardEvent('keydown',{key:' ',bubbles:true}));document.getElementById('main-area').dispatchEvent(new KeyboardEvent('keydown',{key:'Delete',bubbles:true}))");
                    await RequireLibraryScriptAsync(view, "document.getElementById('confirm-overlay').classList.contains('hidden')", "Delete na lixeira ofereceu uma exclusão permanente implícita.");
                    await ClickSidebarAsync("files", 0, 1);
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#files-grid .folder-card').click()");
                    await WaitForLibraryScriptAsync(view, "currentPath==='/Arquivo'&&folderLoadingPath===''");
                    await DeleteVisibleAsync("Fotos", true, false, 7);
                    await ClickSidebarAsync("trash", 0, 2);
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('main-area').dispatchEvent(new KeyboardEvent('keydown',{key:'F5',bubbles:true}))");
                    await WaitForLibraryScriptAsync(view, "window.__refreshes===1");
                    await CheckTrashSerialAsync(2, trashId);
                    await RequireLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .folder-card').length===2", "Pastas apagadas com nomes iguais foram combinadas.");
                    if (handler.DeletedChannels.Count != 1) throw new InvalidDataException("F5 removeu índices ou payloads da lixeira.");
                    await view.CoreWebView2.ExecuteScriptAsync("window.__opened=allApps.find(a=>a.metadataMessageId===document.querySelector('#files-grid .folder-card').dataset.key).folderId;document.querySelector('#files-grid .folder-card').click()");
                    await WaitForLibraryScriptAsync(view, "currentPath==='/@trash/Fotos'&&folderLoadingPath===''");
                    await RequireLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .file-card:not(.folder-card)').length===1&&document.querySelector('#files-grid .file-card').textContent.includes(window.__opened==='photos'?'inside-photos':'inside-other')", "Abrir uma pasta da lixeira misturou filhos de outra pasta com o mesmo nome.");
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('btn-back').click()");
                    await WaitForLibraryScriptAsync(view, "currentPath==='/@trash'&&folderLoadingPath===''");
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('btn-forward').click()");
                    await WaitForLibraryScriptAsync(view, "currentPath==='/@trash/Fotos'&&folderLoadingPath===''");
                    await RequireLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .file-card:not(.folder-card)').length===1&&currentFolderIds[0]===window.__opened", "O histórico perdeu a identidade da pasta aberta.");
                    await ClickSidebarAsync("trash", 0, 2);
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('main-area').dispatchEvent(new KeyboardEvent('keydown',{key:'a',ctrlKey:true,bubbles:true}));document.getElementById('sel-restore').click()");
                    await WaitForLibraryScriptAsync(view, "window.__actions===8");
                    await CheckTrashSerialAsync(0, trashId);
                    if (handler.Entry("root-thread", "photos") is not FolderMetadata { ParentFolderId: "root", Trash: null } || handler.Entry("archive-thread", "other-photos") is not FolderMetadata { ParentFolderId: "archive", Trash: null } || handler.Entry("other-thread", "file-c") == null)
                        throw new InvalidDataException("Restaurar pastas não preservou sua árvore.");
                    await ClickSidebarAsync("files", 0, 2);
                    await DeleteVisibleAsync("Fotos", true, false, 9);
                    await ClickSidebarAsync("trash", 0, 1);
                    await RequireLibraryScriptAsync(view, "$('btn-restore-trash').offsetWidth>0&&!$('btn-restore-trash').disabled", "Restaurar tudo não apareceu na lixeira.");
                    await view.CoreWebView2.ExecuteScriptAsync("$('btn-restore-trash').click()");
                    await WaitForLibraryScriptAsync(view, "window.__actions===10&&$('btn-restore-trash').disabled&&!document.querySelector('#files-grid .file-card')");
                    await CheckTrashSerialAsync(0, trashId);
                    await ClickSidebarAsync("files", 0, 2);
                    await DeleteVisibleAsync("Fotos", true, true, 11);
                    await WaitForPhysicalDeletionAsync("payload-b");
                    if (!handler.DeletedChannels.Contains("payload-b") || !handler.DeletedChannels.Contains("photos-thread") || handler.DeletedChannels.Contains("other-thread") || handler.Entry("other-thread", "file-c") == null)
                        throw new InvalidDataException("Exclusão permanente recursiva atingiu uma pasta não selecionada.");
                    await CheckTrashSerialAsync(0, trashId);
                    Console.WriteLine("LIBRARY_MUTATION_RESULT localWhileBlocked=True resumedWithPayload=True serialBeforeOldMetadata=True restoreIndividual=True restoreSelection=True restoreAll=True previewBytes=True secondary=True");
                    complete.TrySetResult(TestOutcome.Pass());
                }
                catch (Exception ex) { complete.TrySetResult(TestOutcome.Fail(ex.ToString())); }
                finally
                {
                    if (controller != null) await controller.DisposeAsync();
                    view.Dispose(); window.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    SqliteConnection.ClearAllPools();
                    try { root.Delete(true); } catch { }
                }
            };
            window.Show(); Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return await complete.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    private sealed class RemoteWriteGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            try { await Released.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private sealed class TrashDiscordFixture : HttpMessageHandler
    {
        private readonly MetadataService _metadata = new();
        private readonly Dictionary<string, Dictionary<string, DiscordMessageDto>> _channels = new();
        private ulong _nextId = 920000000000000000;
        public Dictionary<string, byte[]> Serials { get; } = new();
        public List<string> DeletedChannels { get; } = [];
        public bool RejectTrashWrites { get; set; }
        public RemoteWriteGate? TrashPostGate { get; set; }
        public RemoteWriteGate? RootDeleteGate { get; set; }

        public TrashDiscordFixture(bool video = false)
        {
            _channels["names"] = []; _channels["serial"] = [];
            foreach (var (id, thread, parent, name) in new[] { ("root", "root-thread", "root", "root"), ("photos", "photos-thread", "root", "Fotos"), ("archive", "archive-thread", "root", "Arquivo"), ("other-photos", "other-thread", "archive", "Fotos") })
            {
                _channels[thread] = [];
                Add("names", _metadata.SerializeFolderIndex(new(id, thread, "")));
                Add(thread, _metadata.SerializeFolderIndexHeader(id, parent, name));
                if (id != "root") Add(parent == "archive" ? "archive-thread" : "root-thread", JsonSerializer.Serialize(new FolderMetadata { FolderId = id, Name = name, ParentFolderId = parent, CreatedAt = "2026-10-04T12:00:00Z" }, JsonOptions.Web));
            }
            Add("root-thread", JsonSerializer.Serialize(new FileMetadata
            {
                FileId = "file-a", ThreadId = "payload-a", Name = "Favorito.png", MimeType = "image/png", SizeBytes = 8192,
                ParentFolderId = "root", UploadedAt = "2026-10-04T12:00:00Z", Favorite = true, Chunks = ["payload-part"],
                Preview = new() { ThumbnailMessageId = "thumb-part" },
                Encryption = new() { Version = 1, Mode = "standard", Salt = "AAAAAAAAAAAAAAAAAAAAAA" },
                Checksums = video ? new() { ChunkSizeBytes = 8192, Sha256 = new string('1', 64), Chunks = [new string('1', 64)] } : null
            }, JsonOptions.Web));
            Add("photos-thread", JsonSerializer.Serialize(new FileMetadata { FileId = "file-b", ThreadId = "payload-b",
                Name = video ? "inside-photos.mp4" : "inside-photos.jpg", MimeType = video ? "video/mp4" : "image/jpeg", SizeBytes = 100,
                ParentFolderId = "photos", UploadedAt = "2026-10-04T12:00:00Z", Chunks = ["part-b"],
                Preview = video ? new() { Video480p = new() { SizeBytes = 50, Chunks = ["preview-b"] } } : null }, JsonOptions.Web));
            Add("other-thread", JsonSerializer.Serialize(new FileMetadata { FileId = "file-c", ThreadId = "payload-c", Name = "inside-other.jpg", MimeType = "image/jpeg", SizeBytes = 200, ParentFolderId = "other-photos", UploadedAt = "2026-10-04T12:00:00Z", Chunks = ["part-c"] }, JsonOptions.Web));
        }

        private DiscordMessageDto Add(string channel, string content)
        {
            var message = new DiscordMessageDto { Id = (++_nextId).ToString(), Content = content,
                Author = new DiscordUserDto { Id = "trash-fixture-bot", Bot = true } };
            _channels[channel][message.Id] = message; return message;
        }
        public FolderIndexMapping AddFavoritesIndex(string serialMessageId)
        {
            _channels["favorites-index"] = [];
            Add("favorites-index", _metadata.SerializeFolderIndexHeader("favorites", "root", "favorites"));
            var mapping = new FolderIndexMapping("favorites", "favorites-index", "", serialMessageId,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Serials[serialMessageId])).ToLowerInvariant());
            return mapping with { NamesMessageId = Add("names", _metadata.SerializeFolderIndex(mapping)).Id };
        }
        public MetadataEntry? Entry(string channel, string id) => _channels.GetValueOrDefault(channel)?.Values
            .Select(m => _metadata.ReadEnvelopeAsync(m).GetAwaiter().GetResult()?.Entry)
            .FirstOrDefault(e => e is FileMetadata f && f.FileId == id || e is FolderMetadata d && d.FolderId == id);
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions.Web), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/users/@me", StringComparison.Ordinal)) return Json(new { id = "trash-fixture-bot", bot = true });
            if (path.StartsWith("/attachments/", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Serials[path.Split('/')[^1]]) };
            var parts = path[(path.IndexOf("/channels/", StringComparison.Ordinal) + 10)..].Split('/');
            var channel = parts[0];
            if (request.Method == HttpMethod.Get)
            {
                if (parts.Length == 1 && _channels.ContainsKey(channel)) return Json(new { id = channel, type = 11, guild_id = "isolated-guild" });
                if (parts.Length == 2 && parts[1] == "messages") return Json(_channels.GetValueOrDefault(channel)?.Values.ToArray() ?? []);
                if (parts.Length == 3 && _channels.GetValueOrDefault(channel)?.TryGetValue(parts[2], out var message) == true) return Json(message);
                return new(HttpStatusCode.NotFound) { Content = new StringContent(request.Method + " " + path) };
            }
            if (request.Method == HttpMethod.Delete)
            {
                if (channel == "root-thread" && parts.Length == 3 && RootDeleteGate is { } deleteGate) await deleteGate.WaitAsync(cancellationToken);
                if (parts.Length == 3) _channels.GetValueOrDefault(channel)?.Remove(parts[2]);
                else { DeletedChannels.Add(channel); _channels.Remove(channel); }
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Patch && parts.Length == 1) return Json(new { id = channel });
            if (request.Method == HttpMethod.Post && channel == "trash-thread" && RejectTrashWrites) return new(HttpStatusCode.Forbidden);
            if (request.Method == HttpMethod.Post && channel == "trash-thread" && TrashPostGate is { } postGate) await postGate.WaitAsync(cancellationToken);
            string content;
            string? threadName = null;
            byte[]? bytes = null;
            if (request.Content is MultipartFormDataContent multipart)
            {
                using var payload = JsonDocument.Parse(await multipart.First(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "payload_json").ReadAsStringAsync(cancellationToken));
                content = payload.RootElement.GetProperty("content").GetString()!;
                bytes = await multipart.First(p => !string.IsNullOrEmpty(p.Headers.ContentDisposition?.FileName)).ReadAsByteArrayAsync(cancellationToken);
            }
            else
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                content = (channel == "folders" ? payload.RootElement.GetProperty("message") : payload.RootElement).GetProperty("content").GetString()!;
                if (channel == "folders") threadName = payload.RootElement.GetProperty("name").GetString();
            }
            if (channel == "folders")
            {
                var thread = threadName?.Contains("storage-v2", StringComparison.Ordinal) == true ? "storage-journal-thread" : "trash-thread";
                _channels[thread] = [];
                var header = Add(thread, content);
                return Json(new { id = thread, message = header });
            }
            var result = request.Method == HttpMethod.Post ? Add(channel, content) : new DiscordMessageDto { Id = parts[2], Content = content };
            if (bytes != null)
            {
                Serials[result.Id] = bytes;
                result.Attachments = [new DiscordAttachmentDto { Id = result.Id,
                    FileName = content.StartsWith("library-operation-v1:", StringComparison.Ordinal) || content == "metadata" ? "metadata.json" : "folder.bin",
                    Size = bytes.Length, Url = "https://trash.fixture/attachments/" + result.Id }];
            }
            _channels[channel][result.Id] = result;
            return Json(result);
        }
    }
}
