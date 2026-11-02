using System.Reflection;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> SerialRefreshAndSilentReconciliationAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        await h.UploadAsync([await h.PrepareAsync("known.bin", [1, 2, 3])]);
        var unindexed = AppEntry.File("unindexed.bin", "external-payload", TransferState.Now(), 4, "/");
        unindexed.ChunkCount = 1; unindexed.ChunkMessageIds = ["external-message"];
        var metadata = new MetadataService(MetadataCodec.ForStore(h.Store), h.Api.DownloadAttachmentBytesAsync);
        await metadata.SendEntryAsync(h.Api, "root-thread", unindexed);
        using var barrier = new MetadataReadBarrier(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(barrier, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        using var web = new WebView2(); var owner = new System.Windows.Window();
        var readStates = new List<bool>();
        var bridge = new WebMessageBridge(web, script =>
        {
            if (script.Contains("_onServerReadState")) readStates.Add(script.Contains("[true]"));
            return Task.CompletedTask;
        });
        await using var controller = new AppController(owner, h.Store, bridge, _ => Task.CompletedTask);
        var session = new DiscordSession(); typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings); controller.SetSession(session);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AppController).GetField("_registry", flags)!.SetValue(controller, registry);
        await ((Task)typeof(AppController).GetMethod("RefreshServerIndexAsync", flags)!.Invoke(controller, [false])!).WaitAsync(TimeSpan.FromSeconds(10));
        await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RequireSync(readStates.SequenceEqual(new[] { true, false }) && !h.Store.LoadCachedEntriesForFolder("root").Any(entry => entry.Name == unindexed.Name),
            "a leitura visível esperou pelas mensagens ou ignorou o serial válido");
        barrier.Allow.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!h.Store.LoadCachedEntriesForFolder("root").Any(entry => entry.Name == unindexed.Name)) await Task.Delay(25, timeout.Token);
        RequireSync(readStates.SequenceEqual(new[] { true, false }), "a reconciliação silenciosa exibiu progresso de leitura");
        return TestOutcome.Pass();
    });

    private static async Task<TestOutcome> JanitorArchivesOnlyIdleAsync()
    {
        using var h = new SharedHarness();
        var entry = (await h.UploadAsync([await h.PrepareAsync("kept.bin", [4, 5, 6])])).Single();
        await using (await h.Api.AcquireThreadWriteAsync(entry.ThreadId, default))
        {
            await h.Registry.RunJanitorAsync(default);
            RequireSync((await h.Api.GetChannelAsync(entry.ThreadId)).ThreadMetadata?.Archived == false,
                "o janitor arquivou um pool com escrita ativa");
        }
        await h.Api.UnarchiveThreadAsync(entry.ThreadId);
        await h.Registry.RunJanitorAsync(default);
        RequireSync((await h.Api.GetChannelAsync(entry.ThreadId)).ThreadMetadata?.Archived == true &&
            h.Server.Messages(entry.ThreadId).Sum(message => message.Attachments.Count) == 1 &&
            (await h.Registry.LoadCachedFolderAsync("root", "/")).Any(candidate => candidate.FileId == entry.FileId),
            "o janitor deixou uma thread inativa aberta ou removeu conteúdo válido");
        return TestOutcome.Pass();
    }

    private sealed class MetadataReadBarrier(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Allow = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/channels/root-thread/messages"))
            {
                Started.TrySetResult(); await Allow.Task.WaitAsync(token);
            }
            return await _forward.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }
}
