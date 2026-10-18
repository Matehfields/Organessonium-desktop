using System.Reflection;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> TrayKeepsActiveTransfersHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        h.Settings.CloseToTray = true; h.Store.Save(h.Settings);
        var oldState = await h.PrepareAsync("download.bin", "download-before-close"u8.ToArray());
        var existing = (await h.UploadAsync([oldState])).Single();
        using var handler = new TrayTransferBarrier(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(handler, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        var app = new App(h.Store, Path.Combine(h.Directory.FullName, "identity.txt"), true);
        var window = new MainWindow(h.Store); // Never shown, no ContentRendered or startup connection.
        var web = (WebView2)window.FindName("WebView");
        var controller = new AppController(window, h.Store, new WebMessageBridge(web), _ => Task.CompletedTask);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetField("_controller", flags)!.SetValue(window, controller);
        var session = new DiscordSession();
        typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
        controller.SetSession(session);
        typeof(AppController).GetField("_registry", flags)!.SetValue(controller, registry);
        typeof(AppController).GetField("_loadedApps", flags)!.SetValue(controller, new List<AppEntry> { existing });
        var uploadFile = new FileInfo(Path.Combine(h.Directory.FullName, "upload.bin"));
        await File.WriteAllBytesAsync(uploadFile.FullName, new byte[8192]);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            var upload = (Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, new object[] { new[] { uploadFile.FullName } })!;
            var downloader = new ForumDownloader(api, h.Settings, h.Encryption, () => new HttpClient(handler, false));
            var download = downloader.DownloadAndAssembleAsync(existing, h.Directory.CreateSubdirectory("destination"), _ => { });
            await Task.WhenAll(handler.UploadStarted.Task, handler.DownloadStarted.Task).WaitAsync(TimeSpan.FromSeconds(20));
            window.Close();
            RequireSync(!window.IsVisible && !closed.Task.IsCompleted &&
                !(bool)typeof(MainWindow).GetField("_shutdownInProgress", flags)!.GetValue(window)! &&
                ReferenceEquals(typeof(MainWindow).GetField("_controller", flags)!.GetValue(window), controller),
                "fechar para a bandeja descartou a sessão da interface");
            handler.Continue.TrySetResult();
            await upload.WaitAsync(TimeSpan.FromSeconds(30));
            var downloaded = await download.WaitAsync(TimeSpan.FromSeconds(30));
            RequireSync((await File.ReadAllBytesAsync(downloaded.FullName)).SequenceEqual("download-before-close"u8.ToArray()) &&
                (await registry.RefreshFolderAsync("root", "/")).Any(entry => entry.Name == "upload.bin"),
                "o upload ou download não concluiu depois de fechar para a bandeja");
            typeof(MainWindow).GetField("_isPreviewVisible", flags)!.SetValue(window, true);
            window.Close();
            RequireSync(!closed.Task.IsCompleted && !(bool)typeof(MainWindow).GetField("_isPreviewVisible", flags)!.GetValue(window)!,
                "fechar durante uma prévia encerrou a janela");
            return TestOutcome.Pass();
        }
        finally
        {
            handler.Continue.TrySetResult();
            typeof(App).GetField("_exiting", flags)!.SetValue(app, true);
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            app.Shutdown();
        }
    });

    private sealed class TrayTransferBarrier(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _next = new(inner, false);
        internal TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DownloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/attachments/", StringComparison.Ordinal))
            { DownloadStarted.TrySetResult(); await Continue.Task.WaitAsync(token); }
            if (request.Method == HttpMethod.Post && request.Content is MultipartFormDataContent multipart &&
                multipart.Any(part => (part.Headers.ContentDisposition?.FileNameStar ?? part.Headers.ContentDisposition?.FileName?.Trim('"')) is string name && name.EndsWith(".bin") && name != "serial.bin"))
            { UploadStarted.TrySetResult(); await Continue.Task.WaitAsync(token); }
            return await _next.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _next.Dispose(); base.Dispose(disposing); }
    }
}
