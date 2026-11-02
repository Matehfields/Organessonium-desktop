using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> SmallUploadBatchIndicesAndRefreshAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-small-upload-");
        try
        {
            using var server = new SyncDiscordFixture();
            using var transport = new ObservedSmallUploadHandler(server);
            var settings = new AppSettings { BotToken = "small-batch-" + Guid.NewGuid(), NamesChannelId = "names",
                FoldersForumId = "folders", AppsForumId = "files", SerialChannelId = "serial",
                MaxParallelUploads = 2, UploadThreads = 2, GenerateVideoPreviews = false };
            var store = new SettingsStore(directory.CreateSubdirectory("data")); store.Save(settings);
            using var api = new DiscordApiClient(settings.BotToken, new HttpClient(transport, false));
            var registry = new AppRegistry(api, store, settings); await registry.RefreshMappingsAsync();
            var folder = new AppEntry { Type = "folder", Name = "Fotos", FolderId = "photos", ParentFolderId = "root", Path = "/" };
            await registry.AddEntryAsync(folder);
            var existing = AppEntry.File("Antes.bin", "old-payload", "2026-10-01T00:00:00Z", 1024, "/Fotos");
            existing.ParentFolderId = "photos";
            existing.ChunkCount = 1; existing.ChunkMessageIds = ["old-message"];
            await registry.AddEntryAsync(existing);
            var owner = new System.Windows.Window();
            using var web = new WebView2(); // Never shown or initialized.
            await using var controller = new AppController(owner, store, new WebMessageBridge(web), _ => Task.CompletedTask);
            var session = new DiscordSession();
            typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
            typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, settings);
            controller.SetSession(session);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AppController).GetField("_registry", flags)!.SetValue(controller, registry);
            typeof(AppController).GetField("_currentPath", flags)!.SetValue(controller, "/Fotos");
            typeof(AppController).GetField("_loadedApps", flags)!.SetValue(controller, new List<AppEntry> { folder });
            var paths = Enumerable.Range(0, 12).Select(index => Path.Combine(directory.FullName, $"small-{index:D2}.bin")).ToArray();
            foreach (var path in paths) await File.WriteAllBytesAsync(path, new byte[4096]);
            transport.IndexThreadId = store.LoadFolderIndexMappings()["photos"].ThreadId;
            transport.Archives.Clear(); transport.HoldMetadata = true;
            var serialWritesBeforeUpload = transport.SerialWrites;
            var upload = (Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, new object[] { paths })!;
            await transport.MetadataStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // The user opens/refreshes the folder after the queue captured its initial snapshot.
            await ((Task)typeof(AppController).GetMethod("LoadFolderAtPathAsync", flags)!.Invoke(controller,
                new object[] { "/Fotos", "[\"photos\"]", false })!).WaitAsync(TimeSpan.FromSeconds(15));
            RequireSync(!transport.Archives.Any(change => change.Thread == transport.IndexThreadId && change.Archived),
                "a atualização fechou o índice enquanto o lote ainda o utiliza");
            // Reconciliation in a separate task must not publish a partial serial.
            RequireSync(transport.SerialWrites == serialWritesBeforeUpload,
                "a recarga publicou um serial antes de terminar o upload");
            transport.AllowMetadata.TrySetResult();
            await upload.WaitAsync(TimeSpan.FromSeconds(40));
            var visible = (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
            var changes = transport.Archives.Where(change => change.Thread == transport.IndexThreadId).ToArray();
            RequireSync(changes.Length == 2 && !changes[0].Archived && changes[1].Archived,
                "o índice continuou alternando arquivamento por arquivo");
            RequireSync(server.PayloadPosts == 1 && server.FileThreads().Sum(thread => server.Messages(thread).Sum(message => message.Attachments.Count)) == 12 &&
                paths.All(File.Exists) && visible.Any(entry => entry.FileId == existing.FileId) &&
                paths.All(path => visible.Any(entry => entry.Name == Path.GetFileName(path) && entry.Path == "/Fotos")),
                $"o lote perdeu arquivos da pasta recarregada, publicou incompleto ou alterou os originais: posts={server.PayloadPosts}, attachments={server.FileThreads().Sum(thread => server.Messages(thread).Sum(message => message.Attachments.Count))}, existing={visible.Any(entry => entry.FileId == existing.FileId)}, cards={visible.Count(entry => entry.Type == "file")}, sources={paths.All(File.Exists)}");
            var cached = await registry.LoadCachedFolderAsync("photos", "/Fotos");
            RequireSync(cached.Count(entry => entry.Type == "file") == 13, "cache/serial perdeu entradas após o lote");
            var checksum = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[4096]));
            RequireSync(cached.Where(entry => entry.Name.StartsWith("small-", StringComparison.Ordinal)).All(entry =>
                entry.Checksums?.Sha256 == checksum && entry.Checksums.Chunks.SequenceEqual(new[] { checksum })),
                "o upload da biblioteca ou o cache/serial perdeu os checksums do arquivo e do chunk");
            var emptyFolder = directory.CreateSubdirectory("Sem arquivos");
            await ((Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller,
                new object[] { new[] { emptyFolder.FullName } })!).WaitAsync(TimeSpan.FromSeconds(15));
            visible = (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
            RequireSync(visible.Any(entry => entry.Type == "folder" && entry.Name == emptyFolder.Name && entry.Path == "/Fotos") &&
                visible.Count(entry => entry.Type == "file" && entry.Path == "/Fotos") == 13,
                "importar uma pasta vazia apagou os cards ou deixou a pasta sem publicação");
            // Two overlapping scopes must not close each other's index.
            transport.Archives.Clear();
            var first = await registry.BeginUploadIndexBatchAsync();
            await using (var second = await registry.BeginUploadIndexBatchAsync())
            {
                var extra = AppEntry.File("Extra.bin", "extra-payload", "2026-10-01T00:00:00Z", 1, "/Fotos");
                extra.ParentFolderId = "photos";
                extra.ChunkCount = 1; extra.ChunkMessageIds = ["extra-message"];
                await registry.AddEntryAsync(extra, synchronizeSerials: false);
                await first.DisposeAsync();
                RequireSync(transport.Archives.Count(change => change.Thread == transport.IndexThreadId) == 1,
                    "o primeiro escopo fechou o lote ainda ativo");
            }
            var overlappingChanges = transport.Archives.Where(change => change.Thread == transport.IndexThreadId).ToArray();
            RequireSync(overlappingChanges.Length == 2 && overlappingChanges.Last().Archived,
                "o índice não foi fechado ao sair do último escopo");
            return TestOutcome.Pass();
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(true); }
    });

    private sealed class ObservedSmallUploadHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        private int _held;
        internal string IndexThreadId = "";
        internal bool HoldMetadata;
        internal bool HoldAllMetadata;
        internal int SerialWrites;
        internal ConcurrentQueue<(string Thread, bool Archived)> Archives { get; } = new();
        internal TaskCompletionSource MetadataStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AllowMetadata { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            var offset = path.IndexOf("/channels/", StringComparison.Ordinal);
            var parts = offset < 0 ? [] : path[(offset + 10)..].Split('/');
            if ((request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch) && parts.Length > 1 && parts[0] == "serial")
                Interlocked.Increment(ref SerialWrites);
            if (request.Method == HttpMethod.Patch && parts.Length == 1)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                if (json.RootElement.TryGetProperty("archived", out var archived)) Archives.Enqueue((parts[0], archived.GetBoolean()));
            }
            if (HoldMetadata && request.Method == HttpMethod.Post && parts.Length == 2 && parts[0] == IndexThreadId &&
                parts[1] == "messages" && (HoldAllMetadata || Interlocked.Exchange(ref _held, 1) == 0))
            {
                MetadataStarted.TrySetResult();
                await AllowMetadata.Task.WaitAsync(token);
            }
            return await _forward.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }

    private static async Task<TestOutcome> ThumbnailWaitsForMemoryAndCancelsAsync()
    {
        var executable = Environment.GetEnvironmentVariable("ORGANESSONIUM_MEDIA_TEST_EXE");
        var ffmpeg = Environment.GetEnvironmentVariable("ORGANESSONIUM_FFMPEG_TEST_EXE");
        if (!File.Exists(executable) || !File.Exists(ffmpeg)) return TestOutcome.Fail("executáveis de mídia não informados");
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-thumbnail-memory-");
        try
        {
            var source = new FileInfo(Path.Combine(directory.FullName, "sample.png"));
            await File.WriteAllBytesAsync(source.FullName, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a9r8AAAAASUVORK5CYII="));
            RamUploadBudget.Pool.TrimFreeBuffers();
            GC.Collect();
            var softPressureBytes = AppMemoryBudget.AdmissionBytes - AppMemoryBudget.CommittedBytes() -
                2L * DiscordLimits.AttachmentBytes - VideoPreviewWorker.ThumbnailWorkingReservationBytes + 24L * 1024 * 1024;
            using (var softPressure = await RamUploadBudget.ReserveExternalBytesAsync(softPressureBytes, 0, default))
            using (var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            using (var drained = await VideoPreviewWorker.GenerateAsync(source, false, false, null, drainTimeout.Token, executable))
                RequireSync(drained.Length > 0 && AppMemoryBudget.PeakCommittedBytes <= AppMemoryBudget.LimitBytes,
                    "a pressão acima do alvo de 800 MiB bloqueou a miniatura apesar da margem disponível dentro de 1 GB");
            var pressureBytes = AppMemoryBudget.LimitBytes - 16L * 1024 * 1024 - AppMemoryBudget.CommittedBytes() -
                2L * DiscordLimits.AttachmentBytes - 48L * 1024 * 1024;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            using var pressure = await RamUploadBudget.ReserveExternalBytesAsync(pressureBytes, 0, timeout.Token);
            var thumbnailTask = VideoPreviewWorker.GenerateAsync(source, false, false, null, timeout.Token, executable);
            var ffmpegTask = FfmpegMediaService.ThumbnailAsync(source, timeout.Token, ffmpeg);
            await Task.Delay(300, timeout.Token);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var chunkTask = RamUploadBudget.ReserveBytesAsync(4096, cancel.Token);
            await Task.Delay(250, timeout.Token);
            RequireSync(!thumbnailTask.IsCompleted && !chunkTask.IsCompleted, "novos chunks ignoraram a prioridade da miniatura");
            cancel.Cancel();
            try { using var unexpected = await chunkTask; return TestOutcome.Fail("a reserva não respeitou o cancelamento"); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(31), timeout.Token);
            RequireSync(!thumbnailTask.IsCompleted && !ffmpegTask.IsCompleted, "a miniatura falhou pelo antigo timeout de 30 segundos");
            pressure.Dispose();
            using var thumbnail = await thumbnailTask.WaitAsync(TimeSpan.FromSeconds(10));
            using var ffmpegThumbnail = await ffmpegTask.WaitAsync(TimeSpan.FromSeconds(10));
            using var stream = new MemoryStream(thumbnail.Bytes);
            var image = System.Windows.Media.Imaging.BitmapDecoder.Create(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            RequireSync(image.Frames.Count > 0 && image.Frames[0].PixelWidth > 0 && ffmpegThumbnail.Length > 0 &&
                AppMemoryBudget.PeakCommittedBytes <= AppMemoryBudget.LimitBytes && directory.GetFiles().Length == 1,
                "a miniatura não foi gerada em RAM dentro do orçamento");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }
}
