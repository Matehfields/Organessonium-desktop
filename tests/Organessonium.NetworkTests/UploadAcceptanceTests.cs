using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AcceptanceRect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AcceptanceMonitor { internal int Size; internal AcceptanceRect Bounds, Work; internal uint Flags; }
    private delegate bool AcceptanceMonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rectangle, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, AcceptanceMonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetAcceptanceMonitor(IntPtr monitor, ref AcceptanceMonitor info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    private static Task<TestOutcome> UploadAcceptanceSecondaryAsync() => OnHiddenDispatcherAsync(async () =>
    {
        AcceptanceRect secondary = default;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new AcceptanceMonitor { Size = Marshal.SizeOf<AcceptanceMonitor>() };
            if (GetAcceptanceMonitor(monitor, ref info) && (info.Flags & 1) == 0) { secondary = info.Work; return false; }
            return true;
        }, IntPtr.Zero);
        RequireSync(secondary.Right > secondary.Left, "o monitor secundário não está disponível");
        using var h = new SharedHarness();
        h.Settings.PreviewEnabled = true; h.Settings.MaxParallelUploads = 16; h.Settings.UploadThreads = 16;
        var unconfigured = h.Settings.Clone(); unconfigured.BotToken = ""; h.Store.Save(unconfigured);
        var window = new Organessonium.MainWindow(h.Store)
        {
            Title = "Organessônium - Teste isolado de upload", ShowActivated = false,
            WindowState = WindowState.Normal,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = secondary.Left + 20, Top = secondary.Top + 20,
            Width = Math.Min(1280, secondary.Right - secondary.Left - 40), Height = Math.Min(850, secondary.Bottom - secondary.Top - 60)
        };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            window.Show();
            SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, secondary.Left + 20, secondary.Top + 20,
                (int)window.Width, (int)window.Height, 0x0014);
            var view = (WebView2)window.FindName("WebView");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (view.CoreWebView2 == null || await view.ExecuteScriptAsync("location.pathname==='/setup.html'&&typeof setupStep!=='undefined'") != "true") await Task.Delay(50, timeout.Token);
            h.Store.Save(h.Settings);
            var controller = (AppController)typeof(Organessonium.MainWindow).GetField("_controller", flags)!.GetValue(window)!;
            typeof(AppController).GetField("_downloadClientFactory", flags)!.SetValue(controller, (Func<HttpClient>)(() => new HttpClient(h.Server, false)));
            var session = new DiscordSession();
            typeof(DiscordSession).GetProperty("Api")!.SetValue(session, h.Api);
            typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
            controller.SetSession(session);
            typeof(AppController).GetField("_registry", flags)!.SetValue(controller, h.Registry);
            typeof(Organessonium.MainWindow).GetMethod("LoadLocalPage", flags)!.Invoke(window, ["index.html"]);
            while (await view.ExecuteScriptAsync("location.pathname==='/index.html'&&typeof allApps!=='undefined'") != "true") await Task.Delay(50, timeout.Token);
            var paths = new List<string>();
            for (var index = 0; index < 25; index++)
            {
                var pixels = new byte[256 * 192 * 4]; new Random(index).NextBytes(pixels);
                var image = BitmapSource.Create(256, 192, 96, 96, PixelFormats.Bgra32, null, pixels, 256 * 4);
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 }; encoder.Frames.Add(BitmapFrame.Create(image));
                var path = Path.Combine(h.Directory.FullName, "Acceptance-" + index.ToString("D2") + ".jpg");
                using (var output = File.Create(path)) encoder.Save(output);
                paths.Add(path);
            }
            await controller.UploadDroppedPathsAsync(paths).WaitAsync(TimeSpan.FromSeconds(60));
            for (var attempt = 0; attempt < 100 && await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===25") != "true"; attempt++) await Task.Delay(50, timeout.Token);
            var entries = h.Store.LoadCachedEntriesForFolder("root").Where(entry => entry.Type == "file").ToArray();
            var batches = h.Server.FileThreads().SelectMany(h.Server.Messages).Where(message => message.Attachments.Count > 0).ToArray();
            RequireSync(entries.Length == 25 && h.Server.PayloadPosts == 1 && batches.Length == 5 && batches.All(message => message.Attachments.Count == 10),
                "o teste no app perdeu arquivos, criou pools adicionais ou fragmentou anexos");
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===25") == "true", "os uploads não apareceram na janela");
            var screenshot = Path.Combine(Environment.CurrentDirectory, "artifacts", "upload-latency-review", "upload-acceptance.png");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            await using (var capture = File.Create(screenshot))
                await view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, capture);
            await Task.Delay(2000, timeout.Token);
            await ((Task)typeof(AppController).GetMethod("DeleteItemsAsync", flags)!.Invoke(controller,
                [JsonSerializer.Serialize(entries.Select(entry => entry.MetadataMessageId)), true])!).WaitAsync(TimeSpan.FromSeconds(30));
            while (h.Server.FileThreads().SelectMany(h.Server.Messages).Any(message => message.Attachments.Count > 0)) await Task.Delay(50, timeout.Token);
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===0&&!document.querySelector('.op-item[data-op-type=cleanup]')") == "true",
                "os arquivos de teste permaneceram na interface ou a limpeza mostrou progresso");
            Console.WriteLine("UPLOAD_ACCEPTANCE_RESULT isolated=True secondary=True files=25 pools=1 messages=5 attachments=50 cleanup=True");
            return TestOutcome.Pass();
        }
        catch (Exception error)
        {
            Console.WriteLine("UPLOAD_ACCEPTANCE_FAILURE " + error);
            throw;
        }
        finally
        {
            window.Hide();
            // Invoke shutdown outside WPF's closing event, including when disposal completes synchronously.
            typeof(Organessonium.MainWindow).GetMethod("OnClosing", flags | BindingFlags.DeclaredOnly)!.Invoke(window, [window, new System.ComponentModel.CancelEventArgs()]);
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    });
}
