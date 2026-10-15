using System.Net;
using System.Text;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> SharedStorageVersionsTrashAndLegacyAsync()
    {
        using var h = new SharedHarness();
        await h.Registry.RefreshMappingsAsync();
        var legacy = await h.PrepareAsync("version.bin", "legacy-version"u8.ToArray());
        var source = new FileInfo(legacy.SourcePath);
        using var memory = new UploadMemoryCoordinator(2);
        var uploader = new ForumUploader(h.Api, "files", MetadataCodec.ForStore(h.Store));
        var context = new EncryptedPayloadContext(legacy.FileId, legacy.Encryption!, EncryptedPayloadRoles.Original, 0, 1);
        var uploaded = await uploader.UploadResumableGeneratedAsync(legacy.FileName, 1,
            index => memory.PreparePartAsync(h.Encryption, source, source.Length, EncryptionService.MaximumPlaintextPayloadBytes,
                context, default, deferBuffers: true), "", "", new Dictionary<int, string>(), encryptedPayload: context,
            useAttachmentReferences: true, streamPreparedParts: true);
        var original = AppEntry.File(legacy.FileName, uploaded.ThreadId, TransferState.Now(), source.Length, "/");
        original.FileId = legacy.FileId; original.ParentFolderId = "root"; original.VersionId = legacy.TransferId;
        original.VersionUploadedAt = original.UploadedAt; original.Encryption = legacy.Encryption;
        original.ChunkReferences = uploaded.ChunkReferences!.ToList(); original.ChunkCount = 1; original.BatchId = uploaded.BatchId;
        await h.Registry.AddEntryAsync(original);
        var current = original;
        foreach (var text in new[] { "shared-version-one", "shared-version-two" })
        {
            var next = (await h.UploadAsync([await h.PrepareAsync(legacy.FileName, Encoding.UTF8.GetBytes(text), legacy.FileId)], false)).Single();
            FileVersioning.PreservePrevious(current, next, TransferState.Now());
            await h.Registry.UpdateEntryAsync(next, expectedPayload: MetadataService.CreateFileMetadata(current)); current = next;
        }
        RequireSync(current.Versions.Count == 2 && current.Storage?.Shared == true && current.Versions[0].Payload.Storage == null &&
            current.Versions[0].Payload.ThreadId != current.ThreadId && (await h.CatalogAsync()).Single().Members.Count == 1,
            "histórico entre layouts/threads ou contagem lógica das versões incorreta");
        RequireSync(FileVersioning.StorageBytes(current) == "legacy-version".Length + "shared-version-one".Length + "shared-version-two".Length,
            "tamanho utilizado não incluiu todas as versões");
        var historical = new MetadataService().ToAppEntry(new MetadataEnvelope(current.MetadataMessageId, current.Versions[0].Payload))!;
        var downloaded = await h.Downloader.DownloadAndAssembleAsync(historical, h.Directory.CreateSubdirectory("historical"), _ => { });
        RequireSync(await File.ReadAllTextAsync(downloaded.FullName) == "legacy-version", "download histórico em outra thread falhou");
        var payloads = SharedStorageManager.Payloads(MetadataService.CreateFileMetadata(current));
        var folder = AppEntry.Folder("Destino", "/"); folder.ParentFolderId = "root"; await h.Registry.AddEntryAsync(folder);
        current.Name = "renamed.bin"; await h.Registry.UpdateEntryAsync(current);
        await h.Registry.MoveEntryAsync(current, folder.FolderId, "/Destino");
        var trashed = await h.Registry.MoveToTrashAsync(current);
        RequireSync(trashed.ParentFolderId == "trash" && trashed.Versions.Count == 2 && h.Server.DeletedChannels.Count == 0,
            "lixeira removeu payloads ou perdeu versões");
        var restored = await h.Registry.RestoreFromTrashAsync(trashed);
        RequireSync(restored.ParentFolderId == folder.FolderId && restored.Trash == null && restored.Name == "renamed.bin" &&
            SharedStorageManager.Payloads(MetadataService.CreateFileMetadata(restored)).SelectMany(payload => payload.Attachments)
                .SequenceEqual(payloads.SelectMany(payload => payload.Attachments)), "mover/restaurar alterou os attachments");
        var sharedThread = restored.ThreadId;
        RequireSync(await h.DeleteAsync(restored) && h.Server.DeletedChannels.Contains(original.ThreadId) &&
            !h.Server.DeletedChannels.Contains(sharedThread) && h.Server.Messages(sharedThread).All(message => message.Attachments.Count == 0),
            "exclusão definitiva deixou versões ou apagou a thread compartilhada");
        // Plaintext metadata and unitary chunks remain readable/deletable without migration on read.
        var old = await h.Api.CreateForumPostAsync("files", "legacy-fixture", "old", default);
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(new FileMetadata { FileId = "legacy-id", ThreadId = old.ThreadId,
            Name = "old.bin", MimeType = "application/octet-stream", SizeBytes = 0, UploadedAt = TransferState.Now(), Chunks = [old.MessageId] }, JsonOptions.Web);
        var seeded = h.Server.SeedLegacyMetadata(legacyJson);
        var reader = new MetadataService(MetadataCodec.ForStore(h.Store), h.Api.DownloadAttachmentBytesAsync);
        var plain = reader.ToAppEntry((await reader.ReadEnvelopeAsync(seeded))!)!; plain.ParentFolderId = "root";
        var originalMessage = h.Server.Messages("root-thread").Single(message => message.Id == seeded.Id);
        RequireSync(originalMessage.Content == "" && originalMessage.Attachments.Count == 1 && plain.Storage == null,
            "ler o legado o regravou ou mudou seu layout");
        RequireSync(await h.DeleteAsync(plain) && h.Server.DeletedChannels.Contains(old.ThreadId), "exclusão do legado dedicado deixou de funcionar");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> SharedStorageResumePermissionsAndPublicationAsync()
    {
        using var h = new SharedHarness();
        var state = await h.PrepareAsync("resume.bin", "resume-content"u8.ToArray());
        var allocation = await h.Storage.ReserveAsync(state.FileId, state.TransferId, default);
        state.Storage = allocation.Layout; state.ThreadId = allocation.ThreadId; state.PayloadLayoutVersion = TransferPayloadLayouts.SharedStorage;
        await h.Transfers.SaveAsync(state);
        var source = new FileInfo(state.SourcePath);
        using var memory = new UploadMemoryCoordinator(2);
        var context = new EncryptedPayloadContext(state.FileId, state.Encryption!, EncryptedPayloadRoles.Original, 0, 1);
        var uploader = new ForumUploader(h.Api, "files", MetadataCodec.ForStore(h.Store));
        var shared = new SharedUploadContext(h.Storage, allocation);
        try
        {
            await uploader.UploadResumableGeneratedAsync(state.FileName, 1,
                index => memory.PreparePartAsync(h.Encryption, source, source.Length, EncryptionService.MaximumPlaintextPayloadBytes,
                    context, default, deferBuffers: true, isolateSendCancellation: true), state.ThreadId, "", new Dictionary<int, string>(),
                encryptedPayload: context, useAttachmentReferences: true, sharedUpload: shared,
                onBatchUploaded: _ => throw new IOException("simulated lost local acknowledgement"));
            return TestOutcome.Fail("a falha de checkpoint não foi propagada");
        }
        catch (IOException) { }
        RequireSync(state.UploadedChunks.Count == 0, "o fixture não simulou perda de confirmação local");
        var restarted = new AppRegistry(h.Api, h.Store, h.Settings);
        var recovered = await restarted.SharedStorage.RecoverChunksAsync(allocation, EncryptedPayloadRoles.Original, 1, default);
        RequireSync(recovered.Count == 1, "o plano remoto não recuperou a confirmação perdida");
        var before = h.Server.Messages(state.ThreadId).Sum(message => message.Attachments.Count);
        var resumed = await uploader.UploadResumableGeneratedAsync(state.FileName, 1,
            _ => throw new IOException("resume attempted a duplicate upload"), state.ThreadId, "", new Dictionary<int, string>(),
            encryptedPayload: context, useAttachmentReferences: true, uploadedChunkReferences: recovered,
            sharedUpload: new SharedUploadContext(restarted.SharedStorage, allocation));
        RequireSync(h.Server.Messages(state.ThreadId).Sum(message => message.Attachments.Count) == before, "retomada duplicou os payloads");
        var entry = AppEntry.File(state.FileName, state.ThreadId, TransferState.Now(), source.Length, "/");
        entry.FileId = state.FileId; entry.VersionId = state.TransferId; entry.VersionUploadedAt = entry.UploadedAt;
        entry.ParentFolderId = "root"; entry.Storage = state.Storage; entry.Encryption = state.Encryption;
        entry.ChunkReferences = resumed.ChunkReferences!.ToList(); entry.ChunkCount = 1; entry.BatchId = resumed.BatchId;
        await restarted.RefreshMappingsAsync();
        h.Server.RejectNextFileProjection = true;
        try { await restarted.AddEntryAsync(entry); return TestOutcome.Fail("o erro de publicação foi ignorado"); }
        catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.Forbidden) { }
        RequireSync(h.Server.Messages(state.ThreadId).Sum(message => message.Attachments.Count) == before, "falha de metadata apagou o payload");
        await restarted.AddEntryAsync(entry);
        RequireSync((await restarted.RefreshFolderAsync("root", "/")).Single(file => file.Type == "file").FileId == state.FileId,
            "retry da publicação não apareceu no índice");
        h.Server.ArchivedThreads[state.ThreadId] = true;
        h.Server.RejectNextUnarchive = true;
        var second = await h.PrepareAsync("permission.bin", "permission"u8.ToArray());
        try { await h.UploadAsync([second], false); return TestOutcome.Fail("permissão de reabertura foi ignorada"); }
        catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.Forbidden) { }
        var next = (await h.UploadAsync([second], false)).Single();
        RequireSync(next.ThreadId == state.ThreadId && h.Server.ArchivedThreads[next.ThreadId], "thread arquivada não foi reutilizada/rearquivada");
        h.Server.LockedThreads[state.ThreadId] = true;
        try { await using var lease = await h.Api.AcquireThreadWriteAsync(state.ThreadId, default); return TestOutcome.Fail("thread locked foi desbloqueada"); }
        catch (IOException) { }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> SharedStorageConcurrentOwnershipAndDeletionAsync()
    {
        using var h = new SharedHarness();
        var entries = await h.UploadAsync([await h.PrepareAsync("a.bin", "a"u8.ToArray()), await h.PrepareAsync("b.bin", "b"u8.ToArray())]);
        var message = entries[0].ChunkReferences!.Single().MessageId;
        var deletes = await Task.WhenAll(entries.Select(h.DeleteAsync));
        RequireSync(deletes.All(done => done) && h.Server.Messages(entries[0].ThreadId).All(item => item.Id != message) &&
            h.Server.DeletedChannels.Count == 0 && (await h.CatalogAsync()).Single().Members.Count == 0,
            "duas exclusões concorrentes perderam uma atualização ou apagaram a thread");
        var pending = await h.PrepareAsync("pending.bin", "pending"u8.ToArray());
        var reserved = await h.Storage.ReserveAsync(pending.FileId, pending.TransferId, default);
        var restarted = new SharedStorageManager(h.Api, h.Store, h.Settings);
        var same = await restarted.ReserveAsync(pending.FileId, pending.TransferId, default);
        RequireSync(reserved == same && (await h.CatalogAsync()).Single().Members.Count == 1, "reiniciar duplicou uma reserva");
        var foreign = new SharedStorageManager(h.Api, h.Store, h.Settings, Guid.NewGuid().ToString("N"));
        var other = await foreign.ReserveAsync("foreign-file", Guid.NewGuid().ToString("N"), default);
        RequireSync(other.ThreadId != same.ThreadId, "duas instalações disputaram as mesmas vagas sem CAS");
        var file = (await h.UploadAsync([pending])).Single();
        var request = await foreign.BeginCleanupAsync(file, default); await h.Registry.DeleteEntryAsync(file);
        RequireSync(!await foreign.CompleteCleanupAsync(request, default) && h.Server.Messages(file.ThreadId).Any(item => item.Attachments.Count > 0),
            "um segundo escritor executou PATCH destrutivo no pool de outra instalação");
        await restarted.ReconcileAsync();
        RequireSync(h.Server.Messages(file.ThreadId).All(item => item.Attachments.Count == 0) && !h.Server.DeletedChannels.Contains(file.ThreadId),
            "o escritor autorizado não retomou a limpeza remota pendente");
        return TestOutcome.Pass();
    }
}
