using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> TransferPauseAndResumeAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        using var blocked = new BlockFirstPayload(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(blocked, false));
        var registry = new AppRegistry(api, h.Store, h.Settings); await registry.RefreshMappingsAsync();
        blocked.CountJournal = (await registry.GetStorageJournalIndexAsync(default)).ThreadId;
        var readsBeforeResume = 0; var resumeJournalReads = -1;
        var uploadPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downloadPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var memoryPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var web = new WebView2();
        var owner = new System.Windows.Window();
        var bridge = new WebMessageBridge(web, script =>
        {
            if (script.Contains("_onAppsBatch") && script.Contains("pause-0.bin"))
                resumeJournalReads = blocked.JournalReads - readsBeforeResume;
            if (script.Contains("\"paused\""))
            {
                if (script.Contains("_opsUploadFileProgress")) uploadPaused.TrySetResult();
                if (script.Contains("_opsUpdate")) downloadPaused.TrySetResult();
                if (script.Contains("1 GB")) memoryPaused.TrySetResult();
            }
            return Task.CompletedTask;
        });
        await using var controller = new AppController(owner, h.Store, bridge, _ => Task.CompletedTask);
        var session = new DiscordSession();
        typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
        controller.SetSession(session);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AppController).GetField("_registry", flags)!.SetValue(controller, registry);
        Task Toggle(string op, int index) => controller.HandleJsonMessageAsync(JsonSerializer.Serialize(new
            { method = "toggleTransferPause", args = new object[] { op, index } }));
        var paths = Enumerable.Range(0, 4).Select(index => Path.Combine(h.Directory.FullName, $"pause-{index}.bin")).ToArray();
        foreach (var path in paths) await File.WriteAllBytesAsync(path, new byte[4096]);
        var upload = (Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, [paths])!;
        await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var operations = (IDictionary)typeof(AppController).GetField("_uploadFileOperations", flags)!.GetValue(controller)!;
        var op = operations.Keys.Cast<string>().Single(key => key.EndsWith(":0"))[..^2];
        await Toggle(op, 0); blocked.AllowPayload.TrySetResult();
        await uploadPaused.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        IReadOnlyList<AppEntry> Visible() => (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
        while (Visible().Count(entry => entry.Type == "file") != 3) await Task.Delay(25, timeout.Token);
        var states = await new TransferStateRepository(h.Store.TransfersDirectory).LoadActiveAsync();
        var paused = states.Single(state => state.FileName == "pause-0.bin");
        RequireSync(paused.Status == TransferStatuses.Paused && paused.UploadedChunks.Count == 1 && !upload.IsCompleted,
            "a pausa perdeu a confirmação, terminou cedo ou cancelou o arquivo");
        readsBeforeResume = blocked.JournalReads;
        await Toggle(op, 0); await upload.WaitAsync(TimeSpan.FromSeconds(30));
        RequireSync(Visible().Count(entry => entry.Type == "file") == 4 &&
            h.Server.FileThreads().SelectMany(h.Server.Messages).Sum(message => message.Attachments.Count) == 4 && paths.All(File.Exists),
            "retomar repetiu anexos confirmados, perdeu cards ou interrompeu os outros arquivos");
        RequireSync(resumeJournalReads == 0, "a pausa na sessão voltou a ler o journal para publicar uma identidade nova");

        Console.WriteLine("PAUSE_UPLOAD_VERIFIED confirmed attachments reused; other files completed");
        using var download = new DirectDownloadFixture(hashes: true);
        var checkpoint = await download.Transfers.CreateDownloadAsync(download.Entry, download.Destination);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        download.BeforeRead = async (index, token) =>
        {
            if (index == 0) return;
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var downloadToken = (CancellationToken)typeof(AppController).GetMethod("BeginTransferOperation", flags)!.Invoke(controller, ["pause-download"])!;
        Func<CancellationToken, Task> read = async token => { await download.Run(checkpoint, token); };
        var downloading = (Task)typeof(AppController).GetMethod("RunPausableDownloadAsync", flags)!.Invoke(controller,
            ["pause-download", checkpoint, downloadToken, read])!;
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!checkpoint.Chunks.Any(chunk => chunk.Index == 0 && chunk.Downloaded)) await Task.Delay(25, downloadTimeout.Token);
        Console.WriteLine("PAUSE_DOWNLOAD_PART_RECEIVED");
        await Toggle("pause-download", -1); await downloadPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RequireSync(checkpoint.Status == TransferStatuses.Paused && File.Exists(checkpoint.PartialFilePath),
            "a pausa do download removeu o arquivo parcial");
        var injectedMemoryFailure = 0;
        download.BeforeRead = (index, _) =>
        {
            if (index == 1 && Interlocked.Exchange(ref injectedMemoryFailure, 1) == 0)
                throw new HttpRequestException("buffer admission", new TransferMemoryPressureException());
            return Task.CompletedTask;
        };
        await Toggle("pause-download", -1); await memoryPaused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RequireSync(checkpoint.Status == TransferStatuses.Paused && File.Exists(checkpoint.PartialFilePath),
            "a falta de memória virou retry infinito de rede ou perdeu o checkpoint");
        download.BeforeRead = null;
        await Toggle("pause-download", -1); await downloading.WaitAsync(TimeSpan.FromSeconds(15));
        RequireSync(File.ReadAllBytes(checkpoint.FinalFilePath!).SequenceEqual(download.Expected) && download.Reads[0] == 1,
            "retomar o download alterou os bytes ou baixou novamente a parte confirmada");
        typeof(AppController).GetMethod("EndTransferOperation", flags)!.Invoke(controller, ["pause-download"]);
        RequireSync(!owner.IsVisible, "o teste abriu uma janela");
        return TestOutcome.Pass();
    });
}
