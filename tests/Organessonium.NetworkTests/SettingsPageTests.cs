using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> SettingsPageHiddenBrowserAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var root = Directory.CreateTempSubdirectory("organessonium-settings-ui-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        try
        {
            var store = new SettingsStore(root.CreateSubdirectory("data"));
            var preferences = store.Load();
            preferences.Theme = "dark";
            preferences.Language = "pt-BR";
            store.Save(preferences);
            var environment = await CoreWebView2Environment.CreateAsync(null, root.CreateSubdirectory("browser").FullName);
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true;
            browser.Bounds = new System.Drawing.Rectangle(0, 0, 1920, 1080);
            var web = root.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            var view = browser.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
            await view.AddScriptToExecuteOnDocumentCreatedAsync("window.__errors=[];addEventListener('error',e=>window.__errors.push(e.message));");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var control = new WebView2();
            var bridge = new WebMessageBridge(control, async script => { await view.ExecuteScriptAsync(script); });
            await using var app = new AppController(new System.Windows.Window(), store, bridge, _ => Task.CompletedTask);
            view.WebMessageReceived += async (_, args) =>
            {
                using var json = JsonDocument.Parse(args.WebMessageAsJson);
                if (json.RootElement.GetProperty("method").GetString() is "saveSettings" or "configureCustomEncryption" or "setEncryptionMode")
                    await app.HandleJsonMessageAsync(args.WebMessageAsJson);
            };
            await bridge.SetSettingsAsync(store.Load());
            await bridge.SetSystemPreferencesAsync("dark", "#0078D4");
            await bridge.NotifyReadyAsync();
            await view.ExecuteScriptAsync("window._onAppsLoaded([{type:'folder',name:'Fotos de teste',folderId:'photos',parentFolderId:'root',path:'/'}]);$('sidebar-settings').click();");
            async Task Check(string expression, string message) => RequireSync(await view.ExecuteScriptAsync(expression) == "true", message);
            async Task WaitFor(string expression, string message)
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (await view.ExecuteScriptAsync(expression) == "true") return;
                    await Task.Delay(50);
                }
                await Check(expression, message);
            }
            var captures = Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, "artifacts", "settings-redesign", "captures"));
            async Task Capture(string name)
            {
                await WaitFor("document.fonts.status==='loaded'", "os ícones locais não carregaram");
                await Task.Delay(260);
                await using var output = File.Create(Path.Combine(captures.FullName, name + ".png"));
                await view.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            }

            await Check("isSettingsOpen()&&$('settings-panel').closest('.app-workspace')&&!$('settings-overlay')&&$('sidebar-settings').getAttribute('aria-current')==='page'&&$('sidebar-files').offsetWidth>0&&document.querySelector('.topbar').offsetHeight>0&&$('main-area').classList.contains('hidden')", "Configurações não abriu dentro da navegação lateral");
            await Check("!$('settings-close')&&document.querySelectorAll('[data-settings-pane]:not(.hidden)').length===1&&$('settings-pane-appearance').offsetHeight>0", "o cabeçalho ainda tem a seta interna ou mistura categorias");
            await Capture("appearance-dark");
            await view.ExecuteScriptAsync("window.__settingsTransitions=[];$('settings-panel').addEventListener('animationstart',e=>{if(e.target.matches('[data-settings-pane]'))window.__settingsTransitions.push(e.target.id)});$('settings-tab-appearance').click();");
            await Task.Delay(300);
            await Check("window.__settingsTransitions.length===0&&$('settings-tab-appearance').getAttribute('aria-selected')==='true'", "clicar na categoria aberta repetiu a transição");
            await view.ExecuteScriptAsync("$('setting-accent-mode').closest('.settings-custom-select').querySelector('button').click()");
            await Check("$('setting-accent-mode-options').offsetHeight>0&&getComputedStyle($('setting-accent-mode-options')).borderRadius==='11px'&&$('setting-accent-mode-options').querySelector('[aria-selected=\"true\"]').textContent==='Usar cor do Windows'", "as opções de cor continuaram usando a lista nativa");
            await Capture("appearance-options-dark");
            await view.ExecuteScriptAsync("$('setting-accent-mode-options').querySelector('[data-value=preset]').click()");
            await Check("settings.accentColorMode==='preset'&&$('accent-presets').offsetHeight>0&&$('setting-accent-mode-options').offsetHeight===0", "selecionar uma opção não atualizou o controle e a paleta");
            await view.ExecuteScriptAsync("$('setting-accent-mode').closest('.settings-custom-select').querySelector('button').click();$('setting-accent-mode-options').querySelector('[data-value=system]').click();$('setting-accent-mode').closest('.settings-custom-select').querySelector('button').click();document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))");
            await Check("isSettingsOpen()&&$('setting-accent-mode-options').offsetHeight===0&&settings.accentColorMode==='system'", "Escape fechou a página ou perdeu o valor do combo");
            await Check("$('settings-tab-previews').textContent.includes('Miniaturas')&&getComputedStyle($('settings-tab-previews').querySelector('.svg-icon')).color===getComputedStyle($('settings-tab-downloads').querySelector('.svg-icon')).color", "o nome Miniaturas ou o ícone neutro não foram aplicados");
            foreach (var category in new[] { "performance", "previews", "uploads", "downloads", "protection", "connection", "application" })
            {
                await view.ExecuteScriptAsync("$('settings-tab-" + category + "').click()");
                await Check("document.querySelectorAll('[data-settings-pane]:not(.hidden)').length===1&&$('settings-pane-" + category + "').offsetHeight>0&&$('settings-tab-" + category + "').getAttribute('aria-selected')==='true'", "categoria inacessível: " + category);
                await Capture(category + "-dark");
            }
            await Check("window.__settingsTransitions.includes('settings-pane-performance')", "uma troca real deixou de animar");
            await view.ExecuteScriptAsync("$('settings-tab-application').dispatchEvent(new KeyboardEvent('keydown',{key:'Home',bubbles:true}));");
            await Check("document.activeElement.id==='settings-tab-appearance'&&$('settings-pane-appearance').offsetHeight>0", "a navegação por teclado não selecionou a categoria");
            await view.ExecuteScriptAsync("document.querySelector('[data-theme-value=light]').click();$('settings-tab-performance').click();$('setting-animations').click();$('settings-tab-downloads').click();$('setting-threads').value=7;$('setting-threads').dispatchEvent(new Event('input',{bubbles:true}));$('btn-back').click();");
            await WaitFor("JSON.parse(window.bridge.getSettings()).downloadThreads===7", "o ajuste de download não foi salvo ao sair");
            RequireSync(store.Load().Theme == "light" && store.Load().DownloadThreads == 7 && !store.Load().AnimationsEnabled, "os controles perderam a persistência real");
            await view.ExecuteScriptAsync("$('sidebar-settings').click();$('settings-tab-appearance').click();$('setting-accent-mode').closest('.settings-custom-select').querySelector('button').click()");
            await Capture("appearance-options-light");
            await view.ExecuteScriptAsync("document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))");
            await view.ExecuteScriptAsync("$('sidebar-settings').click();$('settings-tab-protection').click();$('btn-encryption-custom').click()");
            await Check("settings.encryptionMode==='standard'&&settings.theme==='light'&&$('btn-encryption-custom').getAttribute('aria-checked')==='true'&&!$('encryption-standard-state').classList.contains('hidden')&&$('encryption-current-title').textContent.includes('padrão')&&$('btn-configure-custom-key').disabled&&$('setting-custom-key').offsetHeight>0", "selecionar a configuração da chave mudou o tema ou mostrou uma ativação inexistente");
            await Capture("protection-light-draft");
            await Check("!$('setting-custom-key-confirmed')&&$('setting-custom-passphrase').offsetWidth>0", "a confirmação continuou na página ou a criação da chave perdeu o campo");
            await view.ExecuteScriptAsync("$('setting-custom-passphrase').value='settings-fixture-passphrase';$('setting-custom-passphrase').dispatchEvent(new Event('input'));$('btn-configure-custom-key').click()");
            await Check("!$('confirm-overlay').classList.contains('hidden')&&settings.encryptionMode==='standard'", "a chave foi ativada antes da confirmação");
            await view.ExecuteScriptAsync("$('confirm-cancel').click()");
            RequireSync(store.Load().EncryptionMode == "standard" && !store.Load().CustomKeyAvailable, "cancelar o diálogo criou ou ativou a chave");
            await view.ExecuteScriptAsync("$('btn-configure-custom-key').click();$('confirm-ok').click()");
            await WaitFor("settings.encryptionMode==='custom'&&settings.customKeyAvailable===true&&$('encryption-current-title').textContent.includes('personalizada')", "a ativação real da chave não apareceu na interface");
            RequireSync(store.Load().EncryptionMode == "custom" && store.Load().CustomKeyAvailable && store.Load().Theme == "light", "a ativação da chave alterou o tema ou não persistiu");
            await Check("$('setting-custom-passphrase').offsetWidth===0&&!$('setting-custom-key-confirmed')", "a chave salva ainda exige senha ou checkbox na página");
            await Check("$('encryption-current-badge').textContent==='Ativa'&&!$('encryption-custom-state').classList.contains('hidden')&&$('btn-configure-custom-key').disabled&&!$('btn-export-custom-key').disabled&&$('setting-custom-passphrase').value==='';", "o estado ativo da proteção não ficou explícito");
            foreach (var action in new[] { "export", "import" })
            {
                await view.ExecuteScriptAsync("$('btn-" + action + "-custom-key').click()");
                await Check("!$('confirm-overlay').classList.contains('hidden')", "a confirmação de recuperação de chave desapareceu: " + action);
                await view.ExecuteScriptAsync("$('confirm-cancel').click()");
            }
            await view.ExecuteScriptAsync("$('settings-tab-appearance').click();document.querySelector('[data-theme-value=dark]').click();$('settings-tab-protection').click()");
            await Capture("protection-dark-active");
            await view.ExecuteScriptAsync("$('btn-encryption-standard').click()");
            await WaitFor("settings.encryptionMode==='standard'&&!$('encryption-standard-state').classList.contains('hidden')", "o retorno ao modo Padrão não atualizou a tela");
            RequireSync(store.Load().EncryptionMode == "standard" && store.Load().CustomKeyAvailable, "o modo Padrão removeu a chave salva");
            await view.ExecuteScriptAsync("$('btn-encryption-custom').click();$('btn-configure-custom-key').click();$('confirm-cancel').click()");
            RequireSync(store.Load().EncryptionMode == "standard", "cancelar a ativação da chave salva alterou a proteção");
            await view.ExecuteScriptAsync("$('btn-configure-custom-key').click();$('confirm-ok').click()");
            await WaitFor("settings.encryptionMode==='custom'&&$('setting-custom-passphrase').offsetWidth===0", "a chave salva não foi ativada sem repetir a senha");
            await view.ExecuteScriptAsync("$('btn-encryption-standard').click()");
            await WaitFor("settings.encryptionMode==='standard'", "o modo de teste não voltou ao Padrão");

            foreach (var width in new[] { 1920, 1366, 800 })
            {
                browser.Bounds = new System.Drawing.Rectangle(0, 0, width, 900);
                await Task.Delay(120);
                foreach (var category in new[] { "appearance", "previews", "uploads", "downloads", "protection", "connection", "application" })
                {
                    await view.ExecuteScriptAsync("$('settings-tab-" + category + "').click()");
                    await Check("(()=>{const panel=$('settings-panel').getBoundingClientRect(),sidebar=$('app-sidebar').getBoundingClientRect(),body=document.querySelector('.settings-body');return document.documentElement.scrollWidth<=innerWidth&&panel.left>=sidebar.right-1&&panel.right<=innerWidth&&body.scrollWidth<=body.clientWidth+1})()", "overflow ou sobreposição da navegação em " + category + "/" + width);
                    await Check("(()=>{const pane=document.querySelector('[data-settings-pane]:not(.hidden)'),r=pane.getBoundingClientRect();return [...pane.querySelectorAll('.settings-section')].every(section=>section.getBoundingClientRect().width>r.width*.45)&&[...pane.querySelectorAll('input:not(.native-hidden),select:not(.native-hidden),button')].filter(el=>el.offsetWidth>0&&el.type!=='checkbox').every(el=>{const b=el.getBoundingClientRect();return b.left>=r.left-1&&b.right<=r.right+1})})()", "controle cortado ou seção estreita em " + category + "/" + width);
                }
                if (width == 800) { await view.ExecuteScriptAsync("$('settings-tab-protection').click();$('btn-encryption-custom').click()"); await Capture("protection-compact"); }
            }
            browser.Bounds = new System.Drawing.Rectangle(0, 0, 1366, 900);
            await view.ExecuteScriptAsync("$('settings-tab-appearance').click();$('language-select-button').click();document.querySelector('#language-menu [data-language=en]').click();$('settings-tab-protection').click()");
            await Check("$('settings-title').textContent==='Settings'&&$('encryption-current-title').textContent.includes('Standard')&&$('encryption-current-badge').textContent==='Active'", "a tradução perdeu o estado de proteção");
            await view.ExecuteScriptAsync("$('settings-tab-appearance').click();$('language-select-button').click();document.querySelector('#language-menu [data-language=\"pt-BR\"]').click();$('sidebar-favorites').click()");
            await Check("!isSettingsOpen()&&!$('main-area').classList.contains('hidden')&&$('sidebar-favorites').getAttribute('aria-current')==='page'", "a navegação lateral não saiu das configurações");
            await view.ExecuteScriptAsync("$('sidebar-files').click();createMarkdownDocument();window.__editorTab=activeWorkspaceTab;$('sidebar-settings').click();goBack()");
            await Check("!isSettingsOpen()&&activeWorkspaceTab===window.__editorTab&&!editorArea.classList.contains('hidden')&&$('main-area').classList.contains('hidden')", "voltar das configurações perdeu a guia do editor");
            await view.ExecuteScriptAsync("$('sidebar-settings').click();document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));");
            await Check("!isSettingsOpen()&&window.__errors.length===0", "erro de JavaScript ou Escape não retornou ao conteúdo");
            RequireSync(!IsWindowVisible(host), "o teste intermediário exibiu uma janela");
            return TestOutcome.Pass();
        }
        finally
        {
            browser?.Close();
            DestroyWindow(host);
            SqliteConnection.ClearAllPools();
            try { root.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });
}
