using System.Collections;
using System.Net;
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
    private static Task<TestOutcome> IndividualUploadCancellationAndVisibilityAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var root = Directory.CreateTempSubdirectory("organessonium-upload-cancel-");
        try
        {
            using var server = new SyncDiscordFixture();
            using var blocked = new BlockFirstPayload(server);
            var settings = new AppSettings { BotToken = "cancel-fixture-" + Guid.NewGuid(), NamesChannelId = "names",
                FoldersForumId = "folders", AppsForumId = "files", SerialChannelId = "serial", MaxParallelUploads = 1,
                UploadThreads = 2, GenerateVideoPreviews = false };
            var store = new SettingsStore(root.CreateSubdirectory("data")); store.Save(settings);
            RequireSync(new AppSettings().GenerateVideoPreviews && !store.Load().GenerateVideoPreviews && !store.Load().Clone().GenerateVideoPreviews,
                "a preferência não preservou padrão/persistência");
            using var api = new DiscordApiClient(settings.BotToken, new HttpClient(blocked, false));
            var registry = new AppRegistry(api, store, settings); await registry.RefreshMappingsAsync();
            var folder = new AppEntry { Type = "folder", Name = "Fotos", FolderId = "photos", ParentFolderId = "root", Path = "/" };
            await registry.AddEntryAsync(folder);
            var owner = new System.Windows.Window();
            using var web = new WebView2(); // Not shown or initialized.
            await using var controller = new AppController(owner, store, new WebMessageBridge(web), _ => Task.CompletedTask);
            var session = new DiscordSession();
            typeof(DiscordSession).GetProperty("Api")!.SetValue(session, api);
            typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, settings);
            controller.SetSession(session);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AppController).GetField("_registry", flags)!.SetValue(controller, registry);
            typeof(AppController).GetField("_currentPath", flags)!.SetValue(controller, "/Fotos");
            typeof(AppController).GetField("_loadedApps", flags)!.SetValue(controller, new List<AppEntry> { folder });
            var paths = Enumerable.Range(0, 3).Select(index => Path.Combine(root.FullName, "file-" + index + ".bin")).ToArray();
            foreach (var path in paths) await File.WriteAllBytesAsync(path, new byte[4096]);
            var upload = (Task)typeof(AppController).GetMethod("UploadPathsAsync", flags)!.Invoke(controller, new object[] { paths })!;
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var operations = (IDictionary)typeof(AppController).GetField("_uploadFileOperations", flags)!.GetValue(controller)!;
            try
            {
                var key = operations.Keys.Cast<string>().Single(value => value.EndsWith(":0", StringComparison.Ordinal));
                var id = key[..^2];
                foreach (var index in new[] { 0, 1 })
                    await controller.HandleJsonMessageAsync(JsonSerializer.Serialize(new { method = "cancelUploadFile", args = new object[] { id, index } }));
            }
            finally { blocked.AllowPayload.TrySetResult(); }
            await upload.WaitAsync(TimeSpan.FromSeconds(25));
            var visible = (IReadOnlyList<AppEntry>)typeof(AppController).GetField("_loadedApps", flags)!.GetValue(controller)!;
            RequireSync(server.PayloadPosts == 1 && visible.Any(entry => entry.Name == "file-2.bin" && entry.Path == "/Fotos") &&
                !visible.Any(entry => entry.Name is "file-0.bin" or "file-1.bin") && paths.All(File.Exists) && operations.Count == 0,
                "cancelar um arquivo interrompeu os demais, perdeu a subpasta/listagem ou deixou tokens de operação");
            RequireSync((await registry.LoadCachedFolderAsync("photos", "/Fotos")).Any(entry => entry.Name == "file-2.bin"), "o arquivo concluído não ficou no cache");
            return TestOutcome.Pass();
        }
        finally { SqliteConnection.ClearAllPools(); try { root.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    });

    private sealed class BlockFirstPayload(SyncDiscordFixture inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        private int _held;
        internal string? CountJournal;
        internal int JournalReads;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowPayload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get && CountJournal != null && request.RequestUri!.AbsolutePath.EndsWith("/channels/" + CountJournal + "/messages"))
                Interlocked.Increment(ref JournalReads);
            if (request.Method == HttpMethod.Post && request.Content is MultipartFormDataContent &&
                inner.FileThreads().Any(thread => request.RequestUri!.AbsolutePath.EndsWith("/channels/" + thread + "/messages", StringComparison.Ordinal)) &&
                Interlocked.Exchange(ref _held, 1) == 0)
            {
                Started.TrySetResult();
                await AllowPayload.Task.WaitAsync(token);
            }
            return await _forward.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }
}
