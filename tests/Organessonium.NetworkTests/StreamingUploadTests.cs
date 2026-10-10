using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> TenAttachmentUploadsOverlapAcknowledgementsAsync()
    {
        AppMemoryBudget.Initialize();
        var directory = Directory.CreateTempSubdirectory("organessonium-ten-attachments-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var server = new StreamingUploadHandler { HoldReplies = true };
        try
        {
            var source = new FileInfo(Path.Combine(directory.FullName, "source.bin"));
            const int count = 30;
            var chunkBytes = EncryptionService.MaximumPlaintextPayloadBytes;
            await using (var output = source.Create()) output.SetLength((long)count * chunkBytes);
            var crypto = CreateEncryptionService(out _);
            var context = new EncryptedPayloadContext("ten-attachments", crypto.CreateFileEncryption(EncryptionModes.Standard),
                EncryptedPayloadRoles.Original, 0, count);
            using var api = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(server, false));
            using var memory = new UploadMemoryCoordinator(16, forceParallelism: true);
            using var gate = new AdaptiveUploadGate(1, false);
            var transferClock = System.Diagnostics.Stopwatch.StartNew();
            var upload = new ForumUploader(api, "files").UploadResumableGeneratedAsync("source.bin", count,
                index => memory.PreparePartAsync(crypto, source, source.Length, chunkBytes, context.WithChunkIndex(index),
                    timeout.Token, deferBuffers: true), "thread", "", new Dictionary<int, string>(), cancellationToken: timeout.Token,
                maxParallelParts: 16, adaptiveUploadGate: gate, encryptedPayload: context, useAttachmentReferences: true,
                batchPreparationGate: memory.BatchPreparationGate, streamPreparedParts: true);
            try
            {
                await server.ThreeBodies.Task.WaitAsync(timeout.Token);
                RequireSync(!upload.IsCompleted && server.Sizes.Count == 3 && server.Sizes.All(size => size == DiscordLimits.UploadMessageTargetBytes),
                    "não transmitiu três mensagens de dez anexos/200 MiB antes das confirmações");
                Console.WriteLine($"Local encryption/HTTP stream: {server.Sizes.Sum() / transferClock.Elapsed.TotalSeconds / 1048576d:F1} MiB/s (simulated receiver)");
                server.Release.TrySetResult();
                var result = await upload;
                RequireSync(result.ChunkReferences?.Count == count, "a confirmação concorrente perdeu anexos");
                RequireSync(AppMemoryBudget.PeakCommittedBytes <= AppMemoryBudget.LimitBytes, "upload passou de 1 GB");
                RequireSync(directory.GetFiles().Length == 1, "foram gravados chunks no SSD");
            }
            finally { timeout.Cancel(); server.Release.TrySetResult(); try { await upload; } catch (OperationCanceledException) { } }
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private static async Task<TestOutcome> StreamingUploadReplayAndSourceIntegrityAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-upload-replay-");
        try
        {
            foreach (var changeSource in new[] { false, true })
            {
                var source = new FileInfo(Path.Combine(directory.FullName, "source.bin"));
                await File.WriteAllBytesAsync(source.FullName, Enumerable.Repeat((byte)49, 64 * 1024).ToArray());
                using var server = new StreamingUploadHandler
                {
                    RetryFirst = true,
                    AfterFirst = changeSource ? () => File.WriteAllBytes(source.FullName, new byte[64 * 1024]) : null
                };
                using var api = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(server, false));
                using var memory = new UploadMemoryCoordinator(4);
                var crypto = CreateEncryptionService(out _);
                var context = new EncryptedPayloadContext("replay", crypto.CreateFileEncryption(EncryptionModes.Standard),
                    EncryptedPayloadRoles.Original, 0, 2);
                var upload = new ForumUploader(api, "files").UploadResumableGeneratedAsync("source.bin", 2,
                    index => memory.PreparePartAsync(crypto, source, source.Length, 32 * 1024, context.WithChunkIndex(index),
                        default, deferBuffers: true), "thread", "", new Dictionary<int, string>(),
                    maxParallelParts: 4, encryptedPayload: context, useAttachmentReferences: true,
                    batchPreparationGate: memory.BatchPreparationGate, streamPreparedParts: true,
                    maxPreparedPartBytes: 32 * 1024 + EncryptionService.EnvelopeOverheadBytes);
                try
                {
                    var result = await upload.WaitAsync(TimeSpan.FromSeconds(5));
                    RequireSync(!changeSource && result.ChunkReferences?.Count == 2 && server.Digests.Count == 2 &&
                        server.Digests[0].SequenceEqual(server.Digests[1]), "o retry não repetiu os mesmos bytes cifrados");
                }
                catch (InvalidOperationException) when (changeSource) { }
                RequireSync(directory.GetFiles().Length == 1, "retry criou ciphertext no disco");
            }
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private sealed class StreamingUploadHandler : HttpMessageHandler
    {
        private int _messages, _bodies;
        internal bool HoldReplies, RetryFirst;
        internal Action? AfterFirst;
        internal List<long> Sizes = [];
        internal List<string[]> Digests = [];
        internal TaskCompletionSource ThreeBodies = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method != HttpMethod.Post) return Reply(new { id = "thread" });
            var message = Interlocked.Increment(ref _messages).ToString();
            var attachments = new List<object>();
            var hashes = new List<string>();
            long size = 0;
            foreach (var part in ((MultipartFormDataContent)request.Content!).Where(part => part.Headers.ContentDisposition?.FileName != null))
            {
                using var output = new DigestStream();
                await part.CopyToAsync(output, token);
                var name = part.Headers.ContentDisposition!.FileName!.Trim('"');
                RequireSync(output.Bytes == part.Headers.ContentLength, "corpo do anexo incompleto");
                size += output.Bytes;
                hashes.Add(output.Digest());
                attachments.Add(new { id = message + "-" + attachments.Count, filename = name, size = output.Bytes });
            }
            lock (Sizes) { Sizes.Add(size); Digests.Add(hashes.ToArray()); }
            var bodies = Interlocked.Increment(ref _bodies);
            if (bodies == 3) ThreeBodies.TrySetResult();
            if (RetryFirst && bodies == 1)
            {
                AfterFirst?.Invoke();
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
            }
            if (HoldReplies) await Release.Task.WaitAsync(token);
            return Reply(new { id = message, attachments });
        }

        private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }

    private sealed class DigestStream : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal long Bytes;
        internal string Digest() => Convert.ToHexString(_hash.GetHashAndReset());
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { _hash.AppendData(buffer, offset, count); Bytes += count; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            _hash.AppendData(buffer.Span); Bytes += buffer.Length;
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
    }
}
