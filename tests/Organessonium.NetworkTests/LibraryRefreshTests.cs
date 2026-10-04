using System.Net;
using System.Net.Http;
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
    // Real controller, bridge, SQLite, serial codec and embedded UI. Only Discord's transport is a fixture.
    private static async Task<TestOutcome> WebViewLibraryRefreshAndLayoutAsync()
    {
        var complete = new TaskCompletionSource<TestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var window = new Window { Title = "Organessônium: seleção, cards e índice", Width = 1920, Height = 1000 };
            var view = new WebView2();
            window.Content = view;
            var dispatcher = Dispatcher.CurrentDispatcher;
            window.Loaded += async (_, _) =>
            {
                var root = Directory.CreateTempSubdirectory("organessonium-library-test-");
                AppController? controller = null;
                try
                {
                    using var handler = new LibraryDiscordFixture();
                    using var discord = new DiscordApiClient("library-test-token", new HttpClient(handler));
                    var store = new SettingsStore(Directory.CreateDirectory(Path.Combine(root.FullName, "Data")));
                    var settings = new AppSettings { NamesChannelId = "names", FoldersForumId = "folders", AppsForumId = "files", SerialChannelId = "serial", SearchWorkers = 1, Theme = "dark", PreviewEnabled = false };
                    store.Save(settings);
                    var webRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "web"));
                    var assembly = typeof(AppRegistry).Assembly;
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
                    // Inject the test transport without logging in or touching the user's Discord server.
                    typeof(DiscordSession).GetProperty(nameof(DiscordSession.Api))!.SetValue(session, discord);
                    controller.SetSession(session);
                    view.CoreWebView2.WebMessageReceived += async (_, e) =>
                    {
                        using var message = JsonDocument.Parse(e.WebMessageAsJson);
                        var method = message.RootElement.GetProperty("method").GetString();
                        if (method is "refreshServerIndex" or "refreshCurrentFolder")
                            await controller.HandleJsonMessageAsync(e.WebMessageAsJson);
                    };
                    var navigation = new TaskCompletionSource();
                    view.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) navigation.TrySetResult(); else navigation.TrySetException(new IOException("Navegação falhou.")); };
                    view.CoreWebView2.Navigate("https://organessonium.local/index.html");
                    await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    await bridge.SetSettingsAsync(settings);
                    await view.CoreWebView2.ExecuteScriptAsync("window.onBridgeReady();navigate('/Fotos',false,'photos');window.__refreshes=0;const finished=window._onServerIndexRefreshed;window._onServerIndexRefreshed=(r)=>{finished(r);window.__refreshes++};window._onError=(e)=>{window.__testError=String(e)};");

                    async Task RefreshAsync(int expected, bool button)
                    {
                        await view.CoreWebView2.ExecuteScriptAsync(button
                            ? "document.getElementById('btn-refresh').click()"
                            : "document.getElementById('main-area').dispatchEvent(new KeyboardEvent('keydown',{key:'F5',bubbles:true}))");
                        await WaitForLibraryScriptAsync(view, $"window.__refreshes==={expected}");
                    }
                    async Task CheckSerialAsync(int count)
                    {
                        var mapping = store.LoadFolderIndexMappings()["photos"];
                        var remote = new FolderSerialCodec().DecodeFolder(handler.Serials[mapping.SerialMessageId]);
                        var reopened = new SerialIndexService(discord, settings, store.SerialCacheDirectory);
                        var local = await reopened.LoadCachedFolderAsync("photos");
                        if (local?.Children.Count != count || remote.Children.Count != count || store.LoadCachedEntriesForFolder("photos").Count != count)
                            throw new InvalidDataException("SQLite, serial persistente e serial remoto divergiram.");
                        if (mapping.SerialSha256 != Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(handler.Serials[mapping.SerialMessageId])).ToLowerInvariant())
                            throw new InvalidDataException("Mapping remoto/local não acompanha o SHA do serial.");
                        await RequireLibraryScriptAsync(view, $"document.querySelectorAll('.cards-grid .file-card').length==={count}", "A lista renderizada não acompanha o índice.");
                    }

                    await RefreshAsync(1, button: false);
                    await CheckSerialAsync(24);
                    var initialWrites = handler.SerialWrites;
                    await RefreshAsync(2, button: false);
                    if (handler.SerialWrites != initialWrites) throw new InvalidDataException("F5 republicou uma pasta preenchida sem alterações.");
                    var serialId = store.LoadFolderIndexMappings()["photos"].SerialMessageId;
                    handler.Photos.Add(LibraryDiscordFixture.Photo(25));
                    await RefreshAsync(3, button: true);
                    await CheckSerialAsync(25);
                    if (store.LoadFolderIndexMappings()["photos"].SerialMessageId != serialId) throw new InvalidDataException("Atualizar trocou o ID do serial.");
                    handler.Photos.Clear();
                    await RefreshAsync(4, button: false);
                    await CheckSerialAsync(0);
                    // Even a completely empty, successfully fetched thread must remove the last cached file.
                    handler.IncludePhotoHeader = false;
                    await RefreshAsync(5, button: true);
                    await CheckSerialAsync(0);
                    var writes = handler.SerialWrites;
                    await RefreshAsync(6, button: false);
                    if (handler.SerialWrites != writes) throw new InvalidDataException("F5 republicou seriais sem alterações.");
                    if (handler.Creates != 2 || handler.Replacements != 2) throw new InvalidDataException("Não reutilizou as mensagens dos seriais alterados.");
                    if (handler.MappingUpdates != 4) throw new InvalidDataException("Repasse sem mudança regravou mappings.");

                    var cards = Enumerable.Range(1, 600).Select(i => new { type = "file", fileId = i.ToString("x32"), threadId = (800000000000000000UL + (ulong)i).ToString(), metadataMessageId = i.ToString(), name = $"Imagem {i:0000}.jpg", path = "/Fotos", parentFolderId = "photos", sizeBytes = 8192 }).ToArray();
                    await bridge.CallAsync("_onAppsLoaded", (object)cards);
                    var columns = new List<int>();
                    foreach (var width in new[] { 960d, 1920d, 2560d })
                    {
                        window.Width = width;
                        await Task.Delay(180);
                        columns.Add(JsonSerializer.Deserialize<int>(await view.CoreWebView2.ExecuteScriptAsync("getComputedStyle(document.querySelector('.cards-grid')).gridTemplateColumns.split(' ').length")));
                    }
                    if (!(columns[0] < columns[1] && columns[1] == 6 && columns[2] > columns[1])) throw new InvalidDataException("Densidade não acompanha a largura da janela: " + string.Join(",", columns));
                    window.Width = 1920;
                    await view.CoreWebView2.ExecuteScriptAsync("window.__layoutError='';window.__square=(cards)=>{for(const card of cards){const m=card.querySelector('.card-media').getBoundingClientRect();if(Math.abs(m.width-m.height)>1||m.width<150)window.__layoutError='prévia não quadrada: '+m.width+'x'+m.height}};");
                    await view.CoreWebView2.ExecuteScriptAsync("(async()=>{const main=document.getElementById('main-area');for(let i=0;i<30;i++){main.scrollTop=(i%2?0.85:0.15)*main.scrollHeight;await new Promise(requestAnimationFrame);window.__square(document.querySelectorAll('.cards-grid .file-card'));}window.__scrolled=true})()");
                    await WaitForLibraryScriptAsync(view, "window.__scrolled===true");
                    await RequireLibraryScriptAsync(view, "window.__layoutError===''", "Cards mudaram de proporção durante a rolagem.");
                    // Deliver thumbnails through the same callback used by the app, after cards have been scrolled.
                    var thumbnail = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg' width='320' height='120'><rect width='320' height='120' fill='#306c92'/></svg>"));
                    foreach (var card in cards.Take(18)) await bridge.SetThumbAsync(card.threadId, thumbnail);
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('main-area').scrollTop=0;window.__square(document.querySelectorAll('.cards-grid .file-card'));");
                    await RequireLibraryScriptAsync(view, "window.__layoutError===''&&document.querySelectorAll('.card-preview-img').length===18", "Carregar miniaturas mudou o tamanho reservado.");
                    await view.CoreWebView2.ExecuteScriptAsync("""
                        (async()=>{
                            const main=document.getElementById('main-area'),r=main.getBoundingClientRect(),h=main.querySelector('.content-header').getBoundingClientRect();
                            main.dispatchEvent(new MouseEvent('mousedown',{button:0,clientX:r.left+10,clientY:h.bottom+25,bubbles:true}));
                            document.dispatchEvent(new MouseEvent('mousemove',{clientX:r.right+150,clientY:r.bottom+150,bubbles:true}));
                            window.__clipError='';
                            const checkClip=()=>{
                                const s=marqueeSurfaceBounds(),q=document.querySelector('.selection-rect').getBoundingClientRect();
                                if(q.left<s.left-1||q.top<s.top-1||q.right>s.right+1||q.bottom>s.bottom+1)window.__clipError='seleção escapou';
                            };
                            for(let i=0;i<35;i++){await new Promise(requestAnimationFrame);checkClip();}
                            window.__selectionVisible=selectedKeys.size>0&&!document.getElementById('selection-bar').classList.contains('hidden');
                            document.dispatchEvent(new MouseEvent('mousemove',{clientX:r.left-100,clientY:r.top-100,bubbles:true}));
                            for(let i=0;i<35;i++){await new Promise(requestAnimationFrame);checkClip();}
                            document.dispatchEvent(new MouseEvent('mouseup',{bubbles:true}));
                            main.scrollTop=0;
                            window.__dragged=true;
                        })()
                        """);
                    await WaitForLibraryScriptAsync(view, "window.__dragged===true");
                    await RequireLibraryScriptAsync(view, "window.__clipError===''&&window.__selectionVisible===true", "Seleção não ficou contida ou não apareceu na interface.");
                    if (int.TryParse(Environment.GetEnvironmentVariable("ORGANESSONIUM_LIBRARY_TEST_OBSERVE_SECONDS"), out var seconds))
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 45)));
                    complete.TrySetResult(TestOutcome.Pass());
                }
                catch (Exception ex) { complete.TrySetResult(TestOutcome.Fail(ex.ToString())); }
                finally
                {
                    if (controller != null) await controller.DisposeAsync();
                    view.Dispose();
                    window.Close();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    SqliteConnection.ClearAllPools();
                    try { root.Delete(true); } catch { }
                }
            };
            window.Show();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return await complete.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    private static async Task RequireLibraryScriptAsync(WebView2 view, string condition, string message)
    {
        if (await view.CoreWebView2.ExecuteScriptAsync(condition) != "true")
            throw new InvalidDataException(message + " " + await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify({error:window.__testError,layout:window.__layoutError,clip:window.__clipError})"));
    }

    private static async Task WaitForLibraryScriptAsync(WebView2 view, string condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (await view.CoreWebView2.ExecuteScriptAsync(condition) != "true")
        {
            if (timeout.IsCancellationRequested) throw new TimeoutException(condition + " " + await view.CoreWebView2.ExecuteScriptAsync("window.__testError||'sem resposta'"));
            await Task.Delay(50);
        }
    }

    private sealed class LibraryDiscordFixture : HttpMessageHandler
    {
        private readonly MetadataService _metadata = new();
        private readonly Dictionary<string, FolderIndexMapping> _mappings = new()
        {
            ["root"] = new("root", "root-thread", "900000000000000001"),
            ["photos"] = new("photos", "photos-thread", "900000000000000002")
        };
        public List<FileMetadata> Photos { get; } = Enumerable.Range(1, 24).Select(Photo).ToList();
        public bool IncludePhotoHeader { get; set; } = true;
        public Dictionary<string, byte[]> Serials { get; } = new();
        public int Creates { get; private set; }
        public int Replacements { get; private set; }
        public int MappingUpdates { get; private set; }
        public int SerialWrites => Creates + Replacements;
        public static FileMetadata Photo(int i) => new()
        {
            FileId = i.ToString("x32"), ThreadId = (800000000000000000UL + (ulong)i).ToString(), Name = $"Foto {i:00}.jpg",
            MimeType = "image/jpeg", SizeBytes = 8192, UploadedAt = "2026-10-04T12:00:00Z", ParentFolderId = "photos", Chunks = ["800000000000009999"]
        };
        private static DiscordMessageDto Message(string id, string content) => new() { Id = id, Content = content };
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions.Web), Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/channels/names/messages"))
                return Json(_mappings.Values.Select(m => Message(m.NamesMessageId, _metadata.SerializeFolderIndex(m))).ToArray());
            if (request.Method == HttpMethod.Get && path.EndsWith("/channels/root-thread/messages"))
                return Json(new[] { Message("900000000000000010", JsonSerializer.Serialize(new FolderIndexHeaderMetadata { FolderId = "root", Name = "root" }, JsonOptions.Web)), Message("900000000000000011", JsonSerializer.Serialize(new FolderMetadata { FolderId = "photos", Name = "Fotos", ParentFolderId = "root", CreatedAt = "2026-10-04T12:00:00Z" }, JsonOptions.Web)) });
            if (request.Method == HttpMethod.Get && path.EndsWith("/channels/photos-thread/messages"))
            {
                var entries = Photos.Select((p, i) => Message((900000000000000100UL + (ulong)i).ToString(), JsonSerializer.Serialize(p, JsonOptions.Web))).ToList();
                if (IncludePhotoHeader) entries.Add(Message("900000000000000099", JsonSerializer.Serialize(new FolderIndexHeaderMetadata { FolderId = "photos", Name = "Fotos" }, JsonOptions.Web)));
                return Json(entries);
            }
            if (request.Method == HttpMethod.Get && path.Contains("/channels/serial/messages/"))
                return Json(Message(path.Split('/')[^1], ""));
            if ((request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch) && path.Contains("/channels/serial/messages"))
            {
                var multipart = (MultipartFormDataContent)request.Content!;
                var part = multipart.First(p => !string.IsNullOrEmpty(p.Headers.ContentDisposition?.FileName));
                var bytes = await part.ReadAsByteArrayAsync(cancellationToken);
                var id = request.Method == HttpMethod.Post ? (910000000000000000UL + (ulong)++Creates).ToString() : path.Split('/')[^1];
                if (request.Method == HttpMethod.Patch) Replacements++;
                Serials[id] = bytes;
                return Json(Message(id, ""));
            }
            if (request.Method == HttpMethod.Patch && path.Contains("/channels/names/messages/"))
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var content = payload.RootElement.GetProperty("content").GetString()!;
                var message = Message(path.Split('/')[^1], content);
                var envelope = await _metadata.ReadEnvelopeAsync(message, cancellationToken);
                var mapping = (FolderIndexMetadata)envelope!.Entry;
                _mappings[mapping.FolderId] = new(mapping.FolderId, mapping.ThreadId, message.Id, mapping.SerialMessageId ?? "", mapping.SerialSha256 ?? "");
                MappingUpdates++;
                return Json(message);
            }
            if (request.Method == HttpMethod.Patch && path.Contains("/channels/") && !path.Contains("/messages")) return Json(new { id = path.Split('/')[^1] });
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
