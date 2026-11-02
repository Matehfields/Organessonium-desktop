using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> SerialsAfterBatchMutationsAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        var states = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => h.PrepareAsync($"move-{index}.bin", [1, 2, 3])));
        var entries = await h.UploadAsync(states);
        await h.Registry.SetFavoriteAsync(entries[0], true);
        entries[0].Favorite = true;
        var folder = new AppEntry { Type = "folder", FolderId = "destination", ParentFolderId = "root", Name = "Destino", Path = "/" };
        await h.Registry.AddEntryAsync(folder);
        await h.Registry.EnsureTrashIndexAsync();
        var mappings = h.Store.LoadFolderIndexMappings();
        using var transport = new UploadCadenceHandler(h.Server, mappings["storage-v2"].ThreadId, 0);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(transport, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        await registry.RefreshMappingsAsync();
        using var web = new WebView2();
        var owner = new System.Windows.Window();
        await using var controller = new AppController(owner, h.Store, new WebMessageBridge(web), _ => Task.CompletedTask);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var session = new DiscordSession();
        typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
        controller.SetSession(session);
        typeof(AppController).GetField("_registry", flags)!.SetValue(controller, registry);
        typeof(AppController).GetField("_loadedApps", flags)!.SetValue(controller, entries.Append(folder).ToList());
        IReadOnlyList<AppEntry> Visible() => (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
        Task Invoke(string method, params object[] args) => (Task)typeof(AppController).GetMethod(method, flags)!.Invoke(controller, args)!;
        string Ids() => System.Text.Json.JsonSerializer.Serialize(Visible().Where(entry => entry.Type == "file").Select(entry => entry.MetadataMessageId));
        transport.Reset(mappings["destination"].ThreadId);
        await Invoke("MoveItemsAsync", Ids(), "/Destino");
        RequireSync(transport.FilesAtFirstSerial == 12 && transport.SerialWrites == 3 &&
            Visible().Count(entry => entry.Type == "file" && entry.Path == "/Destino") == 12,
            "mover publicou seriais por item ou perdeu os cards");
        transport.Reset(mappings["destination"].ThreadId);
        await Invoke("RenameItemAsync", Visible().First(entry => entry.Type == "file").MetadataMessageId, "renamed.bin");
        RequireSync(transport.FilesAtFirstSerial == 1 && transport.SerialWrites <= 2 && Visible().Any(entry => entry.Name == "renamed.bin"),
            "renomear não atualizou o card/serial ao concluir");
        transport.Reset(mappings["trash"].ThreadId);
        await Invoke("RelocateTrashItemsAsync", Ids(), false);
        RequireSync(transport.FilesAtFirstSerial == 12 && transport.SerialWrites == 3 &&
            (await registry.LoadFavoritesAsync()).Count == 0 && Visible().Count(entry => entry.ParentFolderId == "trash") == 12,
            "a lixeira publicou índices intermediários ou manteve favorito excluído");
        transport.Reset(mappings["destination"].ThreadId);
        await Invoke("RelocateTrashItemsAsync", Ids(), true);
        RequireSync(transport.FilesAtFirstSerial == 12 && transport.SerialWrites == 3 &&
            (await registry.LoadFavoritesAsync()).Count == 1 && Visible().Count(entry => entry.Type == "file" && entry.Path == "/Destino") == 12,
            "restaurar perdeu favoritos ou publicou índices intermediários");
        Console.WriteLine("SERIAL_BATCH move=12 rename=1 trash=12 restore=12; one write per affected serial");
        return TestOutcome.Pass();
    });

    private static Task<TestOutcome> UploadBatchCadenceAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        const int count = 200;
        await h.Registry.RefreshMappingsAsync();
        var journal = await h.Registry.GetStorageJournalIndexAsync(default);
        using var transport = new UploadCadenceHandler(h.Server, journal.ThreadId);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(transport, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        await registry.RefreshMappingsAsync();
        using var web = new WebView2();
        var owner = new System.Windows.Window();
        var completedCards = 0;
        var bridge = new WebMessageBridge(web, script =>
        {
            if (script.Contains("_onAppsBatch")) Interlocked.Increment(ref completedCards);
            return Task.CompletedTask;
        });
        await using var controller = new AppController(owner, h.Store, bridge, _ => Task.CompletedTask);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var session = new DiscordSession();
        typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings);
        controller.SetSession(session);
        typeof(AppController).GetField("_registry", flags)!.SetValue(controller, registry);
        var paths = Enumerable.Range(0, count).Select(index => Path.Combine(h.Directory.FullName, $"cadence-{index:D3}.bin")).ToArray();
        foreach (var path in paths) await File.WriteAllBytesAsync(path, new byte[4096]);
        transport.Clock.Start();
        await ((Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, [paths])!)
            .WaitAsync(TimeSpan.FromMinutes(3));
        transport.Clock.Stop();
        var entries = (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
        var batches = h.Server.FileThreads().SelectMany(h.Server.Messages).Where(message => message.Attachments.Count > 0).ToArray();
        Console.WriteLine($"BATCH_CADENCE files={count} elapsedMs={transport.Clock.ElapsedMilliseconds} firstPayloadMs={transport.FirstPayloadMs} " +
            $"http={transport.Requests} journalParallel={transport.JournalParallel} serialWrites={transport.SerialWrites} batches={batches.Length}");
        RequireSync(entries.Count(entry => entry.Type == "file") == count && completedCards >= count &&
            entries.All(entry => entry.Type != "file" || entry.ChunkReferences?.Count == 1 && entry.Checksums != null),
            "o lote perdeu cards, referências ou checksums");
        RequireSync(h.Server.PayloadPosts == 1 && batches.Length == count / 10 && batches.All(message => message.Attachments.Count == 10),
            "o lote criou posts por arquivo ou fragmentou os anexos");
        RequireSync(transport.SerialWrites == 1 && transport.FilesAtFirstSerial == count,
            "o serial foi publicado antes do último metadata do lote");
        RequireSync(transport.JournalParallel > 1, "confirmações independentes do journal continuam serializadas por latência HTTP");
        var metadata = new MetadataService(Services.MetadataEncryption.MetadataCodec.ForStore(h.Store), api.DownloadAttachmentBytesAsync);
        var claims = 0;
        foreach (var message in h.Server.Messages(journal.ThreadId))
        {
            using var document = await metadata.ReadJsonAsync(message);
            if (document?.RootElement.GetProperty("entryType").GetString() == "storageMutation") claims++;
        }
        RequireSync(claims == 0, "identidades novas criaram claims de edição por arquivo");
        RequireSync(!owner.IsVisible, "o benchmark abriu uma janela");
        return TestOutcome.Pass();
    });

    private sealed class UploadCadenceHandler(HttpMessageHandler inner, string journal, int latency = 100) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        private int _activeJournal;
        internal readonly Stopwatch Clock = new();
        internal int Requests, JournalParallel, SerialWrites, FilesAtFirstSerial, Files;
        internal long FirstPayloadMs = -1;
        private string _projection = "root-thread";
        internal void Reset(string projection)
        { _projection = projection; SerialWrites = FilesAtFirstSerial = Files = 0; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            var path = request.RequestUri!.AbsolutePath;
            var writing = request.Method == HttpMethod.Post || request.Method == HttpMethod.Patch;
            var journalWrite = writing && path.EndsWith($"/channels/{journal}/messages", StringComparison.Ordinal);
            if (journalWrite)
            {
                var active = Interlocked.Increment(ref _activeJournal);
                int current;
                do { current = JournalParallel; } while (active > current && Interlocked.CompareExchange(ref JournalParallel, active, current) != current);
            }
            try
            {
                if (writing && latency > 0) await Task.Delay(latency, token);
                var response = await _forward.SendAsync(request, token);
                if (writing && path.Contains($"/channels/{_projection}/messages", StringComparison.Ordinal)) Interlocked.Increment(ref Files);
                if (writing && path.Contains("/channels/serial/messages", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref SerialWrites) == 1) FilesAtFirstSerial = Files;
                }
                if (writing && request.Content is System.Net.Http.MultipartFormDataContent &&
                    !path.Contains("/channels/serial/", StringComparison.Ordinal) && !path.Contains($"/channels/{journal}/", StringComparison.Ordinal) &&
                    !path.Contains("/channels/root-thread/", StringComparison.Ordinal))
                    Interlocked.CompareExchange(ref FirstPayloadMs, Clock.ElapsedMilliseconds, -1);
                return response;
            }
            finally { if (journalWrite) Interlocked.Decrement(ref _activeJournal); }
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }
}
