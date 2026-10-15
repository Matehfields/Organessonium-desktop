using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> SharedStorageJournalRoutingAsync()
    {
        using var h = new SharedHarness();
        var originalNames = h.Server.Messages("names").Select(message => message.Id).ToHashSet();
        var states = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => h.PrepareAsync(index + ".bin", [1, 2, 3])));
        var entries = await h.UploadAsync(states);
        var metadata = new MetadataService(MetadataCodec.ForStore(h.Store), h.Api.DownloadAttachmentBytesAsync);
        foreach (var message in h.Server.Messages("names").Where(message => !originalNames.Contains(message.Id)))
        {
            using var json = await metadata.ReadJsonAsync(message);
            RequireSync(json?.RootElement.GetProperty("entryType").GetString() == "folderIndex", "o upload publicou metadados de arquivo/journal em names");
        }
        var journal = await h.Registry.GetStorageJournalIndexAsync(default);
        RequireSync(journal.ThreadId != "root-thread", "o journal foi misturado com os metadados dos arquivos da raiz");
        var fileIds = new HashSet<string>();
        foreach (var message in h.Server.Messages("root-thread"))
        {
            using var json = await metadata.ReadJsonAsync(message);
            if (json?.RootElement.GetProperty("entryType").GetString() == "file") fileIds.Add(json.RootElement.GetProperty("fileId").GetString()!);
        }
        RequireSync(fileIds.SetEquals(entries.Select(entry => entry.FileId)) &&
            (await h.Registry.RefreshFolderAsync("root", "/")).All(entry => entry.FolderId != AppRegistry.StorageJournalFolderId),
            "os arquivos não ficaram no índice da pasta ou o índice técnico virou card");

        // Recreate the initial V2 location using only the simulated backend.
        foreach (var message in h.Server.Messages(journal.ThreadId))
        {
            using var json = await metadata.ReadJsonAsync(message);
            if (json?.RootElement.GetProperty("entryType").GetString()?.StartsWith("storage", StringComparison.Ordinal) != true) continue;
            await h.Api.WriteEncodedDocumentAsync("names", metadata.EncodeJson(json.RootElement.GetRawText()), null, default);
            await h.Api.DeleteMessageAsync(journal.ThreadId, message.Id);
        }
        var legacyIds = h.Server.Messages("names").Select(message => message.Id).ToHashSet();
        var restarted = new SharedStorageManager(h.Api, h.Store, h.Settings);
        var checkpoint = states[0];
        var reservation = new SharedStorageReservation(checkpoint.FileId, checkpoint.TransferId, entries[0].ThreadId, checkpoint.Storage!);
        var recovered = await restarted.RecoverChunksAsync(reservation, EncryptedPayloadRoles.Original, 1, default);
        RequireSync(recovered.Single().AttachmentId == entries[0].ChunkReferences!.Single().AttachmentId, "a correção perdeu a retomada de registros V2 antigos em names");
        await restarted.ReserveAsync(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), default);
        RequireSync(h.Server.Messages("names").Select(message => message.Id).ToHashSet().SetEquals(legacyIds),
            "a compatibilidade antiga voltou a escrever/apagar registros em names");
        RequireSync((await h.CatalogAsync()).Single().Members.Count == 11, "o catálogo antigo não foi atualizado no índice técnico preservando as vagas");
        var protectedNames = new HashSet<string>();
        foreach (var message in h.Server.Messages("names"))
        {
            using var json = await metadata.ReadJsonAsync(message);
            if (json?.RootElement.GetProperty("entryType").GetString()?.StartsWith("storage", StringComparison.Ordinal) != true)
                protectedNames.Add(message.Id);
        }

        try
        {
            await h.Registry.MigrateNamesStorageJournalAsync((stage, done, _) =>
            { if (stage == "verified" && done == 2) throw new IOException("simulated verification interruption"); });
            return TestOutcome.Fail("a interrupção da cópia não foi simulada");
        }
        catch (IOException) { }
        RequireSync(h.Server.Messages("names").Select(message => message.Id).ToHashSet().SetEquals(legacyIds),
            "a migração removeu origens antes de validar todas as cópias");
        try
        {
            await h.Registry.MigrateNamesStorageJournalAsync((stage, done, _) =>
            { if (stage == "removed" && done == 1) throw new IOException("simulated removal interruption"); });
            return TestOutcome.Fail("a interrupção da remoção não foi simulada");
        }
        catch (IOException) { }
        var migrated = await h.Registry.MigrateNamesStorageJournalAsync();
        var remainingNames = h.Server.Messages("names");
        RequireSync(migrated.Removed > 0 && protectedNames.SetEquals(remainingNames.Select(message => message.Id)),
            "a migração não preservou as mensagens não operacionais");
        var origins = new List<string>();
        foreach (var message in h.Server.Messages(journal.ThreadId))
        {
            using var json = await metadata.ReadJsonAsync(message);
            if (json?.RootElement.TryGetProperty("legacyNamesMessageId", out var origin) == true) origins.Add(origin.GetString()!);
        }
        RequireSync(origins.Count == origins.Distinct().Count(), "retomar a migração duplicou registros já copiados");
        var afterMigration = new SharedStorageManager(h.Api, h.Store, h.Settings);
        await afterMigration.ReserveAsync(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), default);
        RequireSync((await h.CatalogAsync()).Single(row => row.Members.Count == 12).ThreadId == entries[0].ThreadId,
            "a ordem da migração sobrepôs o catálogo mais recente e perdeu uma vaga");
        var finalDownload = await h.Downloader.DownloadAndAssembleAsync(entries[0], h.Directory.CreateSubdirectory("migration-download"), _ => { });
        RequireSync((await File.ReadAllBytesAsync(finalDownload.FullName)).SequenceEqual(new byte[] { 1, 2, 3 }) && h.Server.DeletedChannels.Count == 0,
            "a migração alterou payloads ou quebrou o download");
        return TestOutcome.Pass();
    }
}
