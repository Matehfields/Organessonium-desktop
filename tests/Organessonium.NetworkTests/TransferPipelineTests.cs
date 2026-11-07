using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static AppController PipelineController(SharedHarness h, DiscordApiClient api, AppRegistry registry, Action<string>? script = null)
    {
        var web = new WebView2();
        var controller = new AppController(new System.Windows.Window(), h.Store,
            new WebMessageBridge(web, value => { script?.Invoke(value); return Task.CompletedTask; }), _ => Task.CompletedTask);
        var session = new DiscordSession();
        typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
        controller.SetSession(session);
        typeof(AppController).GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, registry);
        return controller;
    }

    private static Task PipelineInvoke(AppController controller, string method, params object[] arguments) =>
        (Task)typeof(AppController).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, arguments)!;

    private static Task<TestOutcome> ResumedUploadDestinationsAndCancellationAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        await h.Registry.RefreshMappingsAsync();
        var state = await h.PrepareAsync("resumed.bin", "resume-original"u8.ToArray());
        var reservation = await h.Storage.ReserveAsync(state.FileId, state.TransferId, default);
        state.Storage = reservation.Layout; state.ThreadId = reservation.ThreadId;
        state.PayloadLayoutVersion = TransferPayloadLayouts.SharedStorage;
        await h.Transfers.SaveAsync(state);
        var fileId = state.FileId; var version = state.TransferId; var salt = state.Encryption!.Salt;
        await h.Registry.RunJanitorAsync(default);
        RequireSync(h.Server.DeletedChannels.Contains(reservation.ThreadId) &&
            (await h.Transfers.RecoverableAsync()).Single().SharedAllocationPending, "o janitor não retirou o pool vazio preservando a retomada");
        using var missing = new PipelineHandler(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(missing, false));
        var registry = new AppRegistry(api, h.Store, h.Settings); await registry.RefreshMappingsAsync();
        var scripts = new ConcurrentQueue<string>();
        await using (var controller = PipelineController(h, api, registry, scripts.Enqueue))
        {
            // Deliberately use the stale in-memory checkpoint, as in an already open resume dialog.
            await PipelineInvoke(controller, "ResumeInterruptedTransfersCoreAsync", new[] { state }, true).WaitAsync(TimeSpan.FromSeconds(25));
            var uploaded = (await registry.LoadCachedFolderAsync("root", "/")).Single();
            RequireSync(uploaded.FileId == fileId && uploaded.VersionId == version && uploaded.Encryption!.Salt == salt &&
                uploaded.ThreadId != reservation.ThreadId && uploaded.ChunkReferences?.Count == 1 &&
                scripts.Any(script => script.Contains("_onAppsBatch")), "a retomada perdeu a identidade, o conteúdo ou o card");
            var downloaded = await h.Downloader.DownloadAndAssembleAsync(uploaded, h.Directory.CreateSubdirectory("verify-resume"), _ => { });
            RequireSync(await File.ReadAllTextAsync(downloaded.FullName) == "resume-original", "o destino novo perdeu os bytes originais");
        }
        var cancelled = await Task.WhenAll(Enumerable.Range(0, 3).Select(index => h.PrepareAsync($"cancel-resume-{index}.bin", new byte[4096])));
        foreach (var transfer in cancelled) { transfer.SharedAllocationPending = true; await h.Transfers.SaveAsync(transfer); }
        using var blocked = new BlockFirstPayload(h.Server);
        using var cancelApi = new DiscordApiClient(h.Settings.BotToken, new HttpClient(blocked, false));
        var cancelRegistry = new AppRegistry(cancelApi, h.Store, h.Settings); await cancelRegistry.RefreshMappingsAsync();
        scripts.Clear();
        await using (var priority = cancelRegistry.SharedStorage.PrioritizeUploads()) // Physical cleanup must not gate cancellation.
        await using (var controller = PipelineController(h, cancelApi, cancelRegistry, scripts.Enqueue))
        {
            var resumed = PipelineInvoke(controller, "ResumeInterruptedTransfersCoreAsync", cancelled, true);
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var operations = (IDictionary)typeof(AppController).GetField("_uploadFileOperations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
            var key = operations.Keys.Cast<string>().First(); var op = key[..key.LastIndexOf(':')];
            await controller.HandleJsonMessageAsync(JsonSerializer.Serialize(new { method = "cancelTransfers", args = new[] { op } }));
            await resumed.WaitAsync(TimeSpan.FromSeconds(8));
            RequireSync(cancelled.All(transfer => transfer.Status == TransferStatuses.Cancelled) &&
                !(await h.Transfers.RecoverableAsync()).Any(transfer => cancelled.Any(item => item.TransferId == transfer.TransferId)) &&
                scripts.Any(script => script.Contains("_opsUpdate") && script.Contains("cancelled")),
                "o lote retomado ficou cancelando ou continuou recuperável");
        }
        cancelApi.Dispose();
        return TestOutcome.Pass();
    });

    private static Task<TestOutcome> ImageUploadCompanionsAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        await h.Registry.RefreshMappingsAsync();
        var paths = Enumerable.Range(0, 10).Select(index => Path.Combine(h.Directory.FullName, $"image-{index}." + (index == 0 ? "jpg" : "png"))).ToArray();
        foreach (var path in paths)
        {
            var pixels = Enumerable.Repeat((byte)110, 320 * 240 * 3).ToArray();
            if (path.EndsWith(".jpg"))
                for (var y = 0; y < 240; y++) for (var x = 0; x < 320; x++)
                { var at = (y * 320 + x) * 3; pixels[at] = x < 160 ? (byte)255 : (byte)0; pixels[at + 1] = 0; pixels[at + 2] = x < 160 ? (byte)0 : (byte)255; }
            var bitmap = BitmapSource.Create(320, 240, 96, 96, PixelFormats.Rgb24, null, pixels, 320 * 3);
            BitmapEncoder encoder = path.EndsWith(".jpg") ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
            var metadata = path.EndsWith(".jpg") ? new BitmapMetadata("jpg") : null;
            metadata?.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
            using var file = File.Create(path); encoder.Save(file);
        }
        var stages = new ConcurrentQueue<string>();
        await using var controller = PipelineController(h, h.Api, h.Registry, stages.Enqueue);
        await PipelineInvoke(controller, "UploadPathsAsync", (object)paths).WaitAsync(TimeSpan.FromSeconds(60));
        var entries = await h.Registry.LoadCachedFolderAsync("root", "/");
        RequireSync(entries.Count == paths.Length && entries.All(entry => entry.ChunkReferences is { Count: 1 } &&
            entry.ThumbnailRef != null && entry.ThumbnailRef.MessageId == entry.ChunkReferences[0].MessageId &&
            entry.ThumbnailRef.AttachmentId != entry.ChunkReferences[0].AttachmentId), "a miniatura não acompanhou a imagem no mesmo multipart");
        RequireSync(!stages.Any(script => script.Contains("Gerando miniatura") || script.Contains("Enviando miniatura")), "a imagem manteve um estágio separado de miniatura");
        var first = entries.Single(entry => entry.Name.EndsWith(".jpg"));
        var thumbnail = await h.Downloader.DownloadMessageAttachmentBytesAsync(first.ThreadId, first.ThumbnailRef!.MessageId,
            encryption: new(first.FileId, first.Encryption!, EncryptedPayloadRoles.Preview, 0, 1), attachmentId: first.ThumbnailRef.AttachmentId);
        using var image = new MemoryStream(thumbnail!);
        var decoded = BitmapDecoder.Create(image, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        RequireSync(decoded.PixelWidth == 200 && decoded.PixelHeight == 200, "o anexo de miniatura não contém uma imagem válida");
        var rgb = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
        var top = new byte[4]; var bottom = new byte[4];
        rgb.CopyPixels(new System.Windows.Int32Rect(100, 30, 1, 1), top, 4, 0);
        rgb.CopyPixels(new System.Windows.Int32Rect(100, 170, 1, 1), bottom, 4, 0);
        RequireSync(top[2] > 180 && top[0] < 70 && bottom[0] > 180 && bottom[2] < 70, "a miniatura perdeu a orientação EXIF da foto");
        var output = await h.Downloader.DownloadAndAssembleAsync(first, h.Directory.CreateSubdirectory("image-download"), _ => { });
        var original = await File.ReadAllBytesAsync(paths.Single(path => Path.GetFileName(path) == first.Name));
        RequireSync((await File.ReadAllBytesAsync(output.FullName)).SequenceEqual(original),
            "o envio conjunto alterou os bytes do original");
        Console.WriteLine("IMAGE_COMPANIONS 10 originals + 10 thumbnails; paired multipart; authenticated roundtrip");
        return TestOutcome.Pass();
    });

    private static async Task<TestOutcome> CancelledUploadOutboxRecoveryAsync()
    {
        using var h = new SharedHarness();
        var fileId = Guid.NewGuid().ToString("N");
        var cancelled = await Task.WhenAll(Enumerable.Range(0, 2).Select(index => h.PrepareAsync($"cancelled-version-{index}.bin", [1, 2, 3], fileId)));
        await h.UploadAsync(cancelled, publish: false);
        var kept = (await h.UploadAsync([await h.PrepareAsync("keep.bin", "keep"u8.ToArray())])).Single();
        var completed = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var subscription = h.Storage.ObserveCleanup(update =>
        {
            if (update.Completed != null && cancelled.Any(state => state.TransferId == update.Completed)) completed[update.Completed] = 0;
            return false;
        }, done.Token);
        await using (h.Storage.PrioritizeUploads())
        {
            foreach (var state in cancelled)
            {
                await h.Storage.CancelReservationAsync(state, default, deferCleanup: true);
                await h.Transfers.CancelAsync(state, false, true);
            }
        }
        // A new manager must recover the durable local intents, independently of the
        // foreground transfer objects that have already been removed.
        var recovered = new SharedStorageManager(h.Api, h.Store, h.Settings, journalIndex: h.Registry.GetStorageJournalIndexAsync);
        while (completed.Count < 2)
        {
            recovered.RequestCleanup();
            await Task.Delay(50, done.Token);
        }
        RequireSync(h.Server.Messages(kept.ThreadId).Sum(message => message.Attachments.Count) == 1 &&
            h.Server.Messages(kept.ThreadId).Any(message => message.Id == kept.ChunkReferences!.Single().MessageId),
            "a limpeza cancelada reteve versões ou removeu os anexos de outro arquivo");
        var output = await h.Downloader.DownloadAndAssembleAsync(kept, h.Directory.CreateSubdirectory("kept-download"), _ => { });
        RequireSync(await File.ReadAllTextAsync(output.FullName) == "keep", "a recuperação da limpeza alterou o arquivo mantido");
        var dedicated = await h.PrepareAsync("dedicated-cancel.bin", [7, 8, 9]);
        var post = await h.Api.CreateForumPostAsync("files", "dedicated-cancel", "test");
        dedicated.ThreadId = post.ThreadId; dedicated.Storage = new StorageLayout { Mode = "dedicated" };
        using (var part = new PreparedUploadPart(new FileInfo(dedicated.SourcePath), 3))
            await h.Api.SendFilesMessageAsync(post.ThreadId, "payload", [part]);
        await h.Storage.CancelReservationAsync(dedicated, default, deferCleanup: true);
        await h.Transfers.CancelAsync(dedicated, false, true);
        while (!h.Server.DeletedChannels.Contains(post.ThreadId)) await Task.Delay(50, done.Token);
        RequireSync(!h.Server.DeletedChannels.Contains(kept.ThreadId), "o cancelamento dedicado removeu o pool de outro arquivo");
        using var empty = new SharedHarness();
        var single = await empty.PrepareAsync("cancel-empty-pool.bin", [1, 2, 3]);
        var only = (await empty.UploadAsync([single], publish: false)).Single();
        await using (empty.Storage.PrioritizeUploads())
        {
            await empty.Storage.CancelReservationAsync(single, default, deferCleanup: true);
            await empty.Transfers.CancelAsync(single, false, true);
        }
        while (!empty.Server.DeletedChannels.Contains(only.ThreadId)) await Task.Delay(50, done.Token);
        RequireSync(empty.Server.Messages(only.ThreadId).Count == 0, "o cancelamento deixou a thread vazia no servidor");
        Console.WriteLine("CANCEL_CLEANUP sharedAttachmentsRemoved=True unrelatedFileKept=True dedicatedThreadDeleted=True emptyPoolDeleted=True");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> ColdPreviewJournalSnapshotAsync()
    {
        using var h = new SharedHarness();
        var journal = await h.Registry.GetStorageJournalIndexAsync(default);
        await Task.WhenAll(Enumerable.Range(0, 220).Select(_ => h.Api.CreateMessageAsync(journal.ThreadId,
            "{\"schemaVersion\":1,\"entryType\":\"storageBatchReceipt\"}", serializeRoute: false)));
        var entry = (await h.UploadAsync([await h.PrepareAsync("preview.bin", "preview-data"u8.ToArray())])).Single();
        await using (await h.Storage.BeginReadAsync("preview:warm", entry.FileId, SharedStorageManager.Payloads(MetadataService.CreateFileMetadata(entry)), default)) { }
        using var transport = new PipelineHandler(h.Server) { Journal = journal.ThreadId };
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(transport, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        await using (await registry.SharedStorage.BeginReadAsync("preview:cold", entry.FileId, SharedStorageManager.Payloads(MetadataService.CreateFileMetadata(entry)), default)) { }
        Console.WriteLine($"PREVIEW_TAIL journalPages={transport.JournalPages} mappingPages={transport.NamesPages}");
        RequireSync(transport.JournalPages <= 2 && transport.JournalPages > 0,
            "a prévia fria releu o histórico completo ou não validou o servidor");
        await h.Storage.BeginCleanupAsync(entry, default); // New remote fence must invalidate the cached permission to read.
        var next = new AppRegistry(api, h.Store, h.Settings);
        try
        {
            await using var unexpected = await next.SharedStorage.BeginReadAsync("preview:deleted", entry.FileId,
                SharedStorageManager.Payloads(MetadataService.CreateFileMetadata(entry)), default);
            return TestOutcome.Fail("o snapshot ignorou uma exclusão remota posterior");
        }
        catch (IOException) { }
        Console.WriteLine("PREVIEW_SNAPSHOT remote tail validated; remote deletion fence preserved");
        return TestOutcome.Pass();
    }

    private sealed class PipelineHandler(SyncDiscordFixture server) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(server, false);
        internal string? Journal;
        internal int JournalPages;
        internal int NamesPages;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/channels/names/messages")) Interlocked.Increment(ref NamesPages);
            if (request.Method == HttpMethod.Get && server.DeletedChannels.Any(thread => path.EndsWith("/channels/" + thread)))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            if (request.Method == HttpMethod.Get && Journal != null && path.EndsWith("/channels/" + Journal + "/messages"))
                Interlocked.Increment(ref JournalPages);
            return _forward.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }
}
