using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> SharedStorageControllerBatchAndLargeRoutingAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        await h.Registry.RefreshMappingsAsync();
        using var web = new WebView2();
        await using var controller = new AppController(new System.Windows.Window(), h.Store, new WebMessageBridge(web), _ => Task.CompletedTask);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var session = new DiscordSession();
        typeof(DiscordSession).GetProperty("Api")!.SetValue(session, h.Api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
        controller.SetSession(session);
        typeof(AppController).GetField("_registry", flags)!.SetValue(controller, h.Registry);
        var sources = h.Directory.CreateSubdirectory("sources");
        var paths = Enumerable.Range(0, 10).Select(index => Path.Combine(sources.FullName, "small-" + index + ".bin")).ToArray();
        foreach (var path in paths) await File.WriteAllBytesAsync(path, new byte[1024]);
        var uploadPaths = typeof(AppController).GetMethod("UploadPathsAsync", flags)!;
        await ((Task)uploadPaths.Invoke(controller, [paths])!).WaitAsync(TimeSpan.FromSeconds(30));
        var visible = (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
        RequireSync(visible.Count(entry => entry.Type == "file") == 10 && visible.Where(entry => entry.Type == "file").All(entry => entry.Storage?.Shared == true),
            "o upload real do controller não publicou todos os cards compartilhados");
        var thread = h.Server.FileThreads().Single();
        RequireSync(h.Server.Messages(thread).Count(message => message.Attachments.Count == 10) == 1,
            "o limite de arquivos ativos impediu o batch real de dez arquivos pequenos");
        var large = new FileInfo(Path.Combine(sources.FullName, "large.bin"));
        using (var sparse = new FileStream(large.FullName, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            if (!DeviceIoControl(sparse.SafeFileHandle.DangerousGetHandle(), 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException("Não foi possível criar o arquivo sintético esparso.");
            sparse.SetLength(DiscordLimits.DedicatedThreadThresholdBytes + 1);
        }
        h.Server.DiscardBinaryPayloads = true;
        await ((Task)uploadPaths.Invoke(controller, [new[] { large.FullName }])!).WaitAsync(TimeSpan.FromSeconds(60));
        visible = (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
        var published = visible.Single(entry => entry.Name == "large.bin");
        RequireSync(published.Storage is { Shared: false, Mode: "dedicated" } && published.SizeBytes == large.Length &&
            published.ThreadId != thread && published.ChunkReferences?.Count == 52 &&
            h.Server.PayloadBytesRead == large.Length + 52 * EncryptionService.EnvelopeOverheadBytes,
            "o controller não manteve o fluxo dedicado acima de 1 GiB ou não transmitiu todos os bytes");
        RequireSync((await h.CatalogAsync()).Single().Members.Count == 10 && visible.Count(entry => entry.Type == "file") == 11,
            "o arquivo grande ocupou uma vaga compartilhada ou removeu cards existentes");
        return TestOutcome.Pass();
    });

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr file, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    private static Task<TestOutcome> SharedStorageCardsHiddenBrowserAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-shared-cards-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1280, 800, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? controller = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, directory.CreateSubdirectory("browser").FullName);
            controller = await environment.CreateCoreWebView2ControllerAsync(host); controller.IsVisible = true;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var web = directory.CreateSubdirectory("web"); var assembly = typeof(AppRegistry).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var source = assembly.GetManifestResourceStream(resource)!;
                await using var output = File.Create(path); await source.CopyToAsync(output);
            }
            var view = controller.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult(); view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var entries = Enumerable.Range(0, 2).Select(index => new AppEntry { Type = "file", Name = index + ".jpg", Path = "/", ParentFolderId = "root",
                ThreadId = "shared-thread", FileId = "shared-file-" + index, MetadataMessageId = "meta-" + index,
                VersionId = Guid.NewGuid().ToString("N"), Storage = new StorageLayout { StorageId = Guid.NewGuid().ToString("N") } }).ToArray();
            await view.ExecuteScriptAsync("window.__errors=[];window.addEventListener('error',e=>window.__errors.push(e.message));" +
                "window.__organessoniumSettings={previewEnabled:true,theme:'dark',animationsEnabled:false,language:'pt-BR'};window.onBridgeReady();" +
                "window.__requests=[];window.bridge.requestPreview=(id)=>window.__requests.push(['preview',id]);window.bridge.startDownload=(id)=>window.__requests.push(['download',id]);" +
                "window._onLocalAppsChanged(" + JsonSerializer.Serialize(entries, JsonOptions.Web) + ");" +
                "window._thumbs={'meta-0':'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9r8AAAAASUVORK5CYII='," +
                "'meta-1':'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=='};window._onThumb('meta-0');window._onThumb('meta-1');");
            await Task.Delay(150);
            await view.ExecuteScriptAsync("document.querySelector('[data-key=\"meta-0\"]').dispatchEvent(new MouseEvent('dblclick',{bubbles:true}));closePreview();document.querySelector('[data-key=\"meta-1\"] .btn-more').click();document.getElementById('ctx-download').click();");
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===2&&document.querySelector('[data-thumb=\"meta-0\"] img').src!==document.querySelector('[data-thumb=\"meta-1\"] img').src&&JSON.stringify(window.__requests)===JSON.stringify([['preview','meta-0'],['download','meta-1']])&&window.__errors.length===0") == "true",
                "a UI confundiu cards, miniaturas ou ações de arquivos no mesmo post");
            RequireSync(!IsWindowVisible(host), "o teste mostrou uma janela");
            return TestOutcome.Pass();
        }
        finally { controller?.Close(); DestroyWindow(host); try { directory.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    });
}
