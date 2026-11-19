using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static byte[] MetadataTestMaster() => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static MetadataCodec TestMetadataCodec() => new(_ => MetadataTestMaster());
    private static void MetadataCheck(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void ExpectMetadataError(Action action, MetadataError error)
    {
        try { action(); }
        catch (MetadataCodecException ex) when (ex.Error == error) { return; }
        throw new InvalidOperationException("Erro de metadados não identificado: " + error);
    }

    private static Task<TestOutcome> MetadataV1RoundTripAndErrorsAsync()
    {
        var codec = TestMetadataCodec();
        string[] samples = ["""{"name":"ação café 📷 漢字 é","null":null,"bool":false,"number":9007199254740991,"array":[1,2,{"nested":["á","🧪"]}]}""",
            JsonSerializer.Serialize(new { name = new string('ç', 9000), sync = new { relativePath = "src/Árvore/😀.md", parents = new[] { "a", "b" }, revision = 7 } }),
            """{"schemaVersion":2,"entryType":"file","fileId":"f","threadId":"t","name":"vídeo.mp4","chunks":["c1","c2"],"preview":{"video480p":{"chunks":["v1"],"sizeBytes":100}},"encryption":{"version":1,"mode":"standard","salt":"AA"}}"""];
        foreach (var sample in samples)
        {
            var encrypted = codec.Encode(sample);
            MetadataCheck(codec.IsEncrypted(encrypted) && codec.Decode(encrypted) == sample, "roundtrip UTF-8/arrays/objetos falhou");
            MetadataCheck(!encrypted.Contains("relativePath") && !encrypted.Contains("fileId") && !encrypted.Contains("ação"), "campo privado escapou do envelope");
        }
        var original = codec.Encode(samples[0]);
        var second = codec.Encode(samples[0]);
        var a = JsonNode.Parse(original)!.AsObject(); var b = JsonNode.Parse(second)!.AsObject();
        MetadataCheck(a["nonce"]!.ToString() != b["nonce"]!.ToString() && a["ciphertext"]!.ToString() != b["ciphertext"]!.ToString(), "nonce/ciphertext foram reutilizados");
        void Change(string property, JsonNode? value, MetadataError error)
        {
            var changed = a.DeepClone().AsObject(); changed[property] = value;
            ExpectMetadataError(() => codec.Decode(changed.ToJsonString()), error);
        }
        void Flip(string property)
        {
            var bytes = Convert.FromBase64String(a[property]!.ToString()); bytes[0] ^= 1;
            Change(property, JsonValue.Create(Convert.ToBase64String(bytes)), MetadataError.AuthenticationFailed);
        }
        Flip("tag"); Flip("ciphertext"); Flip("nonce");
        Change("keyMode", JsonValue.Create("custom"), MetadataError.AuthenticationFailed);
        Change("tag", JsonValue.Create("not base64"), MetadataError.InvalidBase64);
        Change("salt", JsonValue.Create("AA=="), MetadataError.InvalidFormat);
        Change("encrypted", JsonValue.Create(false), MetadataError.InvalidFormat);
        Change("algorithm", JsonValue.Create("AES-CBC"), MetadataError.InvalidFormat);
        Change("version", JsonValue.Create(42), MetadataError.UnsupportedVersion);
        Change("version", JsonValue.Create("1"), MetadataError.InvalidFormat);
        var incomplete = a.DeepClone().AsObject(); incomplete.Remove("tag");
        ExpectMetadataError(() => codec.Decode(incomplete.ToJsonString()), MetadataError.MissingField);
        var wrong = new MetadataCodec(_ => Enumerable.Repeat((byte)255, 32).ToArray());
        ExpectMetadataError(() => wrong.Decode(original), MetadataError.KeyMismatch);
        var unavailable = new MetadataCodec(_ => throw new CustomEncryptionKeyUnavailableException("test"));
        ExpectMetadataError(() => unavailable.Decode(original), MetadataError.KeyUnavailable);
        var badJson = new EncryptedMetadataCodecV1(_ => MetadataTestMaster()).Encrypt("not-json"u8,
            "standard", RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(12));
        ExpectMetadataError(() => codec.Decode(badJson), MetadataError.InvalidPlaintextJson);
        ExpectMetadataError(() => codec.Decode("{\"format\":\"organessonium-metadata\",\"version\":2}"), MetadataError.UnsupportedVersion);
        ExpectMetadataError(() => codec.Decode("{\"encrypted\":true,\"schemaVersion\":2}"), MetadataError.InvalidFormat);
        ExpectMetadataError(() => codec.Encode("not-json"), MetadataError.InvalidPlaintextJson);
        return Task.FromResult(TestOutcome.Pass());
    }

    private static async Task<TestOutcome> MetadataLegacyAndEncryptedWritesAsync()
    {
        var codec = TestMetadataCodec(); var service = new MetadataService(codec);
        const string legacy = """{"schemaVersion":2,"entryType":"folder","folderId":"legacy","name":"Fotos antigas","createdAt":"2026-01-01T00:00:00Z","parentFolderId":null,"fav":true}""";
        MetadataCheck(codec.Decode(legacy) == legacy && !codec.IsEncrypted(legacy), "detector legado falhou");
        var restored = await service.ReadEnvelopeAsync(new DiscordMessageDto { Id = "legacy-message", Content = legacy });
        MetadataCheck(restored?.Entry is FolderMetadata { Name: "Fotos antigas", Favorite: true }, "leitura automática antiga falhou");
        var entry = AppEntry.Folder("Fotos privadas 📷", "/"); entry.Favorite = true;
        var small = await service.CreateWritePayloadAsync(entry);
        MetadataCheck(small.Attachment == null && codec.IsEncrypted(small.Content) && !small.Content.Contains(entry.Name), "escrita inline não foi criptografada");
        MetadataCheck((await service.ReadEnvelopeAsync(new DiscordMessageDto { Id = "small", Content = small.Content }))?.Entry is FolderMetadata { Favorite: true }, "leitura criptografada não materializou pasta");
        entry.Name = new string('á', 9000);
        var large = await service.CreateWritePayloadAsync(entry);
        try
        {
            MetadataCheck(large.Attachment != null, "metadata grande não usou anexo");
            var stored = await File.ReadAllTextAsync(large.Attachment!.FullName);
            MetadataCheck(codec.IsEncrypted(stored) && !stored.Contains(entry.Name), "temporário continha plaintext");
            var reader = new MetadataService(codec, (_, _) => File.ReadAllBytesAsync(large.Attachment.FullName));
            var message = new DiscordMessageDto { Id = "large", Content = "metadata", Attachments = [new() { FileName = "metadata.json", Size = large.Attachment.Length }] };
            MetadataCheck((await reader.ReadEnvelopeAsync(message))?.Entry is FolderMetadata folder && folder.Name == entry.Name, "anexo criptografado não foi recuperado");
        }
        finally { large.Attachment?.Directory?.Delete(true); }
        var missing = codec.Encode("""{"schemaVersion":2,"entryType":"file","name":"missing"}""");
        try { await service.ReadEnvelopeAsync(new DiscordMessageDto { Id = "bad", Content = missing }); }
        catch (MetadataCodecException ex) when (ex.Error == MetadataError.MissingField) { return TestOutcome.Pass(); }
        return TestOutcome.Fail("campo obrigatório ausente foi tratado como metadata vazio");
    }

    private static async Task<string> ReferenceMetadataAsync(object request)
    {
        var info = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "metadata-v1-interop.cjs"));
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Reference encoder unavailable");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request)); process.StandardInput.Close();
        var result = await process.StandardOutput.ReadToEndAsync(); await process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        MetadataCheck(process.ExitCode == 0, "Reference metadata codec failed");
        return result;
    }

    private static async Task<TestOutcome> MetadataV1InteroperabilityAsync()
    {
        using var vector = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "metadata-v1-vector.json")));
        var v = vector.RootElement; var json = v.GetProperty("plaintext").GetString()!;
        var expected = v.GetProperty("envelope").GetRawText();
        var encoded = new EncryptedMetadataCodecV1(_ => MetadataTestMaster()).Encrypt(Encoding.UTF8.GetBytes(json), "standard",
            Convert.FromHexString(v.GetProperty("saltHex").GetString()!), Convert.FromHexString(v.GetProperty("nonceHex").GetString()!));
        MetadataCheck(JsonNode.DeepEquals(JsonNode.Parse(encoded), JsonNode.Parse(expected)), "vetor independente divergiu do C#");
        var codec = TestMetadataCodec(); MetadataCheck(codec.Decode(expected) == json, "vetor não descriptografou");
        var random = codec.Encode(json);
        var fromCs = await ReferenceMetadataAsync(new { operation = "decrypt", masterHex = v.GetProperty("masterHex").GetString(), envelope = JsonNode.Parse(random) });
        MetadataCheck(JsonSerializer.Deserialize<string>(fromCs) == json, "Node não leu C#");
        var fromNode = await ReferenceMetadataAsync(new { operation = "encrypt", masterHex = v.GetProperty("masterHex").GetString(), plaintext = json, keyMode = "custom" });
        MetadataCheck(codec.Decode(fromNode) == json, "C# não leu Node");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> MetadataEncryptedSerialCacheAsync()
    {
        var temporary = Directory.CreateTempSubdirectory("organessonium-metadata-cache-");
        try
        {
            var store = new SettingsStore(temporary); var settings = store.Load(); settings.SerialChannelId = ""; store.Save(settings);
            var codec = new FolderSerialCodec();
            var serial = new FolderSerial { FolderId = "private-root", ParentFolderId = "root", FolderName = "Pasta secreta", GeneratedAt = DateTimeOffset.UtcNow,
                Children = [new() { Id = "private-id", Name = "café.jpg", ThreadId = "thread", UploadedAt = DateTimeOffset.UtcNow }],
                Aggregate = new() { TotalItems = 1, FileCount = 1 } };
            var cacheName = "serial_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial.FolderId))).ToLowerInvariant() + ".bin";
            var cacheFile = Path.Combine(store.SerialCacheDirectory.FullName, cacheName);
            await File.WriteAllBytesAsync(cacheFile, codec.EncodeFolder(serial, 3));
            using var server = new SyncDiscordFixture();
            using var api = new DiscordApiClient("isolated-test-" + Guid.NewGuid().ToString("N"), new HttpClient(server, false));
            var old = new SerialIndexService(api, store, settings);
            MetadataCheck((await old.LoadCachedFolderAsync(serial.FolderId))?.FolderName == serial.FolderName, "cache OSER anterior não abriu");
            await old.SaveFolderAsync(serial, new(serial.FolderId, "thread", ""));
            var bytes = await File.ReadAllBytesAsync(cacheFile);
            MetadataCheck(!bytes.AsSpan().StartsWith("OSER"u8) && !Encoding.UTF8.GetString(bytes).Contains(serial.FolderName), "cache serial novo ficou em plaintext");
            var reopened = new SerialIndexService(api, store, settings);
            MetadataCheck((await reopened.LoadCachedFolderAsync(serial.FolderId))?.Children.Single().Name == "café.jpg", "cache criptografado não persistiu entre instâncias");
            settings.SerialChannelId = "serial"; store.Save(settings);
            var favorites = new FolderSerial { FolderId = "favorites", ParentFolderId = "root", FolderName = "favorites", Kind = FolderSerialKind.Favorites,
                GeneratedAt = DateTimeOffset.UtcNow, Aggregate = new() { TotalItems = 1, FileCount = 1, TotalBytes = 42 },
                Children = [new() { Id = "fav-file", Name = "privado.jpg", ThreadId = "payload", UploadedAt = DateTimeOffset.UtcNow, SizeBytes = 42 }] };
            var publisher = new SerialIndexService(api, store, settings);
            var first = await publisher.SaveStandaloneSerialAsync(favorites);
            var published = server.PublishedContents.Count;
            var same = await publisher.SaveStandaloneSerialAsync(favorites);
            MetadataCheck(first == same && server.PublishedContents.Count == published, "serial v3 inalterado foi republicado por tamanho omitido/nonce novo");
            var provider = new CustomEncryptionKeyProvider(store); provider.ConfigureFromPassphrase("isolated metadata test passphrase");
            settings.EncryptionMode = EncryptionModes.Custom; store.Save(settings);
            var custom = MetadataCodec.ForStore(store);
            var encrypted = custom.Encode("""{"schemaVersion":2,"entryType":"folder","name":"custom"}""");
            MetadataCheck(custom.Decode(encrypted).Contains("custom"), "integração com chave personalizada falhou");
            return TestOutcome.Pass();
        }
        finally { SqliteConnection.ClearAllPools(); try { temporary.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static async Task<TestOutcome> MetadataAtomicLegacyUpdateAsync()
    {
        using var server = new SyncDiscordFixture();
        using var api = new DiscordApiClient("metadata-atomic-test-" + Guid.NewGuid().ToString("N"), new HttpClient(server, false));
        var service = new MetadataService(MetadataCodec.Standard(), api.DownloadAttachmentBytesAsync);
        const string legacy = """{"schemaVersion":2,"entryType":"file","fileId":"legacy-file","threadId":"legacy-payload","name":"Arquivo antigo.txt","mimeType":"text/plain","sizeBytes":4,"uploadedAt":"2026-01-01T00:00:00Z","chunks":["legacy-chunk"]}""";
        var original = server.SeedLegacyMetadata(legacy);
        var envelope = await service.ReadEnvelopeAsync(original);
        var entry = service.ToAppEntry(envelope!)!;
        entry.Name = new string('á', 3500);
        server.RejectNextMetadataUpdate = true;
        var failed = false;
        try { await service.UpsertEntryAsync(api, "root-thread", entry); }
        catch (DiscordApiException ex) when ((int)ex.StatusCode == 403) { failed = true; }
        var untouched = await service.ReadEnvelopeAsync(await api.GetMessageAsync("root-thread", original.Id));
        MetadataCheck(failed && untouched?.Entry is FileMetadata { Name: "Arquivo antigo.txt" } && entry.MetadataMessageId == original.Id,
            "falha de PATCH destruiu metadata anterior ou identidade");
        await service.UpsertEntryAsync(api, "root-thread", entry);
        var replaced = await api.GetMessageAsync("root-thread", original.Id);
        MetadataCheck(replaced.Id == original.Id && replaced.Attachments.Count == 1 &&
            (await service.ReadEnvelopeAsync(replaced))?.Entry is FileMetadata updated && updated.Name == entry.Name,
            "migração por modificação não preservou a mensagem ou os dados");
        entry.Name = "Arquivo atualizado.txt";
        await service.UpsertEntryAsync(api, "root-thread", entry);
        var small = await api.GetMessageAsync("root-thread", original.Id);
        MetadataCheck(small.Attachments.Count == 0 && MetadataCodec.Standard().IsEncrypted(small.Content),
            "troca de anexo para inline deixou o metadata anterior visível");
        MetadataCheck(server.DeletedChannels.Count == 0, "atualizar metadata mexeu nos payloads");
        var temporary = Directory.CreateTempSubdirectory("organessonium-metadata-preview-");
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(1024);
            var file = new FileInfo(Path.Combine(temporary.FullName, "payload.bin"));
            await File.WriteAllBytesAsync(file.FullName, bytes);
            var uploader = new ForumUploader(api, "files");
            var uploaded = await uploader.UploadAsync("imagem privada.jpg", [file]);
            const string marker = "PREVIEW|imagem privada";
            var previewId = await uploader.UploadPreviewFileToThreadAsync(uploaded.ThreadId,
                service.EncodePayloadReference(marker), file);
            var stored = await api.GetMessageAsync(uploaded.ThreadId, previewId);
            MetadataCheck(MetadataCodec.Standard().IsEncrypted(stored.Content) && !stored.Content.Contains("imagem privada"),
                "rótulo de preview legado escapou em plaintext");
            var downloader = new ForumDownloader(api, new AppSettings(), CreateEncryptionService(out _),
                () => new HttpClient(server, false));
            var recovered = await downloader.DownloadMarkedPreviewBytesAsync(uploaded.ThreadId, "PREVIEW|");
            MetadataCheck(recovered != null && bytes.SequenceEqual(recovered),
                "preview com rótulo protegido não foi localizado ou alterou os bytes");
            await api.SendFileMessageAsync(uploaded.ThreadId, marker, file);
            var oldPreview = await downloader.DownloadMarkedPreviewBytesAsync(uploaded.ThreadId, "PREVIEW|");
            MetadataCheck(oldPreview != null && bytes.SequenceEqual(oldPreview), "preview anterior deixou de ser reconhecido");
        }
        finally { temporary.Delete(true); }
        return TestOutcome.Pass();
    }
}
