using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> CheckTransferCloseConfirmationAsync(SharedHarness h, AppController controller,
        WebView2 view, System.Windows.Window window, Task closed)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bodies = (SemaphoreSlim)typeof(SharedPayloadBatcher).GetField("NetworkBodies", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var held = 0;
        Task? upload = null;
        try
        {
            for (; held < 16; held++) await bodies.WaitAsync(timeout.Token);
            var path = Path.Combine(h.Directory.FullName, "close-confirmation.bin");
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }, timeout.Token);
            upload = (Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, [new[] { path }])!;
            while (await view.ExecuteScriptAsync("document.querySelector('.op-file-row[data-file-status=active]')!==null") != "true")
                await Task.Delay(25, timeout.Token);
            window.Close();
            while (await view.ExecuteScriptAsync("!confirmOverlay.classList.contains('hidden')&&document.getElementById('confirm-message').textContent.includes('interrompe')") != "true")
                await Task.Delay(25, timeout.Token);
            RequireSync(window.IsVisible && !closed.IsCompleted, "a janela fechou antes de confirmar a interrupção");
            await view.ExecuteScriptAsync("document.getElementById('confirm-ok').click()");
            await closed.WaitAsync(TimeSpan.FromSeconds(15));
            RequireSync(!window.IsVisible && !controller.HasTransfersInProgress, "confirmar o fechamento manteve a janela ou a sessão ativa");
        }
        finally { if (held > 0) bodies.Release(held); }
        if (upload != null)
            try { await upload.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        Console.WriteLine("CLOSE_CONFIRMATION_RESULT activeUpload=True visibleDialog=True approved=True windowClosed=True");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> CheckVideoUploadIsolationAsync(SharedHarness h, AppController controller,
        WebView2 view, System.Windows.Window window)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var runtime = await EmbeddedMediaRuntime.GetExecutableAsync(timeout.Token);
        var source = Path.Combine(h.Directory.FullName, "video-00.mp4");
        var info = new ProcessStartInfo(runtime) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=640x360:r=10",
            "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "1", source }) info.ArgumentList.Add(arg);
        using (var process = Process.Start(info)!)
        {
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token); await errors;
            RequireSync(process.ExitCode == 0, "a fixture de vídeo não pôde ser criada");
        }
        var paths = new List<string> { source };
        for (var index = 1; index < 16; index++)
        {
            var path = Path.Combine(h.Directory.FullName, $"video-{index:D2}.mp4");
            File.Copy(source, path); paths.Add(path);
        }
        for (var index = 16; index < 257; index++)
        {
            var path = Path.Combine(h.Directory.FullName, index == 256 ? "after-pool.bin" : $"file-{index:D3}.bin");
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 }, timeout.Token); paths.Add(path);
        }
        // Hold real 480p admission. Originals and real FFmpeg thumbnails must continue.
        var codec = await FfmpegMediaService.EnterAsync(timeout.Token);
        Task? upload = null;
        try
        {
            upload = (Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, [paths.ToArray()])!;
            while (!h.Store.LoadCachedEntriesForFolder("root").Any(entry => entry.Name == "after-pool.bin"))
            {
                if (upload.IsCompleted) await upload;
                await Task.Delay(50, timeout.Token);
            }
            while (await view.ExecuteScriptAsync("[...document.querySelectorAll('.op-file-row')].some(row=>row.textContent.includes('after-pool.bin')&&row.dataset.fileStatus==='done')") != "true")
                await Task.Delay(25, timeout.Token);
            Console.WriteLine("VIDEO_ISOLATION_UI " + await view.ExecuteScriptAsync("JSON.stringify({processing:document.querySelectorAll('.op-file-row[data-file-status=processing]').length,active:document.querySelectorAll('.op-file-row[data-file-status=active]').length,marker:[...document.querySelectorAll('.op-file-row')].find(row=>row.textContent.includes('after-pool.bin'))?.dataset.fileStatus})"));
            RequireSync(h.Server.PayloadPosts == 2 && await view.ExecuteScriptAsync(
                "document.querySelectorAll('.op-file-row[data-file-status=processing]').length>0&&" +
                "document.querySelectorAll('.op-file-row[data-file-status=active]').length<=16&&" +
                "[...document.querySelectorAll('.op-file-row')].some(row=>row.textContent.includes('after-pool.bin')&&row.dataset.fileStatus==='done')") == "true",
                "a conversão bloqueou outro arquivo, a troca de pool ou excedeu 16 uploads ativos");
            // The native close button must leave the window and upload intact when rejected.
            window.Close();
            while (await view.ExecuteScriptAsync("!confirmOverlay.classList.contains('hidden')&&document.getElementById('confirm-title').textContent.includes('Fechar')") != "true")
                await Task.Delay(25, timeout.Token);
            await view.ExecuteScriptAsync("document.getElementById('confirm-cancel').click()");
            RequireSync(window.IsVisible && !upload.IsCompleted && controller.HasTransfersInProgress,
                "recusar o fechamento interrompeu as transferências ou fechou a janela");
            await Task.Yield();
            var exit = ((Organessonium.MainWindow)window).ConfirmExitDesktopAsync();
            while (await view.ExecuteScriptAsync("!confirmOverlay.classList.contains('hidden')") != "true") await Task.Delay(25, timeout.Token);
            await view.ExecuteScriptAsync("document.getElementById('confirm-cancel').click()");
            RequireSync(!await exit && window.IsVisible && !upload.IsCompleted,
                "recusar Sair permitiu interromper a sessão");
            Console.WriteLine("VIDEO_ISOLATION originalsAcrossPools=True previewsHeld=True activeUploads<=16 closeRejected=True");
        }
        finally { codec.Dispose(); }
        if (upload != null) await upload.WaitAsync(TimeSpan.FromSeconds(60));
        var entries = h.Store.LoadCachedEntriesForFolder("root").Where(entry => entry.Type == "file").ToArray();
        RequireSync(entries.Length == 257 && entries.Where(entry => entry.Name.EndsWith(".mp4", StringComparison.Ordinal))
            .All(entry => entry.VideoPreviewChunkReferences is { Count: > 0 }),
            "os arquivos ou suas prévias 480p não foram publicados ao liberar o codec");
        await ((Task)typeof(AppController).GetMethod("DeleteItemsAsync", flags)!.Invoke(controller,
            [JsonSerializer.Serialize(entries.Select(entry => entry.MetadataMessageId)), true])!).WaitAsync(TimeSpan.FromSeconds(60));
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var lastRetry = Environment.TickCount64;
        while (h.Server.FileThreads().SelectMany(h.Server.Messages).Any(message => message.Attachments.Count > 0))
        {
            if (Environment.TickCount64 - lastRetry >= 5000)
            {
                h.Storage.RequestCleanup(); lastRetry = Environment.TickCount64;
                Console.WriteLine("VIDEO_CLEANUP pendingAttachments=" + h.Server.FileThreads().SelectMany(h.Server.Messages).Sum(message => message.Attachments.Count));
            }
            await Task.Delay(50, cleanupTimeout.Token);
        }
        RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.file-card').length===0") == "true",
            "os arquivos de teste permaneceram na interface");
        Console.WriteLine("VIDEO_ISOLATION_RESULT files=257 videos=16 pools=2 previewsPublished=True cleanup=True");
        return TestOutcome.Pass();
    }
}
