using System.Security.Cryptography;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> FileChecksumsTransferAndResumeAsync()
    {
        using var h = new SharedHarness();
        var bytes = Enumerable.Range(0, 10003).Select(index => (byte)(index * 37)).ToArray();
        var state = await h.PrepareAsync("checksums.bin", bytes);
        const int chunkBytes = 4096;
        state.TotalChunks = 3; state.ChunkSizeBytes = chunkBytes;
        var source = new FileInfo(state.SourcePath);
        state.Checksums = await FileChecksums.CalculateAsync(() => source.OpenRead(), bytes.Length, chunkBytes, default);
        await h.Transfers.SaveAsync(state);
        var reloaded = (await h.Transfers.Repository.LoadActiveByIdAsync(state.TransferId))!;
        RequireSync(reloaded.Checksums?.Sha256 == Convert.ToHexStringLower(SHA256.HashData(bytes)), "checkpoint perdeu o hash completo");
        using var memory = new UploadMemoryCoordinator(2);
        var context = new EncryptedPayloadContext(state.FileId, state.Encryption!, EncryptedPayloadRoles.Original, 0, 3)
            { Checksums = reloaded.Checksums };
        var uploader = new ForumUploader(h.Api, "files");
        var uploaded = await uploader.UploadResumableGeneratedAsync(state.FileName, 3,
            index => memory.PreparePartAsync(h.Encryption, source, bytes.Length, chunkBytes, context.WithChunkIndex(index), default, deferBuffers: true),
            "", "", new Dictionary<int, string>(), encryptedPayload: context, useAttachmentReferences: true,
            streamPreparedParts: true, maxPreparedPartBytes: chunkBytes + EncryptionService.EnvelopeOverheadBytes);
        var entry = AppEntry.File(state.FileName, uploaded.ThreadId, TransferState.Now(), bytes.Length, "/");
        entry.FileId = state.FileId; entry.Encryption = state.Encryption; entry.Checksums = reloaded.Checksums;
        entry.ChunkReferences = uploaded.ChunkReferences!.ToList(); entry.ChunkCount = 3; entry.ParentFolderId = "root";
        await h.Registry.AddEntryAsync(entry);
        var remote = (await h.Registry.RefreshFolderAsync("root", "/")).Single(item => item.FileId == entry.FileId);
        var cached = h.Store.LoadCachedEntriesForFolder("root").Single(item => item.FileId == entry.FileId);
        RequireSync(remote.Checksums?.Sha256 == entry.Checksums!.Sha256 && cached.Checksums?.Chunks.Count == 3,
            "metadata remoto/cache perdeu os hashes");
        var destination = h.Directory.CreateSubdirectory("download");
        var download = await h.Transfers.CreateDownloadAsync(cached, destination);
        var corruptPart = new FileInfo(Path.Combine(h.Directory.FullName, "saved-plain-chunk.bin"));
        await File.WriteAllBytesAsync(corruptPart.FullName, new byte[chunkBytes]);
        await h.Transfers.MarkDownloadChunkAsync(download, 0, corruptPart.FullName, chunkBytes);
        var stages = new List<string>();
        var output = await h.Downloader.DownloadAndAssembleAsync(cached, destination, download, h.Transfers, stages.Add);
        RequireSync((await File.ReadAllBytesAsync(output.FullName)).SequenceEqual(bytes) && stages.Any(stage => stage.Contains("checksums")),
            "a retomada reutilizou uma parte adulterada ou não validou a montagem");
        using (var video = await h.Downloader.OpenVideoChunksAsync(remote, false))
            RequireSync((await video.DownloadAsync(1, default)).SequenceEqual(bytes.Skip(chunkBytes).Take(chunkBytes)),
                "o streaming não preservou os chunks com checksum");
        var replacement = AppEntry.File(entry.Name, entry.ThreadId, TransferState.Now(), entry.SizeBytes, "/");
        replacement.FileId = entry.FileId;
        FileVersioning.PreservePrevious(remote, replacement, TransferState.Now());
        RequireSync(replacement.Versions.Single().Payload.Checksums?.Sha256 == entry.Checksums.Sha256,
            "o histórico perdeu o checksum da versão anterior");
        remote.Checksums = null;
        var legacy = await h.Downloader.DownloadAndAssembleAsync(remote, h.Directory.CreateSubdirectory("legacy"), _ => { });
        RequireSync((await File.ReadAllBytesAsync(legacy.FullName)).SequenceEqual(bytes), "o download legado deixou de funcionar");
        var empty = await FileChecksums.CalculateAsync(() => new MemoryStream(), 0, chunkBytes, default);
        RequireSync(empty.Sha256 == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" && empty.Chunks.Single() == empty.Sha256,
            "hash de arquivo vazio inválido");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> FileChecksumsRejectCorruptionAsync()
    {
        using var h = new SharedHarness();
        var source = new FileInfo(Path.Combine(h.Directory.FullName, "source.bin"));
        var bytes = "abcdef"u8.ToArray();
        await File.WriteAllBytesAsync(source.FullName, bytes);
        var checksums = await FileChecksums.CalculateAsync(() => source.OpenRead(), 6, 3, default);
        RequireSync(checksums.Sha256 == "bef57ec7f53a6d40beb640a780a639c83bc29ac8a9816f1fc6c5c6dcd93c4721" &&
            checksums.Chunks[0] == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            "vetor SHA-256 não corresponde ao protocolo");
        var encryption = h.Encryption.CreateFileEncryption("standard");
        var original = new EncryptedPayloadContext("checksum-test", encryption, EncryptedPayloadRoles.Original, 0, 2);
        var verified = original with { Checksums = checksums };
        var wrongButAuthenticated = h.Encryption.EncryptBytes("xbc"u8, original);
        await MustReject(() => Task.FromResult(h.Encryption.DecryptBytes(wrongButAuthenticated, verified)));
        await File.WriteAllBytesAsync(source.FullName, "xbcdef"u8.ToArray());
        using (var memory = new UploadMemoryCoordinator(1))
            await MustReject(async () => { using var part = await memory.PreparePartAsync(h.Encryption, source, 6, 3, verified, default); });
        var parts = new[] { new FileInfo(Path.Combine(h.Directory.FullName, "part-0")), new FileInfo(Path.Combine(h.Directory.FullName, "part-1")) };
        await File.WriteAllBytesAsync(parts[0].FullName, "abc"u8.ToArray());
        await File.WriteAllBytesAsync(parts[1].FullName, "def"u8.ToArray());
        var target = new FileInfo(Path.Combine(h.Directory.FullName, "destination.bin"));
        await File.WriteAllTextAsync(target.FullName, "preserve");
        var wrongWhole = JsonSerializer.Deserialize<PayloadChecksums>(JsonSerializer.Serialize(checksums))!;
        wrongWhole.Sha256 = new string('0', 64);
        await MustReject(() => DownloadAssembler.AssembleAsync(parts, target, _ => { }, null, default, wrongWhole));
        await File.WriteAllBytesAsync(parts[1].FullName, "xef"u8.ToArray());
        await MustReject(() => DownloadAssembler.AssembleAsync(parts, target, _ => { }, null, default, checksums));
        RequireSync(await File.ReadAllTextAsync(target.FullName) == "preserve" && !h.Directory.GetFiles("*.assembling").Any(),
            "falha de checksum substituiu o destino ou deixou montagem inválida");
        var future = JsonSerializer.Deserialize<PayloadChecksums>(JsonSerializer.Serialize(checksums))!;
        future.Version = 2;
        try { future.Validate(6, 2); return TestOutcome.Fail("versão desconhecida aceita"); } catch (InvalidDataException) { }
        using var invalidJson = JsonDocument.Parse("{\"checksums\":null}");
        try { ChunkReference.ValidateJsonPresence(invalidJson.RootElement); return TestOutcome.Fail("checksums declarado nulo virou legado"); } catch (InvalidDataException) { }
        return TestOutcome.Pass();

        static async Task MustReject(Func<Task> action)
        {
            try { await action(); } catch (PayloadChecksumException) { return; }
            throw new InvalidOperationException("A corrupção não foi recusada pelo checksum.");
        }
    }
}
