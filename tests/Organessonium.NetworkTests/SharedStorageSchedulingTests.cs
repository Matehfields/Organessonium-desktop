using System.Reflection;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> SharedStoragePublicationPipelineAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        h.Settings.MaxParallelUploads = 1; h.Store.Save(h.Settings);
        using var transport = new ObservedSmallUploadHandler(h.Server) { IndexThreadId = "root-thread", HoldMetadata = true };
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(transport, false));
        var registry = new AppRegistry(api, h.Store, h.Settings); await registry.RefreshMappingsAsync();
        using var web = new WebView2();
        var owner = new System.Windows.Window();
        await using var controller = new AppController(owner, h.Store, new WebMessageBridge(web), _ => Task.CompletedTask);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var session = new DiscordSession();
        typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
        controller.SetSession(session);
        typeof(AppController).GetField("_registry", flags)!.SetValue(controller, registry);
        var paths = new List<string>();
        var multipart = Path.Combine(h.Directory.FullName, "multipart.bin");
        using (var source = File.Create(multipart))
        {
            if (!DeviceIoControl(source.SafeFileHandle.DangerousGetHandle(), 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException("Não foi possível criar a origem esparsa.");
            source.SetLength(DiscordLimits.PlaintextChunkBytes + 1L);
        }
        paths.Add(multipart);
        for (var index = 0; index < 45; index++)
        {
            var path = Path.Combine(h.Directory.FullName, "small-" + index + ".bin");
            await File.WriteAllBytesAsync(path, new byte[1024]); paths.Add(path);
        }
        var upload = (Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, [paths.ToArray()])!;
        string journalThread = "";
        try
        {
            await transport.MetadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            journalThread = h.Store.LoadFolderIndexMappings()["storage-v2"].ThreadId;
            transport.Archives.Clear();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (h.Server.FileThreads().SelectMany(h.Server.Messages).Sum(message => message.Attachments.Count) < 30)
                await Task.Delay(25, timeout.Token);
            await Task.Delay(DiscordLimits.SharedProducerStallMilliseconds + 500, timeout.Token);
            RequireSync(h.Server.FileThreads().SelectMany(h.Server.Messages).Where(message => message.Attachments.Count > 0)
                .All(message => message.Attachments.Count == 10), "a fila cheia fragmentou o próximo lote antes de liberar publicações");
            RequireSync(!upload.IsCompleted, "o upload foi concluído antes de confirmar os metadados");
        }
        finally { transport.AllowMetadata.TrySetResult(); }
        await upload.WaitAsync(TimeSpan.FromSeconds(40));
        var journalChanges = transport.Archives.Where(change => change.Thread == journalThread).ToArray();
        RequireSync(journalChanges.Length == 1 && journalChanges[0].Archived,
            "o journal alternou arquivamento por registro durante o upload em lote");
        var messages = h.Server.FileThreads().SelectMany(h.Server.Messages).Where(message => message.Attachments.Count > 0).ToArray();
        RequireSync(messages.Count(message => message.Attachments.Count == 10) == 4 && messages.Single(message => message.Attachments.Count != 10).Attachments.Count == 7,
            "publicação lenta ou arquivo multipart impediu lotes cheios, exceto o final");
        var visible = (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
        RequireSync(visible.Count(entry => entry.Type == "file") == 46 && visible.All(entry => entry.Type != "file" || entry.MetadataMessageId.Length > 0) && !owner.IsVisible,
            "a separação da publicação perdeu cards, exibiu conclusão antecipada ou abriu janela");
        var limited = Enumerable.Range(0, 2).Select(index => Path.Combine(h.Directory.FullName, "multipart-only-" + index + ".bin")).ToArray();
        foreach (var path in limited)
        {
            using var source = File.Create(path);
            if (!DeviceIoControl(source.SafeFileHandle.DangerousGetHandle(), 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException("Não foi possível criar a origem esparsa.");
            source.SetLength(DiscordLimits.PlaintextChunkBytes + 1L);
        }
        await ((Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, [limited])!).WaitAsync(TimeSpan.FromSeconds(20));
        var afterLimited = (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
        RequireSync(afterLimited.Count(entry => entry.Type == "file") == 48 &&
            h.Server.FileThreads().SelectMany(h.Server.Messages).Sum(message => message.Attachments.Count) == 51,
            "aguardar dez anexos bloqueou os próximos originais sob paralelismo de arquivo igual a um");
        return TestOutcome.Pass();
    });

    private static async Task<TestOutcome> SharedStorageDelayedProducersAsync()
    {
        using var h = new SharedHarness();
        var states = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => h.PrepareAsync("delayed-" + index + ".bin", [1, 2, 3])));
        var reservations = await h.Storage.ReserveManyAsync(states.Select(state => (state.FileId, state.TransferId)).ToArray(), default);
        var expected = states.Select(state => h.Storage.Batcher.ExpectProduction(reservations[state.TransferId], EncryptedPayloadRoles.Preview, default)).ToArray();
        try
        {
            var uploader = new ForumUploader(h.Api, "files", MetadataCodec.ForStore(h.Store));
            using var memory = new UploadMemoryCoordinator(4);
            var results = await Task.WhenAll(states.Select(async (state, index) =>
            {
                var context = new EncryptedPayloadContext(state.FileId, state.Encryption!, EncryptedPayloadRoles.Preview, 0, 1);
                return await uploader.UploadResumableGeneratedAsync("preview", 1, async _ =>
                {
                    // Actual preparation is delayed beyond the former flush window, as with queued media generation.
                    await Task.Delay(index * 100);
                    return await memory.PreparePartAsync(h.Encryption, new FileInfo(state.SourcePath), 3, 3, context,
                        default, deferBuffers: true, isolateSendCancellation: true);
                }, reservations[state.TransferId].ThreadId, "", new Dictionary<int, string>(), encryptedPayload: context,
                    useAttachmentReferences: true, streamPreparedParts: true,
                    sharedUpload: new SharedUploadContext(h.Storage, reservations[state.TransferId]));
            }));
            var thread = results[0].ThreadId;
            RequireSync(results.All(result => result.ChunkReferences!.Single().MessageId == results[0].ChunkReferences!.Single().MessageId) &&
                h.Server.Messages(thread).Single(message => message.Attachments.Count > 0).Attachments.Count == 10,
                "a janela de 250 ms fragmentou produtores já admitidos em mensagens menores");
            using var cancel = new CancellationTokenSource();
            using var abandoned = h.Storage.Batcher.ExpectProduction(reservations[states[0].TransferId], EncryptedPayloadRoles.Original, cancel.Token);
            cancel.Cancel();
            var last = await h.UploadAsync([await h.PrepareAsync("tail.bin", [4, 5])], publish: false).WaitAsync(TimeSpan.FromSeconds(10));
            RequireSync(last.Count == 1, "cancelar um produtor deixou o lote final bloqueado");
            using var stalled = h.Storage.Batcher.ExpectProduction(reservations[states[0].TransferId], EncryptedPayloadRoles.Original, default);
            var drained = await h.UploadAsync([await h.PrepareAsync("drain.bin", [6, 7])], publish: false).WaitAsync(TimeSpan.FromSeconds(10));
            RequireSync(drained.Count == 1, "um produtor bloqueado por recursos impediu a drenagem limitada do lote parcial");
            var closingContext = new EncryptedPayloadContext(states[1].FileId, states[1].Encryption!, EncryptedPayloadRoles.Preview, 0, 1);
            using var closingPart = await memory.PreparePartAsync(h.Encryption, new FileInfo(states[1].SourcePath), 3, 3,
                closingContext, default, deferBuffers: true, isolateSendCancellation: true);
            var closing = h.Storage.Batcher.SendAsync(reservations[states[1].TransferId], closingContext, closingPart, null, default);
            await Task.Delay(300);
            h.Api.Dispose();
            try { await closing.WaitAsync(TimeSpan.FromSeconds(2)); return TestOutcome.Fail("a sessão encerrada confirmou um lote ainda incompleto"); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
            return TestOutcome.Pass();
        }
        finally { foreach (var expectation in expected) expectation.Dispose(); }
    }
}
