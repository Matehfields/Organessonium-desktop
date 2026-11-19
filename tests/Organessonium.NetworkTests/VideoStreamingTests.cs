using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static Task<TestOutcome> VideoVariableChunkMappingAsync()
    {
        const int mib = 1024 * 1024;
        var modern = new VideoChunkIndex([20L * mib, 20L * mib, 12L * mib]);
        var legacy = new VideoChunkIndex([10L * mib, 10L * mib, 7L * mib]);
        var mixed = new VideoChunkIndex([10L * mib, 20L * mib, 3L * mib]);
        var second = modern.Find(25L * mib);
        var invalid = false;
        try { _ = new VideoChunkIndex([10L, 11L], 22); } catch (InvalidDataException) { invalid = true; }
        return Task.FromResult(modern.Find(0).Index == 0 && second.Index == 1 && 25L * mib - second.Start == 5L * mib &&
            legacy.Find(20L * mib).Index == 2 && mixed.Find(25L * mib).Index == 1 && modern.Find(modern.Length - 1).Length == 12 * mib && invalid
            ? TestOutcome.Pass() : TestOutcome.Fail("mapeamento de chunks antigos/novos incorreto"));
    }

    private static async Task<TestOutcome> VideoVirtualResourceRangesAsync()
    {
        await using var video = new VideoStreamSession(new PatternChunks([8, 13, 4]), 0);
        using var registry = new PreviewResourceRegistry();
        var url = registry.RegisterVideo(video, "video/mp4");
        var response = await registry.OpenAsync(url, "GET", "bytes=6-11");
        using var content = response.Content!;
        var bytes = new byte[6];
        await content.ReadExactlyAsync(bytes);
        if (registry.BufferedResponseBytes != 0) return TestOutcome.Fail("resposta consumida reteve RAM");
        var full = await registry.OpenAsync(url, "GET", null);
        full.Content?.Dispose();
        var suffix = await registry.OpenAsync(url, "HEAD", "bytes=-3");
        var invalid = await registry.OpenAsync(url, "GET", "bytes=25-");
        var multi = await registry.OpenAsync(url, "GET", "bytes=0-1,4-5");
        var outside = await registry.OpenAsync(url.Replace("media.organessonium.local", "evil.invalid"), "GET", null);
        var held = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => registry.OpenAsync(url, "GET", $"bytes={i}-{i}")));
        var waiting = registry.OpenAsync(url, "GET", "bytes=5-5");
        await Task.Delay(30);
        if (waiting.IsCompleted || registry.BufferedResponseBytes < 4 || registry.BufferedResponseBytes > 4L * PreviewResourceRegistry.VideoResponseBytes)
            return TestOutcome.Fail("respostas paralelas ultrapassaram o limite de RAM");
        registry.RemoveUrl(url);
        try { await waiting; return TestOutcome.Fail("fechar não cancelou resposta pendente"); }
        catch (OperationCanceledException) { }
        foreach (var heldResponse in held) heldResponse.Content?.Dispose();
        if (registry.BufferedResponseBytes != 0) return TestOutcome.Fail("recurso fechado reteve respostas em RAM");
        var closed = await registry.OpenAsync(url, "GET", null);
        return response.Status == 206 && response.Headers.Contains("bytes 6-11/25") && response.Headers.Contains("Content-Length: 6") &&
            bytes.SequenceEqual(Enumerable.Range(6, 6).Select(i => (byte)i)) && full.Status == 206 && full.Headers.Contains("bytes 0-24/25") &&
            suffix.Headers.Contains("bytes 22-24/25") && suffix.Content == null && invalid.Status == 416 && multi.Status == 416 &&
            outside.Status == 404 && closed.Status == 404
            ? TestOutcome.Pass() : TestOutcome.Fail("HTTP Range, fronteira, suffix, revogação ou isolamento do endereço virtual incorreto");
    }

    private static async Task<TestOutcome> VideoResponseBuffersBoundedAsync()
    {
        await using var video = new VideoStreamSession(new PatternChunks([4 * 1024 * 1024]), 0);
        using var registry = new PreviewResourceRegistry();
        var url = registry.RegisterVideo(video, "video/mp4");
        var destination = new byte[64 * 1024];
        await video.WarmUpAsync();
        var warmup = await registry.OpenAsync(url, "GET", "bytes=0-");
        warmup.Content!.Dispose();
        var before = GC.GetTotalAllocatedBytes(true);
        for (var index = 0; index < 512; index++)
        {
            var start = index % 7 * 128L * 1024;
            var response = await registry.OpenAsync(url, "GET", $"bytes={start}-");
            using var content = response.Content!;
            var copied = 0;
            int read;
            while ((read = await content.ReadAsync(destination)) > 0)
            {
                if (destination[0] != (byte)((start + copied) % 256) || destination[read - 1] != (byte)((start + copied + read - 1) % 256))
                    return TestOutcome.Fail("a reutilização do buffer alterou os bytes entregues ao player");
                copied += read;
            }
            if (copied != PreviewResourceRegistry.VideoResponseBytes || registry.BufferedResponseBytes != 0)
                return TestOutcome.Fail("a faixa expôs bytes excedentes ou reteve a resposta após EOF");
        }
        var allocated = GC.GetTotalAllocatedBytes(true) - before;
        Console.WriteLine("METRIC video responseBytes=" + 512L * PreviewResourceRegistry.VideoResponseBytes + " allocatedBytes=" + allocated);
        return allocated < 32L * 1024 * 1024
            ? TestOutcome.Pass() : TestOutcome.Fail("256 MiB de reprodução ainda geram buffers descartáveis: " + allocated);
    }

    private static async Task<TestOutcome> VideoMemoryAdmissionAndCloseAsync()
    {
        AppMemoryBudget.Initialize();
        var crypto = CreateEncryptionService(out _);
        var source = new byte[DiscordLimits.PlaintextChunkBytes];
        source.AsSpan().Fill(73);
        var context = new EncryptedPayloadContext("memory-admission", crypto.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Original, 0, 1);
        var payload = crypto.EncryptBytes(source, context);
        var requests = 0;
        using var handler = new VideoHttpHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        });
        using var discord = new DiscordApiClient("memory-fixture", new HttpClient(handler, false));
        using var provider = new DiscordVideoChunkProvider(discord, "fixture-thread",
            [new VideoAttachment(0, "fixture-message", "chunk.bin", "https://cdn.invalid/chunk", payload.Length)],
            crypto, context, source.Length, new HttpClient(handler, false));
        GC.Collect();
        var pressureBytes = AppMemoryBudget.AdmissionBytes - AppMemoryBudget.CommittedBytes() -
            2L * DiscordLimits.AttachmentBytes - 8L * 1024 * 1024;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var pressure = await RamUploadBudget.ReserveExternalBytesAsync(pressureBytes, 0, timeout.Token);
        using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            var waiting = provider.DownloadAsync(0, cancelled.Token);
            await Task.Delay(300, timeout.Token);
            RequireSync(!waiting.IsCompleted && requests == 0, "o vídeo iniciou CDN/alocação sem espaço no orçamento global");
            cancelled.Cancel();
            try { await waiting; return TestOutcome.Fail("cancelar não interrompeu a espera por RAM"); }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        }
        pressure.Dispose();
        var restored = await provider.DownloadAsync(0, timeout.Token);
        RequireSync(restored.AsSpan().SequenceEqual(source) && requests == 1, "o vídeo não retomou bytes autenticados após liberar RAM");
        provider.Dispose();
        try { await provider.DownloadAsync(0, timeout.Token); return TestOutcome.Fail("a sessão fechada voltou a baixar chunks"); }
        catch (ObjectDisposedException) { }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> VideoSeekAndDeduplicationAsync()
    {
        var provider = new PatternChunks(Enumerable.Repeat(1024, 20).ToArray()) { DelayMilliseconds = 80 };
        await using var video = new VideoStreamSession(provider, 0);
        await video.WarmUpAsync();
        var first = new byte[32];
        var duplicate = new byte[32];
        await Task.WhenAll(video.ReadAsync(18 * 1024 + 5, first).AsTask(), video.ReadAsync(18 * 1024 + 5, duplicate).AsTask());
        await video.ReadAsync(18 * 1024 + 5, duplicate);
        return first.SequenceEqual(duplicate) && provider.Starts.GetValueOrDefault(0) == 1 &&
            provider.Starts.GetValueOrDefault(18) == 1 && provider.Starts.Count == 2
            ? TestOutcome.Pass() : TestOutcome.Fail("seek baixou partes intermediárias ou houve download duplicado");
    }

    private static async Task<TestOutcome> VideoPrefetchAndReleaseAsync()
    {
        var zero = new PatternChunks(Enumerable.Repeat(1024, 12).ToArray());
        await using (var noPrefetch = new VideoStreamSession(zero, 0))
        {
            await noPrefetch.WarmUpAsync();
            noPrefetch.SetPlaying(true);
            await noPrefetch.ReadAsync(0, new byte[16]);
            await Task.Delay(80);
            if (zero.Starts.Count != 1) return TestOutcome.Fail("prefetch zero fez antecipação");
        }
        var provider = new PatternChunks(Enumerable.Repeat(1024, 12).ToArray()) { BlockIndex = 1 };
        var video = new VideoStreamSession(provider, 3);
        await video.WarmUpAsync();
        video.SetPlaying(true);
        await WaitForVideoConditionAsync(() => provider.Starts.ContainsKey(1));
        await video.ReadAsync(10 * 1024, new byte[16]);
        await WaitForVideoConditionAsync(() => provider.Cancelled.Contains(1));
        await video.DisposeAsync();
        if (video.CachedBytes != 0 || video.State != "Closed" || !provider.Disposed) return TestOutcome.Fail("fechar não liberou a sessão");
        var bounded = new PatternChunks(Enumerable.Repeat(1024, 20).ToArray());
        await using var buffered = new VideoStreamSession(bounded, 3);
        await buffered.WarmUpAsync();
        buffered.SetPlaying(true);
        await WaitForVideoConditionAsync(() => bounded.Starts.Count == 4);
        if (buffered.CachedChunks > 4 || bounded.Starts.Keys.Any(i => i > 3)) return TestOutcome.Fail("antecipação inicial sem limite");
        for (var i = 1; i < 10; i++) await buffered.ReadAsync(i * 1024, new byte[16]);
        await Task.Delay(100);
        return buffered.CachedBytes <= 5 * 1024 && buffered.CachedChunks <= 5
            ? TestOutcome.Pass() : TestOutcome.Fail("cache cresceu além da janela de reprodução");
    }

    private static async Task<TestOutcome> VideoCloseCancelsRequiredReadAsync()
    {
        var provider = new PatternChunks([1024, 1024]) { BlockIndex = 0 };
        var video = new VideoStreamSession(provider);
        var read = video.ReadAsync(0, new byte[64]).AsTask();
        await WaitForVideoConditionAsync(() => provider.Starts.ContainsKey(0));
        await video.DisposeAsync();
        var cancelled = false;
        try { await read; } catch (OperationCanceledException) { cancelled = true; }
        return cancelled && provider.Cancelled.Contains(0) && video.CachedBytes == 0
            ? TestOutcome.Pass() : TestOutcome.Fail("leitura ou download ficaram ativos após o fechamento");
    }

    private static Task<TestOutcome> VideoPlaybackPersistenceAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium-progress-test-");
        try
        {
            var path = Path.Combine(root.FullName, "progress.db");
            using var db = new SqliteConnection("Data Source=" + path);
            db.Open();
            using (var schema = db.CreateCommand())
            {
                schema.CommandText = """
                    CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
                    CREATE TABLE writes(n INTEGER);
                    INSERT INTO writes VALUES(0);
                    CREATE TRIGGER inserted AFTER INSERT ON settings BEGIN UPDATE writes SET n=n+1; END;
                    CREATE TRIGGER changed AFTER UPDATE ON settings BEGIN UPDATE writes SET n=n+1; END;
                    """;
                schema.ExecuteNonQuery();
            }
            var now = DateTimeOffset.UtcNow;
            var store = new VideoPlaybackProgressStore(path);
            var tracker = new VideoPlaybackTracker(store, () => now);
            for (var i = 0; i < 50; i++) tracker.Update("file-a", i, 1000, false);
            using var count = db.CreateCommand();
            count.CommandText = "SELECT n FROM writes";
            if ((long)count.ExecuteScalar()! != 1) return Task.FromResult(TestOutcome.Fail("timeupdate gravou a cada evento"));
            tracker.Update("file-a", 120, 1000, true);
            tracker.FlushAll();
            var reopened = new VideoPlaybackProgressStore(path).Load("file-a");
            tracker.Update("file-b", 990, 1000, true);
            var done = store.Load("file-b");
            tracker.Delete("file-a");
            tracker.Suspend();
            tracker.Update("file-a", 150, 1000, true);
            if (store.Load("file-a") != null) return Task.FromResult(TestOutcome.Fail("logout permitiu gravação tardia"));
            tracker.Resume();
            tracker.Update("file-c", 120, 1000, true);
            return Task.FromResult(reopened?.ResumeAt(1000) == 120 && reopened.ResumeAt(1200) == 0 &&
                done?.Completed == true && done.ResumeAt(1000) == 0 && store.Load("file-a") == null && store.Load("file-c")?.ResumeAt() == 120
                ? TestOutcome.Pass() : TestOutcome.Fail("retomada, conclusão, mudança de duração ou exclusão incorreta"));
        }
        finally { SqliteConnection.ClearAllPools(); root.Delete(true); }
    }

    private static async Task<TestOutcome> VideoEncryptedChunksAndRenewalAsync()
    {
        var crypto = CreateEncryptionService(out _);
        var encryption = crypto.CreateFileEncryption(EncryptionModes.Standard);
        var source = Enumerable.Range(0, 31_000).Select(i => (byte)(i % 251)).ToArray();
        var lengths = new[] { 10_000, 14_000, 7_000 };
        var context = new EncryptedPayloadContext("stable-file", encryption, EncryptedPayloadRoles.Original, 0, lengths.Length);
        var payloads = new byte[lengths.Length][];
        var attachments = new List<VideoAttachment>();
        var offset = 0;
        for (var i = 0; i < lengths.Length; i++)
        {
            payloads[i] = crypto.EncryptBytes(source.AsSpan(offset, lengths[i]), context.WithChunkIndex(i));
            attachments.Add(new VideoAttachment(i, (100 + i).ToString(), "chunk-" + i + ".bin", "https://cdn.invalid/expired/" + i, payloads[i].Length));
            offset += lengths[i];
        }
        var renewals = 0;
        using var handler = new VideoHttpHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "discord.com")
            {
                renewals++;
                var i = int.Parse(request.RequestUri.Segments.Last()) - 100;
                return Task.FromResult(VideoJsonResponse(JsonSerializer.Serialize(new { id = (100 + i).ToString(), content = "", attachments = new[] { new { filename = attachments[i].Name, size = payloads[i].Length, url = "https://cdn.invalid/renewed/" + i } } })));
            }
            if (request.RequestUri.AbsolutePath.Contains("expired")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var index = int.Parse(request.RequestUri.Segments.Last());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payloads[index]) });
        });
        using var discord = new DiscordApiClient("test-" + Guid.NewGuid(), new HttpClient(handler, false));
        await using var video = new VideoStreamSession(new DiscordVideoChunkProvider(discord, "123", attachments, crypto, context, source.Length, new HttpClient(handler, false)), 0);
        var read = new byte[16_000];
        await video.ReadAsync(9000, read);
        return read.SequenceEqual(source.AsSpan(9000, read.Length).ToArray()) && renewals == 3 && video.Length == source.Length
            ? TestOutcome.Pass() : TestOutcome.Fail("envelope antigo, autenticação, fronteira ou renovação de URL incorreta");
    }

    private static async Task<TestOutcome> VideoTamperedPayloadNotExposedAsync()
    {
        var crypto = CreateEncryptionService(out _);
        var metadata = crypto.CreateFileEncryption(EncryptionModes.Standard);
        var context = new EncryptedPayloadContext("tampered-file", metadata, EncryptedPayloadRoles.Original, 0, 1);
        var data = crypto.EncryptBytes(new byte[64], context);
        data[^1] ^= 1;
        using var handler = new VideoHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }));
        using var discord = new DiscordApiClient("test-" + Guid.NewGuid(), new HttpClient(handler, false));
        await using var video = new VideoStreamSession(new DiscordVideoChunkProvider(discord, "123",
            [new VideoAttachment(0, "456", "chunk.bin", "https://cdn.invalid/data", data.Length)], crypto, context, 64, new HttpClient(handler, false)), 0);
        var destination = Enumerable.Repeat((byte)71, 64).ToArray();
        try { await video.ReadAsync(0, destination); return TestOutcome.Fail("bytes sem autenticação chegaram ao player"); }
        catch (IOException)
        {
            return destination.All(b => b == 71) && video.CachedBytes == 0 ? TestOutcome.Pass() : TestOutcome.Fail("payload inválido entrou no cache");
        }
    }

    private static async Task<TestOutcome> VideoStreamingAndFullDownloadIndependentAsync()
    {
        foreach (var encrypted in new[] { false, true })
        {
            var root = Directory.CreateTempSubdirectory("organessonium-stream-download-test-");
            try
            {
                var source = Enumerable.Range(0, 90_000).Select(i => (byte)(i % 251)).ToArray();
                var crypto = CreateEncryptionService(out _);
                var metadata = encrypted ? crypto.CreateFileEncryption(EncryptionModes.Standard) : null;
                var context = metadata == null ? null : new EncryptedPayloadContext("download-file", metadata, EncryptedPayloadRoles.Original, 0, 3);
                var lengths = new[] { 10_000, 60_000, 20_000 };
                var payloads = new List<byte[]>();
                var offset = 0;
                for (var i = 0; i < lengths.Length; i++)
                {
                    var slice = source.AsSpan(offset, lengths[i]);
                    payloads.Add(context == null ? slice.ToArray() : crypto.EncryptBytes(slice, context.WithChunkIndex(i)));
                    offset += lengths[i];
                }
                var messages = payloads.Select((data, i) => new { id = (100 + i).ToString(), content = "", attachments = new[] { new { filename = "chunk-" + i, size = data.Length, url = "https://cdn.invalid/" + i } } }).ToArray();
                using var handler = new VideoHttpHandler(async (request, token) =>
                {
                    if (request.RequestUri!.Host == "discord.com")
                    {
                        var segments = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (segments.Length >= 2 && segments[^2] == "messages")
                        {
                            var requested = messages.FirstOrDefault(message => message.id == segments[^1]);
                            if (requested != null) return VideoJsonResponse(JsonSerializer.Serialize(requested));
                        }
                        return VideoJsonResponse(JsonSerializer.Serialize(messages));
                    }
                    await Task.Delay(75, token);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payloads[int.Parse(request.RequestUri.Segments.Last())]) };
                });
                using var discord = new DiscordApiClient("test-" + Guid.NewGuid(), new HttpClient(handler, false));
                var downloader = new ForumDownloader(discord, new AppSettings { DownloadThreads = 2 }, crypto, () => new HttpClient(handler, false));
                var entry = new AppEntry { FileId = "download-file", ThreadId = "123", Name = "video.mp4", SizeBytes = source.Length,
                    ChunkMessageIds = ["100", "101", "102"], Encryption = metadata };
                var provider = await downloader.OpenVideoChunksAsync(entry, false);
                var video = new VideoStreamSession(provider, 0);
                await video.WarmUpAsync();
                var full = downloader.DownloadAndAssembleAsync(entry, root, _ => { });
                await video.ReadAsync(85_000, new byte[16]);
                await video.DisposeAsync();
                var output = await full;
                if (!(await File.ReadAllBytesAsync(output.FullName)).SequenceEqual(source)) return TestOutcome.Fail("download completo perdeu partes após fechar o player");
                await using var second = new VideoStreamSession(await downloader.OpenVideoChunksAsync(entry, false), 0);
                using var cancelled = new CancellationTokenSource();
                var downloadCancelled = downloader.DownloadAndAssembleAsync(entry, root, _ => { }, cancelled.Token);
                cancelled.Cancel();
                try { await downloadCancelled; } catch (OperationCanceledException) { }
                var buffer = new byte[256];
                await second.ReadAsync(15_000, buffer);
                if (!buffer.SequenceEqual(source.AsSpan(15_000, buffer.Length).ToArray())) return TestOutcome.Fail("cancelar download cancelou o player");
            }
            finally { root.Delete(true); }
        }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> Mp4FastStartOffsetsAndFallbackAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium-faststart-test-");
        try
        {
            foreach (var co64 in new[] { false, true })
            foreach (var extended in new[] { false, true })
            {
                var ftyp = Mp4Box("ftyp", "isom0000"u8.ToArray());
                var sample = Enumerable.Range(0, 48).Select(i => (byte)i).ToArray();
                var mdat = Mp4Box("mdat", sample, extended);
                var table = new byte[co64 ? 16 : 12];
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4, 4), 1);
                var oldOffset = ftyp.Length + (extended ? 16 : 8);
                if (co64) BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(8), (ulong)oldOffset);
                else BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8), (uint)oldOffset);
                var moov = Mp4Box("moov", Mp4Box("trak", Mp4Box("mdia", Mp4Box("minf", Mp4Box("stbl", Mp4Box(co64 ? "co64" : "stco", table))))), extended);
                var originalBytes = ftyp.Concat(mdat).Concat(moov).ToArray();
                var path = Path.Combine(root.FullName, "fixture.mp4");
                await File.WriteAllBytesAsync(path, originalBytes);
                using var prepared = await Mp4FastStart.PrepareAsync(new FileInfo(path));
                if (!prepared.Applied) return TestOutcome.Fail("MP4 compatível não recebeu faststart");
                using var remuxed = new MemoryStream();
                await using (var input = prepared.OpenRead()) await input.CopyToAsync(remuxed);
                var result = remuxed.ToArray();
                var expectedOffset = oldOffset + moov.Length;
                var tablePos = FindAsciiBytes(result, co64 ? "co64" : "stco") + 12;
                var actual = co64 ? BinaryPrimitives.ReadUInt64BigEndian(result.AsSpan(tablePos, 8)) : BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan(tablePos, 4));
                if (actual != (ulong)expectedOffset || !result.AsSpan(expectedOffset, sample.Length).SequenceEqual(sample) || result.Length != originalBytes.Length)
                    return TestOutcome.Fail("stco/co64 ou mídia alterados pelo remux");
                using var ram = new RamMediaPreview(result.ToArray());
                using var already = await Mp4FastStart.PrepareAsync(ram);
                if (already.Applied || already.Length != result.Length) return TestOutcome.Fail("MP4 faststart foi reprocessado");
                var crypto = CreateEncryptionService(out _);
                const int chunkBytes = 31; // Includes ranges that cross the relocated moov and mdat boundaries.
                var count = (result.Length + chunkBytes - 1) / chunkBytes;
                var checksums = await FileChecksums.CalculateAsync(prepared.OpenRead, prepared.Length, chunkBytes, default);
                RequireSync(checksums.Sha256 == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(result)) &&
                    checksums.Sha256 != Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(originalBytes)),
                    "o checksum MP4 foi calculado sobre o arquivo físico em vez da representação transmitida");
                var context = new EncryptedPayloadContext("virtual-mp4", crypto.CreateFileEncryption(EncryptionModes.Standard),
                    EncryptedPayloadRoles.Original, 0, count) { Checksums = checksums };
                using var memory = new UploadMemoryCoordinator(4);
                var chunks = await Task.WhenAll(Enumerable.Range(0, count).Select(async index =>
                {
                    using var part = await memory.PreparePartAsync(crypto, prepared.File, prepared.Length,
                        chunkBytes, context.WithChunkIndex(index), default, openSource: prepared.OpenRead);
                    using var ciphertext = new MemoryStream();
                    await using (var input = part.OpenRead()) await input.CopyToAsync(ciphertext);
                    return crypto.DecryptBytes(ciphertext.ToArray(), context.WithChunkIndex(index));
                }));
                if (!chunks.SelectMany(chunk => chunk).SequenceEqual(result) || prepared.File.FullName != path ||
                    root.EnumerateFiles().Count() != 1) return TestOutcome.Fail("o upload virtual perdeu bytes ou criou cópia em disco");
                using var legacy = await UploadMemoryCoordinator.PreparePlainPartAsync(prepared.File, prepared.Length,
                    chunkBytes, 0, default, prepared.OpenRead);
                using var legacyBytes = new MemoryStream();
                await using (var input = legacy.OpenRead()) await input.CopyToAsync(legacyBytes);
                if (legacy.FileName != "fixture.mp4.001" || !legacyBytes.ToArray().SequenceEqual(result.Take(chunkBytes)))
                    return TestOutcome.Fail("o upload plaintext legado perdeu nome ou bytes");
                var previewMessages = 0;
                var legacyMetadata = new MetadataService(MetadataCodec.ForEncryption(crypto));
                using var server = new VideoHttpHandler(async (request, token) =>
                {
                    if (request.Method != HttpMethod.Post) return VideoJsonResponse("{\"id\":\"thread\"}");
                    var multipart = (MultipartFormDataContent)request.Content!;
                    using var json = JsonDocument.Parse(await multipart.First(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "payload_json").ReadAsStringAsync(token));
                    RequireSync(legacyMetadata.DecodePayloadReference(json.RootElement.GetProperty("content").GetString()!)
                        .StartsWith("ORG_PREVIEW|video-480|", StringComparison.Ordinal), "o upload RAM perdeu o rótulo de prévia legado");
                    foreach (var content in multipart.Where(part => part.Headers.ContentDisposition?.FileName != null))
                        await content.CopyToAsync(Stream.Null, token);
                    return VideoJsonResponse("{\"id\":\"preview-" + Interlocked.Increment(ref previewMessages) + "\"}");
                });
                using var api = new DiscordApiClient("ram-preview-test-" + Guid.NewGuid(), new HttpClient(server, false));
                await new ForumUploader(api, "files").UploadResumableGeneratedAsync("preview.mp4", count,
                    index => UploadMemoryCoordinator.PreparePlainPartAsync(prepared.File, prepared.Length, chunkBytes,
                        index, default, prepared.OpenRead), "thread", "", new Dictionary<int, string>(),
                    legacyPreviewKind: "video-480");
                RequireSync(previewMessages == count, "a prévia plaintext perdeu anexos no upload RAM");
                if (!(await File.ReadAllBytesAsync(path)).SequenceEqual(originalBytes)) return TestOutcome.Fail("arquivo original foi alterado");
            }
            var invalidPath = Path.Combine(root.FullName, "invalid.mp4");
            await File.WriteAllBytesAsync(invalidPath, [0, 0, 0, 1, 109, 111, 111, 118]);
            using var fallback = await Mp4FastStart.PrepareAsync(new FileInfo(invalidPath));
            return !fallback.Applied && fallback.File.FullName == invalidPath && new FileInfo(invalidPath).Length == 8
                ? TestOutcome.Pass() : TestOutcome.Fail("fallback perdeu o original");
        }
        finally { root.Delete(true); }
    }

    private static byte[] Mp4Box(string type, byte[] content, bool extended = false)
    {
        var header = extended ? 16 : 8;
        var bytes = new byte[header + content.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, extended ? 1U : (uint)bytes.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4);
        if (extended) BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8), (ulong)bytes.Length);
        content.CopyTo(bytes, header);
        return bytes;
    }
    private static int FindAsciiBytes(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text));
    private static HttpResponseMessage VideoJsonResponse(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static async Task WaitForVideoConditionAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class PatternChunks(int[] lengths) : IVideoChunkProvider
    {
        public VideoChunkIndex Index { get; } = new(lengths.Select(length => (long)length));
        public ConcurrentDictionary<int, int> Starts { get; } = new();
        public ConcurrentBag<int> Cancelled { get; } = [];
        public int DelayMilliseconds { get; init; }
        public int BlockIndex { get; init; } = -1;
        public bool Disposed { get; private set; }
        public async Task<byte[]> DownloadAsync(int index, CancellationToken cancellationToken)
        {
            Starts.AddOrUpdate(index, 1, (_, count) => count + 1);
            try
            {
                if (index == BlockIndex) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, cancellationToken);
                var chunk = Index.Chunks[index];
                return Enumerable.Range(0, chunk.Length).Select(i => (byte)((chunk.Start + i) % 256)).ToArray();
            }
            catch (OperationCanceledException) { Cancelled.Add(index); throw; }
        }
        public void Dispose() => Disposed = true;
    }
    private sealed class VideoHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
