using System.Net;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
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
        return TestOutcome.Pass();
    }

    private sealed class MaintenanceRootProbe(HttpMessageHandler inner, bool blockFirst) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        internal int RootReads;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
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
