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

    private sealed class AcceptanceDragData(bool files) : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        public int QueryGetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC format) => files && format.cfFormat == 15 ? 0 : unchecked((int)0x80040064);
        public void GetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, out System.Runtime.InteropServices.ComTypes.STGMEDIUM medium) => throw new COMException("Unsupported format", unchecked((int)0x80040064));
        public void GetDataHere(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, out System.Runtime.InteropServices.ComTypes.FORMATETC canonical) { canonical = default; return 1; }
        public void SetData(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, ref System.Runtime.InteropServices.ComTypes.STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public System.Runtime.InteropServices.ComTypes.IEnumFORMATETC EnumFormatEtc(System.Runtime.InteropServices.ComTypes.DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref System.Runtime.InteropServices.ComTypes.FORMATETC format, System.Runtime.InteropServices.ComTypes.ADVF flags, System.Runtime.InteropServices.ComTypes.IAdviseSink sink, out int connection) { connection = 0; return 1; }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out System.Runtime.InteropServices.ComTypes.IEnumSTATDATA enumerator) { enumerator = null!; return 1; }
    }

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
        h.Store.SaveLibraryStatistics(new LibraryStatistics(0, 0, 0));
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
            var targets = (System.Collections.IDictionary)typeof(Organessonium.MainWindow).GetField("_oleDropTargets", flags)!.GetValue(window)!;
            var target = targets.Values.Cast<object>().First();
            var enter = target.GetType().GetMethod("DragEnter")!;
            object[] DragData(bool files) => [new AcceptanceDragData(files), 0, Activator.CreateInstance(enter.GetParameters()[2].ParameterType)!, 0];
            var textDrag = DragData(false); enter.Invoke(target, textDrag);
            await Task.Delay(250, timeout.Token);
            RequireSync((int)textDrag[3] == 0 && await view.ExecuteScriptAsync("dragOverlay.getAttribute('aria-hidden')==='true'") == "true", "arrastar texto abriu o indicador de upload");
            var fileDrag = DragData(true); enter.Invoke(target, fileDrag);
            await Task.Delay(250, timeout.Token);
            RequireSync((int)fileDrag[3] == 1 && await view.ExecuteScriptAsync("dragOverlay.getAttribute('aria-hidden')==='false'&&Number(getComputedStyle(dragOverlay).opacity)>.95") == "true", "arrastar arquivos não mostrou a transição do indicador");
            target.GetType().GetMethod("DragLeave")!.Invoke(target, []);
            await Task.Delay(250, timeout.Token);
            RequireSync(await view.ExecuteScriptAsync("getComputedStyle(dragOverlay).visibility==='hidden'") == "true", "o indicador não encerrou ao sair da janela");
            var paths = new List<string>();
            var sourceFolder = h.Directory.CreateSubdirectory("Acceptance");
            for (var index = 0; index < 25; index++)
            {
                var pixels = new byte[256 * 192 * 4]; new Random(index).NextBytes(pixels);
                var image = BitmapSource.Create(256, 192, 96, 96, PixelFormats.Bgra32, null, pixels, 256 * 4);
                var encoder = new JpegBitmapEncoder { QualityLevel = 85 }; encoder.Frames.Add(BitmapFrame.Create(image));
                var path = Path.Combine(sourceFolder.FullName, "Acceptance-" + index.ToString("D2") + ".jpg");
                using (var output = File.Create(path)) encoder.Save(output);
                paths.Add(path);
            }
            var upload = (Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, [new[] { sourceFolder.FullName }])!;
            AppEntry? folder = null;
            while ((folder = h.Store.LoadCachedEntriesForFolder("root").FirstOrDefault(entry => entry.Name == sourceFolder.Name)) == null)
                await Task.Delay(25, timeout.Token);
            await view.ExecuteScriptAsync("showTrashWorkspace()");
            while (await view.ExecuteScriptAsync("trashLoaded&&folderLoadingPath===''") != "true") await Task.Delay(25, timeout.Token);
            await view.ExecuteScriptAsync("showFilesWorkspace()");
            while (await view.ExecuteScriptAsync("folderLoadingPath===''") != "true") await Task.Delay(25, timeout.Token);
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.folder-card').length===1") == "true", "a pasta em upload desapareceu ao voltar para Arquivos");
            await view.ExecuteScriptAsync("showTrashWorkspace()");
            RequireSync(await view.ExecuteScriptAsync("folderLoadingPath===''&&document.querySelectorAll('.file-card').length===0") == "true", "a Lixeira vazia voltou ao carregamento");
            await view.ExecuteScriptAsync("navigate('/Acceptance',true," + JsonSerializer.Serialize(folder.FolderId) + ")");
            var intermediateStatistics = false;
            while (!upload.IsCompleted)
            {
                if (await view.ExecuteScriptAsync("libraryFileCount>0&&libraryFileCount<25&&libraryTotalBytes>0") == "true") intermediateStatistics = true;
                await Task.Delay(25, timeout.Token);
            }
            await upload.WaitAsync(TimeSpan.FromSeconds(60));
            for (var attempt = 0; attempt < 100 && await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===25") != "true"; attempt++) await Task.Delay(50, timeout.Token);
            var entries = h.Store.LoadCachedEntriesForFolder(folder.FolderId).Where(entry => entry.Type == "file").ToArray();
            var batches = h.Server.FileThreads().SelectMany(h.Server.Messages).Where(message => message.Attachments.Count > 0).ToArray();
            RequireSync(entries.Length == 25 && h.Server.PayloadPosts == 1 && batches.Length == 5 && batches.All(message => message.Attachments.Count == 10),
                "o teste no app perdeu arquivos, criou pools adicionais ou fragmentou anexos");
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===25") == "true", "os uploads não apareceram na janela");
            var initialTotal = paths.Sum(path => new FileInfo(path).Length);
            RequireSync(intermediateStatistics && await view.ExecuteScriptAsync("libraryFileCount===25&&libraryTotalBytes===" + initialTotal) == "true",
                "o espaço utilizado não avançou durante o upload ou o total final divergiu");
            var screenshot = Path.Combine(Environment.CurrentDirectory, "artifacts", "upload-ui-review", "upload-acceptance.png");
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            await using (var capture = File.Create(screenshot))
                await view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, capture);
            await Task.Delay(2000, timeout.Token);
            await using (var append = new FileStream(paths[0], FileMode.Append, FileAccess.Write)) await append.WriteAsync(new byte[1024], timeout.Token);
            var overwrite = (Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, [new[] { paths[0] }])!;
            while (await view.ExecuteScriptAsync("!document.getElementById('upload-conflict-overlay').classList.contains('hidden')") != "true") await Task.Delay(25, timeout.Token);
            await view.ExecuteScriptAsync("document.getElementById('upload-conflict-overwrite').click()");
            await overwrite.WaitAsync(TimeSpan.FromSeconds(30));
            RequireSync(await view.ExecuteScriptAsync("libraryFileCount===25&&libraryTotalBytes===" + (initialTotal + new FileInfo(paths[0]).Length)) == "true",
                "uma nova versão duplicou a contagem de arquivos ou perdeu o espaço do histórico");
            await ((Task)typeof(AppController).GetMethod("DeleteItemsAsync", flags)!.Invoke(controller,
                [JsonSerializer.Serialize(new[] { folder.MetadataMessageId }), true])!).WaitAsync(TimeSpan.FromSeconds(30));
            while (h.Server.FileThreads().SelectMany(h.Server.Messages).Any(message => message.Attachments.Count > 0)) await Task.Delay(50, timeout.Token);
            RequireSync(await view.ExecuteScriptAsync("document.querySelectorAll('.cards-grid .file-card').length===0&&!document.querySelector('.op-item[data-op-type=cleanup]')") == "true",
                "os arquivos de teste permaneceram na interface ou a limpeza mostrou progresso");
            Console.WriteLine("UPLOAD_ACCEPTANCE_RESULT isolated=True secondary=True files=25 pools=1 fullMessages=5 originalsAndThumbnails=50 liveStatistics=True versionTotals=True trashCache=True folderRetained=True fileOnlyDrag=True cleanup=True");
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
