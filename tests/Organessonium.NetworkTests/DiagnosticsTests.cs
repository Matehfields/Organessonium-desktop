using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Organessonium.Services;
using Organessonium.Services.Diagnostics;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static readonly DiagnosticOptions FastDiagnostics = new()
    { SampleMilliseconds = 100, UiStallMilliseconds = 350, BrowserStallMilliseconds = 1000,
        BrowserHeartbeatMilliseconds = 100, FileBytes = 32768, FilesPerSession = 2 };

    private static string ReadDiagnostics(DirectoryInfo directory) => string.Join('\n',
        directory.EnumerateFiles("trace-*.jsonl", SearchOption.AllDirectories).Select(file =>
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));

    private static async Task<TestOutcome> DiagnosticLoggingAndBoundsAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium-diagnostics-");
        try
        {
            using (var session = DevelopmentDiagnostics.Start([], root, FastDiagnostics))
            {
                RequireSync(session != null, "o coletor não iniciou");
                for (var index = 0; index < 160; index++)
                    AppLogger.Error("Area.PreviouslyFiltered", new IOException("PRIVATE-SECRET-PATH-TOKEN-" + index));
                RequireSync(session!.Flush(TimeSpan.FromSeconds(2)), "o coletor não drenou os erros");
                using var server = new SyncDiscordFixture();
                using var api = new DiscordApiClient("PRIVATE-BOT-TOKEN", new HttpClient(server, false));
                await api.GetMessagesAsync("root-thread");
                using (DevelopmentDiagnostics.Begin("bridge", "SecretPersonalName.pdf"))
                    AppLogger.Error("PrivateFilename.zip", new InvalidOperationException("PRIVATE-METHOD-ARGS"));
                try { throw new InvalidOperationException("PRIVATE-CAUGHT-METADATA"); }
                catch (InvalidOperationException exception) { AppLogger.Error("Area.Caught", exception); }
                await Task.Delay(200);
                RequireSync(session.Flush(TimeSpan.FromSeconds(2)), "o coletor não persistiu a sessão");
                var text = ReadDiagnostics(root);
                RequireSync(text.Contains("log.error") && text.Contains("System.IO.IOException") && text.Contains("System.InvalidOperationException") &&
                    text.Contains("operation.end") && text.Contains("statusCode") && text.Contains("runtime.sample") &&
                    !text.Contains("PRIVATE-") && !text.Contains("root-thread") && !text.Contains("SecretPersonalName") &&
                    !text.Contains("PrivateFilename"), "eventos ausentes ou informações privadas expostas");
                RequireSync(session.Directory.EnumerateFiles("trace-*.jsonl").Count() <= FastDiagnostics.FilesPerSession,
                    "a rotação excedeu a quantidade de arquivos");
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    using (JsonDocument.Parse(line)) { }
                session.Complete(0);
            }
            RequireSync(!root.EnumerateFiles("active.json", SearchOption.AllDirectories).Any(), "saída normal deixou marcador de interrupção");
            var block = new FileInfo(Path.Combine(root.FullName, "not-a-directory"));
            await File.WriteAllTextAsync(block.FullName, "fixture");
            using var unavailable = DevelopmentDiagnostics.Start([], new DirectoryInfo(block.FullName));
            await Task.Delay(200);
            RequireSync(unavailable?.Healthy != true, "falha de disco não desativou o coletor");
            return TestOutcome.Pass();
        }
        finally { root.Delete(true); }
    }

    private static Task<TestOutcome> DiagnosticHiddenBrowserAndStallAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var root = Directory.CreateTempSubdirectory("organessonium-diagnostic-ui-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? controller = null;
        DiagnosticWebView? observer = null;
        using var session = DevelopmentDiagnostics.Start([], root.CreateSubdirectory("logs"), FastDiagnostics);
        try
        {
            RequireSync(session != null, "o coletor não iniciou");
            DevelopmentDiagnostics.AttachDispatcher(Dispatcher.CurrentDispatcher);
            var environment = await CoreWebView2Environment.CreateAsync(null, root.CreateSubdirectory("browser").FullName);
            controller = await environment.CreateCoreWebView2ControllerAsync(host);
            controller.IsVisible = true;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var directory = root.CreateSubdirectory("web");
            var assembly = typeof(AppRegistry).Assembly;
            foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(directory.FullName, name[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(name)!;
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            var view = controller.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", directory.FullName, CoreWebView2HostResourceAccessKind.Allow);
            observer = await DiagnosticWebView.AttachAsync(view);
            RequireSync(observer != null, "o observador do browser não iniciou");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, _) => loaded.TrySetResult();
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await view.ExecuteScriptAsync("window.__organessoniumSettings={language:'pt-BR',theme:'dark',animationsEnabled:false};window.onBridgeReady();window._onDiagnosticStatus(" +
                JsonSerializer.Serialize(DevelopmentDiagnostics.Status()) + ");document.getElementById('sidebar-settings').click()");
            RequireSync(await view.ExecuteScriptAsync("document.getElementById('diagnostic-status').textContent.includes('Ativo')&&document.getElementById('diagnostic-directory').textContent.length>0&&document.getElementById('development-diagnostics').getBoundingClientRect().width>0") == "true",
                "Configurações não mostrou o estado e a pasta do coletor");
            await view.ExecuteScriptAsync("setTimeout(()=>{throw new TypeError('PRIVATE-JAVASCRIPT-TOKEN')},10);setTimeout(()=>Promise.reject(new Error('PRIVATE-PROMISE-TOKEN')),20)");
            await Task.Delay(300);
            Thread.Sleep(800); // Real dispatcher stall in an invisible, isolated host.
            await Task.Delay(300);
            RequireSync(session!.Flush(TimeSpan.FromSeconds(2)), "o coletor não persistiu o diagnóstico da interface");
            var text = ReadDiagnostics(root);
            RequireSync(text.Contains("javascript.error") && text.Contains("javascript.rejection") &&
                text.Contains("ui.stalled") && text.Contains("ui.recovered") && !text.Contains("PRIVATE-"),
                "erros JavaScript ou a pausa real do dispatcher não foram registrados de forma segura");
            await view.ExecuteScriptAsync("window._onDiagnosticStatus({enabled:false,directory:''})");
            RequireSync(await view.ExecuteScriptAsync("document.getElementById('diagnostic-status').textContent.includes('Desativado')&&document.getElementById('diagnostic-directory').textContent===''") == "true",
                "o estado desativado não apareceu na interface");
            RequireSync(!IsWindowVisible(host), "o teste abriu uma janela");
            session.Complete(0);
            return TestOutcome.Pass();
        }
        finally
        {
            observer?.Dispose(); controller?.Close(); DestroyWindow(host); session?.Dispose();
            try { root.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    });

    private static async Task<TestOutcome> DiagnosticAbruptExitAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium-diagnostic-exit-");
        Process? process = null;
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executável de teste indisponível.");
            var launch = new ProcessStartInfo(executable)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                launch.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            launch.ArgumentList.Add("--diagnostic-probe"); launch.ArgumentList.Add(root.FullName);
            process = Process.Start(launch)!;
            var ready = Path.Combine(root.FullName, "ready");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(ready) && !process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
            RequireSync(File.Exists(ready), "o processo isolado não iniciou o coletor antes do WPF" +
                (process.HasExited ? ": " + await process.StandardError.ReadToEndAsync() : ""));
            process.Kill(); // Only the child created above, never the user's app.
            await process.WaitForExitAsync();
            RequireSync(root.EnumerateFiles("active.json", SearchOption.AllDirectories).Any(), "a interrupção não preservou evidências");
            var text = ReadDiagnostics(root);
            RequireSync(text.IndexOf("session.started", StringComparison.Ordinal) < text.IndexOf("probe.appConstructed", StringComparison.Ordinal),
                "o diagnóstico começou depois da construção do App");
            using var next = DevelopmentDiagnostics.Start([], root, FastDiagnostics);
            RequireSync(next != null, "o coletor não reiniciou");
            deadline.Restart();
            var recovered = false;
            while (!recovered && deadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                await Task.Delay(50);
                next!.Flush(TimeSpan.FromSeconds(1));
                recovered = ReadDiagnostics(root).Contains("previous.uncleanExit");
            }
            RequireSync(recovered, "o reinício não identificou a sessão interrompida");
            next!.Complete(0);
            return TestOutcome.Pass();
        }
        finally
        {
            if (process is { HasExited: false }) { process.Kill(); await process.WaitForExitAsync(); }
            process?.Dispose(); root.Delete(true);
        }
    }

    private static async Task<int> RunDiagnosticProbeAsync(string path)
    {
        await OnHiddenDispatcherAsync(async () =>
        {
            using var session = DevelopmentDiagnostics.Start([], new DirectoryInfo(path), FastDiagnostics);
            var application = new Organessonium.App();
            application.InitializeComponent(); // No Run(), startup, window, settings or Discord connection.
            session!.Record("probe.appConstructed", "test");
            session.Flush(TimeSpan.FromSeconds(2));
            await File.WriteAllTextAsync(Path.Combine(path, "ready"), "ready");
            await Task.Delay(Timeout.Infinite);
            return TestOutcome.Pass();
        });
        return 0;
    }
}
