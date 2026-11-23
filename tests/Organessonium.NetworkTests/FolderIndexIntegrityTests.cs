using System.IO;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> FolderIndexWriteIntegrityAsync()
    {
        using var h = new SharedHarness();
        var folder = AppEntry.Folder("Documentos", "/");
        folder.ParentFolderId = AppRegistry.RootFolderId;
        await h.Registry.AddEntryAsync(folder);
        RequireSync((await h.Api.GetChannelAsync(folder.ThreadId)).Name == $"Documentos [{folder.FolderId}]",
            "a thread criada não mostra nome e identificação da pasta");
        folder.Name = "Documentos renomeados";
        await h.Registry.UpdateEntryAsync(folder);
        RequireSync((await h.Api.GetChannelAsync(folder.ThreadId)).Name == $"Documentos renomeados [{folder.FolderId}]",
            "a renomeação não chegou à thread da pasta");
        var serialEntry = h.Server.Messages("serial").Single(message =>
            SerialMessageEnvelope.TryParse(message.Content, out var envelope) && envelope.FolderId == folder.FolderId);
        using var locator = JsonDocument.Parse(serialEntry.Content);
        RequireSync(locator.RootElement.GetProperty("name").GetString() == folder.Name && serialEntry.Attachments.Count == 1,
            "o serial perdeu o nome visível ou seu anexo");

        var metadata = new MetadataService(MetadataCodec.ForStore(h.Store), h.Api.DownloadAttachmentBytesAsync);
        var entry = AppEntry.File("relatorio.bin", "payload-ausente", TransferState.Now(), 123, "/Documentos renomeados");
        entry.FileId = "mesma-identidade";
        entry.ParentFolderId = "outra-pasta";
        entry.ChunkMessageIds = ["1"];
        await metadata.SendEntryAsync(h.Api, folder.ThreadId, entry);
        await metadata.SendEntryAsync(h.Api, folder.ThreadId, entry);
        var auditor = new FolderIndexAudit(h.Api, metadata);
        var inventory = await auditor.ScanAsync(folder.ThreadId, folder.FolderId, null, default, payloadBudget: 4);
        RequireSync(inventory is { Headers: 1, UniqueFiles: 1, WrongFolder: 2, Duplicates: 1, Orphans: 1 },
            "a auditoria não encontrou os metadados repetidos, deslocados e sem payload");

        await h.Api.CreateMessageAsync("names", metadata.SerializeFolderIndex(new("outra-pasta", folder.ThreadId, "")));
        await h.Registry.RefreshMappingsAsync();
        var blocked = false;
        try { await h.Registry.AddEntryAsync(entry); }
        catch (InvalidDataException) { blocked = true; }
        RequireSync(blocked && h.Server.Messages(folder.ThreadId).Count == 3,
            "a publicação enviou metadados para uma thread de outra pasta");

        entry.ParentFolderId = folder.FolderId;
        entry.FileId = "publicado-sem-serial";
        entry.MetadataMessageId = "";
        await metadata.SendEntryAsync(h.Api, folder.ThreadId, entry);
        var writes = h.Server.PublishedContents.Count;
        await h.Registry.RefreshServerIndexAsync(false, default);
        RequireSync(writes == h.Server.PublishedContents.Count,
            "a revisão passiva publicou mensagens");
        RequireSync((await h.Registry.LoadCachedFolderAsync(folder.FolderId, entry.Path)).Single().FileId == entry.FileId,
            "a navegação voltou ao serial desatualizado depois da revisão passiva");
        return TestOutcome.Pass();
    }
}
