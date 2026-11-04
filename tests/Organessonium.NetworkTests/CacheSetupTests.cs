using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
    private static async Task<TestOutcome> EncryptedLocalPreviewsAndLogoutAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium-cache-regression-");
        try
        {
            var previews = root.CreateSubdirectory("Previews");
            await File.WriteAllTextAsync(Path.Combine(previews.FullName, "old.pdf"), "%PDF-legacy-private-data");
            var store = new SettingsStore(root);
            RequireSync(!File.Exists(Path.Combine(previews.FullName, "old.pdf")), "a prévia antiga em claro foi mantida");
            var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 71);
            var file = store.GetPreviewCacheFile("account-file", "full", "private.pdf");
            using (var input = new MemoryStream(bytes)) await EncryptedPreviewCache.StoreAsync(file, input);
            RequireSync(File.ReadAllBytes(file.FullName).AsSpan().IndexOf(bytes.AsSpan(0, 256)) < 0 &&
                EncryptedPreviewCache.IsEncrypted(file), "o cache foi gravado em claro");
            using var registry = new PreviewResourceRegistry();
            var url = registry.Register(store.GetCachedPreviewFile("account-file", "full", "private.pdf")!, "application/pdf");
            var range = await registry.OpenAsync(url, "GET", "bytes=65503-132001");
            await using (var content = range.Content!)
            {
                using var output = new MemoryStream(); await content.CopyToAsync(output);
                RequireSync(range.Status == 206 && output.ToArray().SequenceEqual(bytes[65503..132002]), "o leitor cifrado não serviu a faixa exata do PDF");
            }
            var thumbnail = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jBzEAAAAASUVORK5CYII=");
            var thumbnailUrl = ThumbnailCache.Store("photo", thumbnail); ThumbnailCache.Clear();
            RequireSync(thumbnailUrl != null && ThumbnailCache.Get("photo") == thumbnailUrl &&
                previews.GetFiles().All(EncryptedPreviewCache.IsEncrypted), "a miniatura não voltou do disco cifrado");
            var image = await registry.OpenAsync(thumbnailUrl!, "GET", null);
            await using (var content = image.Content!)
            {
                using var output = new MemoryStream(); await content.CopyToAsync(output);
                RequireSync(image.Status == 200 && image.Headers.Contains("Content-Type: image/png") &&
                    thumbnailUrl!.StartsWith("https://" + PreviewResourceRegistry.Host + "/thumbnail/") &&
                    output.ToArray().SequenceEqual(thumbnail), "a miniatura não foi entregue como imagem binária autenticada");
            }
            var rejected = await registry.OpenAsync("https://" + PreviewResourceRegistry.Host + "/thumbnail/%2e%2e%2fsettings", "GET", null);
            RequireSync(rejected.Status == 404, "a rota de miniaturas aceitou um caminho fora do cache");
            using var document = new MemoryStream();
            using (var zip = new ZipArchive(document, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
                writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>Cache protegido</w:t></w:r></w:p></w:body></w:document>");
            var office = store.GetPreviewCacheFile("document", "full", "private.docx");
            EncryptedPreviewCache.Store(office, document.ToArray());
            var rendered = (string)typeof(AppController).GetMethod("ExtractDocxPreview", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [office])!;
            RequireSync(rendered.Contains("Cache protegido"), "o documento cifrado não produziu sua prévia");
            // Reject altered local ciphertext before exposing the affected block.
            var damaged = File.ReadAllBytes(file.FullName); damaged[^1] ^= 1; await File.WriteAllBytesAsync(file.FullName, damaged);
            try
            {
                using var invalid = EncryptedPreviewCache.OpenRead(file); invalid.Position = invalid.Length - 1;
                invalid.ReadByte(); return TestOutcome.Fail("cache adulterado entregou plaintext");
            }
            catch (AuthenticationTagMismatchException) { }
            registry.Dispose();
            store.SaveLibraryStatistics(new LibraryStatistics(7, 2, 12345));
            store.ClearLocalDataForLogout();
            RequireSync(!Directory.Exists(previews.FullName) && store.LoadLibraryStatistics() == null &&
                string.IsNullOrEmpty(store.Load().BotToken) && ThumbnailCache.Get("photo") == null,
                "o logout conservou cache, totais ou configuração da conta");
            return TestOutcome.Pass();
        }
        finally { ThumbnailCache.Clear(); SqliteConnection.ClearAllPools(); try { root.Delete(true); } catch (IOException) { } }
    }

    private static async Task<TestOutcome> CachedSerialStatisticsAtStartupAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium-startup-stats-");
        try
        {
            var store = new SettingsStore(root);
            var codec = new FolderSerialCodec();
            async Task Save(FolderSerial serial) => await File.WriteAllBytesAsync(Path.Combine(store.SerialCacheDirectory.FullName,
                "serial_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial.FolderId))).ToLowerInvariant() + ".bin"), codec.EncodeFolder(serial, 3));
            await Save(new FolderSerial { FolderId = "root", Aggregate = new() { TotalBytes = 20L * 1024 * 1024 * 1024 },
                Children = [new() { Id = "photo", Name = "photo.jpg" }, new() { Id = "folder", IsFolder = true, Name = "Photos" }] });
            await Save(new FolderSerial { FolderId = "folder", Aggregate = new() { TotalBytes = 40L * 1024 * 1024 * 1024 },
                Children = [new() { Id = "video", Name = "video.mov" }] });
            using var api = new DiscordApiClient("isolated-cache-test", new HttpClient(new RejectStartupNetwork()));
            var timer = Stopwatch.StartNew();
            var statistics = await new AppRegistry(api, store, new AppSettings()).GetCachedLibraryStatisticsAsync();
            RequireSync(statistics is { FileCount: 2, FolderCount: 1, TotalBytes: 64424509440 }, "os totais locais ignoraram uma pasta/versão ou dependeram da rede");
            store.SaveLibraryStatistics(statistics!);
            RequireSync(new SettingsStore(root).LoadLibraryStatistics() == statistics, "o agregado não sobreviveu à reabertura");
            Console.WriteLine("METRIC cached startup statistics ms=" + timer.ElapsedMilliseconds);
            var nested = Path.Combine(store.SerialCacheDirectory.FullName, "serial_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("folder"))).ToLowerInvariant() + ".bin");
            await File.WriteAllBytesAsync(nested, "invalid-serial"u8.ToArray());
            RequireSync(await new AppRegistry(api, store, new AppSettings()).GetCachedLibraryStatisticsAsync() == null &&
                store.LoadLibraryStatistics() == statistics, "serial local inválido apagou o último total ou bloqueou o fallback");
            return TestOutcome.Pass();
        }
        finally { SqliteConnection.ClearAllPools(); try { root.Delete(true); } catch (IOException) { } }
    }

    private sealed class RejectStartupNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A abertura dos seriais locais tentou acessar a rede.");
    }

    private static Task<TestOutcome> SetupAndEncryptedPreviewHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var root = Directory.CreateTempSubdirectory("organessonium-setup-ui-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, root.CreateSubdirectory("browser").FullName);
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true; browser.Bounds = new System.Drawing.Rectangle(0, 0, 1000, 760);
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
            async Task Navigate(string page)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Loaded(object? sender, CoreWebView2NavigationCompletedEventArgs args) => done.TrySetResult();
                view.NavigationCompleted += Loaded; view.Navigate("https://organessonium.local/" + page);
                await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); view.NavigationCompleted -= Loaded;
            }
            async Task Check(string script, string message) => RequireSync(await view.ExecuteScriptAsync(script) == "true", message);
            await Navigate("setup.html");
            await view.ExecuteScriptAsync("window.__organessoniumSettings={language:'pt-BR',theme:'dark'};window.onBridgeReady();window._onNativeTitleBarMetrics({enabled:true,height:48});");
            await Check("getComputedStyle(proxyPanel).display==='none'&&getComputedStyle(btnOpenProxy).display==='none'&&getComputedStyle(btnToggleToken).opacity==='1'", "proxy visível sem erro ou botão do token oculto");
            await view.ExecuteScriptAsync("inpToken.value='long-token-value-'.repeat(6);window._onSetupError('Falha de DNS',true);btnOpenProxy.click();document.querySelector('#sel-proxy-mode + .setup-select-trigger').click();");
            await Check("!proxyPanel.classList.contains('hidden')&&!document.querySelector('.setup-select-options').classList.contains('hidden')&&getComputedStyle(document.querySelector('.setup-select-options')).borderRadius==='10px'", "o erro de conexão não abriu proxy/seletor temático");
            await view.ExecuteScriptAsync("document.querySelectorAll('#sel-proxy-mode ~ .setup-select-options button')[2].click();");
            await Check("selProxyMode.value==='manual'&&!proxyManual.classList.contains('hidden')", "escolher proxy manual não mostrou os campos");
            await Check("document.querySelector('.page').getBoundingClientRect().top>=48&&document.documentElement.scrollHeight===innerHeight&&getComputedStyle(document.querySelector('.page')).overflowY==='auto'", "a barra de rolagem invadiu a barra nativa");
            await view.ExecuteScriptAsync("window._onSetupError('Token inválido',false);");
            await Check("proxyPanel.classList.contains('hidden')&&btnOpenProxy.classList.contains('hidden')", "erro de token mostrou proxy");
            await view.ExecuteScriptAsync("window._onSetupError('conexão',true);window._onSetupChannelsMissing('Canal não encontrado');");
            await Check("proxyPanel.classList.contains('hidden')&&btnOpenProxy.classList.contains('hidden')", "canal inexistente mostrou proxy");
            // Real pointer movement checks hover visibility and the reserved token slot.
            var point = JsonSerializer.Deserialize<double[]>(await view.ExecuteScriptAsync("(()=>{const r=inpToken.getBoundingClientRect();return[r.left+20,r.top+15]})()"))!;
            await view.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseMoved", x = point[0], y = point[1] }));
            await Task.Delay(180);
            await Check("getComputedStyle(btnToggleToken).opacity==='1'&&inpToken.getBoundingClientRect().right<=btnToggleToken.getBoundingClientRect().left+1", "o botão não surgiu no hover ou cobriu o token");
            await view.ExecuteScriptAsync("btnToggleToken.click()");
            await Check("inpToken.type==='text'", "o botão de token não revelou o valor");
            foreach (var theme in new[] { "dark", "light" })
            {
                await view.ExecuteScriptAsync("document.documentElement.dataset.theme='" + theme + "'");
                await Task.Delay(200); // Measure the selected theme after its existing color transition.
                await Check("(()=>{const luminance=s=>{const c=s.match(/[0-9.]+/g).slice(0,3).map(n=>{n=Number(n)/255;return n<=.04045?n/12.92:((n+.055)/1.055)**2.4});return c[0]*.2126+c[1]*.7152+c[2]*.0722};const bg=luminance(getComputedStyle(document.querySelector('.card')).backgroundColor);return ['.subtitle','.hint','.guide-link'].every(sel=>{const fg=luminance(getComputedStyle(document.querySelector(sel)).color);return(Math.max(bg,fg)+.05)/(Math.min(bg,fg)+.05)>=4.5})})()", "contraste secundário abaixo de 4,5:1 em " + theme);
            }
            RequireSync(ConnectionErrorClassifier.Classify(new HttpRequestException("", null, HttpStatusCode.Unauthorized)).Kind == ConnectionErrorKind.InvalidToken &&
                ConnectionErrorClassifier.Classify(new HttpRequestException("", null, HttpStatusCode.NotFound)).Kind == ConnectionErrorKind.InvalidChannelConfiguration &&
                ConnectionErrorClassifier.Classify(new TimeoutException()).Kind != ConnectionErrorKind.InvalidToken, "classificação de erros incorreta");
            var keyStore = new SettingsStore(root.CreateSubdirectory("setup-key-data"));
            using var setupControl = new WebView2();
            var setupBridge = new WebMessageBridge(setupControl, async script => { await view.ExecuteScriptAsync(script); });
            await using var setupController = new AppController(new System.Windows.Window(), keyStore, setupBridge, _ => Task.CompletedTask);
            view.WebMessageReceived += async (_, args) =>
            {
                using var json = JsonDocument.Parse(args.WebMessageAsJson);
                if (json.RootElement.GetProperty("method").GetString() == "setupEncryption")
                    await setupController.HandleJsonMessageAsync(args.WebMessageAsJson);
            };
            await view.ExecuteScriptAsync("""
                window.__keyRequests=[];window.__connectRequests=[];window.__setupKeyCommand=window.bridge.setupEncryption;
                window.bridge.setupEncryption=(action,...args)=>{window.__keyRequests.push(action);if(action==='create')window.__setupKeyCommand(action,...args)};
                window.bridge.connect=(...args)=>window.__connectRequests.push(args);
                document.getElementById('setup-encryption-custom').click();btnConnect.click();
                """);
            await Check("!document.getElementById('setup-encryption-custom-fields').classList.contains('hidden')&&window.__connectRequests.length===0&&errorBox.textContent.includes('chave')", "a configuração deixou conectar sem a chave personalizada");
            await view.ExecuteScriptAsync("document.getElementById('setup-encryption-import').click();window._onSetupEncryptionIdle()");
            await Check("window.__keyRequests[0]==='import'&&!document.getElementById('setup-encryption-import').disabled", "a importação não está acessível antes de conectar");
            await view.ExecuteScriptAsync("document.getElementById('setup-key-password').value='setup-fixture-passphrase';document.getElementById('setup-key-confirm').value='setup-fixture-passphrase';document.getElementById('setup-key-acknowledge').checked=true;document.getElementById('setup-key-create').click()");
            for (var attempt = 0; attempt < 100 && await view.ExecuteScriptAsync("setupCustomKeyAvailable&&!setupEncryptionBusy") != "true"; attempt++) await Task.Delay(50);
            await Check("setupCustomKeyAvailable&&!setupEncryptionBusy&&document.getElementById('setup-encryption-status').textContent.includes('disponível')&&document.getElementById('setup-key-password').value==='';", "a chave criada antes da conexão não ficou disponível na tela inicial");
            RequireSync(!setupController.HasSession && keyStore.Load().EncryptionMode == "custom", "a criação da chave dependeu de uma sessão conectada");
            var sourceCrypto = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(keyStore));
            var recovery = Path.Combine(root.FullName, "fixture.organessonium-key");
            await sourceCrypto.ExportCustomKeyAsync(recovery);
            var restoredStore = new SettingsStore(root.CreateSubdirectory("restored-key-data"));
            var restoredCrypto = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(restoredStore));
            await restoredCrypto.ImportCustomKeyAsync(recovery);
            var context = new EncryptedPayloadContext("setup-recovery", sourceCrypto.CreateFileEncryption("custom"), EncryptedPayloadRoles.Original, 0, 1);
            RequireSync(restoredCrypto.DecryptBytes(sourceCrypto.EncryptBytes([1, 2, 3], context), context).SequenceEqual(new byte[] { 1, 2, 3 }), "a chave importada antes da conexão não abriu os arquivos existentes");
            await view.ExecuteScriptAsync("btnConnect.click()");
            await Check("window.__connectRequests.length===1&&window.__connectRequests[0][7]==='custom'", "a conexão não recebeu o modo de proteção escolhido");
            await view.ExecuteScriptAsync("inpToken.value='';inpToken.type='password';document.documentElement.dataset.theme='dark';document.querySelector('.page').scrollTop=0");
            await Task.Delay(200);
            var setupCaptures = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "ui-captures"));
            await using (var capture = File.Create(Path.Combine(setupCaptures.FullName, "setup-encryption.png")))
                await view.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, capture);
            await Navigate("index.html");
            var store = new SettingsStore(root.CreateSubdirectory("data"));
            var file = store.GetPreviewCacheFile("photo", "full", "photo.png");
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jBzEAAAAASUVORK5CYII=");
            EncryptedPreviewCache.Store(file, png);
            using var resources = new PreviewResourceRegistry();
            using var adapter = new WebView2PreviewAdapter(view, resources); adapter.Attach();
            var url = resources.Register(file, "image/png");
            await view.ExecuteScriptAsync("window.__organessoniumSettings={previewEnabled:true};window.onBridgeReady();window.__photo={fileId:'photo',threadId:'photo',name:'photo.png',type:'file',path:'/',parentFolderId:'root'};window._onAppsLoaded([window.__photo]);window.bridge.requestPreview=(id,name)=>window._onPreviewReady('image',name," + JsonSerializer.Serialize(url) + ",id);openPreview(window.__photo);");
            const string imageReady = "document.querySelector('#preview-body .preview-img')?.naturalWidth===1";
            for (var attempt = 0; attempt < 50 && await view.ExecuteScriptAsync(imageReady) != "true"; attempt++) await Task.Delay(100);
            await Check(imageReady, "o painel de prévia não exibiu a imagem do cache cifrado");
            await view.ExecuteScriptAsync("closePreview()");
            using var dummyView = new WebView2();
            var bridge = new WebMessageBridge(dummyView, script => view.ExecuteScriptAsync(script));
            await using var controller = new AppController(new System.Windows.Window(), store, bridge, _ => Task.CompletedTask);
            using (var connection = new SqliteConnection("Data Source=" + store.DatabasePath))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT OR REPLACE INTO settings(key,value,updated_at) VALUES('tempPath','retired-test-folder',''),('downloadPath','','')";
                command.ExecuteNonQuery();
            }
            var defaults = store.Load();
            RequireSync(defaults.DownloadPath == WindowsDownloadsFolder.DefaultPath && Path.IsPathFullyQualified(defaults.DownloadPath),
                "a configuração vazia não usou a pasta Downloads do Windows");
            store.Save(defaults);
            RequireSync(!JsonSerializer.Serialize(store.Load(), JsonOptions.Web).Contains("tempPath", StringComparison.Ordinal),
                "a configuração temporária continua publicada na interface");
            await bridge.SetSettingsAsync(defaults);
            await view.ExecuteScriptAsync("loadSettings();openSettings()");
            await Check("!document.getElementById('setting-tmppath')&&document.getElementById('setting-dlpath').offsetWidth>0&&document.getElementById('setting-dlpath').value===" +
                JsonSerializer.Serialize(defaults.DownloadPath), "a tela não removeu a pasta temporária ou não exibiu Downloads como padrão");
            view.WebMessageReceived += async (_, args) =>
            {
                using var json = JsonDocument.Parse(args.WebMessageAsJson);
                if (json.RootElement.GetProperty("method").GetString() == "saveSettings") await controller.HandleJsonMessageAsync(args.WebMessageAsJson);
            };
            var customDownload = root.CreateSubdirectory("chosen-downloads").FullName;
            await bridge.CallAsync("_onPathChosen", "download", customDownload);
            for (var attempt = 0; attempt < 50 && store.Load().DownloadPath != customDownload; attempt++) await Task.Delay(50);
            RequireSync(new SettingsStore(store.DataDirectory).Load().DownloadPath == customDownload, "a escolha personalizada de downloads não foi preservada");
            await (Task)typeof(AppController).GetMethod("SendLibraryStatisticsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, [new LibraryStatistics(2, 1, 64424509440)])!;
            await view.ExecuteScriptAsync("window._onAppsLoaded([]);window._onServerReadState(true)");
            await Check("document.getElementById('storage-display').textContent.includes('60')&&document.getElementById('server-read-status').querySelector('.svg-icon').getAnimations()[0].effect.getKeyframes().at(-1).transform.includes('-360deg')", "totais locais ou sentido da animação incorretos");
            await Check("document.getElementById('server-item-count').textContent==='3'", "a quantidade anterior de itens não apareceu");
            await view.ExecuteScriptAsync("localStorage.setItem('private-account','old-bot');");
            await view.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            await Navigate("setup.html");
            await Check("localStorage.getItem('private-account')===null", "a limpeza da conta preservou dados no WebView");
            await Navigate("index.html");
            var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var writing = (Task)typeof(AppController).GetMethod("RunAsync", flags)!.Invoke(controller, [(Func<Task>)(async () =>
            {
                await releaseWriter.Task;
                EncryptedPreviewCache.Store(store.GetPreviewCacheFile("late", "full", "late.png"), png);
            }), true])!;
            var leaving = (Task)typeof(AppController).GetMethod("LogoutAsync", flags)!.Invoke(controller, [])!;
            await Task.Delay(100);
            RequireSync(!leaving.IsCompleted, "o logout não aguardou a escrita anterior");
            releaseWriter.TrySetResult();
            await Task.WhenAll(writing, leaving).WaitAsync(TimeSpan.FromSeconds(15));
            for (var attempt = 0; attempt < 30 && await view.ExecuteScriptAsync("location.pathname") != "\"/setup.html\""; attempt++) await Task.Delay(100);
            await Check("location.pathname==='/setup.html'&&document.getElementById('inp-token').value===''", "o logout não exibiu a configuração vazia");
            RequireSync(!Directory.Exists(store.PreviewCacheDirectory.FullName), "uma escrita atrasada recriou o cache após o logout");
            RequireSync(!IsWindowVisible(host), "o teste abriu uma janela");
            return TestOutcome.Pass();
        }
        finally { browser?.Close(); DestroyWindow(host); SqliteConnection.ClearAllPools(); try { root.Delete(true); } catch (IOException) { } }
    });
}
