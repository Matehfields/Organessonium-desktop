using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> LibraryOperationSerialHandoffAsync()
    {
        using var h = new SharedHarness();
        var file = (await h.UploadAsync([await h.PrepareAsync("original.bin", [2, 3, 5])])).Single();
        var folder = AppEntry.Folder("Destino", "/");
        await h.Registry.AddEntryAsync(folder);
        var move = new LibraryMutation(LibraryMutationKind.Move,
            [new(file, file.AtLocation(folder.FolderId, "/Destino"))], TransferState.Now());
        await h.Registry.EnsureLibraryOperationSerialAsync(move, () => { }, default);
        await using (await h.Storage.AcquireMetadataMutationAsync(file, default, AppRegistry.LibraryOperationClaim(move, file)))
        {
            // Simulate an interrupted Desktop after reserving the mutation but before the destination POST.
        }

        var other = new SettingsStore(h.Directory.CreateSubdirectory("other-desktop"));
        other.Save(h.Settings);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(h.Server, false));
        var registry = new AppRegistry(api, other, h.Settings);
        await registry.RefreshMappingsAsync();
        var pending = (await registry.LoadLibraryOperationSerialsAsync(default)).Single();
        var moved = (await registry.ApplyLibraryRelocationsAsync(pending, () => { }, default)).Single();
        RequireSync(moved.FileId == file.FileId && moved.ParentFolderId == folder.FolderId,
            "a segunda instalação não conseguiu retomar a movimentação");
        var recovered = (await registry.LoadLibraryOperationSerialsAsync(default)).Single();
        var replay = (await registry.ApplyLibraryRelocationsAsync(recovered, () => { }, default)).Single();
        await registry.CompleteLibraryOperationSerialAsync(recovered, default);
        RequireSync(replay.MetadataMessageId == moved.MetadataMessageId &&
            (await registry.RefreshFolderAsync(folder.FolderId, "/Destino")).Count == 1 &&
            (await registry.RefreshFolderAsync("root", "/")).All(entry => entry.FileId != file.FileId),
            "a retomada repetida duplicou o arquivo ou preservou a origem");

        var renamed = moved.AtLocation(folder.FolderId, "/Destino");
        renamed.Name = "renomeado.bin";
        var rename = new LibraryMutation(LibraryMutationKind.Rename, [new(moved, renamed)], TransferState.Now());
        await registry.EnsureLibraryOperationSerialAsync(rename, () => { }, default);
        var remoteRename = (await h.Registry.LoadLibraryOperationSerialsAsync(default)).Single();
        await h.Registry.ApplyLibraryRenameAsync(remoteRename, default);
        await h.Registry.CompleteLibraryOperationSerialAsync(remoteRename, default);
        RequireSync((await registry.RefreshFolderAsync(folder.FolderId, "/Destino")).Single().Name == renamed.Name &&
            (await registry.LoadLibraryOperationSerialsAsync(default)).Count == 0, "o renomeio não sincronizou ou deixou histórico no canal");

        var kinds = new List<string>();
        var metadata = new MetadataService(MetadataCodec.ForStore(h.Store), h.Api.DownloadAttachmentBytesAsync);
        foreach (var message in h.Server.Messages((await h.Registry.GetStorageJournalIndexAsync(default)).ThreadId))
        {
            using var json = await metadata.ReadJsonAsync(message);
            if (json?.RootElement.TryGetProperty("entryType", out var type) == true)
                kinds.Add(type.GetString()!);
        }
        RequireSync(!kinds.Contains("storageBatchReceipt") && kinds.Count(kind => kind == "storageReference") == 1,
            "o Desktop continuou publicando recibos ou referências redundantes");
        return TestOutcome.Pass();
    }
}
