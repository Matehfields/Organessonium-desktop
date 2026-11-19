using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> ThumbnailViewportAndCrashRecoveryHiddenAsync() => ThumbnailViewportHiddenAsync(600);
    private static Task<TestOutcome> ThumbnailLargeFolderScrollHiddenAsync() => ThumbnailViewportHiddenAsync(3000);

    private static Task<TestOutcome> ThumbnailViewportHiddenAsync(int fileCount) => OnHiddenDispatcherAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-thumbnail-viewport-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        try
        {
            AppMemoryBudget.Initialize();
            var environment = await CoreWebView2Environment.CreateAsync(null, directory.CreateSubdirectory("browser").FullName);
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true; browser.Bounds = new System.Drawing.Rectangle(0, 0, 1366, 768);
            var resources = directory.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(resources.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            var view = browser.CoreWebView2;
            ThumbnailCache.Configure(directory.CreateSubdirectory("thumbnails"));
            using var thumbnailResources = new PreviewResourceRegistry();
            using var thumbnailAdapter = new Organessonium.Bridge.WebView2PreviewAdapter(view, thumbnailResources);
            thumbnailAdapter.Attach();
            using var control = new Microsoft.Web.WebView2.Wpf.WebView2();
            var bridge = new Organessonium.Bridge.WebMessageBridge(control, script => view.ExecuteScriptAsync(script));
            var pixels = new byte[200 * 200 * 4]; new Random(7).NextBytes(pixels);
            for (var offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(200, 200, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, 200 * 4);
            var png = ThumbnailCache.CreateSquarePng(bitmap);
            var deliveryFailure = new TaskCompletionSource<Exception>();
            view.WebMessageReceived += async (_, args) =>
            {
                try
                {
                    using var message = System.Text.Json.JsonDocument.Parse(args.WebMessageAsJson);
                    if (message.RootElement.GetProperty("method").GetString() != "loadVisibleThumbnails") return;
                    var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(message.RootElement.GetProperty("args")[0].GetString()!)!;
                    foreach (var id in ids)
                    {
                        var url = ThumbnailCache.Get(id) ?? ThumbnailCache.Store(id, png);
                        await bridge.SetThumbAsync(id, url!);
                    }
                }
                catch (Exception error) { deliveryFailure.TrySetResult(error); }
            };
            var processFailure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ProcessFailed += (_, args) => processFailure.TrySetResult(args.ProcessFailedKind.ToString());
            view.SetVirtualHostNameToFolderMapping("organessonium.local", resources.FullName, CoreWebView2HostResourceAccessKind.Allow);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await view.ExecuteScriptAsync("window.__fileCount=" + fileCount);
            await view.ExecuteScriptAsync("""
                window.__organessoniumSettings={previewEnabled:true,language:'pt-BR',animationsEnabled:false};window.onBridgeReady();
                window.__errors=[];window.addEventListener('error',e=>window.__errors.push(e.message));
                window.__delivered=new Set();window.__onThumb=window._onThumb;
                window._onThumb=id=>{window.__delivered.add(id);window.__onThumb(id)};
                window.__entries=Array.from({length:window.__fileCount},(_,i)=>({name:'photo-'+String(i).padStart(4,'0')+'.png',fileId:'photo-'+i,threadId:'thread-'+i,metadataMessageId:'metadata-'+i,type:'file',sizeBytes:2048,path:'/',parentFolderId:'root'}));
                window._onAppsLoaded(window.__entries);
                """).WaitAsync(TimeSpan.FromSeconds(15));
            for (var attempt = 0; attempt < 80 && await view.ExecuteScriptAsync("document.querySelectorAll('[data-thumb] img').length>0").WaitAsync(TimeSpan.FromSeconds(5)) != "true"; attempt++)
                await Task.Delay(100);
            async Task Check(string script, string message) => RequireSync(await view.ExecuteScriptAsync(script) == "true", message);
            await Check("document.querySelectorAll('.cards-grid .file-card').length===100", "a pasta não começou com apenas cem cards");
            await Check("document.querySelectorAll('[data-thumb] img').length>0", "as miniaturas iniciais não apareceram");
            const string prefetchedRow = "(()=>{const main=document.getElementById('main-area').getBoundingClientRect();const cards=[...document.querySelectorAll('.cards-grid .file-card')];const next=cards.find(card=>card.getBoundingClientRect().top>=main.bottom);if(!next)return false;const y=next.getBoundingClientRect().top;return cards.filter(card=>Math.abs(card.getBoundingClientRect().top-y)<2).every(card=>{const image=card.querySelector('[data-thumb] img');return image?.complete&&image.naturalWidth>0})})()";
            for (var attempt = 0; attempt < 30 && await view.ExecuteScriptAsync(prefetchedRow) != "true"; attempt++)
                await Task.Delay(100);
            await Check(prefetchedRow, "a linha abaixo da tela não foi antecipada");
            await view.ExecuteScriptAsync("window.__firstCard=document.querySelector('.cards-grid .file-card');window.__firstImage=window.__firstCard.querySelector('img');window._onThumb(window.__firstCard.querySelector('[data-thumb]').dataset.thumb);render()");
            await Task.Delay(100);
            await Check("window.__firstCard===document.querySelector('.cards-grid .file-card')&&window.__firstImage===window.__firstCard.querySelector('img')",
                "uma resposta repetida/renderização recriou o card ou a imagem visível");
            await view.ExecuteScriptAsync("window.__outside={name:'other.png',fileId:'outside',threadId:'outside-thread',metadataMessageId:'outside-meta',type:'file',path:'/Other',parentFolderId:'other'};window._onAppsBatch([window.__outside]);window._onAppsRemoved([window.__outside]);");
            await Check("window.__firstCard===document.querySelector('.cards-grid .file-card')&&window.__firstImage===window.__firstCard.querySelector('img')",
                "uma alteração em outra pasta recriou a imagem da pasta aberta");
            await view.ExecuteScriptAsync("window._onAppsBatch([{...window.__entries[0],sizeBytes:4096}]);window.__firstCard=document.querySelector('.cards-grid .file-card')");
            await Check("window.__firstCard.querySelector('img')===window.__firstImage", "a atualização do metadata substituiu a miniatura já exibida");
            await Task.Delay(150);
            await view.ExecuteScriptAsync("window.__firstCard=document.querySelector('.cards-grid .file-card')");
            await view.ExecuteScriptAsync("window.__firstImageSource=window.__firstImage.getAttribute('src');window.__firstCard.click()");
            for (var shown = 100; shown < fileCount; shown += 100)
            {
                await view.ExecuteScriptAsync("document.getElementById('main-area').scrollTop=document.getElementById('main-area').scrollHeight");
                await Task.Delay(160);
                await Check("document.querySelectorAll('.cards-grid .file-card').length===" + Math.Min(shown + 100, fileCount),
                    "a rolagem não acrescentou os cards do próximo lote");
            }
            for (var step = 0; step <= 24; step++)
            {
                await view.ExecuteScriptAsync("document.getElementById('main-area').scrollTop=(document.getElementById('main-area').scrollHeight-innerHeight)*" + step + "/24");
                await Task.Delay(160);
                await Check("window.__firstImage.isConnected&&window.__firstImage.getAttribute('src')===window.__firstImageSource&&Object.keys(window._thumbs||{}).length<=128",
                    "a rolagem descarregou a primeira miniatura ou perdeu o limite do cache de URLs");
            }
            await Check("window.__delivered.size>300&&window.__firstCard.isConnected&&allApps.length===window.__fileCount&&document.querySelectorAll('.cards-grid .file-card').length===window.__fileCount",
                "o teste não percorreu a biblioteca ou removeu cards ao sair da tela");
            RequireSync(!processFailure.Task.IsCompleted && !deliveryFailure.Task.IsCompleted, "o runtime ou a entrega binária falhou durante a rolagem");
            await Check("Object.values(window._thumbs).every(url=>url.startsWith('https://media.organessonium.local/thumbnail/'))&&document.querySelector('[data-thumb] img')?.naturalWidth===200",
                "a miniatura usou Base64 ou não chegou à interface como imagem binária");
            await view.ExecuteScriptAsync("document.getElementById('main-area').scrollTop=0");
            await Task.Delay(300);
            await Check("document.querySelector('.cards-grid .file-card')?.dataset.key===itemKey(window.__entries[0])&&document.querySelector('.cards-grid .file-card')?.classList.contains('selected')&&document.querySelector('.cards-grid .file-card img')===window.__firstImage",
                "voltar ao começo perdeu o primeiro arquivo, sua miniatura ou a seleção");
            await view.ExecuteScriptAsync("document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'a',ctrlKey:true,bubbles:true}))");
            await Check("getSelectedApps().length===window.__fileCount&&!document.getElementById('selection-bar').classList.contains('hidden')",
                "selecionar todos deixou arquivos de fora por não terem cards montados");
            await view.ExecuteScriptAsync("clearSelection()");
            AppMemoryBudget.RegisterWebViewProcesses(environment.GetProcessInfos().Select(info => info.ProcessId));
            var scrollPeak = AppMemoryBudget.PeakCommittedBytes;
            Console.WriteLine("METRIC scroll cards=" + fileCount + " peakBytes=" + scrollPeak);
            RequireSync(scrollPeak < AppMemoryBudget.LimitBytes, "a rolagem atingiu o limite de 1 GB");
            if (fileCount == 3000)
            {
                RequireSync(!IsWindowVisible(host), "o teste exibiu uma janela");
                return TestOutcome.Pass();
            }
            await view.ExecuteScriptAsync("""
                (async()=>{
                    const main=document.getElementById('main-area'),bounds=main.getBoundingClientRect();
                    const header=main.querySelector('.content-header').getBoundingClientRect();
                    main.dispatchEvent(new MouseEvent('mousedown',{button:0,clientX:bounds.left+10,clientY:header.bottom+8,bubbles:true}));
                    document.dispatchEvent(new MouseEvent('mousemove',{clientX:bounds.right+20,clientY:bounds.bottom+20,bubbles:true}));
                    main.scrollTop=main.scrollHeight*.6;
                    await new Promise(requestAnimationFrame);await new Promise(requestAnimationFrame);
                    document.dispatchEvent(new MouseEvent('mousemove',{clientX:bounds.right+20,clientY:bounds.bottom+20,bubbles:true}));
                    document.dispatchEvent(new MouseEvent('mouseup',{bubbles:true}));
                    window.__marqueeDone=true;
                })()
                """);
            for (var attempt = 0; attempt < 30 && await view.ExecuteScriptAsync("window.__marqueeDone===true") != "true"; attempt++) await Task.Delay(50);
            await Check("getSelectedApps().length>128&&!document.getElementById('selection-bar').classList.contains('hidden')&&document.getElementById('selection-count').textContent.includes(String(getSelectedApps().length))",
                "a seleção por arraste perdeu os arquivos fora da tela");
            await view.ExecuteScriptAsync("clearSelection();document.getElementById('main-area').scrollTop=0");
            await view.ExecuteScriptAsync("""
                window.__refreshRequests=[];window.__specialReads=0;
                window.bridge.refreshServerIndex=rebuild=>window.__refreshRequests.push(rebuild);
                window.bridge.loadFavorites=window.bridge.loadSynchronizedDevices=()=>window.__specialReads++;
                $('btn-refresh').click();
                document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'F5',bubbles:true}));
                document.body.dispatchEvent(new KeyboardEvent('keydown',{key:'F5',ctrlKey:true,bubbles:true}));
                window._onSpecialItemsLoaded('favorites',[]);$('sidebar-favorites').click();$('btn-refresh').click();window._onServerIndexRefreshed(false);
                window._onSpecialItemsLoaded('devices',[]);$('sidebar-devices').click();$('btn-refresh').click();window._onServerIndexRefreshed(false);
                $('sidebar-files').click();
                """);
            await Check("JSON.stringify(window.__refreshRequests)==='[false,false,true,false,false]'&&window.__specialReads===0",
                "Atualizar/F5 iniciou reconstrução, Ctrl+F5 não a solicitou ou uma aba especial iniciou outra leitura");
            await view.ExecuteScriptAsync("navigate('/',false,'root');window._onFolderLoadState('/',false);window._onAppsLoaded(window.__entries)");
            await view.ExecuteScriptAsync("window.__cardsBeforeViewSwitch=document.querySelectorAll('.cards-grid .file-card').length");
            for (var step = 0; step < 4; step++)
            {
                await view.ExecuteScriptAsync("setView('list')"); await Task.Delay(50);
                await view.ExecuteScriptAsync("setView('grid')"); await Task.Delay(100);
            }
            await Check("document.querySelectorAll('.cards-grid .file-card').length===window.__cardsBeforeViewSwitch", "alternar lista/grade perdeu cards já admitidos");
            await view.ExecuteScriptAsync("window._onAppsLoaded([])");
            await Task.Delay(100);
            await Check("!document.querySelector('[data-thumb]')&&visibleThumbnailIds.size===0&&window.__errors.length===0",
                "trocar grade/lista/pasta conservou alvos removidos ou gerou erro");
            ThumbnailCache.Configure(directory.CreateSubdirectory("thumbnails"));
            ThumbnailCache.Clear();
            var expected = ThumbnailCache.Store("evicted-first", png);
            for (var index = 0; index < 180; index++) ThumbnailCache.Store("cached-" + index, png);
            var cachedMemory = (System.Collections.IDictionary)typeof(ThumbnailCache).GetField("Memory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            RequireSync(cachedMemory.Count == 128 && !cachedMemory.Contains("evicted-first") && ThumbnailCache.Get("evicted-first") == expected,
                "o cache C# cresceu sem limite ou não recarregou a miniatura descartada a partir do disco");
            ThumbnailCache.Clear();
            AppMemoryBudget.RegisterWebViewProcesses(environment.GetProcessInfos().Select(info => info.ProcessId));
            RequireSync(AppMemoryBudget.PeakCommittedBytes < AppMemoryBudget.LimitBytes, "a navegação atingiu o limite de 1 GB");

            // Terminate only this fixture's renderer, never a user browser or the app.
            var window = new MainWindow(new SettingsStore(directory.CreateSubdirectory("data")));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var recovering = typeof(MainWindow).GetField("_recoveringWebView", flags)!;
            var shuttingDown = typeof(MainWindow).GetField("_shutdownInProgress", flags)!;
            var callback = typeof(MainWindow).GetMethod("OnWebViewProcessFailed", flags)!;
            var failed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.ProcessFailed += (_, args) =>
            {
                if (args.ProcessFailedKind != CoreWebView2ProcessFailedKind.RenderProcessExited) return;
                callback.Invoke(window, [view, args]);
                failed.TrySetResult(!(bool)recovering.GetValue(window)!);
                shuttingDown.SetValue(window, true); // Prevent a replacement WPF control in this never-shown test window.
            };
            try
            {
                var renderer = environment.GetProcessInfos().First(info => info.Kind == CoreWebView2ProcessKind.Renderer);
                using var process = Process.GetProcessById(renderer.ProcessId);
                process.Kill();
                RequireSync(await failed.Task.WaitAsync(TimeSpan.FromSeconds(10)), "a recuperação descartou o WebView durante o callback nativo");
                await Task.Delay(100);
                RequireSync(!window.IsVisible && !IsWindowVisible(host), "o teste exibiu uma janela");
            }
            finally
            {
                shuttingDown.SetValue(window, false);
                window.Closing -= typeof(MainWindow).GetMethod("OnClosing", flags, null,
                    [typeof(object), typeof(System.ComponentModel.CancelEventArgs)], null)!.CreateDelegate<System.ComponentModel.CancelEventHandler>(window);
                window.Close(); // No AppController/startup was created for this callback-only shell.
            }
            return TestOutcome.Pass();
        }
        finally
        {
            browser?.Close(); DestroyWindow(host); SqliteConnection.ClearAllPools();
            ThumbnailCache.Clear();
            try { directory.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });
}
