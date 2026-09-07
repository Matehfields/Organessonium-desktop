using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task<TestOutcome>> Run)> Tests = new()
    {
        ("encryptedPayloadRoundTripAndChunks", EncryptedPayloadRoundTripAndChunksAsync),
        ("encryptedPayloadMessageProtocol", EncryptedPayloadMessageProtocolAsync),
        ("encryptedPayloadTamperAndTruncation", EncryptedPayloadTamperAndTruncationAsync),
        ("encryptedPreviewAndVideoPayloads", EncryptedPreviewAndVideoPayloadsAsync),
        ("urlSafeEncryptionSaltCompatibility", UrlSafeEncryptionSaltCompatibilityAsync),
        ("customKeyExportImportAndMismatch", CustomKeyExportImportAndMismatchAsync),
        ("legacyEntryHasNoEncryptionMetadata", LegacyEntryHasNoEncryptionMetadataAsync),
        ("urlSafeEncryptedMetadataIsLoaded", UrlSafeEncryptedMetadataIsLoadedAsync),
        ("folderSerialRoundTrip", FolderSerialRoundTripAsync),
        ("serialMessageEnvelopeProtocol", SerialMessageEnvelopeProtocolAsync),
        ("httpProxyForwardsGet", HttpProxyForwardsGetAsync),
        ("httpProxyAuthAcceptAndReject", HttpProxyAuthAcceptAndRejectAsync),
        ("socks5ProxyForwardsGet", Socks5ProxyForwardsGetAsync),
        ("socks5WrongCredentialsRejected", Socks5WrongCredentialsRejectedAsync),
        ("socks4aProxyForwardsGet", Socks4aProxyForwardsGetAsync),
        ("directModeBypassesProxy", DirectModeBypassesProxyAsync),
        ("refusedProxyClassified", RefusedProxyClassifiedAsync),
        ("unreachableProxyClassified", UnreachableProxyClassifiedAsync),
        ("autoDetectChoosesHttp", AutoDetectChoosesHttpAsync),
        ("autoDetectChoosesSocks5", AutoDetectChoosesSocks5Async),
        ("discordCheckThroughProxySucceeds", DiscordCheckThroughProxySucceedsAsync),
        ("failedProbeReportedAsUnreachable", FailedProbeReportedAsUnreachableAsync)
    };

    public static async Task<int> Main()
    {
        var passed = 0;
        var failed = 0;
        foreach (var (name, run) in Tests)
        {
            try
            {
                var outcome = await run();
                if (outcome.Ok)
                {
                    Console.WriteLine("PASS  " + name);
                    passed++;
                }
                else
                {
                    Console.WriteLine("FAIL  " + name + " :: " + outcome.Message);
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR " + name + " :: " + ex.Message);
                failed++;
            }
            finally
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total: {Tests.Count}  Passou: {passed}  Falhou: {failed}");
        return failed == 0 ? 0 : 1;
    }

    private sealed record TestOutcome(bool Ok, string Message)
    {
        public static TestOutcome Pass() => new(true, "");
        public static TestOutcome Fail(string message) => new(false, message);
    }

    private static async Task<TestOutcome> EncryptedPayloadRoundTripAndChunksAsync()
    {
        var sourceDir = Directory.CreateTempSubdirectory("organessonium_crypto_test_");
        try
        {
            var inputBytes = RandomNumberGenerator.GetBytes(280_000);
            var input = new FileInfo(Path.Combine(sourceDir.FullName, "original.bin"));
            await File.WriteAllBytesAsync(input.FullName, inputBytes);
            var parts = await ArchiveSplitter.SplitAsync(input, maxPartBytes: 64 * 1024);
            var service = CreateEncryptionService(out _);
            var freshInstallService = CreateEncryptionService(out _);
            var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
            using var staged = await service.StageFilesAsync(parts, "file-roundtrip", encryption, EncryptedPayloadRoles.Original);
            if (staged.Files.Count < 2 || staged.Files.Any(part => !part.Name.StartsWith("chunk-", StringComparison.Ordinal) || !part.Name.EndsWith(".bin", StringComparison.Ordinal)))
            {
                return TestOutcome.Fail("as partes criptografadas não receberam nomes genéricos");
            }

            using var output = new MemoryStream();
            byte[]? firstPayload = null;
            foreach (var part in staged.Files.Select((file, index) => (file, index)))
            {
                var encrypted = await File.ReadAllBytesAsync(part.file.FullName);
                if (part.index == 0)
                {
                    firstPayload = encrypted.ToArray();
                }

                var plaintext = freshInstallService.DecryptBytes(
                    encrypted,
                    new EncryptedPayloadContext("file-roundtrip", encryption, EncryptedPayloadRoles.Original, part.index, staged.Files.Count));
                await output.WriteAsync(plaintext);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(encrypted);
            }

            var outputBytes = output.ToArray();
            var matches = CryptographicOperations.FixedTimeEquals(inputBytes, outputBytes);
            var hashesMatch = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(inputBytes),
                SHA256.HashData(outputBytes));
            var envelopeMatches = firstPayload != null && firstPayload.AsSpan(0, 8).SequenceEqual("ORGAESGC"u8);
            var nonceDiffers = firstPayload != null && staged.Files.Count > 1 &&
                                !(await File.ReadAllBytesAsync(staged.Files[1].FullName)).AsSpan(42, 12).SequenceEqual(firstPayload.AsSpan(42, 12));
            CryptographicOperations.ZeroMemory(firstPayload ?? []);
            CryptographicOperations.ZeroMemory(outputBytes);
            ArchiveSplitter.CleanupParts(parts, input);
            return matches && hashesMatch && envelopeMatches && nonceDiffers
                ? TestOutcome.Pass()
                : TestOutcome.Fail("a reconstrução ou os nonces dos chunks não correspondeu ao esperado");
        }
        finally
        {
            try { sourceDir.Delete(true); } catch { }
        }
    }

    private static Task<TestOutcome> EncryptedPayloadTamperAndTruncationAsync()
    {
        var service = CreateEncryptionService(out _);
        var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
        var context = new EncryptedPayloadContext("file-tamper", encryption, EncryptedPayloadRoles.Original, 0);
        var payload = service.EncryptBytes("conteúdo de teste"u8, context);
        var tampered = payload.ToArray();
        tampered[^1] ^= 0x01;
        var tamperDetected = Throws<EncryptedPayloadAuthenticationException>(() => service.DecryptBytes(tampered, context));
        var truncated = payload[..^1];
        var truncationDetected = Throws<EncryptedPayloadFormatException>(() => service.DecryptBytes(truncated, context));
        CryptographicOperations.ZeroMemory(payload);
        CryptographicOperations.ZeroMemory(tampered);
        CryptographicOperations.ZeroMemory(truncated);
        return Task.FromResult(tamperDetected && truncationDetected
            ? TestOutcome.Pass()
            : TestOutcome.Fail("alteração ou truncamento não foi rejeitado"));
    }

    private static Task<TestOutcome> EncryptedPayloadMessageProtocolAsync()
    {
        var context = new EncryptedPayloadContext(
            "file-message-protocol",
            new EncryptionMetadata { Version = 1, Mode = EncryptionModes.Standard, Salt = "NyasiAVslEKTO__DCbEeKg" },
            EncryptedPayloadRoles.Original,
            1,
            3);
        var message = EncryptedPayloadProtocol.Message(context, 3);
        var parsed = EncryptedPayloadProtocol.TryParse(message, out var payload);
        var legacyRejected = !EncryptedPayloadProtocol.TryParse(
            "OES1|file-message-protocol|original|2/3",
            out _);
        var matches = parsed && legacyRejected &&
                      payload.FileId == context.FileId &&
                      payload.Role == context.Role &&
                      payload.ChunkIndex == context.ChunkIndex &&
                      payload.TotalParts == context.ChunkCount;
        return Task.FromResult(matches
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o formato de mensagem criptografada não corresponde ao padrão do servidor"));
    }

    private static Task<TestOutcome> EncryptedPreviewAndVideoPayloadsAsync()
    {
        var service = CreateEncryptionService(out _);
        var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
        var preview = RandomNumberGenerator.GetBytes(1_024);
        var video = RandomNumberGenerator.GetBytes(4_096);
        var previewPayload = service.EncryptBytes(preview, new EncryptedPayloadContext("file-preview", encryption, EncryptedPayloadRoles.Preview, 0));
        var videoPayload = service.EncryptBytes(video, new EncryptedPayloadContext("file-preview", encryption, EncryptedPayloadRoles.Video480p, 0));
        var previewRoundTrip = service.DecryptBytes(previewPayload, new EncryptedPayloadContext("file-preview", encryption, EncryptedPayloadRoles.Preview, 0));
        var videoRoundTrip = service.DecryptBytes(videoPayload, new EncryptedPayloadContext("file-preview", encryption, EncryptedPayloadRoles.Video480p, 0));
        var valid = CryptographicOperations.FixedTimeEquals(preview, previewRoundTrip) &&
                    CryptographicOperations.FixedTimeEquals(video, videoRoundTrip);
        CryptographicOperations.ZeroMemory(preview);
        CryptographicOperations.ZeroMemory(video);
        CryptographicOperations.ZeroMemory(previewPayload);
        CryptographicOperations.ZeroMemory(videoPayload);
        CryptographicOperations.ZeroMemory(previewRoundTrip);
        CryptographicOperations.ZeroMemory(videoRoundTrip);
        return Task.FromResult(valid ? TestOutcome.Pass() : TestOutcome.Fail("prévia ou vídeo 480p não voltou idêntico"));
    }

    private static Task<TestOutcome> UrlSafeEncryptionSaltCompatibilityAsync()
    {
        var service = CreateEncryptionService(out _);
        var encryption = service.CreateFileEncryption(EncryptionModes.Standard);
        var generatedSaltIsCanonical = encryption.Salt.Length > 0 &&
            encryption.Salt.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
        if (!generatedSaltIsCanonical)
        {
            return Task.FromResult(TestOutcome.Fail("o salt novo não foi gravado em Base64URL sem padding"));
        }

        encryption.Salt = "NyasiAVslEKTO__DCbEeKg";
        var original = RandomNumberGenerator.GetBytes(4096);
        var context = new EncryptedPayloadContext("file-url-safe-salt", encryption, EncryptedPayloadRoles.Original, 0);
        var payload = service.EncryptBytes(original, context);
        var restored = service.DecryptBytes(payload, context);
        var matches = CryptographicOperations.FixedTimeEquals(original, restored);
        CryptographicOperations.ZeroMemory(original);
        CryptographicOperations.ZeroMemory(payload);
        CryptographicOperations.ZeroMemory(restored);
        return Task.FromResult(matches
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o salt base64url não foi aceito na criptografia"));
    }

    private static async Task<TestOutcome> CustomKeyExportImportAndMismatchAsync()
    {
        var serviceA = CreateEncryptionService(out _);
        serviceA.ConfigureCustomKey("senha de teste A");
        var encryption = serviceA.CreateFileEncryption(EncryptionModes.Custom);
        var context = new EncryptedPayloadContext("file-custom", encryption, EncryptedPayloadRoles.Original, 0);
        var original = RandomNumberGenerator.GetBytes(4096);
        var payload = serviceA.EncryptBytes(original, context);

        var serviceB = CreateEncryptionService(out _);
        serviceB.ConfigureCustomKey("senha de teste B");
        var rejected = Throws<EncryptedPayloadAuthenticationException>(() => serviceB.DecryptBytes(payload, context));

        var path = Path.Combine(Path.GetTempPath(), "organessonium-test-" + Guid.NewGuid().ToString("N") + ".organessonium-key");
        try
        {
            await serviceA.ExportCustomKeyAsync(path);
            var serviceImported = CreateEncryptionService(out _);
            await serviceImported.ImportCustomKeyAsync(path);
            var restored = serviceImported.DecryptBytes(payload, context);
            var matches = CryptographicOperations.FixedTimeEquals(original, restored);
            var replacementBlocked = Throws<InvalidOperationException>(() => serviceA.ConfigureCustomKey("senha diferente"));
            CryptographicOperations.ZeroMemory(restored);
            return rejected && matches && replacementBlocked
                ? TestOutcome.Pass()
                : TestOutcome.Fail("a chave personalizada não rejeitou a chave errada, falhou após a importação ou foi substituída sem aviso");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(original);
            CryptographicOperations.ZeroMemory(payload);
            try { File.Delete(path); } catch { }
        }
    }

    private static async Task<TestOutcome> LegacyEntryHasNoEncryptionMetadataAsync()
    {
        var entry = AppEntry.File("legacy.txt", "thread", "2026-01-01T00:00:00Z", 1, "/");
        entry.ChunkMessageIds = ["legacy-chunk-message"];
        entry.ChunkCount = 1;
        var payload = await new MetadataService().CreateWritePayloadAsync(entry);
        try
        {
            return entry.Encryption == null && !payload.Content.Contains("\"encryption\"", StringComparison.Ordinal)
                ? TestOutcome.Pass()
                : TestOutcome.Fail("arquivo legado recebeu metadados de criptografia");
        }
        finally
        {
            try { payload.Attachment?.Delete(); } catch { }
        }
    }

    private static async Task<TestOutcome> UrlSafeEncryptedMetadataIsLoadedAsync()
    {
        var messages = new[]
        {
            new DiscordMessageDto
            {
                Id = "1542915922841436287",
                Content = """
                    {"fileId":"5mqlhvddv3errvi7c2sa4zp2qeo9g2t2","threadId":"1542915920857661451","name":"Documento (2).pdf","mimeType":"application/pdf","sizeBytes":17803,"uploadedAt":"2026-08-28T15:17:02Z","batchId":"k7opmdfl3v","parentFolderId":"root","chunks":["1542915920857661451"],"encryption":{"version":1,"mode":"standard","salt":"NyasiAVslEKTO__DCbEeKg"},"schemaVersion":2,"entryType":"file"}
                    """
            },
            new DiscordMessageDto
            {
                Id = "1543972701318684816",
                Content = """
                    {"fileId":"hi6iv49u6u0e0jzjwnr7pspn3ndm5k3c","threadId":"1543972697506316288","name":"IMG_20260403_140807.jpg","mimeType":"image/jpeg","sizeBytes":3161925,"uploadedAt":"2026-08-31T13:16:16Z","batchId":"yqzrf3n1xt","parentFolderId":"root","chunks":["1543972697506316288"],"preview":{"thumbnailMessageId":"1543972700282822786"},"encryption":{"version":1,"mode":"standard","salt":"cPEsp9RSk8AZKL7p-OPicw"},"schemaVersion":2,"entryType":"file"}
                    """
            }
        };
        var service = new MetadataService();
        foreach (var message in messages)
        {
            var envelope = await service.ReadEnvelopeAsync(message);
            if (envelope?.Entry is not FileMetadata file || file.Encryption == null)
            {
                return TestOutcome.Fail("metadata criptografada em base64url não foi carregada");
            }
        }

        var standardContent = messages[0].Content.Replace(
            "NyasiAVslEKTO__DCbEeKg",
            "AAECAwQFBgcICQoLDA0ODw==",
            StringComparison.Ordinal);
        var standardEnvelope = await service.ReadEnvelopeAsync(new DiscordMessageDto
        {
            Id = "standard-salt-must-be-rejected",
            Content = standardContent
        });
        if (standardEnvelope != null)
        {
            return TestOutcome.Fail("metadata em Base64 padrão foi aceita, mas o formato do servidor é Base64URL");
        }

        return TestOutcome.Pass();
    }

    private static Task<TestOutcome> FolderSerialRoundTripAsync()
    {
        var codec = new FolderSerialCodec();
        var generatedAt = DateTimeOffset.Parse("2026-09-07T15:00:00Z");
        var serial = new FolderSerial
        {
            FolderId = "root",
            ParentFolderId = "root",
            FolderName = "root",
            GeneratedAt = generatedAt,
            Aggregate = new FolderSerialAggregate
            {
                TotalItems = 1,
                FileCount = 1,
                FolderCount = 0,
                TotalBytes = 42,
                ImageCount = 1,
                VideoCount = 0,
                TypeCounts = new Dictionary<string, long> { ["jpg"] = 1 }
            },
            Children =
            [
                new FolderSerialEntry
                {
                    Id = "file-id",
                    NamesMessageId = "names-message-id",
                    ParentFolderId = "root",
                    Name = "foto.jpg",
                    ThreadId = "thread-id",
                    MimeType = "image/jpeg",
                    UploadedAt = generatedAt,
                    PreviewMessageId = "preview-id",
                    Encryption = new EncryptionMetadata
                    {
                        Version = 1,
                        Mode = EncryptionModes.Standard,
                        Salt = "NyasiAVslEKTO__DCbEeKg"
                    }
                }
            ]
        };

        var restored = codec.DecodeFolder(codec.EncodeFolder(serial));
        var child = restored.Children.SingleOrDefault();
        var valid = restored.FolderId == serial.FolderId &&
                    restored.GeneratedAt == generatedAt &&
                    restored.Aggregate.TotalBytes == 42 &&
                    child?.Id == "file-id" &&
                    child.Encryption?.Salt == "NyasiAVslEKTO__DCbEeKg" &&
                    child.MimeType == "image/jpeg";
        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o serial OSER não voltou com a estrutura esperada"));
    }

    private static Task<TestOutcome> SerialMessageEnvelopeProtocolAsync()
    {
        var content = SerialMessageEnvelope.Encode("folder-id", "Fotos", new string('a', 64));
        var parsed = SerialMessageEnvelope.TryParse(content, out var envelope);
        var rejected = !SerialMessageEnvelope.TryParse(
            "{\"schema\":1,\"entryType\":\"folderSerial\",\"folderId\":\"folder-id\",\"sha256\":\"bad\"}",
            out _);
        var valid = parsed && rejected && envelope.FolderId == "folder-id" && envelope.FolderName == "Fotos";
        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o envelope do canal serial não correspondeu ao padrão existente"));
    }

    private static EncryptionService CreateEncryptionService(out InMemoryEncryptionSecretStore store)
    {
        store = new InMemoryEncryptionSecretStore();
        return new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(store));
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }

    private sealed class InMemoryEncryptionSecretStore : IEncryptionSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public string ReadEncryptionSecret(string key)
        {
            return _values.TryGetValue(key, out var value) ? value : "";
        }

        public void WriteEncryptionSecret(string key, string value)
        {
            _values[key] = value;
        }
    }

    private static ProxySettings Manual(string host, int port, ProxyProtocol protocol, bool auth = false, string user = "", string pass = "")
    {
        return new ProxySettings
        {
            Mode = ProxyMode.Manual,
            Host = host,
            Port = port,
            Protocol = protocol,
            AuthenticationEnabled = auth,
            Username = user,
            Password = pass
        };
    }

    private static async Task<TestOutcome> HttpProxyForwardsGetAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new HttpProxyServer();
        var network = new NetworkConfig();
        network.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http));
        using var http = network.CreateClient();
        using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK && body.Contains("OK", StringComparison.Ordinal)
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"resposta inesperada: {(int)response.StatusCode} {body}");
    }

    private static async Task<TestOutcome> HttpProxyAuthAcceptAndRejectAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new HttpProxyServer(requireAuth: true, user: "usuario", pass: "senha");

        var withAuth = new NetworkConfig();
        withAuth.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http, true, "usuario", "senha"));
        using (var http = withAuth.CreateClient())
        {
            using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return TestOutcome.Fail("com credenciais corretas o proxy rejeitou o pedido");
            }
        }

        var withoutAuth = new NetworkConfig();
        withoutAuth.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http));
        using (var http = withoutAuth.CreateClient())
        {
            using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
            return response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired
                ? TestOutcome.Pass()
                : TestOutcome.Fail($"esperado 407, recebido {(int)response.StatusCode}");
        }
    }

    private static async Task<TestOutcome> Socks5ProxyForwardsGetAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new Socks5Server();
        var network = new NetworkConfig();
        network.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Socks5));
        using var http = network.CreateClient();
        using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK && body.Contains("OK", StringComparison.Ordinal)
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"resposta inesperada: {(int)response.StatusCode} {body}");
    }

    private static async Task<TestOutcome> Socks5WrongCredentialsRejectedAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new Socks5Server(requireAuth: true, user: "usuario", pass: "senha");
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Socks5, true, "usuario", "senhaErrada");
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success == false && result.Kind == ProxyTestErrorKind.ProxyAuthenticationRequired
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado ProxyAuthenticationRequired, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static async Task<TestOutcome> Socks4aProxyForwardsGetAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new Socks4aServer();
        var network = new NetworkConfig();
        network.Apply(Manual("127.0.0.1", proxy.Port, ProxyProtocol.Socks4a));
        using var http = network.CreateClient();
        using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        return response.StatusCode == HttpStatusCode.OK && body.Contains("OK", StringComparison.Ordinal)
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"resposta inesperada: {(int)response.StatusCode} {body}");
    }

    private static async Task<TestOutcome> DirectModeBypassesProxyAsync()
    {
        using var target = new HttpTargetServer();
        var network = new NetworkConfig();
        network.Apply(new ProxySettings { Mode = ProxyMode.Disabled });
        using var http = network.CreateClient();
        using var response = await http.GetAsync(target.ProbeUrl(""), HttpCompletionOption.ResponseHeadersRead);
        return response.StatusCode == HttpStatusCode.OK
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"resposta inesperada: {(int)response.StatusCode}");
    }

    private static async Task<TestOutcome> RefusedProxyClassifiedAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new RefusalServer();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http);
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success == false && result.Kind == ProxyTestErrorKind.ProxyRefused
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado ProxyRefused, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static async Task<TestOutcome> UnreachableProxyClassifiedAsync()
    {
        using var target = new HttpTargetServer();
        var settings = Manual("proxy-host-nao-existe.invalid", 8080, ProxyProtocol.Http);
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success == false && result.Kind == ProxyTestErrorKind.ProxyUnreachable
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado ProxyUnreachable, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static async Task<TestOutcome> AutoDetectChoosesHttpAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new HttpProxyServer();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Auto);
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success && result.DetectedProtocol == ProxyProtocol.Http
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado detecção HTTP, recebido Success={result.Success} Protocol={result.DetectedProtocol} Msg={result.Message}");
    }

    private static async Task<TestOutcome> AutoDetectChoosesSocks5Async()
    {
        using var target = new HttpTargetServer();
        using var proxy = new Socks5Server();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Auto);
        var result = await ProxyTester.TestAsync(settings, false, default, target.ProbeUrl(""));
        return result.Success && result.DetectedProtocol == ProxyProtocol.Socks5
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado detecção SOCKS5, recebido Success={result.Success} Protocol={result.DetectedProtocol} Msg={result.Message}");
    }

    private static async Task<TestOutcome> DiscordCheckThroughProxySucceedsAsync()
    {
        using var target = new HttpTargetServer();
        using var proxy = new HttpProxyServer();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http);
        var result = await ProxyTester.TestAsync(settings, true, default, target.ProbeUrl(""));
        return result.Success
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado sucesso, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static async Task<TestOutcome> FailedProbeReportedAsUnreachableAsync()
    {
        using var proxy = new HttpProxyServer();
        var settings = Manual("127.0.0.1", proxy.Port, ProxyProtocol.Http);
        var closedPortUrl = "http://127.0.0.1:" + ClosedPort() + "/";
        var result = await ProxyTester.TestAsync(settings, true, default, closedPortUrl);
        return result.Success == false && result.Kind == ProxyTestErrorKind.TargetUnreachable
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"esperado TargetUnreachable, recebido Success={result.Success} Kind={result.Kind} Msg={result.Message}");
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class HttpTargetServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public HttpTargetServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public string ProbeUrl(string path)
        {
            return $"http://127.0.0.1:{Port}/{path.TrimStart('/')}";
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private static async Task HandleAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                await ReadUntilHeaderEndAsync(stream);
                var body = "OK";
                var response = "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n" + body;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            }
            catch
            {
                // ignore
            }
            finally
            {
                client.Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private sealed class HttpProxyServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public HttpProxyServer(bool requireAuth = false, string? user = null, string? pass = null)
        {
            RequireAuth = requireAuth;
            User = user ?? "";
            Pass = pass ?? "";
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }
        public bool RequireAuth { get; }
        public string User { get; }
        public string Pass { get; }
        private int _requests;
        private int _authRejections;

        public int AuthRejections => Volatile.Read(ref _authRejections);
        public int Requests => Volatile.Read(ref _requests);

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var requestLine = await ReadLineAsync(stream);
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                Interlocked.Increment(ref _requests);
                var headers = await ReadHeadersAsync(stream);
                if (RequireAuth && !HasValidAuth(headers))
                {
                    Interlocked.Increment(ref _authRejections);
                    await WriteStatusAsync(stream, "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    return;
                }

                if (requestLine.StartsWith("CONNECT ", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteStatusAsync(stream, "HTTP/1.1 200 Connection Established\r\n\r\n");
                    var target = requestLine["CONNECT ".Length..].Trim();
                    var (host, port) = SplitHostPort(target);
                    TcpClient? upstream = null;
                    try
                    {
                        upstream = new TcpClient();
                        await upstream.ConnectAsync(host, port, _cts.Token);
                        await RelayAsync(stream, upstream.GetStream(), _cts.Token);
                    }
                    catch
                    {
                        if (upstream == null)
                        {
                            await Task.Delay(1500, _cts.Token);
                        }
                    }
                    finally
                    {
                        upstream?.Dispose();
                    }

                    return;
                }

                if (requestLine.StartsWith("GET ", StringComparison.OrdinalIgnoreCase))
                {
                    var absolute = requestLine["GET ".Length..].Split(' ')[0];
                    if (!Uri.TryCreate(absolute, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
                    {
                        await WriteStatusAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }

                    using var upstream = new TcpClient();
                    try
                    {
                        await upstream.ConnectAsync(uri.Host, uri.Port, _cts.Token);
                    }
                    catch
                    {
                        await WriteStatusAsync(stream, "HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }

                    var upstreamStream = upstream.GetStream();
                    var forward = $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Host}\r\nConnection: close\r\n\r\n";
                    await upstreamStream.WriteAsync(Encoding.ASCII.GetBytes(forward), _cts.Token);
                    await RelayStreamAsync(upstreamStream, stream, _cts.Token);
                    return;
                }

                await WriteStatusAsync(stream, "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            }
            catch
            {
                // ignore
            }
            finally
            {
                client.Dispose();
            }
        }

        private bool HasValidAuth(IReadOnlyList<string> headers)
        {
            foreach (var header in headers)
            {
                if (!header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = header[(header.IndexOf(':') + 1)..].Trim();
                if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value["Basic ".Length..].Trim()));
                    return decoded == User + ":" + Pass;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        private static (string Host, int Port) SplitHostPort(string target)
        {
            var parts = target.Split(':');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[^1], out var port) ? port : 80);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private sealed class Socks5Server : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public Socks5Server(bool requireAuth = false, string? user = null, string? pass = null)
        {
            RequireAuth = requireAuth;
            User = user ?? "";
            Pass = pass ?? "";
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }
        public bool RequireAuth { get; }
        public string User { get; }
        public string Pass { get; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var greeting = await ReadExactlyAsync(stream, 2);
                var methods = await ReadExactlyAsync(stream, greeting[1]);

                var supportsAuth = methods.Contains((byte)0x02);
                var supportsNone = methods.Contains((byte)0x00);

                if (RequireAuth)
                {
                    if (!supportsAuth)
                    {
                        stream.Write(new byte[] { 0x05, 0xFF });
                        return;
                    }

                    stream.Write(new byte[] { 0x05, 0x02 });
                    var authVersion = await ReadExactlyAsync(stream, 1);
                    var userNameLength = (await ReadExactlyAsync(stream, 1))[0];
                    var userName = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, userNameLength));
                    var passwordLength = (await ReadExactlyAsync(stream, 1))[0];
                    var password = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, passwordLength));
                    if (userName != User || password != Pass)
                    {
                        stream.Write(new byte[] { 0x01, 0xFF });
                        return;
                    }

                    stream.Write(new byte[] { 0x01, 0x00 });
                }
                else
                {
                    if (!supportsNone)
                    {
                        stream.Write(new byte[] { 0x05, 0xFF });
                        return;
                    }

                    stream.Write(new byte[] { 0x05, 0x00 });
                }

                var request = await ReadExactlyAsync(stream, 4);
                var command = request[1];
                var atyp = request[3];
                string host;
                if (atyp == 0x01)
                {
                    var ip = await ReadExactlyAsync(stream, 4);
                    host = string.Join('.', ip);
                }
                else if (atyp == 0x03)
                {
                    var length = (await ReadExactlyAsync(stream, 1))[0];
                    host = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, length));
                }
                else
                {
                    stream.Write(new byte[] { 0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                var portBytes = await ReadExactlyAsync(stream, 2);
                var port = (portBytes[0] << 8) | portBytes[1];

                if (command != 0x01)
                {
                    stream.Write(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                using var upstream = new TcpClient();
                try
                {
                    await upstream.ConnectAsync(host, port, _cts.Token);
                }
                catch
                {
                    stream.Write(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                stream.Write(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                await RelayAsync(stream, upstream.GetStream(), _cts.Token);
            }
            catch
            {
                // ignore
            }
            finally
            {
                client.Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private sealed class Socks4aServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public Socks4aServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                var header = await ReadExactlyAsync(stream, 8);
                var command = header[1];
                var port = (header[2] << 8) | header[3];

                string host;
                if (header[4] == 0 && header[5] == 0 && header[6] == 0 && header[7] != 0)
                {
                    await ReadLineUntilNullAsync(stream);
                    host = await ReadLineUntilNullAsync(stream);
                }
                else
                {
                    host = string.Join('.', header.Skip(4).Take(4));
                    await ReadLineUntilNullAsync(stream);
                }

                if (string.IsNullOrWhiteSpace(host))
                {
                    stream.Write(new byte[] { 0, 0x5B, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                if (command != 0x01)
                {
                    stream.Write(new byte[] { 0, 0x5B, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                using var upstream = new TcpClient();
                try
                {
                    await upstream.ConnectAsync(host, port, _cts.Token);
                }
                catch
                {
                    stream.Write(new byte[] { 0, 0x5B, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                stream.Write(new byte[] { 0, 0x5A, 0, 0, 0, 0, 0, 0 });
                await RelayAsync(stream, upstream.GetStream(), _cts.Token);
            }
            catch
            {
                // ignore
            }
            finally
            {
                client.Dispose();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private sealed class RefusalServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public RefusalServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private static Task HandleAsync(TcpClient client)
        {
            try
            {
                client.LingerState = new LingerOption(true, 0);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.LingerState = new LingerOption(true, 0);
            }
            catch
            {
                // ignore
            }

            client.Dispose();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    private static async Task RelayAsync(NetworkStream left, NetworkStream right, CancellationToken cancellationToken)
    {
        var leftToRight = RelayStreamAsync(left, right, cancellationToken);
        var rightToLeft = RelayStreamAsync(right, left, cancellationToken);
        await Task.WhenAny(leftToRight, rightToLeft);
    }

    private static async Task RelayStreamAsync(NetworkStream from, NetworkStream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            try { to.Dispose(); } catch { }
        }
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return builder.ToString();
            }

            if (buffer[0] == (byte)'\n')
            {
                return builder.ToString();
            }

            if (buffer[0] != (byte)'\r')
            {
                builder.Append((char)buffer[0]);
            }
        }
    }

    private static async Task<string> ReadLineUntilNullAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0 || buffer[0] == 0)
            {
                return builder.ToString();
            }

            builder.Append((char)buffer[0]);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadHeadersAsync(NetworkStream stream)
    {
        var headers = new List<string>();
        while (true)
        {
            var line = (await ReadLineAsync(stream)).TrimEnd();
            if (line.Length == 0)
            {
                return headers;
            }

            headers.Add(line);
        }
    }

    private static async Task ReadUntilHeaderEndAsync(NetworkStream stream)
    {
        var buffer = new byte[1];
        var tails = 0;
        while (tails < 4)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                return;
            }

            if (buffer[0] == (byte)'\r' || buffer[0] == (byte)'\n')
            {
                tails++;
            }
            else
            {
                tails = 0;
            }
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        return buffer;
    }

    private static async Task WriteStatusAsync(NetworkStream stream, string status)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(status));
    }
}
