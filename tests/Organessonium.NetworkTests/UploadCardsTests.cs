using Microsoft.Web.WebView2.Core;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> UploadCardsHiddenBrowserAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-upload-cards-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, directory.CreateSubdirectory("browser").FullName);
            controller = await environment.CreateCoreWebView2ControllerAsync(host);
            controller.IsVisible = true;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var web = directory.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                await using var output = File.Create(path); await input.CopyToAsync(output);
            }
            var view = controller.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await view.ExecuteScriptAsync("""
                window.__errors=[];window.addEventListener('error',e=>window.__errors.push(e.message));
                window.__organessoniumSettings={previewEnabled:true,theme:'dark',animationsEnabled:false,language:'pt-BR'};
                window.onBridgeReady();
                window.__items=Array.from({length:24},(_,i)=>({name:'Foto-'+String(i).padStart(2,'0')+'.jpg',
                    fileId:'file-'+i,threadId:'thread-'+i,metadataMessageId:'meta-'+i,type:'file',sizeBytes:4096,path:'/',parentFolderId:'root'}));
                window.__items.push({name:'Fotos',folderId:'photos',type:'folder',path:'/',parentFolderId:'root'});
                window.__items.push({name:'Dentro.jpg',fileId:'inside',threadId:'inside',metadataMessageId:'inside-meta',type:'file',sizeBytes:1024,path:'/Fotos',parentFolderId:'photos'});
                window.__items[0].name='Foto-00.HEIC';window.__items[1].name='Video-01.MOV';window.__items[2].name='Foto-02.AVIF';
                window._thumbs={'thread-0':'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9r8AAAAASUVORK5CYII='};
                window._onAppsLoaded(window.__items);
                window._onLibraryStatistics({fileCount:25,folderCount:1,totalBytes:99328});
                window.__summary=[document.getElementById('storage-used')?.textContent,serverItemCount.textContent,storageDisp.textContent];
                window._opsUploadBatch('media-types',['Foto.HEIC','Video.MOV','Foto.AVIF'],[1,1,1]);
                """);
            // Wait for initial image decoding and the first viewport observer notification.
            await Task.Delay(200);
            await view.ExecuteScriptAsync("""
                window.__card=document.querySelector('[data-key="meta-0"]');
                window.__folder=document.querySelector('.folders-grid .folder-card');
                window.__image=window.__card.querySelector('img');window.__card.click();
                window.__removals=0;
                window.__observer=new MutationObserver(records=>records.forEach(r=>r.removedNodes.forEach(n=>{
                    if(n===window.__card||n.contains?.(window.__card))window.__removals++;
                })));
                window.__observer.observe(document.getElementById('files-grid'),{childList:true,subtree:true});
                for(let i=0;i<20;i++){
                    window.__items.push({name:'Novo-'+i+'.bin',fileId:'new-'+i,threadId:'new-'+i,metadataMessageId:'new-meta-'+i,type:'file',sizeBytes:1,path:'/',parentFolderId:'root'});
                    window._onAppsBatch(JSON.parse(JSON.stringify(window.__items)));
                }
                window._onAppsBatch(window.__items.slice(0,26)); // A late completion snapshot must not remove newer cards.
                """);
            await Task.Delay(150);
            async Task Check(string script, string message) => RequireSync(await view.ExecuteScriptAsync(script) == "true", message);
            Console.WriteLine("Upload summary continuity: " + await view.ExecuteScriptAsync("JSON.stringify({files:document.querySelectorAll('#files-grid .file-card:not(.folder-card)').length,items:serverItemCount.textContent,bytes:storageDisp.textContent,previous:window.__summary})"));
            await Check("serverItemCount.textContent===window.__summary[1]&&storageDisp.textContent===window.__summary[2]&&document.querySelectorAll('#files-grid .file-card:not(.folder-card)').length===44",
                "uma conclusão atrasada removeu cards ou zerou o armazenamento/contagem durante o upload");
            await Check("['HEIC','HEIF','AVIF','JXL','TIFF','JFIF','ICO'].every(ext=>fileType('photo.'+ext)==='image')&&['MOV','MTS','M2TS','WMV','OGV','3G2','MPEG'].every(ext=>fileType('video.'+ext)==='video')&&document.querySelector('#op-file-media-types-0 .op-file-icon .transfer-default-icon').textContent==='image'&&document.querySelector('#op-file-media-types-1 .op-file-icon .transfer-default-icon').textContent==='movie'",
                "a interface não classificou as imagens/vídeos ou manteve ícones genéricos nas transferências");
            Console.WriteLine("Upload card continuity: " + await view.ExecuteScriptAsync("JSON.stringify({removals:window.__removals,card:window.__card===document.querySelector('[data-key=\"meta-0\"]'),folder:window.__folder===document.querySelector('.folders-grid .folder-card'),selected:window.__card.classList.contains('selected'),image:window.__image===window.__card.querySelector('img'),decoded:!!window.__image?.naturalWidth,errors:window.__errors})"));
            await Check("window.__removals===0&&window.__card===document.querySelector('[data-key=\"meta-0\"]')&&window.__folder===document.querySelector('.folders-grid .folder-card')&&window.__card.classList.contains('selected')&&window.__image===window.__card.querySelector('img')&&window.__image.complete&&window.__image.naturalWidth>0",
                "atualizar o lote removeu/recriou o card, miniatura, pasta ou seleção");
            await view.ExecuteScriptAsync("setView('list');window.__row=document.querySelector('[data-key=\"meta-0\"]');window._onLocalAppsChanged(JSON.parse(JSON.stringify(window.__items)));");
            await Check("window.__row===document.querySelector('[data-key=\"meta-0\"]')&&window.__row.classList.contains('selected')&&document.querySelectorAll('.file-row').length===45&&document.getElementById('files-grid').children.length===0",
                "a lista perdeu linhas/seleção ou deixou cards duplicados na área oculta");
            await Check("serverItemCount.textContent===window.__summary[1]&&storageDisp.textContent===window.__summary[2]", "uma alteração local zerou o resumo lateral");
            await view.ExecuteScriptAsync("window._onLibraryStatistics({fileCount:45,folderCount:1,totalBytes:99348});");
            await Check("serverItemCount.textContent==='46'&&storageDisp.textContent===fmtSize(99348)", "o resumo não aceitou o agregado atualizado após concluir o lote");
            await view.ExecuteScriptAsync("window.__items=window.__items.filter(a=>a.metadataMessageId!=='new-meta-0');window.__items.find(a=>a.metadataMessageId==='meta-1').name='Renomeada.jpg';window._onAppsLoaded(window.__items);setView('grid');");
            await Check("!document.querySelector('[data-key=\"new-meta-0\"]')&&document.querySelector('[data-key=\"meta-1\"] .card-name').textContent==='Renomeada.jpg'",
                "reconciliação deixou um arquivo removido ou ignorou rename");
            await view.ExecuteScriptAsync("navigate('/Fotos',true,'photos');window._onAppsLoaded(window.__items);window._onFolderLoadState('/Fotos',false);");
            await Check("document.querySelectorAll('.cards-grid .file-card').length===1&&document.querySelector('.card-name').textContent==='Dentro.jpg'&&document.getElementById('empty-state').classList.contains('hidden')",
                "a pasta navegada ficou vazia após atualização");
            await Check("window.__errors.length===0", "erro JavaScript durante atualização da biblioteca");
            await view.ExecuteScriptAsync("window.__items[0].sha256='" + new string('a', 64) + "';openFileDetails(window.__items[0],false);window._onFileInformation(fileDetailsRequest,{entry:window.__items[0],chunkCount:1,storageBytes:4096});");
            await Check("document.querySelector('#file-details-content').textContent.includes('SHA-256')&&document.querySelector('#file-details-content').textContent.includes('" + new string('a', 64) + "')",
                "o checksum não apareceu nas informações do arquivo");
            await view.ExecuteScriptAsync("closeFileDetails();navigate('/Drive/Lembranças de 2024/8ª Mostra Cultural');");
            await Check("Array.from(document.querySelectorAll('#path-crumbs .p-crumb')).every(el=>el.scrollWidth<=el.clientWidth+1)&&document.getElementById('current-folder-name').scrollWidth<=document.getElementById('current-folder-name').clientWidth+1", "o caminho foi reduzido apesar da largura disponível");
            await view.ExecuteScriptAsync("window.__pauseCalls=[];window.bridge.toggleTransferPause=(...args)=>window.__pauseCalls.push(args);window._opsUploadBatch('pause-ui',['foto.jpg']);document.querySelector('#op-file-pause-ui-0 .transfer-pause-toggle').click();window._opsUploadFileProgress('pause-ui',0,'paused',55,'Pausado');window._opsAdd('download-ui','download','foto.jpg');window._opsUpdate('download-ui','paused','Pausado');");
            await Check("window.__pauseCalls.length===1&&document.querySelector('#op-file-pause-ui-0 .transfer-pause-toggle').textContent==='play_arrow'&&document.querySelector('#op-download-ui .transfer-pause-toggle').textContent==='play_arrow'&&document.querySelector('#op-file-pause-ui-0 .transfer-pause-toggle').getAttribute('aria-label').includes('Retomar')", "o controle no ícone não pausou ou não exibiu a retomada");
            await view.ExecuteScriptAsync("window._opsTransferResumed('pause-ui',0);window._opsUploadFileProgress('pause-ui',0,'active',56,'Retomando');window._opsTransferResumed('download-ui');window._opsUpdate('download-ui','active','Retomando');");
            await Check("document.querySelector('#op-file-pause-ui-0 .transfer-pause-toggle').textContent==='pause'&&document.querySelector('#op-download-ui .transfer-pause-toggle').textContent==='pause'", "o controle não voltou a permitir pausa");
            RequireSync(!IsWindowVisible(host), "o teste exibiu uma janela");
            return TestOutcome.Pass();
        }
        finally
        {
            controller?.Close(); DestroyWindow(host);
            try { directory.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });
}
