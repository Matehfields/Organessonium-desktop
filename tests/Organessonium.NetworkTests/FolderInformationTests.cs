using System.Reflection;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> FolderInformationTotalsAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        await h.Registry.RefreshMappingsAsync();
        var folder = new AppEntry { Type = "folder", Name = "Fotos", FolderId = "photo-folder", ParentFolderId = "root", Path = "/" };
        await h.Registry.AddEntryAsync(folder);
        var nested = new AppEntry { Type = "folder", Name = "Dentro", FolderId = "nested-folder", ParentFolderId = folder.FolderId, Path = "/Fotos" };
        await h.Registry.AddEntryAsync(nested);
        async Task AddFile(string parent, string path, long size, long history)
        {
            var entry = AppEntry.File(Guid.NewGuid().ToString("N") + ".png", "payload", TransferState.Now(), size, path);
            entry.ParentFolderId = parent; entry.ChunkMessageIds = ["part"];
            if (history > 0)
            {
                var previous = MetadataService.CreateFileMetadata(entry); previous.SizeBytes = history;
                previous.VersionId = "old"; previous.Versions = null;
                entry.Versions = [new FileVersion { VersionId = "old", ArchivedAt = TransferState.Now(), Payload = previous }];
            }
            await h.Registry.AddEntryAsync(entry);
        }
        await AddFile(folder.FolderId, "/Fotos", 100, 50);
        await AddFile(nested.FolderId, "/Fotos/Dentro", 200, 25);
        await AddFile(nested.FolderId, "/Fotos/Dentro", 0, 0);
        await AddFile("root", "/", 999, 0);
        h.Store.CacheFolderEntries(folder.FolderId, []);
        h.Store.CacheFolderEntries(nested.FolderId, []);
        var totals = await h.Registry.GetFolderInformationAsync(folder.FolderId, "/Fotos");
        RequireSync(totals == new FolderInformation(3, 300, 375), "a pasta perdeu descendentes, arquivo vazio ou bytes das versões");
        using var web = new WebView2();
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new WebMessageBridge(web, script =>
        {
            if (script.Contains("_onFileInformation\"")) response.TrySetResult(script);
            return Task.CompletedTask;
        });
        await using var controller = new AppController(new System.Windows.Window(), h.Store, bridge, _ => Task.CompletedTask);
        var session = new DiscordSession(); typeof(DiscordSession).GetProperty("Api")!.SetValue(session, h.Api);
        typeof(DiscordSession).GetProperty("Settings")!.SetValue(session, h.Settings); controller.SetSession(session);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(AppController).GetField("_registry", flags)!.SetValue(controller, h.Registry);
        typeof(AppController).GetField("_loadedApps", flags)!.SetValue(controller, await h.Registry.LoadCachedFolderAsync("root", "/"));
        await (Task)typeof(AppController).GetMethod("SendFileInformationAsync", flags)!.Invoke(controller, [folder.MetadataMessageId, "folder-info"])!;
        var script = await response.Task.WaitAsync(TimeSpan.FromSeconds(5));
        RequireSync(script.Contains("\"sizeBytes\":300") && script.Contains("\"storageBytes\":375") && script.Contains("\"fileCount\":3"),
            "os totais calculados não chegaram à resposta da interface");
        return TestOutcome.Pass();
    });
}
