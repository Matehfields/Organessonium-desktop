using System.Reflection;
using System.Security.Cryptography;
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
    private static Task<TestOutcome> FileVersionsLifecycleAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var root = Directory.CreateTempSubdirectory("organessonium-version-test-");
        try
        {
            using var server = new SyncDiscordFixture();
            var settings = new AppSettings { BotToken = "versions-fixture-" + Guid.NewGuid().ToString("N"),
                NamesChannelId = "names", FoldersForumId = "folders", AppsForumId = "files", SerialChannelId = "serial",
                DownloadThreads = 2, UploadThreads = 2, KeepTransferHistory = true };
            var store = new SettingsStore(root.CreateSubdirectory("data"));
            store.Save(settings);
            using var api = new DiscordApiClient(settings.BotToken, new HttpClient(server, false));
            var registry = new AppRegistry(api, store, settings);
            await registry.RefreshMappingsAsync();
            var owner = new System.Windows.Window();
            using var web = new WebView2(); // Never initialized or shown. UI validation is the separate hidden-browser test.
            await using var controller = new AppController(owner, store, new WebMessageBridge(web), _ => Task.CompletedTask);
            var session = new DiscordSession();
            typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
            typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, settings);
            controller.SetSession(session);
            typeof(AppController).GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, registry);
            var encryption = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(store));
            var uploader = new ForumUploader(api, "files");
            var source = new FileInfo(Path.Combine(root.FullName, "Dados.bin"));
            var originals = new List<byte[]>();
            var entries = new List<AppEntry>();
            async Task<AppEntry> Upload(FileReplacement? replacement)
            {
                var bytes = RandomNumberGenerator.GetBytes(3000 + originals.Count * 1000);
                originals.Add(bytes);
                await File.WriteAllBytesAsync(source.FullName, bytes);
                source.Refresh();
                var method = typeof(AppController).GetMethod("UploadSingleAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var values = new Dictionary<string, object?>
                {
                    ["opId"] = Guid.NewGuid().ToString("N"), ["fileIndex"] = 0, ["fileTotal"] = 1,
                    ["file"] = source, ["targetPath"] = "/", ["allEntries"] = entries,
                    ["uploader"] = uploader, ["registry"] = registry, ["settings"] = settings,
                    ["throttle"] = Activator.CreateInstance(typeof(AppController).GetNestedType("UiThrottle", BindingFlags.NonPublic)!, nonPublic:true),
                    ["cancellationToken"] = CancellationToken.None,
                    ["replacement"] = replacement
                };
                return await (Task<AppEntry>)method.Invoke(controller, method.GetParameters()
                    .Select(parameter => values.TryGetValue(parameter.Name!, out var value) ? value : parameter.DefaultValue).ToArray())!;
            }
            var first = await Upload(null);
            entries.Add(first);
            var second = await Upload(FileVersioning.PrepareReplacement(first));
            entries[0] = second;
            var third = await Upload(FileVersioning.PrepareReplacement(second));
            entries[0] = third;
            RequireSync(server.PayloadPosts == 1 && first.ThreadId == third.ThreadId &&
                first.FileId == third.FileId && first.MetadataMessageId == third.MetadataMessageId,
                "substituir criou outro post ou alterou a identidade");
            RequireSync(third.Versions.Count == 2 && third.UploadedAt == first.UploadedAt &&
                first.Encryption!.Salt != second.Encryption!.Salt && third.ModifiedAt.Length > 0 &&
                FileVersioning.StorageBytes(third) == originals.Take(3).Sum(bytes => bytes.Length),
                $"histórico/timestamps/salt/tamanho: {third.Versions.Count}, datas iguais {third.UploadedAt == first.UploadedAt}, salt mudou {first.Encryption!.Salt != second.Encryption!.Salt}, bytes {FileVersioning.StorageBytes(third)}/{originals.Take(3).Sum(bytes => bytes.Length)}");
            var service = new MetadataService(Organessonium.Services.MetadataEncryption.MetadataCodec.Standard(), api.DownloadAttachmentBytesAsync);
            var authoritative = await registry.LoadFileDetailsAsync(third, forceRefresh: true);
            RequireSync(authoritative.Versions.Count == 2, "histórico não foi publicado no metadata cifrado");
            var cachedThroughSerial = (await registry.LoadCachedFolderAsync("root", "/")).Single(item => item.FileId == third.FileId);
            RequireSync(cachedThroughSerial.Versions.Count == 2 && cachedThroughSerial.LogicalChunkCount == third.LogicalChunkCount &&
                cachedThroughSerial.SizeBytes == third.SizeBytes, "abrir o serial v3 descartou os detalhes/histórico em cache");
            var downloader = new ForumDownloader(api, settings, encryption, () => new HttpClient(server, false));
            var oldEntries = authoritative.Versions.Select(version => service.ToAppEntry(new MetadataEnvelope(third.MetadataMessageId, version.Payload))!).ToList();
            oldEntries.Add(authoritative);
            for (var index = 0; index < oldEntries.Count; index++)
            {
                var downloaded = await downloader.DownloadAndAssembleAsync(oldEntries[index], root.CreateSubdirectory("download-" + index), _ => { });
                RequireSync((await File.ReadAllBytesAsync(downloaded.FullName)).SequenceEqual(originals[index]),
                    "a versão " + index + " não recuperou o conteúdo original");
            }
            // A copy is an independent encrypted upload, not a shared reference to the original's payloads.
            var copySource = new FileInfo(Path.Combine(root.FullName, "Cópia de Dados.bin"));
            await File.WriteAllBytesAsync(copySource.FullName, originals[2]);
            var copyEncryption = encryption.CreateFileEncryption(settings.EncryptionMode);
            var copyId = Guid.NewGuid().ToString("N");
            using var memory = new UploadMemoryCoordinator(2);
            var context = new EncryptedPayloadContext(copyId, copyEncryption, EncryptedPayloadRoles.Original, 0, 1);
            var copied = await uploader.UploadResumableGeneratedAsync(copySource.Name, 1,
                index => memory.PreparePartAsync(encryption, copySource, copySource.Length, EncryptionService.MaximumPlaintextPayloadBytes,
                    context.WithChunkIndex(index), default, deferBuffers: true), "", "", new Dictionary<int,string>(),
                encryptedPayload: context, useAttachmentReferences: true, streamPreparedParts: true, batchPreparationGate: memory.BatchPreparationGate);
            var copy = AppEntry.File(copySource.Name, copied.ThreadId, TransferState.Now(), copySource.Length, "/");
            copy.FileId = copyId; copy.Encryption = copyEncryption; copy.ChunkReferences = copied.ChunkReferences!.ToList();
            copy.BatchId = copied.BatchId; copy.ParentFolderId = "root";
            await registry.AddEntryAsync(copy);
            RequireSync(copy.FileId != third.FileId && copy.ThreadId != third.ThreadId && copy.Versions.Count == 0, "a cópia compartilhou identidade ou histórico");
            var copyBytes = await downloader.DownloadAndAssembleAsync(copy, root.CreateSubdirectory("copy-download"), _ => { });
            RequireSync((await File.ReadAllBytesAsync(copyBytes.FullName)).SequenceEqual(originals[2]), "a cópia alterou o conteúdo");
            authoritative.Name = "Renomeado.bin"; authoritative.ModifiedAt = TransferState.Now();
            await registry.UpdateEntryAsync(authoritative);
            await registry.SetFavoriteAsync(authoritative, true);
            var folder = AppEntry.Folder("Fotos", "/"); folder.ParentFolderId = "root";
            await registry.AddEntryAsync(folder);
            await registry.MoveEntryAsync(authoritative, folder.FolderId, "/Fotos");
            var moved = await registry.LoadFileDetailsAsync(authoritative, forceRefresh: true);
            var restarted = new SettingsStore(store.DataDirectory).LoadCachedEntriesForFolder(folder.FolderId).Single();
            RequireSync(moved.Versions.Count == 2 && moved.Favorite && restarted.Versions.Count == 2 &&
                restarted.ModifiedAt.Length > 0 && restarted.Path == "/Fotos", "renomear, favoritar, mover ou reiniciar perdeu o histórico");
            // Reject metadata commit after chunks were sent. The previous active payload remains readable.
            server.RejectNextMetadataUpdate = true;
            var failed = false;
            try { await Upload(FileVersioning.PrepareReplacement(moved)); }
            catch (DiscordApiException) { failed = true; }
            var afterFailure = await registry.LoadFileDetailsAsync(moved, forceRefresh: true);
            RequireSync(failed && afterFailure.VersionId == third.VersionId && afterFailure.Versions.Count == 2 &&
                !server.DeletedChannels.Contains(first.ThreadId), "falha de commit apagou o post ou a versão anterior");
            RequireSync(!owner.IsVisible, "o teste exibiu uma janela");
            return TestOutcome.Pass();
        }
        finally { SqliteConnection.ClearAllPools(); try { root.Delete(true); } catch (IOException) { } }
    });

    private static Task<TestOutcome> FileActionsHiddenBrowserAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-file-ui-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, directory.FullName);
            controller = await environment.CreateCoreWebView2ControllerAsync(host);
            // Render inside a HWND that has no WS_VISIBLE flag. No on-screen window is opened.
            controller.IsVisible = true;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1920, 1080);
            var web = directory.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            var view = controller.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await view.ExecuteScriptAsync("window.__errors=[];window.addEventListener('error',e=>window.__errors.push(e.message));window.__organessoniumSettings={language:'pt-BR',theme:'dark',animationsEnabled:false};window.onBridgeReady();window.__calls=[];window.bridge.copyFile=(...args)=>window.__calls.push(['copyFile',...args]);window.bridge.downloadVersion=(...args)=>window.__calls.push(['downloadVersion',...args]);window.bridge.requestFileInformation=(mid,id)=>window.__infoRequest=id;window.__entry={name:'Foto.jpg',fileId:'file-fixture',threadId:'thread-fixture',metadataMessageId:'metadata-fixture',type:'file',mimeType:'image/jpeg',sizeBytes:4096,path:'/Fotos',parentFolderId:'folder-fixture',uploadedAt:'2026-01-01T12:00:00Z',modifiedAt:'2026-10-10T12:00:00Z',versions:[]};window.__info={entry:window.__entry,storageBytes:6144,chunkCount:2};");
            async Task Check(string script, string message) => RequireSync(await view.ExecuteScriptAsync(script) == "true", message);
            await view.ExecuteScriptAsync("showCtx({clientX:500,clientY:200,preventDefault(){},stopPropagation(){}},window.__entry);window._onFileInformation(window.__infoRequest,window.__info)");
            await Check("!document.getElementById('ctx-open')&&!document.getElementById('ctx-copy').classList.contains('hidden')&&document.getElementById('ctx-versions').classList.contains('hidden')&&document.getElementById('ctx-favorite-text').textContent==='Adicionar à pasta \"Com estrela\"'&&document.getElementById('ctx-dl-label').textContent==='Fazer o download'", "ações ou textos do menu incorretos");
            await view.ExecuteScriptAsync("document.getElementById('ctx-copy').click()");
            await Check("window.__calls.some(c=>c[0]==='copyFile'&&c[1]==='metadata-fixture')", "Fazer cópia não enviou a ação para o desktop");
            await view.ExecuteScriptAsync("showCtx({clientX:500,clientY:200,preventDefault(){},stopPropagation(){}},window.__entry);window.__entry.versions=[{versionId:'old-version',archivedAt:'2026-10-10T12:00:00Z',payload:{name:'Foto antiga.jpg',sizeBytes:2048,uploadedAt:'2026-01-01T12:00:00Z'}}];window._onFileInformation(window.__infoRequest,window.__info)");
            await Check("!document.getElementById('ctx-versions').classList.contains('hidden')", "Versões não apareceu após ler o histórico");
            await view.ExecuteScriptAsync("document.getElementById('ctx-info').click();window._onFileInformation(window.__infoRequest,window.__info)");
            await Check("!document.getElementById('file-details-overlay').classList.contains('hidden')&&document.getElementById('file-details-content').textContent.includes('/Fotos')&&document.getElementById('file-details-content').textContent.includes('6 KB')&&document.getElementById('file-details-content').textContent.includes('metadata-fixture')&&document.getElementById('file-details-content').textContent.includes('Quantidade de chunks')", "as informações não ficaram visíveis");
            await view.ExecuteScriptAsync("closeFileDetails();openFileDetails(window.__entry,true);window._onFileInformation(window.__infoRequest,window.__info);document.querySelector('[data-version-index=\"1\"]').click()");
            await Check("document.getElementById('file-details-content').textContent.includes('Foto antiga.jpg')&&window.__calls.some(c=>c[0]==='downloadVersion'&&c[2]==='old-version')", "a versão antiga não ficou acessível na interface");
            await view.ExecuteScriptAsync("closeFileDetails();window.__editorPanel=document.createElement('div');window.__editorPanel.style.cssText='position:fixed;top:0;left:0;width:650px;height:400px';document.body.appendChild(window.__editorPanel);window.__editorEntry={id:'material-editor',app:window.__entry,panel:window.__editorPanel,zoom:1};window.__editorPanel.appendChild(createMarkdownEditor('# Título',window.__editorEntry,'Teste.md'));syncMarkdownToolbarLabels(window.__editorPanel.querySelector('.markdown-editor-toolbar'))");
            await Check("[...window.__editorPanel.querySelectorAll('[data-cmd=\"bold\"],[data-cmd=\"italic\"],[data-cmd^=\"formatBlock:h\"]')].length===8&&[...window.__editorPanel.querySelectorAll('[data-cmd=\"bold\"],[data-cmd=\"italic\"],[data-cmd^=\"formatBlock:h\"]')].every(button=>button.querySelector('.svg-icon')?.getBoundingClientRect().width>0)", "o editor compacto não exibiu os ícones Google");
            await view.ExecuteScriptAsync("closeFileDetails();document.getElementById('file-details-content').innerHTML='<div>'+['image','movie','audio_file','picture_as_pdf','folder_zip','description','grid_on','slideshow','code','insert_drive_file','format_bold','format_italic','content_copy','info','history','restore_from_trash','edit_square','star_border','star','more_vert'].map(name=>icon(name,24,'red')).join('')+'</div>';document.fonts.load('24px \"Material Symbols Rounded\"');");
            await Task.Delay(250);
            var missing = await view.ExecuteScriptAsync("(()=>{const canvas=document.createElement('canvas'),ctx=canvas.getContext('2d');ctx.font='24px \"Material Symbols Rounded\"';return [...new Set([...document.querySelectorAll('.svg-icon')].map(el=>el.textContent))].filter(name=>ctx.measureText(name).width>26)})()");
            RequireSync(missing == "[]", "glyphs ausentes no font Google: " + missing);
            await Check("[...document.querySelectorAll('.svg-icon')].every(el=>getComputedStyle(el).color==='rgb(196, 199, 197)'&&getComputedStyle(el).fontFamily.includes('Material Symbols Rounded'))", "ícones escuros não ficaram neutros");
            await view.ExecuteScriptAsync("applyTheme('light')");
            await Task.Delay(250);
            var unexpectedColor = await view.ExecuteScriptAsync("[...document.querySelectorAll('.svg-icon')].filter(el=>getComputedStyle(el).color!=='rgb(95, 99, 104)').slice(0,3).map(el=>[el.textContent,getComputedStyle(el).color,document.body.dataset.theme,document.documentElement.dataset.theme])");
            RequireSync(unexpectedColor == "[]", "ícones claros: " + unexpectedColor);
            await view.ExecuteScriptAsync("window.__editorEntry.toolbarWrapCleanup?.();window.__editorPanel.remove()");
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 800, 600);
            await view.ExecuteScriptAsync("openFileDetails(window.__entry,false);window._onFileInformation(window.__infoRequest,window.__info)");
            await Check("document.querySelector('.file-details-dialog').getBoundingClientRect().right<=window.innerWidth&&document.querySelector('.file-details-dialog').getBoundingClientRect().bottom<=window.innerHeight&&window.__errors.length===0", "dialog fora da tela ou erro JavaScript");
            RequireSync(!IsWindowVisible(host), "o teste exibiu uma janela");
            return TestOutcome.Pass();
        }
        finally { controller?.Close(); DestroyWindow(host); try { directory.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    });
}
