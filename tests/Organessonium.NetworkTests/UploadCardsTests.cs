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
                window._thumbs={'thread-0':'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9r8AAAAASUVORK5CYII='};
                window._onAppsLoaded(window.__items);
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
                    window._onLocalAppsChanged(JSON.parse(JSON.stringify(window.__items)));
                    window._onAppsLoaded(JSON.parse(JSON.stringify(window.__items)));
                }
                """);
            await Task.Delay(150);
            async Task Check(string script, string message) => RequireSync(await view.ExecuteScriptAsync(script) == "true", message);
            Console.WriteLine("Upload card continuity: " + await view.ExecuteScriptAsync("JSON.stringify({removals:window.__removals,card:window.__card===document.querySelector('[data-key=\"meta-0\"]'),folder:window.__folder===document.querySelector('.folders-grid .folder-card'),selected:window.__card.classList.contains('selected'),image:window.__image===window.__card.querySelector('img'),decoded:!!window.__image?.naturalWidth,errors:window.__errors})"));
            await Check("window.__removals===0&&window.__card===document.querySelector('[data-key=\"meta-0\"]')&&window.__folder===document.querySelector('.folders-grid .folder-card')&&window.__card.classList.contains('selected')&&window.__image===window.__card.querySelector('img')&&window.__image.complete&&window.__image.naturalWidth>0",
                "atualizar o lote removeu/recriou o card, miniatura, pasta ou seleção");
            await view.ExecuteScriptAsync("setView('list');window.__row=document.querySelector('[data-key=\"meta-0\"]');window._onLocalAppsChanged(JSON.parse(JSON.stringify(window.__items)));");
            await Check("window.__row===document.querySelector('[data-key=\"meta-0\"]')&&window.__row.classList.contains('selected')&&document.querySelectorAll('.file-row').length===45&&document.getElementById('files-grid').children.length===0",
                "a lista perdeu linhas/seleção ou deixou cards duplicados na área oculta");
            await view.ExecuteScriptAsync("window.__items=window.__items.filter(a=>a.metadataMessageId!=='new-meta-0');window.__items.find(a=>a.metadataMessageId==='meta-1').name='Renomeada.jpg';window._onAppsLoaded(window.__items);setView('grid');");
            await Check("!document.querySelector('[data-key=\"new-meta-0\"]')&&document.querySelector('[data-key=\"meta-1\"] .card-name').textContent==='Renomeada.jpg'",
                "reconciliação deixou um arquivo removido ou ignorou rename");
            await view.ExecuteScriptAsync("navigate('/Fotos',true,'photos');window._onAppsLoaded(window.__items);window._onFolderLoadState('/Fotos',false);");
            await Check("document.querySelectorAll('.cards-grid .file-card').length===1&&document.querySelector('.card-name').textContent==='Dentro.jpg'&&document.getElementById('empty-state').classList.contains('hidden')",
                "a pasta navegada ficou vazia após atualização");
            await Check("window.__errors.length===0", "erro JavaScript durante atualização da biblioteca");
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
