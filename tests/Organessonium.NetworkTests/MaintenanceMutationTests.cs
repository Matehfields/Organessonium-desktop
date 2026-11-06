using System.Net;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> MutationIdentityAndWriteBatchesAsync()
    {
        using var h = new SharedHarness();
        var entry = (await h.UploadAsync([await h.PrepareAsync("identity.bin", [1, 2, 3])])).Single();
        var owner = h.Storage.OwnerId;
        await h.Storage.WriteRecordAsync(new { schemaVersion = 1, entryType = "storageMutation",
            operationId = Guid.NewGuid().ToString("N"), fileId = entry.FileId, ownerId = owner, state = "pending" }, null, default);
        h.Store.ClearLocalDataForLogout();
        RequireSync(string.IsNullOrEmpty(h.Store.Load().BotToken), "o logout conservou a credencial da conta");
        h.Store.Save(h.Settings);
        using var probe = new MaintenanceRootProbe(h.Server, blockFirst: false);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(probe, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        await registry.RefreshMappingsAsync();
        RequireSync(registry.SharedStorage.OwnerId == owner, "o logout trocou a identidade e abandonou reservas remotas");
        await registry.SharedStorage.EnsureMetadataAvailableAsync(entry.FileId, default).WaitAsync(TimeSpan.FromSeconds(10));
        var journal = (await registry.GetStorageJournalIndexAsync(default)).ThreadId;
        probe.ArchiveChanges.Clear();
        entry.Name = "identity-renamed.bin";
        await registry.UpdateEntryAsync(entry).WaitAsync(TimeSpan.FromSeconds(10));
        RequireSync(probe.ArchiveChanges.GetValueOrDefault(journal) <= 2 &&
            (await registry.RefreshFolderAsync("root", "/")).Any(item => item.FileId == entry.FileId && item.Name == entry.Name),
            "a edição alternou o arquivamento do journal por registro ou perdeu o metadata");
        await registry.EnsureTrashIndexAsync();
        probe.ArchiveChanges.Clear();
        var moved = await registry.MoveToTrashAsync(entry).WaitAsync(TimeSpan.FromSeconds(10));
        RequireSync(probe.ArchiveChanges.Values.All(count => count <= 2) &&
            (await registry.RefreshFolderAsync("trash", "/@trash")).Any(item => item.FileId == entry.FileId),
            "enviar à lixeira alternou os índices durante os seriais ou perdeu o arquivo: " +
            System.Text.Json.JsonSerializer.Serialize(probe.ArchiveChanges));
        await registry.RestoreFromTrashAsync(moved);
        probe.ArchiveChanges.Clear();
        var folder = AppEntry.Folder("Fast folder", "/"); folder.ParentFolderId = "root";
        await registry.AddEntryAsync(folder).WaitAsync(TimeSpan.FromSeconds(10));
        RequireSync(probe.ArchiveChanges.GetValueOrDefault("root-thread") <= 2 &&
            h.Store.LoadCachedEntriesForFolder("root").Any(item => item.FolderId == folder.FolderId),
            "criar uma pasta alternou o índice pai ou não publicou a pasta");
        probe.ArchiveChanges.Clear();
        await using (await registry.BeginUploadIndexBatchAsync())
        {
            var uploadFolder = AppEntry.Folder("Upload folder", "/"); uploadFolder.ParentFolderId = "root";
            await registry.AddEntryAsync(uploadFolder, synchronizeSerials: false);
            await registry.SynchronizeFolderSerialsAsync(["root", uploadFolder.FolderId]);
        }
        RequireSync(probe.ArchiveChanges.GetValueOrDefault("root-thread") <= 2,
            "a preparação da pasta arquivou o índice pai antes de abrir o lote de upload");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> MaintenanceYieldsToMutationsAsync()
    {
        using (var h = new SharedHarness())
        {
            var entries = await h.UploadAsync(await Task.WhenAll(Enumerable.Range(0, 3)
                .Select(index => h.PrepareAsync("maintenance-" + index + ".bin", [(byte)index]))));
            foreach (var entry in entries.Take(2))
            {
                await h.Storage.BeginCleanupAsync(entry, default);
                await h.Registry.DeleteEntryAsync(entry, synchronizeSerials: false);
            }
            using var probe = new MaintenanceRootProbe(h.Server, blockFirst: true);
            using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(probe, false));
            var registry = new AppRegistry(api, h.Store, h.Settings);
            await registry.RefreshMappingsAsync();
            var maintenance = registry.SharedStorage.ReconcileAsync();
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var folder = AppEntry.Folder("Created during cleanup", "/");
            folder.ParentFolderId = "root";
            await registry.AddEntryAsync(folder).WaitAsync(TimeSpan.FromSeconds(10));
            await maintenance.WaitAsync(TimeSpan.FromSeconds(5));
            RequireSync(probe.Cancelled.Task.IsCompleted && !probe.Release.Task.IsCompleted &&
                h.Store.LoadCachedEntriesForFolder("root").Any(entry => entry.FolderId == folder.FolderId),
                "criar uma pasta aguardou a varredura bloqueada ou perdeu a publicação");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (h.Server.Messages(entries[0].ThreadId).Sum(message => message.Attachments.Count) > 1)
            {
                RequireSync(DateTime.UtcNow < deadline, "a manutenção não retomou a limpeza após criar a pasta");
                await Task.Delay(50);
            }
            var output = await h.Downloader.DownloadAndAssembleAsync(entries[2], h.Directory.CreateSubdirectory("kept"), _ => { });
            RequireSync((await File.ReadAllBytesAsync(output.FullName)).SequenceEqual(new byte[] { 2 }),
                "a manutenção retomada atingiu o arquivo preservado");
        }
        using (var h = new SharedHarness())
        {
            var entries = await h.UploadAsync(await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(index => h.PrepareAsync("proof-" + index + ".bin", [(byte)index]))));
            foreach (var entry in entries.Take(3))
            {
                await h.Storage.BeginCleanupAsync(entry, default);
                await h.Registry.DeleteEntryAsync(entry, synchronizeSerials: false);
            }
            using var probe = new MaintenanceRootProbe(h.Server, blockFirst: false);
            using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(probe, false));
            var registry = new AppRegistry(api, h.Store, h.Settings);
            await registry.RefreshMappingsAsync();
            await registry.SharedStorage.ReconcileAsync().WaitAsync(TimeSpan.FromSeconds(15));
            RequireSync(probe.RootReads == 1 && h.Server.Messages(entries[0].ThreadId).Sum(message => message.Attachments.Count) == 1,
                $"a manutenção repetiu a prova remota por arquivo ou não concluiu o lote: leituras={probe.RootReads}");
        }
        using (var h = new SharedHarness())
        {
            var entries = await h.UploadAsync(await Task.WhenAll(Enumerable.Range(0, 2)
                .Select(index => h.PrepareAsync("retry-" + index + ".bin", [(byte)index]))));
            await h.Storage.BeginCleanupAsync(entries[0], default);
            await h.Registry.DeleteEntryAsync(entries[0], synchronizeSerials: false);
            using var probe = new MaintenanceRootProbe(h.Server, blockFirst: false) { RejectRootProof = true };
            using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(probe, false));
            var registry = new AppRegistry(api, h.Store, h.Settings);
            using var observer = registry.SharedStorage.ObserveCleanup(_ => true, api.PayloadLifetime);
            try { await registry.SharedStorage.ReconcileAsync(); throw new InvalidOperationException("a falha de conferência não foi aplicada"); }
            catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.Forbidden) { }
            RequireSync(h.Server.Messages(entries[0].ThreadId).Sum(message => message.Attachments.Count) == 2,
                "a falha de conferência removeu conteúdo sem prova");
            probe.RejectRootProof = false;
            registry.SharedStorage.ResumePendingCleanup();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (h.Server.Messages(entries[0].ThreadId).Sum(message => message.Attachments.Count) != 1) await Task.Delay(25, timeout.Token);
            RequireSync(h.Store.LoadCachedEntriesForFolder("root").Single(entry => entry.Type == "file").FileId == entries[1].FileId,
                "a retomada periódica removeu o arquivo preservado");
        }
        return TestOutcome.Pass();
    }

    private sealed class MaintenanceRootProbe(HttpMessageHandler inner, bool blockFirst) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        internal int RootReads;
        internal bool RejectRootProof;
        internal System.Collections.Concurrent.ConcurrentDictionary<string, int> ArchiveChanges { get; } = new(StringComparer.Ordinal);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (RejectRootProof && request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.EndsWith("/channels/root-thread/messages", StringComparison.Ordinal))
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            if (request.Method == HttpMethod.Patch && segments.Length >= 2 && segments[^2] == "channels")
                ArchiveChanges.AddOrUpdate(segments[^1], 1, (_, count) => count + 1);
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/channels/root-thread/messages", StringComparison.Ordinal) &&
                Interlocked.Increment(ref RootReads) == 1 && blockFirst)
            {
                Started.TrySetResult();
                try { await Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { Cancelled.TrySetResult(); throw; }
            }
            return await _forward.SendAsync(request, token);
        }

        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }
}
