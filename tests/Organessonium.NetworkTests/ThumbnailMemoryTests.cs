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
                window.__delivered=new Set();window.__images={};
                window.__image=id=>{if(window.__images[id])return window.__images[id];const c=document.createElement('canvas');c.width=c.height=200;const g=c.getContext('2d');g.fillStyle='#58a';g.fillRect(0,0,200,200);g.fillStyle='white';g.fillText(id,8,80);return window.__images[id]=c.toDataURL('image/png')};
                window.bridge.loadVisibleThumbnails=json=>JSON.parse(json).forEach(id=>{window.__delivered.add(id);window._thumbs=window._thumbs||{};window._thumbs[id]=window.__image(id);window._onThumb(id)});
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
            for (var shown = 100; shown < fileCount; shown += 100)
            {
                await view.ExecuteScriptAsync("document.getElementById('main-area').scrollTop=document.getElementById('main-area').scrollHeight");
                await Task.Delay(160);
                await Check("document.querySelectorAll('.cards-grid .file-card').length===" + Math.Min(shown + 100, fileCount),
                    "a rolagem não acrescentou o próximo lote de cem cards");
            }
            for (var step = 0; step <= 24; step++)
            {
                await view.ExecuteScriptAsync("document.getElementById('main-area').scrollTop=(document.getElementById('main-area').scrollHeight-innerHeight)*" + step + "/24");
                await Task.Delay(160);
                await Check("document.querySelectorAll('[data-thumb] img').length<=64&&Object.keys(window._thumbs||{}).length<=128",
                    "a rolagem reteve imagens fora da tela ou ultrapassou o cache limitado");
            }
            await Check("window.__delivered.size>300&&!window.__firstCard.querySelector('img')&&document.querySelectorAll('.cards-grid .file-card').length===window.__fileCount",
                "o teste não percorreu a biblioteca, reteve a primeira imagem ou removeu cards");
            RequireSync(!processFailure.Task.IsCompleted, "o runtime falhou durante a rolagem");
            await view.ExecuteScriptAsync("document.getElementById('main-area').scrollTop=0");
            await Task.Delay(300);
            await Check("!!window.__firstCard.querySelector('img')", "voltar ao começo não recarregou a miniatura descartada");
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
            for (var step = 0; step < 4; step++)
            {
                await view.ExecuteScriptAsync("setView('list')"); await Task.Delay(50);
                await view.ExecuteScriptAsync("setView('grid')"); await Task.Delay(100);
            }
            await view.ExecuteScriptAsync("window._onAppsLoaded([])");
            await Task.Delay(100);
            await Check("!document.querySelector('[data-thumb]')&&visibleThumbnailIds.size===0&&window.__errors.length===0",
                "trocar grade/lista/pasta conservou alvos removidos ou gerou erro");
            ThumbnailCache.Configure(directory.CreateSubdirectory("thumbnails"));
            ThumbnailCache.Clear();
            var png = Convert.FromBase64String(System.Text.Json.JsonSerializer.Deserialize<string>(
                await view.ExecuteScriptAsync("window.__image('cache-test').split(',')[1]"))!);
            var expected = ThumbnailCache.Store("evicted-first", png);
            for (var index = 0; index < 180; index++) ThumbnailCache.Store("cached-" + index, png);
            var cachedMemory = (System.Collections.IDictionary)typeof(ThumbnailCache).GetField("Memory", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            RequireSync(cachedMemory.Count == 128 && !cachedMemory.Contains("evicted-first") && ThumbnailCache.Get("evicted-first") == expected,
                "o cache C# cresceu sem limite ou não recarregou a miniatura descartada a partir do disco");
            // Exercise the byte budget as well as entry count, using data stored only in this fixture directory.
            var larger = new byte[1024 * 1024]; png.CopyTo(larger, 0);
            for (var index = 0; index < 12; index++) ThumbnailCache.Store("larger-" + index, larger);
            var characters = (long)typeof(ThumbnailCache).GetField("MemoryCharacters", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            RequireSync(characters <= 8L * 1024 * 1024 && ThumbnailCache.Get("larger-0") != null,
                "o orçamento de 16 MiB do cache C# foi excedido ou o fallback local falhou");
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
