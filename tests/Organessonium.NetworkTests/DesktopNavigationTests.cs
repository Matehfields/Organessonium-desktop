using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out AcceptanceRect rectangle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendWindowCommand(IntPtr handle, uint message, IntPtr command, IntPtr parameter);

    private static Task<TestOutcome> DesktopNavigationHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-desktop-navigation-");
        var store = new SettingsStore(directory);
        store.CacheEntries([
            new AppEntry { Type = "file", Name = "Árvore.png", Path = "/Arquivo/Viagens", ParentFolderId = "trip", MetadataMessageId = "12" },
            new AppEntry { Type = "file", Name = "Árvore excluída.png", Path = "/@trash", ParentFolderId = "trash", MetadataMessageId = "13" }
        ]);
        RequireSync(store.SearchCachedLibrary("arvore") is [{ RequestId: "12" }], "a busca de arquivos no SQLite não retornou a entrada esperada");
        var window = new MainWindow(store) { Opacity = 0, ShowActivated = false, ShowInTaskbar = false, WindowState = WindowState.Normal };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            window.Show();
            var view = (WebView2)window.FindName("WebView");
            async Task CheckAsync(string expression, string failure)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!timeout.IsCancellationRequested)
                {
                    if (view.CoreWebView2 != null && await view.ExecuteScriptAsync(expression) == "true") return;
                    await Task.Delay(50);
                }
                var searchState = await view.ExecuteScriptAsync("JSON.stringify({status:document.getElementById('library-search-status')?.textContent," +
                    "requests:window._searchTestRequests,results:window._searchTestResults,error:window._searchTestError})");
                throw new InvalidOperationException(failure + " " + searchState);
            }
            await CheckAsync("location.pathname==='/setup.html' && typeof openTokenEditMenu==='function'", "o menu do token não carregou");
            await view.ExecuteScriptAsync("""
                window._testClipboard='abc123';
                Object.defineProperty(navigator,'clipboard',{value:{readText:async()=>_testClipboard,writeText:async value=>{_testClipboard=value;}}});
                inpToken.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,clientX:50,clientY:50}));
                document.querySelector('[data-token-edit=paste]').click();
                """);
            await CheckAsync("inpToken.value==='abc123' && inpToken.type==='password'", "o menu não colou no campo protegido");
            await view.ExecuteScriptAsync("""
                openTokenEditMenu(50,50); document.querySelector('[data-token-edit=select]').click();
                openTokenEditMenu(50,50); document.querySelector('[data-token-edit=cut]').click();
                """);
            await CheckAsync("inpToken.value==='' && _testClipboard==='abc123'", "o menu não selecionou e recortou o token fictício");

            typeof(MainWindow).GetMethod("LoadLocalPage", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, ["index.html"]);
            await CheckAsync("location.pathname==='/index.html' && document.readyState==='complete' && !!window.__organessoniumSettings",
                "a interface de busca não carregou");
            await view.ExecuteScriptAsync("""
                window._searchPreview=null; bridge.requestPreview=(...args)=>{_searchPreview=args;};
                window.addEventListener('error',event=>{window._searchTestError=event.message;});
                const searchRequest=bridge.searchLibrary, searchResult=window._onLibrarySearchResults;
                bridge.searchLibrary=(...args)=>{window._searchTestRequests=args;searchRequest(...args);};
                window._onLibrarySearchResults=(...args)=>{window._searchTestResults=args;searchResult(...args);};
                (()=>{const input=document.getElementById('search-input');input.value='arvore';input.dispatchEvent(new Event('input'));})();
                """);
            await CheckAsync("""
                (()=>{const list=document.getElementById('library-search-results'),row=list.firstElementChild;
                if(list.children.length!==1 || !row.textContent.includes('Árvore.png') ||
                    !row.textContent.includes('/Arquivo/Viagens') || currentPath!=='/') return false;
                const rect=row.getBoundingClientRect();return row.contains(document.elementFromPoint(rect.x+20,rect.y+10));})()
                """, "a busca não retornou o arquivo de outra pasta ou incluiu a lixeira");
            await view.ExecuteScriptAsync("""
                (()=>{const input=document.getElementById('search-input');
                for(const key of ['ArrowDown','Enter']) input.dispatchEvent(new KeyboardEvent('keydown',{key,bubbles:true}));})();
                """);
            await CheckAsync("_searchPreview?.[0]==='12' && document.getElementById('library-search-popup').hidden", "escolher o resultado não abriu a prévia");
            await view.ExecuteScriptAsync("closePreview(); document.getElementById('server-read-status').classList.remove('hidden');");
            await CheckAsync("""
                (()=>{const y=selector=>{const r=document.querySelector(selector).getBoundingClientRect();return r.top+r.height/2;};
                return Math.abs(y('#storage-display')-y('#server-item-count'))<1 && Math.abs(y('#server-read-status')-y('#server-items-label'))<1 &&
                Math.abs(y('#sidebar-settings > [data-icon]')-y('#sidebar-settings-text'))<1;})()
                """, "os ícones e textos da barra lateral ficaram desalinhados");

            var handle = new WindowInteropHelper(window).Handle;
            var screen = System.Windows.Forms.Screen.FromHandle(handle).Bounds;
            RequireSync(GetWindowRect(handle, out var original), "não foi possível ler a geometria inicial da janela");
            window.SetBridgeFullscreen(true);
            RequireSync(GetWindowRect(handle, out var full) && full.Left == screen.Left && full.Top == screen.Top &&
                full.Right == screen.Right && full.Bottom == screen.Bottom, "a janela não ocupou todo o monitor físico");
            SendWindowCommand(handle, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
            RequireSync(IsIconic(handle) && window.WindowState == WindowState.Minimized, "o comando nativo de minimizar não minimizou a janela");
            window.WindowState = WindowState.Normal;
            RequireSync(GetWindowRect(handle, out var restored) && Math.Abs(restored.Left - original.Left) <= 1 &&
                Math.Abs(restored.Top - original.Top) <= 1 && Math.Abs(restored.Right - original.Right) <= 1 &&
                Math.Abs(restored.Bottom - original.Bottom) <= 1, "a janela não recuperou a geometria após restaurar");
            return TestOutcome.Pass();
        }
        finally
        {
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(12));
        }
    });
}
