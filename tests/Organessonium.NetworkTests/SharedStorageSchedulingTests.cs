using System.Reflection;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> MappingRefreshPreservesConcurrentCreationAsync()
    {
        using var h = new SharedHarness();
        await h.Registry.RefreshMappingsAsync();
        await h.Registry.AddEntryAsync(AppEntry.Folder("Antes", "/"));
        using var gate = new NamesSnapshotGate(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(gate, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        await registry.RefreshMappingsAsync();
        gate.HoldNext = 1;
        var refresh = registry.RefreshMappingsAsync();
        var folder = AppEntry.Folder("Durante", "/");
        string newRootHash;
        try
        {
            await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await registry.AddEntryAsync(folder).WaitAsync(TimeSpan.FromSeconds(10));
            newRootHash = h.Store.LoadFolderIndexMappings()["root"].SerialSha256;
        }
        finally { gate.Release.TrySetResult(); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        var mappings = h.Store.LoadFolderIndexMappings();
        var entries = await registry.LoadFolderSerialAsync("root", "/");
        RequireSync(mappings.ContainsKey(folder.FolderId) && mappings["root"].SerialSha256 == newRootHash &&
            entries.Any(entry => entry.FolderId == folder.FolderId),
            "uma leitura anterior removeu a pasta nova ou substituiu seu serial confirmado");
        await api.DeleteMessageAsync("names", mappings[folder.FolderId].NamesMessageId);
        await registry.RefreshMappingsAsync();
        RequireSync(!h.Store.LoadFolderIndexMappings().ContainsKey(folder.FolderId),
            "a proteção de concorrência impediu reconhecer uma remoção remota posterior");
        return TestOutcome.Pass();
    }

    private sealed class NamesSnapshotGate(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        internal int HoldNext;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await _forward.SendAsync(request, token);
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/channels/names/messages", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref HoldNext, 0) == 1)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }

    private static async Task<TestOutcome> SharedSingleUploadStartsImmediatelyAsync()
    {
        using var h = new SharedHarness();
        var state = await h.PrepareAsync("single.bin", [1, 2, 3]);
        var reservation = await h.Storage.ReserveAsync(state.FileId, state.TransferId, default);
        using var memory = new UploadMemoryCoordinator(1);
        var context = new EncryptedPayloadContext(state.FileId, state.Encryption!, EncryptedPayloadRoles.Original, 0, 1);
        using var part = await memory.PreparePartAsync(h.Encryption, new FileInfo(state.SourcePath), 3, 3, context, default, deferBuffers: true);
        using var expectation = h.Storage.Batcher.ExpectProduction(reservation, EncryptedPayloadRoles.Original, default);
        using var diagnostics = Organessonium.Services.Diagnostics.DevelopmentDiagnostics.Start([], h.Directory.CreateSubdirectory("dispatch-diagnostics"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var sent = await h.Storage.Batcher.SendAsync(reservation, context, part, null, default).WaitAsync(TimeSpan.FromSeconds(3));
        var totalMilliseconds = watch.ElapsedMilliseconds;
        RequireSync(diagnostics != null && diagnostics.Flush(TimeSpan.FromSeconds(3)), "o diagnóstico de despacho não foi gravado");
        diagnostics!.Dispose();
        double? dispatchWait = null;
        foreach (var line in diagnostics!.Directory.EnumerateFiles("trace-*.jsonl").SelectMany(file => File.ReadLines(file.FullName)))
        {
            using var json = System.Text.Json.JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("kind").GetString() == "upload.batch")
                dispatchWait = json.RootElement.GetProperty("data").GetProperty("waitMilliseconds").GetDouble();
        }
        var message = h.Server.Messages(reservation.ThreadId).Single(message => message.Id == sent.MessageId);
        var restored = h.Encryption.DecryptBytes(await h.Api.DownloadAttachmentBytesAsync(message.Attachments.Single()), context);
        RequireSync(dispatchWait is >= 0 and < 125 && restored.SequenceEqual(new byte[] { 1, 2, 3 }),
            "o arquivo individual esperou a janela de agrupamento ou perdeu conteúdo: " + dispatchWait);
        Console.WriteLine("SINGLE_UPLOAD_RESULT dispatchWaitMs=" + dispatchWait + " confirmedMilliseconds=" + totalMilliseconds);
        return TestOutcome.Pass();
    }

    private static Task<TestOutcome> SharedStoragePublicationPipelineAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        h.Settings.MaxParallelUploads = 1; h.Store.Save(h.Settings);
        using var transport = new ObservedSmallUploadHandler(h.Server) { IndexThreadId = "root-thread", HoldMetadata = true, HoldAllMetadata = true };
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
            // Publication occupies the 16 file slots; only a full message must finish before it is released.
            while (h.Server.FileThreads().SelectMany(h.Server.Messages).Sum(message => message.Attachments.Count) < 10)
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
