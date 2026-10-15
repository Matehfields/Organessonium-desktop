using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private sealed class SharedHarness : IDisposable
    {
        internal DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("organessonium-shared-test-");
        internal SyncDiscordFixture Server { get; } = new();
        internal AppSettings Settings { get; } = new() { BotToken = "shared-test-" + Guid.NewGuid(), NamesChannelId = "names",
            FoldersForumId = "folders", AppsForumId = "files", SerialChannelId = "serial", PreviewEnabled = false,
            GenerateVideoPreviews = false, DownloadThreads = 2, UploadThreads = 4 };
        internal SettingsStore Store { get; }
        internal DiscordApiClient Api { get; }
        internal AppRegistry Registry { get; }
        internal SharedStorageManager Storage => Registry.SharedStorage;
        internal TransferManager Transfers { get; }
        internal EncryptionService Encryption { get; }
        internal SharedHarness()
        {
            Store = new SettingsStore(Directory.CreateSubdirectory("data")); Store.Save(Settings);
            Api = new DiscordApiClient(Settings.BotToken, new HttpClient(Server, false));
            Registry = new AppRegistry(Api, Store, Settings);
            Transfers = new TransferManager(new TransferStateRepository(Store.TransfersDirectory));
            Encryption = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(Store));
        }
        internal ForumDownloader Downloader => new(Api, Settings, Encryption, () => new HttpClient(Server, false), new StorageReadContext(Storage));
        internal async Task<TransferState> PrepareAsync(string name, byte[] bytes, string? fileId = null)
        {
            var file = new FileInfo(Path.Combine(Directory.FullName, Guid.NewGuid().ToString("N") + "-" + name));
            await File.WriteAllBytesAsync(file.FullName, bytes);
            var transfer = await Transfers.CreateUploadAsync(file, "/", "root", "application/octet-stream",
                EncryptionService.MaximumPlaintextPayloadBytes, 1);
            transfer.FileName = name; transfer.FileId = fileId ?? Guid.NewGuid().ToString("N");
            transfer.Encryption = Encryption.CreateFileEncryption("standard");
            await Transfers.SaveAsync(transfer); return transfer;
        }
        internal async Task<List<AppEntry>> UploadAsync(IReadOnlyList<TransferState> states, bool publish = true)
        {
            var allocations = await Storage.ReserveManyAsync(states.Select(state => (state.FileId, state.TransferId)).ToArray(), default);
            await using var lease = await Storage.HoldWritesAsync(allocations.Values.Select(allocation => allocation.ThreadId), default);
            var uploader = new ForumUploader(Api, "files", MetadataCodec.ForStore(Store));
            using var memory = new UploadMemoryCoordinator(4);
            var entries = await Task.WhenAll(states.Select(async state =>
            {
                var allocation = allocations[state.TransferId];
                state.Storage = allocation.Layout; state.ThreadId = allocation.ThreadId; state.PayloadLayoutVersion = TransferPayloadLayouts.SharedStorage;
                await Transfers.SaveAsync(state);
                var source = new FileInfo(state.SourcePath);
                var encryption = new EncryptedPayloadContext(state.FileId, state.Encryption!, EncryptedPayloadRoles.Original, 0, 1);
                var result = await uploader.UploadResumableGeneratedAsync(state.FileName, 1,
                    index => memory.PreparePartAsync(Encryption, source, source.Length, EncryptionService.MaximumPlaintextPayloadBytes,
                        encryption, default, deferBuffers: true, isolateSendCancellation: true), state.ThreadId, "", new Dictionary<int, string>(),
                    encryptedPayload: encryption, useAttachmentReferences: true, streamPreparedParts: true,
                    onBatchUploaded: batch => Transfers.MarkUploadChunksAsync(state, batch, default),
                    sharedUpload: new SharedUploadContext(Storage, allocation));
                var entry = AppEntry.File(state.FileName, result.ThreadId, TransferState.Now(), source.Length, "/");
                entry.ParentFolderId = "root"; entry.FileId = state.FileId; entry.VersionId = state.TransferId;
                entry.VersionUploadedAt = entry.UploadedAt; entry.ModifiedAt = entry.UploadedAt; entry.Storage = state.Storage;
                entry.ChunkReferences = result.ChunkReferences!.ToList(); entry.ChunkMessageIds = result.ChunkMessageIds.ToList();
                entry.ChunkCount = 1; entry.BatchId = result.BatchId; entry.Encryption = state.Encryption;
                return entry;
            }));
            if (publish)
            {
                await Registry.RefreshMappingsAsync();
                await using var indices = await Registry.BeginUploadIndexBatchAsync();
                foreach (var entry in entries) await Registry.AddEntryAsync(entry, synchronizeSerials: false);
                await Registry.SynchronizeFolderSerialsAsync(["root"]);
                await Storage.ReconcileReservationsAsync(default);
            }
            return entries.ToList();
        }
        internal async Task<bool> DeleteAsync(AppEntry entry)
        {
            var intent = await Storage.BeginCleanupAsync(entry, default);
            await Registry.DeleteEntryAsync(entry);
            return await Storage.CompleteCleanupAsync(intent, default);
        }
        internal async Task<List<StorageThreadRecord>> CatalogAsync()
        {
            var result = new List<StorageThreadRecord>();
            var metadata = new MetadataService(MetadataCodec.ForStore(Store), Api.DownloadAttachmentBytesAsync);
            foreach (var message in Server.Messages("names"))
            {
                using var json = await metadata.ReadJsonAsync(message);
                if (json?.RootElement.TryGetProperty("entryType", out var type) == true && type.GetString() == "storageThread")
                    result.Add(json.RootElement.Deserialize<StorageThreadRecord>(JsonOptions.Web)!);
            }
            return result;
        }
        public void Dispose()
        {
            Api.Dispose(); Server.Dispose(); SqliteConnection.ClearAllPools();
            try { Directory.Delete(true); } catch (IOException) { }
        }
    }

    private static async Task<TestOutcome> SharedStorageCapacityAndPackingAsync()
    {
        using var h = new SharedHarness();
        var states = new List<TransferState>();
        for (var index = 0; index < 256; index++) states.Add(await h.PrepareAsync(index + ".bin", Encoding.UTF8.GetBytes("file-" + index)));
        var entries = await h.UploadAsync(states, publish: false);
        RequireSync(entries.Select(entry => entry.ThreadId).Distinct().Count() == 1, "256 arquivos criaram mais de um post");
        var first = (await h.CatalogAsync()).Single();
        RequireSync(first.Members.Count == 256 && first.Members.Select(member => member.FileId).Distinct().Count() == 256, "capacidade incorreta");
        var extra = (await h.UploadAsync([await h.PrepareAsync("257.bin", "extra"u8.ToArray())], publish: false)).Single();
        RequireSync(extra.ThreadId != entries[0].ThreadId && (await h.CatalogAsync()).All(row => row.Members.Count <= 256), "o arquivo 257 não abriu outro post");
        var poolMessages = h.Server.Messages(entries[0].ThreadId);
        RequireSync(poolMessages.Single(message => message.Id == first.ControlMessageId).Attachments.Count == 0 &&
            poolMessages.Where(message => message.Attachments.Count > 0).All(message => message.Attachments.Count <= 10) &&
            poolMessages.Sum(message => message.Attachments.Count) == 256 &&
            entries.Select(entry => entry.ChunkReferences!.Single().AttachmentId).Distinct().Count() == 256,
            "mensagem inicial, limite de attachments ou associação dos arquivos inválida");
        RequireSync(poolMessages.Count(message => message.Attachments.Count == 10) > 0, "não houve agrupamento global de dez arquivos");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> SharedStorageReferencesDownloadsAndThumbnailsAsync()
    {
        using var h = new SharedHarness();
        var states = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => h.PrepareAsync(index + ".jpg", Encoding.UTF8.GetBytes("image-" + index))));
        var entries = await h.UploadAsync(states);
        var message = entries[0].ChunkReferences!.Single().MessageId;
        RequireSync(entries.All(entry => entry.ChunkReferences!.Single().MessageId == message), "dez arquivos pequenos não compartilharam uma mensagem");
        h.Api.InvalidatePayloadMessage(entries[0].ThreadId, message);
        var reads = h.Server.MessageReads.GetValueOrDefault((entries[0].ThreadId, message));
        var downloads = await Task.WhenAll(entries.Take(2).Select(async entry =>
            await h.Downloader.DownloadAndAssembleAsync(entry, h.Directory.CreateSubdirectory("download-" + entry.FileId), _ => { })));
        RequireSync((await File.ReadAllTextAsync(downloads[0].FullName)) == "image-0" && (await File.ReadAllTextAsync(downloads[1].FullName)) == "image-1",
            "downloads confundiram attachments da mesma mensagem");
        RequireSync(h.Server.MessageReads.GetValueOrDefault((entries[0].ThreadId, message)) == reads + 1, "a resolução não deduplicou o GET da mensagem");
        using var memory = new UploadMemoryCoordinator(2);
        var thumbnailTasks = entries.Take(2).Select(async (entry, index) =>
        {
            var thumbnail = new FileInfo(Path.Combine(h.Directory.FullName, "thumb-" + index + ".jpg"));
            await File.WriteAllBytesAsync(thumbnail.FullName, Encoding.UTF8.GetBytes("thumb-" + index));
            var context = new EncryptedPayloadContext(entry.FileId, entry.Encryption!, EncryptedPayloadRoles.Preview, 0, 1);
            using var part = await memory.PreparePartAsync(h.Encryption, thumbnail, thumbnail.Length, EncryptionService.MaximumPlaintextPayloadBytes, context, default);
            var reference = await h.Storage.Batcher.SendAsync(new(entry.FileId, entry.VersionId, entry.ThreadId, entry.Storage!), context, part, null, default);
            var expected = MetadataService.CreateFileMetadata(entry);
            entry.ThumbnailRef = new AttachmentReference { MessageId = reference.MessageId, AttachmentId = reference.AttachmentId };
            entry.PreviewMessageId = reference.MessageId;
            await h.Registry.UpdateEntryAsync(entry, expectedPayload: expected);
        });
        await Task.WhenAll(thumbnailTasks);
        RequireSync(entries[0].ThumbnailRef!.MessageId == entries[1].ThumbnailRef!.MessageId && entries[0].ThumbnailRef != entries[1].ThumbnailRef,
            "miniaturas não compartilharam uma mensagem com IDs separados");
        var thumbs = await Task.WhenAll(entries.Take(2).Select(entry => h.Downloader.DownloadMessageAttachmentBytesAsync(entry.ThreadId,
            entry.ThumbnailRef!.MessageId, encryption: new(entry.FileId, entry.Encryption!, EncryptedPayloadRoles.Preview, 0, 1), attachmentId: entry.ThumbnailRef.AttachmentId)));
        RequireSync(Encoding.UTF8.GetString(thumbs[0]!) == "thumb-0" && Encoding.UTF8.GetString(thumbs[1]!) == "thumb-1", "autenticação/resolução de thumbnailRef falhou");
        RequireSync(entries[0].ThumbnailCacheKey != entries[1].ThumbnailCacheKey && entries[0].ThumbnailCacheKey.Contains(entries[0].ThumbnailRef!.AttachmentId),
            "cache das miniaturas ignora a identidade exata do attachment");
        using var provider = await h.Downloader.OpenVideoChunksAsync(entries[0], false);
        RequireSync(Encoding.UTF8.GetString(await provider.DownloadAsync(0, default)) == "image-0", "streaming não resolveu chunkRefs compartilhado");
        try
        {
            await h.Downloader.DownloadMessageAttachmentBytesAsync(entries[1].ThreadId, entries[0].ThumbnailRef!.MessageId,
                encryption: new(entries[1].FileId, entries[1].Encryption!, EncryptedPayloadRoles.Preview, 0, 1), attachmentId: entries[0].ThumbnailRef!.AttachmentId);
            return TestOutcome.Fail("uma miniatura de outro arquivo passou na autenticação");
        }
        catch (EncryptedPayloadIntegrityException) { }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> SharedStorageSelectiveCleanupAndRecoveryAsync()
    {
        using var h = new SharedHarness();
        var entries = await h.UploadAsync([await h.PrepareAsync("one.bin", "one"u8.ToArray()), await h.PrepareAsync("two.bin", "two"u8.ToArray())]);
        var thread = entries[0].ThreadId; var sharedMessage = entries[0].ChunkReferences!.Single().MessageId;
        RequireSync(entries[1].ChunkReferences!.Single().MessageId == sharedMessage, "fixture não agrupou os arquivos");
        var control = (await h.CatalogAsync()).Single().ControlMessageId;
        var cleanup = await h.Storage.BeginCleanupAsync(entries[0], default); await h.Registry.DeleteEntryAsync(entries[0]);
        h.Server.RejectNextAttachmentEdit = true;
        try { await h.Storage.ReconcileAsync(); return TestOutcome.Fail("o PATCH bloqueado foi ignorado"); }
        catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.Forbidden) { }
        RequireSync(h.Server.Messages(thread).Single(message => message.Id == sharedMessage).Attachments.Count == 2, "falha alterou os attachments");
        var restarted = new AppRegistry(h.Api, h.Store, h.Settings);
        await restarted.SharedStorage.ReconcileAsync();
        var remaining = h.Server.Messages(thread).Single(message => message.Id == sharedMessage);
        RequireSync(remaining.Attachments.Count == 1 && remaining.Attachments[0].Id == entries[1].ChunkReferences!.Single().AttachmentId,
            "limpeza retomada não preservou o outro attachment");
        RequireSync(await h.DeleteAsync(entries[1]) && h.Server.Messages(thread).All(message => message.Id != sharedMessage) &&
            h.Server.Messages(thread).Any(message => message.Id == control) && !h.Server.DeletedChannels.Contains(thread), "o último arquivo apagou a thread ou seu controle");
        RequireSync((await h.CatalogAsync()).Single().Members.Count == 0, "as vagas não foram reconciliadas após a exclusão");
        return TestOutcome.Pass();
    }
}
