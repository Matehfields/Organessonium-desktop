using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> UploadChecksumOverlapsNetworkAndResumesAsync()
    {
        using var h = new SharedHarness();
        const int chunkBytes = 64 * 1024;
        var bytes = Enumerable.Range(0, chunkBytes * 2 + 137).Select(index => (byte)(index * 31)).ToArray();
        var state = await h.PrepareAsync("streaming-checksum.bin", bytes);
        state.ChunkSizeBytes = chunkBytes; state.TotalChunks = 3; state.PayloadLayoutVersion = TransferPayloadLayouts.MultiAttachment;
        var source = new FileInfo(state.SourcePath);
        using var anotherReader = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hashTail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        using var memory = new UploadMemoryCoordinator(2);
        var uploader = new ForumUploader(h.Api, "files", MetadataCodec.ForStore(h.Store));
        await using (var checksums = await StreamingUploadChecksums.StartAsync(state, h.Transfers,
            () => new ChecksumTailStream(source.OpenRead(), chunkBytes, tailStarted, hashTail), bytes.Length, chunkBytes, cancel.Token))
        {
            var encryption = new EncryptedPayloadContext(state.FileId, state.Encryption!, EncryptedPayloadRoles.Original, 0, 3)
                { UploadPlaintextObserved = checksums!.ObserveAsync };
            try
            {
                await uploader.UploadResumableGeneratedAsync(state.FileName, 3,
                    index => memory.PreparePartAsync(h.Encryption, source, bytes.Length, chunkBytes, encryption.WithChunkIndex(index), cancel.Token, deferBuffers: true),
                    "", "", new Dictionary<int, string>(), encryptedPayload: encryption, useAttachmentReferences: true,
                    streamPreparedParts: true, maxPreparedPartBytes: chunkBytes + EncryptionService.EnvelopeOverheadBytes,
                    cancellationToken: cancel.Token, onBatchUploaded: async batch =>
                    {
                        await tailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        RequireSync(!hashTail.Task.IsCompleted && h.Server.PayloadPosts > 0,
                            "o primeiro corpo de upload esperou o checksum completo");
                        await h.Transfers.MarkUploadChunksAsync(state, batch);
                        cancel.Cancel();
                    });
                return TestOutcome.Fail("o cancelamento após a primeira parte não foi aplicado");
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        }
        var saved = (await h.Transfers.Repository.LoadActiveByIdAsync(state.TransferId))!;
        RequireSync(saved.UploadChecksumPending && saved.UploadedChunks.Count == 1 &&
            saved.Checksums!.Chunks[0] == Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, chunkBytes))),
            "a retomada perdeu a prova durável do plaintext já transmitido");
        var prefixJson = System.Text.Json.JsonSerializer.Serialize(saved, JsonOptions.Web);
        var firstMessage = saved.UploadedChunks[0].MessageId;
        await using (var checksums = await StreamingUploadChecksums.StartAsync(saved, h.Transfers,
            source.OpenRead, bytes.Length, chunkBytes, default))
        {
            var encryption = new EncryptedPayloadContext(saved.FileId, saved.Encryption!, EncryptedPayloadRoles.Original, 0, 3)
                { UploadPlaintextObserved = checksums!.ObserveAsync };
            var result = await uploader.UploadResumableGeneratedAsync(saved.FileName, 3,
                index => memory.PreparePartAsync(h.Encryption, source, bytes.Length, chunkBytes, encryption.WithChunkIndex(index), default, deferBuffers: true),
                saved.ThreadId, saved.BatchId, saved.UploadedChunks.ToDictionary(chunk => chunk.Index, chunk => chunk.MessageId),
                encryptedPayload: encryption, useAttachmentReferences: true, streamPreparedParts: true,
                uploadedChunkReferences: saved.UploadedChunks.Select(chunk => new ChunkReference
                    { Index = chunk.Index, MessageId = chunk.MessageId, AttachmentId = chunk.AttachmentId! }).ToArray(),
                onBatchUploaded: batch => h.Transfers.MarkUploadChunksAsync(saved, batch));
            await checksums.CompleteAsync(default);
            var entry = AppEntry.File(saved.FileName, result.ThreadId, TransferState.Now(), bytes.Length, "/");
            entry.FileId = saved.FileId; entry.ParentFolderId = "root"; entry.Encryption = saved.Encryption;
            entry.ChunkReferences = result.ChunkReferences!.ToList(); entry.ChunkCount = 3; entry.Checksums = saved.Checksums;
            await h.Registry.AddEntryAsync(entry);
            var downloaded = await h.Downloader.DownloadAndAssembleAsync(entry, h.Directory.CreateSubdirectory("verified"), _ => { });
            RequireSync(!saved.UploadChecksumPending && entry.Checksums!.Sha256 == Convert.ToHexStringLower(SHA256.HashData(bytes)) &&
                result.ChunkReferences![0].MessageId == firstMessage && (await File.ReadAllBytesAsync(downloaded.FullName)).SequenceEqual(bytes),
                "o arquivo retomado não preservou as referências, os bytes ou os hashes completos");
        }
        // Changing a confirmed prefix is detected even when only the tail would be transmitted on resume.
        anotherReader.Dispose();
        bytes[0] ^= 1; await File.WriteAllBytesAsync(source.FullName, bytes);
        var changed = System.Text.Json.JsonSerializer.Deserialize<TransferState>(prefixJson, JsonOptions.Web)!;
        await using (var checksums = await StreamingUploadChecksums.StartAsync(changed, h.Transfers,
            source.OpenRead, bytes.Length, chunkBytes, default))
        {
            try
            {
                await checksums!.ObserveAsync(0, bytes.AsMemory(0, chunkBytes), default);
                return TestOutcome.Fail("um prefixo confirmado alterado foi aceito");
            }
            catch (PayloadChecksumException) { }
        }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> UploadPreemptsBackgroundCleanupAsync()
    {
        using var h = new SharedHarness();
        var old = (await h.UploadAsync([await h.PrepareAsync("cleanup.bin", "old"u8.ToArray())])).Single();
        var intent = await h.Storage.BeginCleanupAsync(old, default);
        await h.Registry.DeleteEntryAsync(old, synchronizeSerials: false);
        using var gate = new CleanupIndexGate(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(gate, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        await registry.RefreshMappingsAsync();
        var maintenance = registry.SharedStorage.ReconcileAsync();
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        RequireSync((await registry.SharedStorage.ReserveManyAsync([], default).WaitAsync(TimeSpan.FromSeconds(2))).Count == 0,
            "um lote sem arquivos compartilhados aguardou o gate e consultou o histórico");
        AppEntry uploaded;
        await using (registry.SharedStorage.PrioritizeUploads())
        {
            await maintenance.WaitAsync(TimeSpan.FromSeconds(5));
            uploaded = (await h.UploadAsync([await h.PrepareAsync("priority.bin", "new-content"u8.ToArray())])).Single();
            RequireSync(!gate.Release.Task.IsCompleted && h.Server.Messages(uploaded.ThreadId).Any(message =>
                message.Attachments.Any(attachment => attachment.Id == uploaded.ChunkReferences!.Single().AttachmentId)),
                "a transmissão aguardou a varredura de manutenção");
        }
        await gate.Restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.TrySetResult();
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (h.Server.Messages(old.ThreadId).Any(message => message.Attachments.Any(attachment => attachment.Id == old.ChunkReferences!.Single().AttachmentId)))
        {
            if (DateTime.UtcNow >= timeout) return TestOutcome.Fail("a manutenção interrompida não retomou a limpeza");
            await Task.Delay(50);
        }
        var output = await h.Downloader.DownloadAndAssembleAsync(uploaded, h.Directory.CreateSubdirectory("priority-download"), _ => { });
        RequireSync(await File.ReadAllTextAsync(output.FullName) == "new-content", "a manutenção retomada atingiu o upload novo");
        await h.Storage.WriteRecordAsync(new
        {
            schemaVersion = 1, entryType = "storageMutation", operationId = Guid.NewGuid().ToString("N"),
            fileId = uploaded.FileId, ownerId = h.Storage.OwnerId, state = "pending"
        }, null, default);
        uploaded.Name = "priority-recovered.bin";
        await h.Registry.UpdateEntryAsync(uploaded);
        RequireSync((await h.Registry.RefreshFolderAsync("root", "/")).Single(file => file.Type == "file").Name == uploaded.Name,
            "um claim inativo desta instalação bloqueou a publicação após interromper a manutenção");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> ColdUploadJournalReadsInParallelAsync()
    {
        using var h = new SharedHarness();
        await h.UploadAsync(await Task.WhenAll(Enumerable.Range(0, 40).Select(index => h.PrepareAsync("history-" + index + ".bin", [1, 2, 3]))));
        var metadata = new MetadataService(MetadataCodec.ForStore(h.Store), h.Api.DownloadAttachmentBytesAsync);
        var planPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in h.Server.Messages((await h.Registry.GetStorageJournalIndexAsync(default)).ThreadId))
        {
            using var json = await metadata.ReadJsonAsync(row);
            if (json?.RootElement.GetProperty("entryType").GetString() == "storageBatchPlan")
                foreach (var attachment in row.Attachments) planPaths.Add(new Uri(attachment.Url).AbsolutePath);
        }
        RequireSync(planPaths.Count >= 4, "o histórico de teste não contém os anexos técnicos necessários");
        using var gate = new JournalAttachmentGate(h.Server, planPaths);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(gate, false));
        var registry = new AppRegistry(api, h.Store, h.Settings);
        await registry.RefreshMappingsAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var state = await h.PrepareAsync("cold-start.bin", [7, 8, 9]);
        var reservation = await registry.SharedStorage.ReserveAsync(state.FileId, state.TransferId, timeout.Token);
        await gate.Started.Task.WaitAsync(timeout.Token);
        using var memory = new UploadMemoryCoordinator(1);
        var encryption = new EncryptedPayloadContext(state.FileId, state.Encryption!, EncryptedPayloadRoles.Original, 0, 1);
        var uploader = new ForumUploader(api, "files", MetadataCodec.ForStore(h.Store));
        var sent = await uploader.UploadResumableGeneratedAsync(state.FileName, 1,
            _ => memory.PreparePartAsync(h.Encryption, new FileInfo(state.SourcePath), 3,
                EncryptionService.MaximumPlaintextPayloadBytes, encryption, timeout.Token, deferBuffers: true),
            reservation.ThreadId, "", new Dictionary<int, string>(), encryptedPayload: encryption,
            useAttachmentReferences: true, streamPreparedParts: true, cancellationToken: timeout.Token,
            sharedUpload: new SharedUploadContext(registry.SharedStorage, reservation));
        RequireSync(!gate.Release.Task.IsCompleted && sent.ChunkReferences?.Count == 1,
            "o corpo do upload aguardou a leitura completa do histórico");
        gate.Release.TrySetResult();
        await registry.SharedStorage.EnsureMetadataAvailableAsync(state.FileId, timeout.Token);
        RequireSync(gate.MaximumConcurrent >= 4 && gate.MaximumConcurrent <= 8 && planPaths.All(path => gate.Reads.GetValueOrDefault(path) == 1),
            "a preparação serializou os anexos técnicos ou baixou novamente os planos já autenticados");
        RequireSync((await h.CatalogAsync()).Single(row => row.ThreadId == reservation.ThreadId).Members.Count == 41,
            "a preparação paralela perdeu membros do catálogo");
        return TestOutcome.Pass();
    }

    private sealed class JournalAttachmentGate(HttpMessageHandler inner, HashSet<string> planPaths) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        internal int MaximumConcurrent;
        internal ConcurrentDictionary<string, int> Reads { get; } = new(StringComparer.Ordinal);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method != HttpMethod.Get || !planPaths.Contains(path))
                return await _forward.SendAsync(request, token);
            Reads.AddOrUpdate(path, 1, (_, count) => count + 1);
            var active = Interlocked.Increment(ref _active);
            int previous;
            do { previous = MaximumConcurrent; }
            while (active > previous && Interlocked.CompareExchange(ref MaximumConcurrent, active, previous) != previous);
            if (active >= 4) Started.TrySetResult();
            try { await Release.Task.WaitAsync(token); return await _forward.SendAsync(request, token); }
            finally { Interlocked.Decrement(ref _active); }
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ChecksumTailStream(Stream input, long boundary, TaskCompletionSource started,
        TaskCompletionSource release) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => input.Position = value; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position >= boundary) { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return await input.ReadAsync(buffer, token);
        }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => input.Seek(offset, origin);
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CleanupIndexGate(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        private int _scans;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Restarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/channels/root-thread/messages", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _scans) == 1) Started.TrySetResult();
                else Restarted.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            return await _forward.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }
}
