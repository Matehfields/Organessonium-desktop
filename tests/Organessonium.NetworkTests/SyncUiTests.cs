using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.Sync;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> WebViewSyncControlsAsync()
    {
        var result = new TaskCompletionSource<TestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var window = new Window { Title = "Organessônium: sincronização", Width = 1280, Height = 800 };
            var view = new WebView2(); window.Content = view;
            var dispatcher = Dispatcher.CurrentDispatcher;
            window.Loaded += async (_, _) =>
            {
                var root = Directory.CreateTempSubdirectory("organessonium-sync-ui-");
                AppController? controller = null; SyncBackgroundHost? host = null;
                try
                {
                    using var server = new SyncDiscordFixture();
                    using var discord = new DiscordApiClient("sync-ui-" + Guid.NewGuid().ToString("N"), new System.Net.Http.HttpClient(server, false));
                    var store = new SettingsStore(Directory.CreateDirectory(Path.Combine(root.FullName, "Data")));
                    var settings = new AppSettings { BotToken = "fixture", NamesChannelId = "names", FoldersForumId = "folders",
                        AppsForumId = "files", SerialChannelId = "serial", Theme = "dark", Language = "pt-BR", SearchWorkers = 1, PreviewEnabled = false };
                    store.Save(settings);
                    host = new SyncBackgroundHost(store, Path.Combine(root.FullName, "identity.txt"),
                        s => new CloudSyncRepository(store, s, discord, () => new System.Net.Http.HttpClient(server, false)));
                    await host.StartAsync();
                    var webRoot = Directory.CreateDirectory(Path.Combine(root.FullName, "web"));
                    var assembly = typeof(AppController).Assembly;
                    foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("web/", StringComparison.Ordinal)))
                    {
                        var target = Path.Combine(webRoot.FullName, name[4..].Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        using var source = assembly.GetManifestResourceStream(name)!; using var output = File.Create(target);
                        await source.CopyToAsync(output);
                    }
                    await view.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, Path.Combine(root.FullName, "WebView")));
                    view.CoreWebView2.SetVirtualHostNameToFolderMapping("organessonium.local", webRoot.FullName, CoreWebView2HostResourceAccessKind.Allow);
                    var bridge = new WebMessageBridge(view);
                    controller = new AppController(window, store, bridge, _ => Task.CompletedTask);
                    var session = new DiscordSession(); typeof(DiscordSession).GetProperty(nameof(DiscordSession.Api))!.SetValue(session, discord);
                    controller.SetSession(session); controller.AttachSyncHost(host);
                    var local = Directory.CreateDirectory(Path.Combine(root.FullName, "Local"));
                    var file = Path.Combine(local.FullName, "documento.txt"); await File.WriteAllTextAsync(file, "primeira versão"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-10));
                    view.CoreWebView2.WebMessageReceived += async (_, e) =>
                    {
                        using var message = JsonDocument.Parse(e.WebMessageAsJson);
                        var method = message.RootElement.GetProperty("method").GetString();
                        if (method == "chooseSyncFolder") await bridge.CallAsync("_onSyncFolderPicked", local.FullName);
                        else if (method is "loadSyncState" or "discoverSyncFolders" or "addSyncFolder" or "updateSyncFolder" or "removeSyncFolder" or
                            "syncNow" or "pauseSync" or "resolveSyncConflict" or "loadSynchronizedDevices" or "setCurrentPath")
                            await controller.HandleJsonMessageAsync(e.WebMessageAsJson);
                    };
                    var navigation = new TaskCompletionSource();
                    view.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) navigation.TrySetResult(); else navigation.TrySetException(new IOException("UI navigation failed")); };
                    view.CoreWebView2.Navigate("https://organessonium.local/index.html");
                    await navigation.Task.WaitAsync(TimeSpan.FromSeconds(20)); await bridge.SetSettingsAsync(settings);
                    await view.CoreWebView2.ExecuteScriptAsync("window.__testError='';window._onError=e=>window.__testError=String(e);window.onBridgeReady();document.getElementById('sidebar-devices').click();");
                    await WaitForLibraryScriptAsync(view, "window.__syncState&&document.getElementById('sync-add').getClientRects().length>0");
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('sync-add').click();document.querySelector('#sync-dialog-body button').click()");
                    await WaitForLibraryScriptAsync(view, "document.getElementById('sync-local-path')?.value.length>0");
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('sync-folder-name').value='Projetos';document.querySelector('#sync-dialog-actions .btn-primary').click()");
                    await WaitForLibraryScriptAsync(view, "window.__syncState.folders.length===1&&window.__syncState.statuses.some(s=>s.state==='synced')");
                    await WaitForLibraryScriptAsync(view, "[...document.querySelectorAll('#files-grid .folder-card')].some(c=>c.textContent.includes('" + Environment.MachineName + "'))");
                    await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('#files-grid .folder-card')].find(c=>c.textContent.includes('" + Environment.MachineName + "')).click()");
                    await WaitForLibraryScriptAsync(view, "document.querySelector('#files-grid .folder-card')?.textContent.includes('Projetos')");
                    await WaitForLibraryScriptAsync(view, "document.querySelector('.sync-card-status')?.textContent==='Sincronizado'");
                    await RequireLibraryScriptAsync(view, "document.querySelector('.sync-card-status').getBoundingClientRect().width>80&&document.querySelector('.sync-card-status').getBoundingClientRect().height>8", "O status não ficou visível no card da pasta.");
                    var screenshot = Path.Combine(Path.GetTempPath(), "organessonium-sync-ui-" + Guid.NewGuid().ToString("N") + ".png");
                    using (var output = File.Create(screenshot)) await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
                    Console.WriteLine("Sync UI screenshot: " + screenshot);
                    await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#files-grid .folder-card').click()");
                    await WaitForLibraryScriptAsync(view, "document.querySelector('#files-grid .file-card:not(.folder-card)')?.textContent.includes('documento.txt')");
                    await File.WriteAllTextAsync(Path.Combine(local.FullName, "novo.txt"), "nova criação pelo watcher");
                    await WaitForLibraryScriptAsync(view, "document.querySelectorAll('#files-grid .file-card:not(.folder-card)').length===2");
                    var folder = host.Folders.Single();
                    using var reader = new CloudSyncRepository(store, settings, discord);
                    var revision = (await reader.ReadAsync(folder, default)).Heads["documento.txt"].Single();
                    var issue = new SyncIssue { SyncFolderId = folder.SyncFolderId, RelativePath = "documento.txt", Kind = "remoteUpdate",
                        Message = "Existe uma versão mais recente deste arquivo na nuvem.", LocalHash = revision.Hash,
                        LocalModifiedUtc = DateTime.UtcNow.AddHours(-1).ToString("O"), Remote = [revision] };
                    host.State.SaveIssue(issue); host.Pause(true);
                    await WaitForLibraryScriptAsync(view, "!document.getElementById('sync-overlay').classList.contains('hidden')&&document.getElementById('sync-dialog-body').textContent.includes('documento.txt')");
                    await RequireLibraryScriptAsync(view, "document.getElementById('sync-dialog-body').textContent.includes('Local:')&&document.getElementById('sync-dialog-body').textContent.includes('Nuvem:')&&document.querySelectorAll('#sync-dialog-actions button').length>=4", "A pergunta sobre versão remota está incompleta.");
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('sync-remember').checked=true;[...document.querySelectorAll('#sync-dialog-actions button')].find(b=>b.textContent==='Usar versão da nuvem').click()");
                    await WaitForLibraryScriptAsync(view, "window.__syncState.issues[0]?.resolution==='cloud'");
                    RequireSync(host.State.Issues(folder.SyncFolderId).Single().Remember, "a decisão visível não persistiu pelo bridge");
                    host.State.DeleteIssue(folder.SyncFolderId, issue.RelativePath);
                    await view.CoreWebView2.ExecuteScriptAsync("document.getElementById('sync-manage').click();");
                    window.Width = 680; await Task.Delay(200);
                    await RequireLibraryScriptAsync(view, "document.querySelector('.sync-binding')?.textContent.includes('Projetos')&&document.getElementById('sync-overlay').getBoundingClientRect().width<=innerWidth&&document.querySelector('.sync-dialog').getBoundingClientRect().right<=innerWidth+1", "A configuração de pastas não cabe na janela menor.");
                    await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('.sync-binding-actions button')].find(b=>b.textContent==='Remover vínculo').click();document.querySelector('#sync-dialog-actions .btn-danger').click()");
                    await WaitForLibraryScriptAsync(view, "window.__syncState.folders.length===0");
                    RequireSync(File.Exists(file), "remover vínculo apagou arquivos locais");
                    result.TrySetResult(TestOutcome.Pass());
                }
                catch (Exception ex) { result.TrySetResult(TestOutcome.Fail(ex.ToString())); }
                finally
                {
                    if (controller != null) await controller.DisposeAsync(); if (host != null) await host.DisposeAsync();
                    view.Dispose(); window.Close(); dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    SqliteConnection.ClearAllPools(); try { root.Delete(true); } catch { }
                }
            };
            window.Show(); Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return await result.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }

    // Run last: WPF supports one Application per process. The runtime and tray are real, with isolated data.
    private static async Task<TestOutcome> SyncHeadlessTrayWindowLifecycleAsync()
    {
        var result = new TaskCompletionSource<TestOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Directory.CreateTempSubdirectory("organessonium-sync-runtime-");
            var store = new SettingsStore(Directory.CreateDirectory(Path.Combine(root.FullName, "Data")));
            store.Save(new AppSettings { CloseToTray = true });
            var browsers = Process.GetProcessesByName("msedgewebview2").Select(p => p.Id).ToHashSet();
            var app = new App(store, Path.Combine(root.FullName, "identity.txt"), true); app.InitializeComponent();
            app.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await app.DesktopStarted.WaitAsync(TimeSpan.FromSeconds(20));
                    RequireSync(app.Windows.Count == 0 && app.SyncHost != null, "background criou uma janela ou não iniciou o host");
                    RequireSync(!Process.GetProcessesByName("msedgewebview2").Any(p => !browsers.Contains(p.Id)), "background inicializou WebView2");
                    async Task<MainWindow> OpenAsync()
                    {
                        app.OpenMainWindow(); var window = app.Windows.OfType<MainWindow>().Single();
                        var view = (WebView2)window.FindName("WebView");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                        while (view.CoreWebView2 == null || view.Source?.AbsolutePath != "/setup.html")
                        { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(50); }
                        return window;
                    }
                    var first = await OpenAsync(); var firstView = (WebView2)first.FindName("WebView");
                    var browser = (int)firstView.CoreWebView2.BrowserProcessId;
                    first.Close();
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    {
                        bool Exited() { try { using var process = Process.GetProcessById(browser); return process.HasExited; } catch (ArgumentException) { return true; } }
                        while (app.Windows.Count > 0 || !Exited()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(100); }
                    }
                    RequireSync(app.SyncHost != null && !app.Exiting, "fechar encerrou o serviço configurado para tray");
                    var second = await OpenAsync();
                    RequireSync(!ReferenceEquals(first, second) && ((WebView2)second.FindName("WebView")).CoreWebView2 != null,
                        "abrir novamente não recriou a interface");
                    await app.ExitDesktopAsync();
                    RequireSync(app.SyncHost == null, "Sair manteve o worker ativo");
                    result.TrySetResult(TestOutcome.Pass());
                }
                catch (Exception ex) { result.TrySetResult(TestOutcome.Fail(ex.ToString())); await app.ExitDesktopAsync(); }
                finally
                {
                    SqliteConnection.ClearAllPools(); try { root.Delete(true); } catch { }
                    app.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return await result.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}
