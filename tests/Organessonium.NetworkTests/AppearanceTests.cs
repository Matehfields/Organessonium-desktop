using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> AppearanceHiddenBrowserAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-appearance-ui-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? controller = null;
        try
        {
            AppMemoryBudget.Initialize();
            var store = new SettingsStore(directory.CreateSubdirectory("data"));
            var preferences = store.Load();
            RequireSync(preferences.AccentColorMode == "system", "a instalação sem preferência não usa a cor do Windows");
            preferences.AccentColorMode = "custom";
            preferences.AccentColor = "#abcdef";
            store.Save(preferences.Clone());
            RequireSync(store.Load().AccentColorMode == "custom" && store.Load().AccentColor == "#ABCDEF", "a cor personalizada não persistiu");
            RequireSync(WindowsAccentService.NormalizeColor(WindowsAccentService.Read()).StartsWith('#'), "a leitura da cor do Windows falhou");

            var environment = await CoreWebView2Environment.CreateAsync(null, directory.CreateSubdirectory("browser").FullName);
            controller = await environment.CreateCoreWebView2ControllerAsync(host);
            // The parent HWND never receives WS_VISIBLE, including during preview capture.
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
            await view.ExecuteScriptAsync("""
                window.__errors=[];window.addEventListener('error',e=>window.__errors.push(e.message));
                window.__organessoniumSettings={language:'pt-BR',theme:'dark',animationsEnabled:true,cardSize:'medium',accentColorMode:'system',accentColor:'#0078D4'};
                window.__organessoniumSystemPreferences={theme:'dark',accentColor:'#0078D4'};
                window.onBridgeReady();
                window.__entries=Array.from({length:24},(_,i)=>({name:['Foto.jpg','Vídeo.mp4','Arquivo.7z'][i%3],fileId:'file-'+i,threadId:'thread-'+i,metadataMessageId:'metadata-'+i,type:'file',sizeBytes:4096,path:'/',parentFolderId:'root',favorite:i===0}));
                window.__entries.push({name:'Fotos',folderId:'photos',type:'folder',path:'/',parentFolderId:'root'});
                window._onAppsLoaded(window.__entries);
                window._onSpecialItemsLoaded('favorites',window.__entries.filter(e=>e.favorite));
                window._onSpecialItemsLoaded('devices',[]);
                window.__rowCount=()=>{const cards=[...document.querySelectorAll('.cards-grid .file-card:not(.folder-card)')];return cards.filter(el=>Math.abs(el.getBoundingClientRect().top-cards[0].getBoundingClientRect().top)<1).length};
                """);
            await Task.Delay(350);
            async Task Check(string script, string message) => RequireSync(await view.ExecuteScriptAsync(script) == "true", message);
            await Check("window.__rowCount()===6", "a grade Full HD não exibe seis cards no tamanho Médio");
            foreach (var width in new[] { 1920, 1366, 800, 3840 })
            {
                controller.Bounds = new System.Drawing.Rectangle(0, 0, width, 1080);
                await Task.Delay(180);
                await Check("[...document.querySelectorAll('.cards-grid .file-card')].every(el=>{const r=el.getBoundingClientRect(),m=el.querySelector('.card-media').getBoundingClientRect();return Math.abs(r.width-r.height)<1&&r.width<=301&&m.height<m.width&&Math.abs(m.left-r.left-9)<1})&&document.documentElement.scrollWidth<=innerWidth", "card retangular, margem ou overflow na largura " + width);
                var columns = JsonSerializer.Deserialize<int>(await view.ExecuteScriptAsync("window.__rowCount()"));
                RequireSync(width == 1920 ? columns == 6 : width < 1920 ? columns < 6 : columns > 6, "densidade não acompanha a janela de " + width);
            }
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1920, 1080);
            await Task.Delay(200);
            await Check("getComputedStyle(document.querySelector('.folder-card .svg-icon')).fontVariationSettings.includes('\"FILL\" 1')&&[...document.querySelectorAll('.card-file-type .icon-media')].every(el=>getComputedStyle(el).color==='rgb(234, 67, 53)')", "pastas não preenchidas ou ícones de mídia sem vermelho");
            var glyphBounds = JsonSerializer.Deserialize<int[]>(await view.ExecuteScriptAsync("(()=>{const r=document.querySelector('.folder-card .svg-icon').getBoundingClientRect();return [Math.floor(r.left),Math.floor(r.top),Math.ceil(r.width),Math.ceil(r.height)]})()"))!;
            async Task<int> GlyphPixels(int fill)
            {
                await view.ExecuteScriptAsync("document.querySelector('.folder-card .svg-icon').style.fontVariationSettings='\"FILL\" " + fill + "'");
                await Task.Delay(80);
                using var preview = new MemoryStream();
                await view.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
                preview.Position = 0;
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(preview, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                var bitmap = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                var pixels = new byte[glyphBounds[2] * glyphBounds[3] * 4];
                bitmap.CopyPixels(new System.Windows.Int32Rect(glyphBounds[0], glyphBounds[1], glyphBounds[2], glyphBounds[3]), pixels, glyphBounds[2] * 4, 0);
                return Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4] > 100 && pixels[i * 4 + 1] > 100 && pixels[i * 4 + 2] > 100);
            }
            var filledPixels = await GlyphPixels(1);
            var outlinePixels = await GlyphPixels(0);
            RequireSync(filledPixels > outlinePixels * 1.3, "o desenho de pasta continua vazado: " + filledPixels + "/" + outlinePixels);
            await view.ExecuteScriptAsync("document.querySelector('.folder-card .svg-icon').style.removeProperty('font-variation-settings')");
            await Check("['files','favorites','devices','trash'].every(id=>getComputedStyle(document.querySelector('#sidebar-'+id+' .svg-icon')).color!=='rgb(196, 199, 197)')", "cores da navegação lateral ausentes");
            await view.ExecuteScriptAsync("window.__neutral=()=>[...document.querySelectorAll('.file-card.selected,#sidebar-files,#btn-grid')].map(el=>[getComputedStyle(el).backgroundColor,getComputedStyle(el).borderColor]);document.querySelector('.cards-grid .file-card').click()");
            await Task.Delay(160);
            await view.ExecuteScriptAsync("window.__selectionColors=JSON.stringify(window.__neutral());window._onSystemAccentChanged('#FF0000')");
            await Task.Delay(160);
            await Check("document.querySelector('.file-card.selected')&&JSON.stringify(window.__neutral())===window.__selectionColors&&getComputedStyle(document.querySelector('#sidebar-settings .svg-icon')).color==='rgb(196, 199, 197)'", "seleção/botão ativo seguem a cor de destaque ou a engrenagem está colorida");
            await view.ExecuteScriptAsync("(()=>{const r=$('main-area').getBoundingClientRect();$('main-area').dispatchEvent(new MouseEvent('mousedown',{bubbles:true,button:0,clientX:r.right-8,clientY:r.bottom-30}));document.dispatchEvent(new MouseEvent('mousemove',{bubbles:true,clientX:r.left+300,clientY:r.top+300}))})()");
            await Check("(()=>{const rect=document.querySelector('.selection-rect'),style=rect&&getComputedStyle(rect);return !!rect&&style.borderTopColor==='rgb(154, 160, 166)'&&style.backgroundColor==='rgba(196, 199, 197, 0.22)'})()", "a seleção por arraste continua usando a cor de destaque");
            var captures = Path.Combine(Environment.CurrentDirectory, "artifacts", "interface-polish");
            Directory.CreateDirectory(captures);
            await using (var preview = File.Create(Path.Combine(captures, "neutral-selection-dark.png")))
                await view.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
            await view.ExecuteScriptAsync("document.dispatchEvent(new MouseEvent('mouseup',{bubbles:true}));window.bridge.requestFileInformation=(mid,id)=>window.__menuInfo=id;document.querySelector('.cards-grid .file-card').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:500,clientY:350}));window.__menuAnimation=$('ctx-menu').getAnimations().find(a=>a.animationName==='contextMenuEnter')");
            await Check("!!window.__menuAnimation&&!$('ctx-menu').classList.contains('hidden')", "o botão direito não animou a abertura do menu");
            await view.ExecuteScriptAsync("window._onFileInformation(window.__menuInfo,{entry:ctxTarget})");
            await Check("$('ctx-menu').getAnimations().includes(window.__menuAnimation)", "a resposta dos metadados reiniciou a animação do menu");
            await view.ExecuteScriptAsync("hideCtx();clearSelection();window.__mainBefore=JSON.stringify($('main-area').getBoundingClientRect().toJSON())");

            await view.ExecuteScriptAsync("window.__indicatorStart=$('sidebar-selection-indicator').getBoundingClientRect().top;$('sidebar-favorites').click()");
            await Check("$('main-area').getAnimations({subtree:true}).some(a=>a.animationName==='workspaceContentEnter')&&!$('main-area').getAnimations().length&&getComputedStyle($('main-area')).transform==='none'&&JSON.stringify($('main-area').getBoundingClientRect().toJSON())===window.__mainBefore&&getComputedStyle($('sidebar-favorites')).color==='rgb(242, 242, 242)'&&$('sidebar-favorites').getAttribute('aria-current')==='page'", "a troca lateral move o fundo escuro ou não anima o conteúdo interno");
            await Task.Delay(300);
            await Check("$('sidebar-selection-indicator').getBoundingClientRect().top>window.__indicatorStart&&getComputedStyle($('main-area')).opacity==='1'&&getComputedStyle($('main-area')).transform==='none'", "a linha lateral não moveu ou o fundo escuro foi animado");
            await view.ExecuteScriptAsync("$('sidebar-files').click();createMarkdownDocument()");
            await Check("document.querySelector('.markdown-editor-tab:not(.hidden)').getAnimations().some(a=>a.animationName==='workspaceContentEnter')", "a guia do editor não usa a transição solicitada");
            await view.ExecuteScriptAsync("document.querySelector('.workspace-tab[data-tab-id=\"files\"]').click()");
            await Check("$('main-area').getAnimations({subtree:true}).some(a=>a.animationName==='workspaceContentEnter')&&getComputedStyle($('main-area')).transform==='none'", "a guia Arquivos não anima dentro da área fixa ao voltar do editor");
            await view.ExecuteScriptAsync("settings.animationsEnabled=false;syncMotionPreference();$('sidebar-favorites').click()");
            await Check("!$('main-area').getAnimations({subtree:true}).some(a=>a.animationName==='workspaceContentEnter')", "a preferência de desativar animações foi ignorada");
            await view.ExecuteScriptAsync("showBackgroundCtx({target:$('main-area'),clientX:500,clientY:350,preventDefault(){}})");
            await Check("!$('ctx-menu').getAnimations().some(a=>a.animationName==='contextMenuEnter')", "o menu animou com as animações desativadas");
            await view.ExecuteScriptAsync("hideCtx()");

            await view.ExecuteScriptAsync("$('sidebar-settings').click();window._onSystemAccentChanged('#D13438')");
            await Check("getComputedStyle(document.documentElement).getPropertyValue('--accent-base').trim()==='#D13438'&&$('sidebar-settings').getAttribute('aria-current')==='page'", "a cor do Windows ou a seleção Configurações não atualizou");
            await view.ExecuteScriptAsync("$('setting-accent-mode').value='preset';$('setting-accent-mode').dispatchEvent(new Event('change'));document.querySelector('[data-accent=\"#107C10\"]').click();window._onSystemAccentChanged('#E3008C')");
            await Check("!$('accent-presets').classList.contains('hidden')&&getComputedStyle(document.documentElement).getPropertyValue('--accent-base').trim()==='#107C10'&&document.querySelector('[data-accent=\"#107C10\"]').getAttribute('aria-pressed')==='true'", "a paleta não aparece ou a cor do Windows sobrescreve a preferência");
            await view.ExecuteScriptAsync("$('setting-accent-mode').value='custom';$('setting-accent-mode').dispatchEvent(new Event('change'));$('setting-accent-hex').value='#abcdef';$('setting-accent-hex').dispatchEvent(new Event('input'));saveSettings(false);loadSettings()");
            await Check("settings.accentColor==='#ABCDEF'&&settings.accentColorMode==='custom'&&window.__organessoniumSettings.accentColor==='#ABCDEF'&&!$('accent-custom-controls').classList.contains('hidden')", "cor personalizada não foi aplicada ou salva pelo bridge");
            await view.ExecuteScriptAsync("$('setting-accent-hex').value='invalid';$('setting-accent-hex').dispatchEvent(new Event('input'));setLanguage('en')");
            await Check("settings.accentColor==='#ABCDEF'&&$('setting-accent-label').textContent==='Accent color'", "cor inválida foi aceita ou o label não acompanha o idioma");
            await view.ExecuteScriptAsync("applyTheme('light');$('setting-accent-hex').value='#ffffff';$('setting-accent-hex').dispatchEvent(new Event('input'))");
            await Task.Delay(200);
            await Check("(()=>{const rgb=getComputedStyle(document.documentElement).getPropertyValue('--accent-rgb').split(',').map(Number);return (accentLuminance([248,249,250])+.05)/(accentLuminance(rgb)+.05)>=3})()", "cor clara não foi ajustada para contraste no tema claro");
            await Check("[...document.querySelectorAll('.card-file-type .icon-media')].every(el=>getComputedStyle(el).color==='rgb(234, 67, 53)')", "mídia perdeu a cor no tema claro");
            await Check("getComputedStyle(document.querySelector('#sidebar-settings .svg-icon')).color==='rgb(95, 99, 104)'", "a engrenagem não é neutra no tema claro: " + await view.ExecuteScriptAsync("[getComputedStyle(document.querySelector('#sidebar-settings .svg-icon')).color,getComputedStyle($('sidebar-settings')).getPropertyValue('--icon-color'),document.documentElement.dataset.theme]"));
            await using (var preview = File.Create(Path.Combine(captures, "appearance-light-1920.png")))
                await view.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
            await view.ExecuteScriptAsync("setLanguage('pt-BR');closeSettings();$('sidebar-files').click();settings.animationsEnabled=true;syncMotionPreference();applyTheme('dark');settings.accentColorMode='system';window._onSystemAccentChanged('#0078D4');render()");
            await Task.Delay(400);
            await using (var preview = File.Create(Path.Combine(captures, "cards-dark-1920.png")))
                await view.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
            await Check("window.__errors.length===0", "erro JavaScript na interface: " + await view.ExecuteScriptAsync("window.__errors"));
            await view.ExecuteScriptAsync("""
                document.documentElement.setAttribute('data-theme','light');
                window.__volumePlayer=document.createElement('video');
                window.__volumeFrame=createVideoChrome(window.__volumePlayer);
                window.__volumePlayer.volume=.23;
                window.__volumePlayer.dispatchEvent(new Event('volumechange'));
                window.__nextPlayer=document.createElement('video');
                window.__nextFrame=createVideoChrome(window.__nextPlayer);
                openPreview(window.__entries[0]);window._closePreviewFromHost();
                """);
            await Check("Math.abs(window.__nextPlayer.volume-.23)<.001&&window.__nextFrame.querySelector('[data-video-volume]').value==='0.23'",
                "o player esqueceu o volume entre vídeos");
            await Check("document.getElementById('preview-overlay').classList.contains('hidden')&&document.querySelector('.preview-topbar').firstElementChild.id==='preview-close'",
                "fechar a prévia pelo host falhou ou o botão permaneceu junto dos controles da janela");
            await Check("getComputedStyle(document.getElementById('sidebar-files')).getPropertyValue('--icon-color').trim()==='#c77800'&&getComputedStyle(document.getElementById('sidebar-devices')).getPropertyValue('--icon-color').trim()==='#0078bd'",
                "o tema claro não aplicou os tons mais saturados da navegação");
            await view.ExecuteScriptAsync("settings.previewEnabled=true;openPreview(window.__entries[0]);openPreview(window.__entries[3]);window._onPreviewReady('image','obsolete.jpg','data:image/png;base64,invalid',fileRequestId(window.__entries[0]))");
            await Check("!document.querySelector('#preview-body img[alt=\"obsolete.jpg\"]')&&previewAppRef.fileId==='file-3'",
                "a resposta de uma foto anterior substituiu a prévia atual");
            await view.ExecuteScriptAsync("window.__dragRects=[];window.bridge.setTitleBarDragRegion=(...rects)=>window.__dragRects=rects;window._onNativeTitleBarMetrics({enabled:true,leftInset:0,rightInset:138,height:48});$('preview-zoom-controls').classList.remove('hidden')");
            foreach (var height in new[] { 48, 40 })
            {
                await view.ExecuteScriptAsync("window._onNativeTitleBarMetrics({enabled:true,leftInset:0,rightInset:138,height:" + height + "})");
                await Task.Delay(250);
                await Check("(()=>{const close=$('preview-close').getBoundingClientRect(),bar=document.querySelector('.preview-topbar').getBoundingClientRect(),actions=document.querySelector('.preview-topbar-actions').getBoundingClientRect();return window.__dragRects.length===4&&window.__dragRects[0]>=close.right&&window.__dragRects[0]+window.__dragRects[2]<=actions.left&&bar.height===" + height + "&&Math.abs((close.top+close.bottom)/2-" + height + "/2)<1&&Math.abs((actions.top+actions.bottom)/2-" + height + "/2)<1&&actions.right<=innerWidth-138})()",
                    "a região de arraste cobre controles ou os ícones não alinham com a barra nativa");
            }
            await view.ExecuteScriptAsync("$('preview-close').click()");
            await Check("$('preview-overlay').classList.contains('hidden')", "o X ao lado do nome não fechou a prévia");
            await view.ExecuteScriptAsync("window._onNativeTitleBarMetrics({enabled:false,height:48})");
            var movie = new FileInfo(Path.Combine(directory.FullName, "synthetic.mov"));
            var ffmpeg = Path.Combine(Path.GetDirectoryName(assembly.Location)!, "media", "ffmpeg.exe");
            var start = new System.Diagnostics.ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=640x360:r=30", "-t", "12",
                "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", "-crf", "18", "-pix_fmt", "yuv420p", "-an", "-movflags", "+faststart", "-f", "mov", movie.FullName }) start.ArgumentList.Add(argument);
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                var errors = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                RequireSync(process.ExitCode == 0, "não foi possível gerar o MOV sintético: " + await errors);
            }
            using var media = new PreviewResourceRegistry();
            using var adapter = new Organessonium.Bridge.WebView2PreviewAdapter(view, media);
            adapter.Attach();
            var original = await File.ReadAllBytesAsync(movie.FullName);
            var crypto = CreateEncryptionService(out _);
            const int chunkBytes = 128 * 1024;
            var chunkCount = (original.Length + chunkBytes - 1) / chunkBytes;
            RequireSync(original.Length > 2 * PreviewResourceRegistry.VideoResponseBytes, "o MOV não exercita várias faixas de streaming");
            var context = new EncryptedPayloadContext("hidden-video", crypto.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Original, 0, chunkCount);
            var payloads = Enumerable.Range(0, chunkCount).Select(index => crypto.EncryptBytes(original.AsSpan(index * chunkBytes, Math.Min(chunkBytes, original.Length - index * chunkBytes)), context.WithChunkIndex(index))).ToArray();
            using var handler = new VideoHttpHandler((request, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.ByteArrayContent(payloads[int.Parse(request.RequestUri!.Segments.Last())])
            }));
            using var discord = new DiscordApiClient("hidden-fixture", new System.Net.Http.HttpClient(handler, false));
            var attachments = payloads.Select((payload, index) => new VideoAttachment(index, index.ToString(), "chunk.bin", "https://cdn.invalid/" + index, payload.Length)).ToArray();
            await using var video = new VideoStreamSession(new DiscordVideoChunkProvider(discord, "hidden-thread", attachments, crypto, context, original.Length, new System.Net.Http.HttpClient(handler, false)));
            video.SetPlaying(true);
            var processFailures = new List<string>();
            view.ProcessFailed += (_, args) => processFailures.Add(args.ProcessFailedKind.ToString());
            var mediaData = JsonSerializer.Serialize(new { src = media.RegisterVideo(video, "video/quicktime"), mime = "video/quicktime", quality = "full", qualities = new[] { "full" } });
            await view.ExecuteScriptAsync("window._onPreviewReady('video','synthetic.mov'," + JsonSerializer.Serialize(mediaData) + ")");
            for (var attempt = 0; attempt < 50 && await view.ExecuteScriptAsync("document.querySelector('#preview-body video')?.readyState>=2") != "true"; attempt++)
                await Task.Delay(100);
            await Check("(()=>{const video=document.querySelector('#preview-body video');return video.readyState>=2&&!video.error&&!video.querySelector('source').hasAttribute('type')})()",
                "o MOV H.264 não foi carregado pelo player: " + await view.ExecuteScriptAsync("(()=>{const v=document.querySelector('#preview-body video');return {state:v?.readyState,error:v?.error?.code}})()"));
            await view.ExecuteScriptAsync("(()=>{const v=document.querySelector('#preview-body video');v.muted=true;v.play().catch(()=>{})})()");
            await Task.Delay(1200);
            await Check("document.querySelector('#preview-body video').currentTime>.5&&!document.querySelector('#preview-body video').error", "o vídeo carregou mas não reproduziu");
            await view.ExecuteScriptAsync("document.querySelector('#preview-body video').currentTime=9");
            for (var attempt = 0; attempt < 60 && await view.ExecuteScriptAsync("document.querySelector('#preview-body video').currentTime>9.5") != "true"; attempt++)
                await Task.Delay(100);
            await Check("document.querySelector('#preview-body video').currentTime>9.5&&!document.querySelector('#preview-body video').error", "o seek interrompeu a reprodução por chunks");
            RequireSync(processFailures.Count == 0, "o WebView falhou durante a reprodução: " + string.Join(",", processFailures));
            AppMemoryBudget.RegisterWebViewProcesses(environment.GetProcessInfos().Select(info => info.ProcessId));
            RequireSync(AppMemoryBudget.PeakCommittedBytes < AppMemoryBudget.LimitBytes, "a reprodução atingiu o limite de 1 GB");
            await view.ExecuteScriptAsync("closePreview()");
            RequireSync(!IsWindowVisible(host), "o teste exibiu uma janela");
            return TestOutcome.Pass();
        }
        finally
        {
            controller?.Close();
            DestroyWindow(host);
            SqliteConnection.ClearAllPools();
            try { directory.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });
}
