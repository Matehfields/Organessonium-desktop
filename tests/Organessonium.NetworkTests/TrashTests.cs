using System.Net;
using System.Reflection;
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

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    // The UI, bridge, controller, SQLite and OSER writer are real. Only the remote transport is simulated.
    private static async Task<TestOutcome> WebViewTrashLifecycleAsync()
    {
        var complete = new TaskCompletionSource<TestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var window = new Window { Title = "Organessônium: lixeira", Width = 1280, Height = 800 };
            var view = new WebView2();
            window.Content = view;
            var dispatcher = Dispatcher.CurrentDispatcher;
            window.Loaded += async (_, _) =>
            {
                var root = Directory.CreateTempSubdirectory("organessonium-trash-test-");
                AppController? controller = null;
                try
                {
                    using var handler = new TrashDiscordFixture();
                    using var discord = new DiscordApiClient("trash-test-token", new HttpClient(handler));
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
                    controller = new AppController(window, store, bridge, _ => Task.CompletedTask);
                    var session = new DiscordSession();
                    typeof(DiscordSession).GetProperty(nameof(DiscordSession.Api))!.SetValue(session, discord);
                    controller.SetSession(session);
                    view.CoreWebView2.WebMessageReceived += async (_, e) =>
                    {
                        using var message = JsonDocument.Parse(e.WebMessageAsJson);
                        var method = message.RootElement.GetProperty("method").GetString();
                        if (method is "refreshServerIndex" or "refreshCurrentFolder" or "setCurrentPath" or "deleteItems" or "restoreItems" or "loadFavorites")
                            await controller.HandleJsonMessageAsync(e.WebMessageAsJson);
                    };
                    var navigation = new TaskCompletionSource();
                    view.CoreWebView2.NavigationCompleted += (_, e) =>
                    {
                        if (e.IsSuccess) navigation.TrySetResult();
                        else navigation.TrySetException(new IOException("WebView test navigation: " + e.WebErrorStatus + " HTTP " + e.HttpStatusCode));
                    };
                    view.CoreWebView2.Navigate("https://organessonium.local/index.html");
                    await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await bridge.SetSettingsAsync(settings);
                    await view.CoreWebView2.ExecuteScriptAsync("window.onBridgeReady();window.__actions=0;window.__refreshes=0;window.__testError='';const status=window._onStatus;window._onStatus=(s,l)=>{status(s,l);if(String(s).startsWith('OK'))window.__actions++};const refreshed=window._onServerIndexRefreshed;window._onServerIndexRefreshed=(r)=>{refreshed(r);window.__refreshes++};window._onError=(e)=>{window.__testError=String(e)};document.getElementById('main-area').dispatchEvent(new KeyboardEvent('keydown',{key:'F5',bubbles:true}));");
                    await WaitForLibraryScriptAsync(view, "window.__refreshes===1");

                    async Task ClickSidebarAsync(string name, int files, int folders)
                    {
                        await view.CoreWebView2.ExecuteScriptAsync($"document.getElementById('sidebar-{name}').click()");
                        await WaitForLibraryScriptAsync(view, $"activeLibraryView==='{name}'&&folderLoadingPath===''&&document.querySelectorAll('#files-grid .file-card:not(.folder-card)').length==={files}&&document.querySelectorAll('#files-grid .folder-card').length==={folders}");
                    }
                    async Task DeleteVisibleAsync(string name, bool folder, bool shift, int expected)
                    {
                        await view.CoreWebView2.ExecuteScriptAsync($"clearSelection();window.__testError='';var card=[...document.querySelectorAll('#files-grid .file-card')].find(c=>c.textContent.includes('{name}'));card.focus();card.dispatchEvent(new KeyboardEvent('keydown',{{key:' ',bubbles:true}}));card.dispatchEvent(new KeyboardEvent('keydown',{{key:'Delete',shiftKey:{shift.ToString().ToLowerInvariant()},bubbles:true}}));");
                        await RequireLibraryScriptAsync(view, "!document.getElementById('confirm-overlay').classList.contains('hidden')", "Delete não abriu a confirmação visível.");
                        await RequireLibraryScriptAsync(view, $"document.getElementById('confirm-ok').textContent==='{(shift ? "Apagar permanentemente" : "Mover para a lixeira")}'", "Delete e Shift+Delete usam a mesma ação.");
                        await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('confirm-ok').click()");
                        if (expected >= 0) await WaitForLibraryScriptAsync(view, $"window.__actions==={expected}");
                        else await WaitForLibraryScriptAsync(view, "window.__testError.length>0");
                    }
                    async Task CheckTrashSerialAsync(int children, string? stableId = null)
                    {
                        var mapping = store.LoadFolderIndexMappings()[AppRegistry.TrashFolderId];
                        var remote = new FolderSerialCodec().DecodeFolder(handler.Serials[mapping.SerialMessageId]);
                        var reopened = new SerialIndexService(discord, settings, store.SerialCacheDirectory);
                        var cached = await reopened.LoadCachedFolderAsync(AppRegistry.TrashFolderId);
                        if (remote.Kind != FolderSerialKind.Folder || remote.FolderId != "trash" || remote.Children.Count != children || cached?.Children.Count != children || store.LoadCachedEntriesForFolder("trash").Count != children)
                            throw new InvalidDataException("A lixeira não acompanha o serial remoto, persistente e SQLite.");
                        if (stableId != null && stableId != mapping.SerialMessageId)
                            throw new InvalidDataException("A lixeira trocou o ID da mensagem em vez de substituir o .bin.");
                        if (mapping.SerialSha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(handler.Serials[mapping.SerialMessageId])).ToLowerInvariant())
                            throw new InvalidDataException("O mapping da lixeira não acompanha o SHA-256 do serial.");
                    }

                    await ClickSidebarAsync("favorites", 1, 0);
                    handler.RejectTrashWrites = true;
                    await DeleteVisibleAsync("Favorito.png", false, false, -1);
                    if (handler.Entry("root-thread", "file-a") == null || handler.DeletedChannels.Count != 0)
                        throw new InvalidDataException("Falha no destino removeu o arquivo original.");
                    await RequireLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .file-card').length===1", "Falha no envio ocultou o favorito.");
                    handler.RejectTrashWrites = false;
                    await DeleteVisibleAsync("Favorito.png", false, false, 1);
                    await RequireLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .file-card').length===0", "O item enviado à lixeira continua nos favoritos.");
                    if (handler.DeletedChannels.Count != 0 || handler.Entry("root-thread", "file-a") != null)
                        throw new InvalidDataException("Delete apagou payloads ou deixou metadata na origem.");
                    var trashed = handler.Entry("trash-thread", "file-a") as FileMetadata;
                    if (trashed?.Trash?.OriginalParentFolderId != "root" || !DateTimeOffset.TryParse(trashed.Trash.DeletedAt, out _) || trashed.Chunks?.Single() != "payload-part" || trashed.Encryption?.Salt != "AAAAAAAAAAAAAAAAAAAAAA" || trashed.Preview?.ThumbnailMessageId != "thumb-part")
                        throw new InvalidDataException("O envio à lixeira perdeu os metadados completos.");
                    await ClickSidebarAsync("trash", 1, 0);
                    await CheckTrashSerialAsync(1);
                    var trashId = store.LoadFolderIndexMappings()["trash"].SerialMessageId;
                    await RequireLibraryScriptAsync(view, "document.getElementById('current-folder-name').textContent==='Lixeira'&&document.getElementById('sidebar-trash').getAttribute('aria-current')==='page'", "A aba Lixeira não apareceu na interface.");
                    var screenshot = Path.Combine(Path.GetTempPath(), "organessonium-trash-ui-" + Guid.NewGuid().ToString("N") + ".png");
                    using (var output = File.Create(screenshot)) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                    Console.WriteLine("Trash UI screenshot: " + screenshot);
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#files-grid .file-card').click();document.getElementById('sel-restore').click()");
                    await WaitForLibraryScriptAsync(view, "window.__actions===2");
                    if (handler.Entry("root-thread", "file-a") is not FileMetadata { Trash: null, Favorite: true } || handler.DeletedChannels.Count != 0)
                        throw new InvalidDataException("Restaurar não preservou o arquivo e seu favorito.");
                    await CheckTrashSerialAsync(0, trashId);
                    await ClickSidebarAsync("favorites", 1, 0);
                    await ClickSidebarAsync("files", 1, 2);
                    await DeleteVisibleAsync("Favorito.png", false, true, 3);
                    if (!handler.DeletedChannels.SequenceEqual(new[] { "payload-a" }))
                        throw new InvalidDataException("Shift+Delete não apagou somente o payload selecionado.");
                    await CheckTrashSerialAsync(0, trashId);
                    await ClickSidebarAsync("favorites", 0, 0);
                    await ClickSidebarAsync("files", 0, 2);
                    await DeleteVisibleAsync("Fotos", true, false, 4);
                    if (handler.Entry("photos-thread", "file-b") == null || handler.DeletedChannels.Count != 1)
                        throw new InvalidDataException("Mover uma pasta apagou seus filhos.");
                    await view.CoreWebView2.ExecuteScriptAsync("searchQuery='inside-photos';render()");
                    await RequireLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .file-card:not(.folder-card)').length===0", "A busca normal exibe filhos da lixeira.");
                    await ClickSidebarAsync("trash", 0, 1);
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#files-grid .folder-card').dispatchEvent(new KeyboardEvent('keydown',{key:' ',bubbles:true}));document.getElementById('main-area').dispatchEvent(new KeyboardEvent('keydown',{key:'Delete',bubbles:true}))");
                    await RequireLibraryScriptAsync(view, "document.getElementById('confirm-overlay').classList.contains('hidden')", "Delete na lixeira ofereceu uma exclusão permanente implícita.");
                    await ClickSidebarAsync("files", 0, 1);
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#files-grid .folder-card').click()");
                    await WaitForLibraryScriptAsync(view, "currentPath==='/Arquivo'&&folderLoadingPath===''");
                    await DeleteVisibleAsync("Fotos", true, false, 5);
                    await ClickSidebarAsync("trash", 0, 2);
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('main-area').dispatchEvent(new KeyboardEvent('keydown',{key:'F5',bubbles:true}))");
                    await WaitForLibraryScriptAsync(view, "window.__refreshes===2");
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
                    await WaitForLibraryScriptAsync(view, "window.__actions===6");
                    await CheckTrashSerialAsync(0, trashId);
                    if (handler.Entry("root-thread", "photos") is not FolderMetadata { ParentFolderId: "root", Trash: null } || handler.Entry("archive-thread", "other-photos") is not FolderMetadata { ParentFolderId: "archive", Trash: null } || handler.Entry("other-thread", "file-c") == null)
                        throw new InvalidDataException("Restaurar pastas não preservou sua árvore.");
                    await ClickSidebarAsync("files", 0, 2);
                    await DeleteVisibleAsync("Fotos", true, true, 7);
                    if (!handler.DeletedChannels.Contains("payload-b") || !handler.DeletedChannels.Contains("photos-thread") || handler.DeletedChannels.Contains("other-thread") || handler.Entry("other-thread", "file-c") == null)
                        throw new InvalidDataException("Exclusão permanente recursiva atingiu uma pasta não selecionada.");
                    await CheckTrashSerialAsync(0, trashId);
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

    private sealed class TrashDiscordFixture : HttpMessageHandler
    {
        private readonly MetadataService _metadata = new();
        private readonly Dictionary<string, Dictionary<string, DiscordMessageDto>> _channels = new();
        private ulong _nextId = 920000000000000000;
        public Dictionary<string, byte[]> Serials { get; } = new();
        public List<string> DeletedChannels { get; } = [];
        public bool RejectTrashWrites { get; set; }

        public TrashDiscordFixture()
        {
            _channels["names"] = []; _channels["serial"] = [];
            foreach (var (id, thread, parent, name) in new[] { ("root", "root-thread", "root", "root"), ("photos", "photos-thread", "root", "Fotos"), ("archive", "archive-thread", "root", "Arquivo"), ("other-photos", "other-thread", "archive", "Fotos") })
            {
                _channels[thread] = [];
                Add("names", _metadata.SerializeFolderIndex(new(id, thread, "")));
                Add(thread, _metadata.SerializeFolderIndexHeader(id, parent, name));
                if (id != "root") Add(parent == "archive" ? "archive-thread" : "root-thread", JsonSerializer.Serialize(new FolderMetadata { FolderId = id, Name = name, ParentFolderId = parent, CreatedAt = "2026-10-04T12:00:00Z" }, JsonOptions.Web));
            }
            Add("root-thread", JsonSerializer.Serialize(new FileMetadata { FileId = "file-a", ThreadId = "payload-a", Name = "Favorito.png", MimeType = "image/png", SizeBytes = 8192, ParentFolderId = "root", UploadedAt = "2026-10-04T12:00:00Z", Favorite = true, Chunks = ["payload-part"], Preview = new() { ThumbnailMessageId = "thumb-part" }, Encryption = new() { Version = 1, Mode = "standard", Salt = "AAAAAAAAAAAAAAAAAAAAAA" } }, JsonOptions.Web));
            Add("photos-thread", JsonSerializer.Serialize(new FileMetadata { FileId = "file-b", ThreadId = "payload-b", Name = "inside-photos.jpg", MimeType = "image/jpeg", SizeBytes = 100, ParentFolderId = "photos", UploadedAt = "2026-10-04T12:00:00Z", Chunks = ["part-b"] }, JsonOptions.Web));
            Add("other-thread", JsonSerializer.Serialize(new FileMetadata { FileId = "file-c", ThreadId = "payload-c", Name = "inside-other.jpg", MimeType = "image/jpeg", SizeBytes = 200, ParentFolderId = "other-photos", UploadedAt = "2026-10-04T12:00:00Z", Chunks = ["part-c"] }, JsonOptions.Web));
        }

        private DiscordMessageDto Add(string channel, string content)
        {
            var message = new DiscordMessageDto { Id = (++_nextId).ToString(), Content = content };
            _channels[channel][message.Id] = message; return message;
        }
        public MetadataEntry? Entry(string channel, string id) => _channels.GetValueOrDefault(channel)?.Values
            .Select(m => _metadata.ReadEnvelopeAsync(m).GetAwaiter().GetResult()?.Entry)
            .FirstOrDefault(e => e is FileMetadata f && f.FileId == id || e is FolderMetadata d && d.FolderId == id);
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions.Web), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/attachments/", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Serials[path.Split('/')[^1]]) };
            var parts = path[(path.IndexOf("/channels/", StringComparison.Ordinal) + 10)..].Split('/');
            var channel = parts[0];
            if (request.Method == HttpMethod.Get)
            {
                if (parts.Length == 2 && parts[1] == "messages") return Json(_channels.GetValueOrDefault(channel)?.Values.ToArray() ?? []);
                if (parts.Length == 3 && _channels.GetValueOrDefault(channel)?.TryGetValue(parts[2], out var message) == true) return Json(message);
                return new(HttpStatusCode.NotFound);
            }
            if (request.Method == HttpMethod.Delete)
            {
                if (parts.Length == 3) _channels.GetValueOrDefault(channel)?.Remove(parts[2]);
                else { DeletedChannels.Add(channel); _channels.Remove(channel); }
                return new(HttpStatusCode.NoContent);
            }
            if (request.Method == HttpMethod.Patch && parts.Length == 1) return Json(new { id = channel });
            if (request.Method == HttpMethod.Post && channel == "trash-thread" && RejectTrashWrites) return new(HttpStatusCode.Forbidden);
            string content;
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
            }
            if (channel == "folders")
            {
                _channels["trash-thread"] = [];
                var header = Add("trash-thread", content);
                return Json(new { id = "trash-thread", message = header });
            }
            var result = request.Method == HttpMethod.Post ? Add(channel, content) : new DiscordMessageDto { Id = parts[2], Content = content };
            if (bytes != null)
            {
                Serials[result.Id] = bytes;
                result.Attachments = [new DiscordAttachmentDto { Id = result.Id, FileName = "folder.bin", Size = bytes.Length, Url = "https://trash.fixture/attachments/" + result.Id }];
            }
            _channels[channel][result.Id] = result;
            return Json(result);
        }
    }
}
