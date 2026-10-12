using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> MultiAttachmentMultipartLimitsAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-multipart-");
        try
        {
            using var server = new GroupedDiscordFixture { InspectLengthsOnly = true };
            using var api = new DiscordApiClient("multipart-" + Guid.NewGuid(), new HttpClient(server, false));
            var files = new List<PreparedUploadPart>();
            for (var index = 0; index < 11; index++)
            {
                var file = new FileInfo(Path.Combine(directory.FullName, "chunk-" + index + ".bin"));
                await using (var stream = file.Create()) stream.SetLength(DiscordLimits.AttachmentBytes);
                files.Add(new PreparedUploadPart(file, DiscordLimits.PlaintextChunkBytes));
            }
            foreach (var count in new[] { 1, 2, 10 })
            {
                var reply = await api.SendFilesMessageAsync("thread", "", files.Take(count).ToArray());
                RequireSync(reply.Attachments.Count == count && server.RequestCounts.Last() == count,
                    "multipart não enviou os índices/attachments esperados");
            }
            RequireSync(server.LastBodyLength > 10L * DiscordLimits.AttachmentBytes,
                "o corpo de 200 MiB não manteve o streaming multipart");
            var calls = server.RequestCounts.Count;
            foreach (var invalid in new[] { files, new List<PreparedUploadPart>() })
            {
                try { await api.SendFilesMessageAsync("thread", "", invalid); return TestOutcome.Fail("quantidade inválida chegou ao HTTP"); }
                catch (ArgumentException) { }
            }
            return calls == server.RequestCounts.Count ? TestOutcome.Pass() : TestOutcome.Fail("validação local perdeu o limite de 10");
        }
        finally { directory.Delete(true); }
    }

    private static async Task<TestOutcome> MultiAttachmentEncryptedResumeDownloadAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-grouped-resume-");
        try
        {
            const int count = 26;
            var crypto = CreateEncryptionService(out _);
            var encryption = crypto.CreateFileEncryption(EncryptionModes.Standard);
            var context = new EncryptedPayloadContext("grouped-file", encryption, EncryptedPayloadRoles.Original, 0, count);
            using var server = new GroupedDiscordFixture();
            using var api = new DiscordApiClient("grouped-" + Guid.NewGuid(), new HttpClient(server, false));
            var uploader = new ForumUploader(api, "files");
            using var memory = new UploadMemoryCoordinator(2, forceParallelism: true);
            memory.SelectPlan(1024);
            var seenBuffers = new List<byte[]>();
            Task<PreparedUploadPart> Prepare(int index) => PrepareGroupedTestPartAsync(directory, crypto, context, memory, seenBuffers, index);
            var original = Enumerable.Range(0, count).SelectMany(index => GroupedPlaintext(index)).ToArray();
            var source = new FileInfo(Path.Combine(directory.FullName, "source.bin"));
            await File.WriteAllBytesAsync(source.FullName, original);
            var repository = new TransferStateRepository(Directory.CreateDirectory(Path.Combine(directory.FullName, "checkpoints")));
            var manager = new TransferManager(repository);
            var state = await manager.CreateUploadAsync(source, "/", "root", "application/octet-stream", 1024, count);
            state.FileId = context.FileId; state.Encryption = encryption;
            using var interrupted = new CancellationTokenSource();
            var checkpointSizes = new List<int>();
            try
            {
                await uploader.UploadResumableGeneratedAsync(source.Name, count, Prepare, "", "", new Dictionary<int, string>(),
                    cancellationToken: interrupted.Token, encryptedPayload: context, cleanupPart: file => file.Delete(),
                    useAttachmentReferences: true, batchPreparationGate: memory.BatchPreparationGate,
                    maxPreparedPartBytes: 1024 + count + EncryptionService.EnvelopeOverheadBytes,
                    onBatchUploaded: async batch =>
                    {
                        await manager.MarkUploadChunksAsync(state, batch);
                        checkpointSizes.Add((await repository.LoadActiveByIdAsync(state.TransferId))!.UploadedChunks.Count);
                        if (state.UploadedChunks.Count >= 11) interrupted.Cancel();
                    });
                return TestOutcome.Fail("a interrupção entre batches não foi observada");
            }
            catch (OperationCanceledException) when (interrupted.IsCancellationRequested) { }
            var cumulative = 0;
            RequireSync(checkpointSizes.SequenceEqual(server.RequestCounts.Select(size => cumulative += size)) &&
                checkpointSizes[0] == 1 && checkpointSizes[^1] >= 11, "o batch não foi persistido como uma atualização completa");
            state = (await new TransferStateRepository(repository.RootDirectory).LoadActiveByIdAsync(state.TransferId))!;
            var confirmedIds = state.UploadedChunks.Select(chunk => chunk.AttachmentId!).ToArray();
            var resumedReferences = state.UploadedChunks.Select(chunk => new ChunkReference
                { Index = chunk.Index, MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId! }).ToArray();
            var progress = new List<int>();
            var result = await uploader.UploadResumableGeneratedAsync(source.Name, count, Prepare, state.ThreadId, state.BatchId,
                state.UploadedChunks.ToDictionary(chunk => chunk.Index, chunk => chunk.MessageId), progress.Add,
                encryptedPayload: context, cleanupPart: file => file.Delete(), useAttachmentReferences: true,
                uploadedChunkReferences: resumedReferences, batchPreparationGate: memory.BatchPreparationGate,
                maxPreparedPartBytes: 1024 + count + EncryptionService.EnvelopeOverheadBytes,
                onBatchUploaded: batch => manager.MarkUploadChunksAsync(state, batch));
            RequireSync(result.ChunkReferences?.Count == count && result.ChunkMessageIds.Count == 0 &&
                progress.First() == confirmedIds.Length && progress.Last() == count && state.PendingChunks.Count == 0,
                "retomada perdeu referências, progresso lógico ou o layout novo");
            RequireSync(server.RequestCounts.First() == 1 && server.RequestCounts.Sum() == count && server.RequestCounts.All(size => size is >= 1 and <= 10) &&
                result.ChunkReferences!.Take(confirmedIds.Length).Select(reference => reference.AttachmentId).SequenceEqual(confirmedIds),
                "um chunk confirmado foi reenviado ou os batches tiveram ordem incorreta");
            RequireSync(seenBuffers.All(buffer => buffer.All(value => value == 0)), "a conclusão reteve conteúdo nos buffers");

            var entry = AppEntry.File(source.Name, result.ThreadId, DateTime.UtcNow.ToString("O"), original.Length, "/");
            entry.FileId = context.FileId; entry.Encryption = encryption; entry.ChunkReferences = result.ChunkReferences!.ToList();
            var metadata = new MetadataService(MetadataCodec.Standard(), api.DownloadAttachmentBytesAsync);
            await metadata.SendEntryAsync(api, "names", entry);
            var remote = server.Messages[entry.MetadataMessageId];
            var decoded = await metadata.ReadEnvelopeAsync(remote) ?? throw new InvalidDataException("metadata publicado não foi lido");
            var loaded = metadata.ToAppEntry(decoded)!;
            var store = new SettingsStore(Directory.CreateDirectory(Path.Combine(directory.FullName, "cache")));
            store.CacheFolderEntries("root", [loaded]);
            loaded = new SettingsStore(store.DataDirectory).LoadCachedEntriesForFolder("root").Single();
            RequireSync(loaded.ChunkReferences!.SequenceEqual(entry.ChunkReferences) && loaded.ChunkMessageIds.Count == 0,
                "o cache perdeu attachmentId ou inventou chunks legados");
            var destination = Directory.CreateDirectory(Path.Combine(directory.FullName, "download"));
            var download = await manager.CreateDownloadAsync(loaded, destination);
            for (var index = 0; index < 2; index++)
            {
                var partial = Path.Combine(destination.FullName, "confirmed-" + index);
                await File.WriteAllBytesAsync(partial, crypto.DecryptBytes(server.Bytes[confirmedIds[index]], context.WithChunkIndex(index)));
                await manager.MarkDownloadChunkAsync(download, index, partial, GroupedPlaintext(index).Length);
            }
            download = (await new TransferStateRepository(repository.RootDirectory).LoadActiveByIdAsync(download.TransferId))!;
            var listsBefore = server.MessageLists;
            var downloader = new ForumDownloader(api, new AppSettings { DownloadThreads = 3 }, crypto, () => new HttpClient(server, false));
            var downloadProgress = new List<DownloadProgress>();
            var encryptedDiskFiles = 0;
            using var watcher = new FileSystemWatcher(destination.FullName, "*.encrypted") { IncludeSubdirectories = true, EnableRaisingEvents = true };
            watcher.Created += (_, _) => Interlocked.Increment(ref encryptedDiskFiles);
            var assembled = await downloader.DownloadAndAssembleAsync(loaded, destination, download, manager, _ => { },
                onDetailedProgress: item => { lock (downloadProgress) downloadProgress.Add(item); });
            await Task.Delay(50);
            RequireSync(encryptedDiskFiles == 0 && downloadProgress.Any(item => !item.Assembling && item.Percent == 80) &&
                downloadProgress.Where(item => !item.Assembling).All(item => item.Percent <= 80) &&
                downloadProgress.Any(item => item.Assembling && item.Percent == 80) && downloadProgress.Any(item => item.Assembling && item.Percent == 99),
                "download gravou ciphertext no disco ou não reservou 20% para montar o arquivo");
            RequireSync((await File.ReadAllBytesAsync(assembled.FullName)).SequenceEqual(original) && server.MessageLists - listsBefore == 1 &&
                confirmedIds.Take(2).All(id => !server.CdnReads.ContainsKey(id)),
                "download não deduplicou mensagens, perdeu ChunkCount global ou baixou chunks já confirmados");
            loaded.ChunkReferences = loaded.ChunkReferences!.Select(reference => reference.Index == 12
                ? reference with { AttachmentId = "missing" } : reference).ToList();
            try { await downloader.DownloadAndAssembleAsync(loaded, destination, _ => { }); return TestOutcome.Fail("attachment ausente recebeu fallback silencioso"); }
            catch (InvalidDataException) { }
            return TestOutcome.Pass();
        }
        finally { SqliteConnection.ClearAllPools(); directory.Delete(true); }
    }

    private static async Task<PreparedUploadPart> PrepareGroupedTestPartAsync(DirectoryInfo directory,
        EncryptionService crypto, EncryptedPayloadContext context, UploadMemoryCoordinator? memory, List<byte[]> buffers, int index)
    {
        var plaintext = GroupedPlaintext(index);
        var name = EncryptedPayloadProtocol.FileName(context.Role, index, context.ChunkCount);
        if (memory?.TryRentBuffers(plaintext.Length, out var lease) == true && lease != null)
        {
            try
            {
                crypto.EncryptBytesInto(plaintext, lease.Ciphertext.Span, context.WithChunkIndex(index));
                MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)lease.Ciphertext, out var buffer);
                lock (buffers) buffers.Add(buffer.Array!);
                lease.ReleasePlaintext();
                return new PreparedUploadPart(name, plaintext.Length + EncryptionService.EnvelopeOverheadBytes, plaintext.Length, lease);
            }
            catch { lease.Dispose(); throw; }
        }
        var file = new FileInfo(Path.Combine(directory.FullName, name));
        await File.WriteAllBytesAsync(file.FullName, crypto.EncryptBytes(plaintext, context.WithChunkIndex(index)));
        return new PreparedUploadPart(file, plaintext.Length);
    }

    private static byte[] GroupedPlaintext(int index) => Enumerable.Repeat((byte)(index + 1), 1024 + index).ToArray();

    private static async Task<TestOutcome> MultiAttachmentMetadataCompatibilityAsync()
    {
        var metadata = new MetadataService();
        var entry = AppEntry.File("Vídeo café.mp4", "thread", DateTime.UtcNow.ToString("O"), 5000, "/");
        entry.ChunkReferences = Enumerable.Range(0, 3).Select(index => new ChunkReference
            { Index = index, MessageId = "message", AttachmentId = "attachment-" + index }).ToList();
        entry.VideoPreviewChunkReferences = Enumerable.Range(0, 12).Select(index => new ChunkReference
            { Index = index, MessageId = "preview-" + index / 10, AttachmentId = "video-" + index }).ToList();
        entry.VideoPreviewSizeBytes = 3000;
        foreach (var mutation in new[] { "initial", "rename", "favorite", "trash", "restore" })
        {
            entry.Name = mutation + ".mp4"; entry.MimeType = "video/mp4"; entry.Favorite = mutation == "favorite";
            entry.Trash = mutation == "trash" ? new TrashMetadata { DeletedAt = DateTime.UtcNow.ToString("O") } : null;
            var payload = await metadata.CreateWritePayloadAsync(entry);
            try
            {
                var stored = payload.Attachment == null ? payload.Content : await File.ReadAllTextAsync(payload.Attachment.FullName);
                RequireSync(MetadataCodec.Standard().IsEncrypted(stored), "chunkRefs foi publicado em plaintext");
                using var decoded = JsonDocument.Parse(metadata.DecodeJson(stored));
                RequireSync(!decoded.RootElement.TryGetProperty("chunks", out _) &&
                    !decoded.RootElement.GetProperty("preview").GetProperty("video480p").TryGetProperty("chunks", out _),
                    "a escrita nova publicou uma lista de messageIds enganosa");
                var envelope = await metadata.ReadEnvelopeAsync(new DiscordMessageDto { Id = "metadata", Content = stored });
                var loaded = metadata.ToAppEntry(envelope!)!;
                RequireSync(loaded.ChunkReferences!.SequenceEqual(entry.ChunkReferences) &&
                    loaded.VideoPreviewChunkReferences!.SequenceEqual(entry.VideoPreviewChunkReferences),
                    "uma alteração de metadata perdeu as referências do original ou da prévia");
            }
            finally { payload.Attachment?.Directory?.Delete(true); }
        }
        const string legacy = """
            {"schemaVersion":2,"entryType":"file","fileId":"legacy","threadId":"thread","name":"antigo.mp4","mimeType":"video/mp4",
             "sizeBytes":20,"chunks":["old-1","old-2"],"preview":{"video480p":{"sizeBytes":10,"chunks":["old-preview"]}}}
            """;
        var old = metadata.ToAppEntry((await metadata.ReadEnvelopeAsync(new DiscordMessageDto { Id = "old", Content = legacy }))!)!;
        RequireSync(old.ChunkReferences == null && old.ChunkMessageIds.SequenceEqual(new[] { "old-1", "old-2" }) &&
            old.VideoPreviewChunkReferences == null && old.VideoPreviewChunkMessageIds.Single() == "old-preview", "o legado deixou de ser lido");
        var updated = await metadata.CreateWritePayloadAsync(old);
        try
        {
            var stored = updated.Attachment == null ? updated.Content : await File.ReadAllTextAsync(updated.Attachment.FullName);
            using var json = JsonDocument.Parse(metadata.DecodeJson(stored));
            RequireSync(json.RootElement.GetProperty("chunks").GetArrayLength() == 2 && !json.RootElement.TryGetProperty("chunkRefs", out _),
                "modificar metadata legado migrou o payload automaticamente");
        }
        finally { updated.Attachment?.Directory?.Delete(true); }
        foreach (var invalid in new[] { "null", "[]", "[{\"messageId\":\"m\",\"attachmentId\":\"a\"}]",
            "[{\"index\":0,\"messageId\":\"m\",\"attachmentId\":\"a\"},{\"index\":0,\"messageId\":\"m\",\"attachmentId\":\"b\"}]" })
        {
            var json = legacy.Replace("\"chunks\":[\"old-1\",\"old-2\"]", "\"chunkRefs\":" + invalid);
            try
            {
                await metadata.ReadEnvelopeAsync(new DiscordMessageDto { Id = "invalid", Content = metadata.EncodeJson(json) });
                return TestOutcome.Fail("chunkRefs inválido recebeu fallback legado");
            }
            catch (InvalidDataException) { }
        }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> MultiAttachmentRetriesAndFallbackAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-grouped-retry-");
        try
        {
            var crypto = CreateEncryptionService(out _);
            var context = new EncryptedPayloadContext("retry-file", crypto.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Original, 0, 10);
            foreach (var failure in new[] { "network", "429", "limit" })
            {
                var partCount = failure == "limit" ? 21 : 10;
                var uploadContext = context with { ChunkCount = partCount };
                using var server = new GroupedDiscordFixture
                    { FailNetworkOnce = failure == "network", RateLimitOnce = failure == "429", MaximumAcceptedAttachments = failure == "limit" ? 2 : 10 };
                using var api = new DiscordApiClient("retry-" + Guid.NewGuid(), new HttpClient(server, false));
                var uploader = new ForumUploader(api, "files");
                var result = await uploader.UploadResumableGeneratedAsync("retry.bin", partCount,
                    index => PrepareGroupedTestPartAsync(directory, crypto, uploadContext, null, [], index),
                    "thread", "", new Dictionary<int, string>(), encryptedPayload: uploadContext,
                    cleanupPart: file => file.Delete(), useAttachmentReferences: true);
                RequireSync(result.ChunkReferences?.Count == partCount && server.Bytes.Count == partCount, "retry/fallback perdeu chunks confirmados");
                var attempts = server.AttemptPayloads.ToArray();
                if (failure != "limit")
                    RequireSync(attempts.Length == 2 && attempts[0].Zip(attempts[1]).All(pair => pair.First.SequenceEqual(pair.Second)),
                        "retry não reabriu todos os streams com o mesmo ciphertext");
                else
                    RequireSync(server.RequestCounts.Contains(10) && server.RequestCounts.Contains(2) &&
                        server.RequestCounts.Count(count => count > 2) == 2 &&
                        result.ChunkReferences![0].MessageId == result.ChunkReferences[1].MessageId,
                        "o agrupamento não reduziu após rejeição específica de limite");
                foreach (var reference in result.ChunkReferences!)
                    RequireSync(crypto.DecryptBytes(server.Bytes[reference.AttachmentId], uploadContext.WithChunkIndex(reference.Index))
                        .SequenceEqual(GroupedPlaintext(reference.Index)), "o fallback alterou os envelopes dos chunks");
            }
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private static async Task<TestOutcome> MultiAttachmentFailuresCancellationAndLegacyResumeAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-grouped-errors-");
        try
        {
            var crypto = CreateEncryptionService(out _);
            var context = new EncryptedPayloadContext("errors-file", crypto.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Original, 0, 10);
            foreach (var failure in new[] { "count", "id", "unrelated-400" })
            {
                using var server = new GroupedDiscordFixture { WrongAttachmentCount = failure == "count",
                    MissingAttachmentId = failure == "id", RejectUnrelatedBadRequest = failure == "unrelated-400" };
                using var api = new DiscordApiClient("errors-" + Guid.NewGuid(), new HttpClient(server, false));
                var checkpoints = 0;
                try
                {
                    await new ForumUploader(api, "files").UploadResumableGeneratedAsync("errors.bin", 10,
                        index => PrepareGroupedTestPartAsync(directory, crypto, context, null, [], index),
                        "thread", "", new Dictionary<int, string>(), encryptedPayload: context, cleanupPart: file => file.Delete(),
                        useAttachmentReferences: true, onBatchUploaded: _ => { checkpoints++; return Task.CompletedTask; });
                    return TestOutcome.Fail("resposta inconsistente confirmou um batch");
                }
                catch (Exception ex) when (ex is InvalidDataException or DiscordApiException) { }
                RequireSync(checkpoints == 0 && server.RequestCounts.Count == 1, "falha inválida criou checkpoint ou retry indiscriminado");
            }
            using (var server = new GroupedDiscordFixture { BlockUploads = true })
            using (var api = new DiscordApiClient("cancel-" + Guid.NewGuid(), new HttpClient(server, false)))
            using (var memory = new UploadMemoryCoordinator(2, forceParallelism: true))
            using (var cancellation = new CancellationTokenSource())
            {
                memory.SelectPlan(1024);
                var buffers = new List<byte[]>(); var confirmed = 0;
                var pending = new ForumUploader(api, "files").UploadResumableGeneratedAsync("cancel.bin", 10,
                    index => PrepareGroupedTestPartAsync(directory, crypto, context, memory, buffers, index),
                    "thread", "", new Dictionary<int, string>(), cancellationToken: cancellation.Token,
                    encryptedPayload: context, cleanupPart: file => file.Delete(), useAttachmentReferences: true,
                    batchPreparationGate: memory.BatchPreparationGate,
                    onBatchUploaded: _ => { confirmed++; return Task.CompletedTask; });
                await server.UploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                cancellation.Cancel();
                try { await pending; return TestOutcome.Fail("cancelamento não interrompeu o batch"); }
                catch (OperationCanceledException) { }
                RequireSync(confirmed == 0 && buffers.All(buffer => buffer.All(value => value == 0)) &&
                    !directory.EnumerateFiles("chunk-*.bin").Any(), "cancelamento confirmou chunks ou reteve buffers/temporários");
            }
            var legacy = JsonSerializer.Deserialize<TransferState>("{\"totalChunks\":3,\"uploadedChunks\":[{\"index\":0,\"messageId\":\"confirmed\"}]}", JsonOptions.Web)!;
            RequireSync(legacy.PayloadLayoutVersion == TransferPayloadLayouts.SingleAttachment, "checkpoint antigo foi promovido ao layout novo");
            using var legacyServer = new GroupedDiscordFixture();
            using var legacyApi = new DiscordApiClient("legacy-" + Guid.NewGuid(), new HttpClient(legacyServer, false));
            var legacyContext = context.WithChunkCount(3);
            var resumed = await new ForumUploader(legacyApi, "files").UploadResumableGeneratedAsync("legacy.bin", 3,
                index => PrepareGroupedTestPartAsync(directory, crypto, legacyContext, null, [], index),
                "thread", "batch", legacy.UploadedChunks.ToDictionary(chunk => chunk.Index, chunk => chunk.MessageId),
                encryptedPayload: legacyContext, cleanupPart: file => file.Delete());
            RequireSync(resumed.ChunkReferences == null && resumed.ChunkMessageIds[0] == "confirmed" &&
                legacyServer.RequestCounts.SequenceEqual(new[] { 1, 1 }), "retomada antiga deixou de usar uma mensagem por chunk");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private static async Task<TestOutcome> MultiAttachmentVideoPreviewSeekAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-grouped-video-");
        try
        {
            var crypto = CreateEncryptionService(out _);
            var context = new EncryptedPayloadContext("video-file", crypto.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Original, 0, 13);
            using var server = new GroupedDiscordFixture();
            using var api = new DiscordApiClient("video-grouped-" + Guid.NewGuid(), new HttpClient(server, false));
            var uploader = new ForumUploader(api, "files");
            var original = await uploader.UploadResumableGeneratedAsync("video.mp4", 13,
                index => PrepareGroupedTestPartAsync(directory, crypto, context, null, [], index), "", "", new Dictionary<int, string>(),
                encryptedPayload: context, cleanupPart: file => file.Delete(), useAttachmentReferences: true);
            var previewContext = context with { Role = EncryptedPayloadRoles.Video480p };
            var preview = await uploader.UploadResumableGeneratedAsync("preview.mp4", 13,
                index => PrepareGroupedTestPartAsync(directory, crypto, previewContext, null, [], index), original.ThreadId, "", new Dictionary<int, string>(),
                encryptedPayload: previewContext, cleanupPart: file => file.Delete(), useAttachmentReferences: true);
            var expected = Enumerable.Range(0, 13).SelectMany(GroupedPlaintext).ToArray();
            var entry = AppEntry.File("video.mp4", original.ThreadId, "", expected.Length, "/");
            entry.FileId = context.FileId; entry.Encryption = context.Encryption; entry.ChunkReferences = original.ChunkReferences!.ToList();
            entry.VideoPreviewChunkReferences = preview.ChunkReferences!.ToList(); entry.VideoPreviewSizeBytes = expected.Length;
            var downloader = new ForumDownloader(api, new AppSettings(), crypto, () => new HttpClient(server, false));
            var previewFile = await downloader.DownloadVideoPreview480Async(entry, directory);
            RequireSync((await File.ReadAllBytesAsync(previewFile!.FullName)).SequenceEqual(expected) && preview.ChunkReferences!.Count == 13,
                "prévia 480p com mais de 10 chunks não foi remontada");
            server.CdnReads.Clear();
            server.ExpiredAttachments.UnionWith(original.ChunkReferences!.Select(reference => reference.AttachmentId));
            var provider = await downloader.OpenVideoChunksAsync(entry, lowQuality: false);
            server.RenewWithDuplicateNames = true;
            await using (var video = new VideoStreamSession(provider, 1))
            {
                await video.WarmUpAsync();
                video.SetPlaying(true);
                await WaitForVideoConditionAsync(() => server.CdnReads.ContainsKey(original.ChunkReferences![1].AttachmentId));
                var actual = new byte[1600];
                await video.ReadAsync(9000, actual);
                RequireSync(actual.SequenceEqual(expected.AsSpan(9000, actual.Length).ToArray()) &&
                    server.CdnReads.Count < 13, "seek/prefetch perdeu índices ou baixou o vídeo inteiro");
                video.SetPlaying(false, seeking: true);
                await video.ReadAsync(100, new byte[16]);
            }
            server.RenewWithDuplicateNames = false;
            await using var low = new VideoStreamSession(await downloader.OpenVideoChunksAsync(entry, lowQuality: true), 0);
            var lowBytes = new byte[1600]; await low.ReadAsync(500, lowBytes);
            RequireSync(lowBytes.SequenceEqual(expected.AsSpan(500, lowBytes.Length).ToArray()), "streaming 480p perdeu o contexto de criptografia");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private sealed class GroupedDiscordFixture : HttpMessageHandler
    {
        private long _next = 500000;
        public bool InspectLengthsOnly;
        public bool FailNetworkOnce;
        public bool RateLimitOnce;
        public bool WrongAttachmentCount;
        public bool MissingAttachmentId;
        public bool RejectUnrelatedBadRequest;
        public bool BlockUploads;
        public bool RenewWithDuplicateNames;
        public int MaximumAcceptedAttachments = 10;
        public long LastBodyLength;
        public int MessageLists;
        public ConcurrentDictionary<string, DiscordMessageDto> Messages { get; } = new();
        public ConcurrentDictionary<string, byte[]> Bytes { get; } = new();
        public ConcurrentDictionary<string, int> CdnReads { get; } = new();
        public ConcurrentQueue<int> RequestCounts { get; } = new();
        public ConcurrentQueue<byte[][]> AttemptPayloads { get; } = new();
        public HashSet<string> ExpiredAttachments { get; } = [];
        public TaskCompletionSource UploadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions.Web), Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "group.fixture")
            {
                var id = uri.AbsolutePath.Split('/')[^1];
                CdnReads.AddOrUpdate(id, 1, (_, count) => count + 1);
                if (ExpiredAttachments.Contains(id) && !uri.Query.Contains("renewed")) return new(HttpStatusCode.Forbidden);
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes[id]) };
            }
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var channel = segments[3];
            if (request.Method == HttpMethod.Get)
            {
                if (segments.Length == 5)
                {
                    Interlocked.Increment(ref MessageLists);
                    return Reply(Messages.Values.Where(message => message.Content != "metadata-fixture").ToArray());
                }
                var message = Messages[segments[^1]];
                if (!RenewWithDuplicateNames) return Reply(message);
                return Reply(new DiscordMessageDto { Id = message.Id, Content = message.Content,
                    Attachments = message.Attachments.Select(attachment => new DiscordAttachmentDto
                    { Id = attachment.Id, FileName = "renamed.bin", Size = attachment.Size, Url = attachment.Url + "?renewed=1" }).ToList() });
            }
            if (request.Method == HttpMethod.Patch && segments.Length == 4) return Reply(new { id = channel });
            if (request.Content is not MultipartFormDataContent multipart)
            {
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var content = json.RootElement.GetProperty("content").GetString()!;
                var simple = new DiscordMessageDto { Id = Interlocked.Increment(ref _next).ToString(), Content = content };
                Messages[simple.Id] = simple;
                return Reply(simple);
            }
            var parts = multipart.Where(part => part.Headers.ContentDisposition?.FileName != null).ToArray();
            RequestCounts.Enqueue(parts.Length);
            LastBodyLength = multipart.Headers.ContentLength ?? -1;
            RequireSync(parts.All(part => part is StreamContent or UploadPartContent), "payload multipart foi bufferizado integralmente");
            using var payloadJson = JsonDocument.Parse(await multipart.First(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "payload_json").ReadAsStringAsync(token));
            var forum = segments[^1] == "threads";
            var payload = forum ? payloadJson.RootElement.GetProperty("message") : payloadJson.RootElement;
            var declared = payload.GetProperty("attachments").EnumerateArray().ToArray();
            var bodies = new byte[parts.Length][];
            var names = new string[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                names[index] = parts[index].Headers.ContentDisposition!.FileName!.Trim('"');
                RequireSync(parts[index].Headers.ContentDisposition!.Name!.Trim('"') == "files[" + index + "]" &&
                    declared[index].GetProperty("id").GetString() == index.ToString() &&
                    declared[index].GetProperty("filename").GetString() == names[index], "índices do JSON/multipart não correspondem");
                bodies[index] = InspectLengthsOnly ? [] : await parts[index].ReadAsByteArrayAsync(token);
            }
            AttemptPayloads.Enqueue(bodies);
            if (FailNetworkOnce) { FailNetworkOnce = false; throw new HttpRequestException("fixture connection interruption"); }
            if (RateLimitOnce)
            {
                RateLimitOnce = false;
                return new(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"retry_after\":0.01,\"global\":false}") };
            }
            if (RejectUnrelatedBadRequest)
                return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"code\":50035,\"errors\":{\"content\":{\"_errors\":[{\"code\":\"INVALID_CONTENT\"}]}}}") };
            if (parts.Length > MaximumAcceptedAttachments)
                return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"code\":50035,\"errors\":{\"attachments\":{\"_errors\":[{\"code\":\"BASE_TYPE_MAX_LENGTH\"}]}}}") };
            if (BlockUploads)
            {
                UploadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            var reply = new DiscordMessageDto { Id = Interlocked.Increment(ref _next).ToString(), Content = payload.GetProperty("content").GetString()! };
            for (var index = 0; index < parts.Length; index++)
            {
                var id = MissingAttachmentId ? "" : Interlocked.Increment(ref _next).ToString();
                Bytes[id] = bodies[index];
                reply.Attachments.Add(new DiscordAttachmentDto
                { Id = id, FileName = names[index], Size = InspectLengthsOnly ? parts[index].Headers.ContentLength!.Value : bodies[index].Length,
                    Url = "https://group.fixture/attachments/" + id });
            }
            if (WrongAttachmentCount) reply.Attachments.RemoveAt(reply.Attachments.Count - 1);
            // Discord may return attachments in a different order.
            reply.Attachments.Reverse();
            Messages[reply.Id] = reply;
            return forum ? Reply(new { id = "thread", message = reply }) : Reply(reply);
        }
    }
}
