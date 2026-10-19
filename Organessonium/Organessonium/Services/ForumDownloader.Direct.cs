using System.IO;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class ForumDownloader
{
    private static async Task<FileInfo?> RecoverFinalizedDownloadAsync(TransferState state, AppEntry entry,
        Action<string> status, Action<DownloadProgress>? progress, CancellationToken token)
    {
        if (state.DownloadCheckpointVersion != TransferState.DirectDownloadVersion || !state.DownloadReadyToFinalize ||
            state.DownloadIdentity != TransferManager.DownloadIdentity(entry) || !PartialDownloadFile.OwnsPartial(state) ||
            File.Exists(state.PartialFilePath) || !File.Exists(state.FinalFilePath) || !PartialDownloadFile.IsDigest(state.DownloadSha256) ||
            state.Chunks.Count == 0 || state.Chunks.Any(chunk => !chunk.Downloaded || chunk.LogicalSizeBytes < 0)) return null;
        long length = 0;
        foreach (var chunk in state.Chunks.OrderBy(chunk => chunk.Index))
        {
            if (chunk.Offset != length) return null;
            length = checked(length + chunk.LogicalSizeBytes);
        }
        var file = new FileInfo(state.FinalFilePath!);
        if (file.Length != length || (entry.Checksums != null && state.DownloadSha256 != entry.Checksums.Sha256)) return null;
        status("Verificando integridade do arquivo...");
        progress?.Invoke(new DownloadProgress(99, "Verificando integridade do arquivo...", length, length, Assembling: true));
        if (!await FileChecksums.MatchesAsync(file, state.DownloadSha256!, token)) return null;
        if (entry.Encryption != null) EnsureExpectedPlaintextLength(file, entry.SizeBytes);
        status("Download concluído.");
        progress?.Invoke(new DownloadProgress(100, "Download concluído.", length, length, Assembling: true));
        return file;
    }

    private async Task<FileInfo> DownloadDirectAsync(IReadOnlyList<Part> parts, DirectoryInfo destination, string name,
        Action<string> status, CancellationToken token, Action<DownloadProgress>? progress,
        EncryptedPayloadContext? encryption = null, long expectedLength = -1, PayloadChecksums? checksums = null,
        TransferState? checkpoint = null, TransferManager? manager = null, string? identity = null,
        PartDiscovery? discovery = null)
    {
        var knownParts = new ConcurrentDictionary<int, Part>(parts.ToDictionary(part => part.Index));
        var references = discovery?.References ?? parts;
        var layout = new DownloadFileLayout(parts.Select(part => (part.Index, part.Size)), encryption != null,
            discovery == null ? checksums : null);
        if (discovery != null && checksums != null && layout.Regions.Any(region => region.Length !=
                Math.Min(checksums.ChunkSizeBytes, checked(expectedLength - (long)region.Index * checksums.ChunkSizeBytes))))
            throw new InvalidDataException("Os tamanhos dos chunks divergem do manifesto de checksums.");
        var totalLength = discovery != null && expectedLength >= 0 ? expectedLength : layout.Length;
        var state = checkpoint ?? new TransferState { TransferType = TransferTypes.Download, FileName = name, DestinationPath = destination.FullName };
        var finalPath = PartialDownloadFile.Destination(destination, name);
        var partialPath = PartialDownloadFile.PartialPath(finalPath, state.TransferId);
        var modern = state.DownloadCheckpointVersion == TransferState.DirectDownloadVersion;
        if (modern && (state.DownloadIdentity != identity || !PartialDownloadFile.OwnsPartial(state) ||
            !SameFilePath(state.FinalFilePath!, finalPath)))
            throw new InvalidDataException("O checkpoint não corresponde ao arquivo, versão ou destino solicitado.");
        if (PartialDownloadFile.IsActive(finalPath)) throw new DownloadAlreadyActiveException();
        destination.Create();
        if (!modern && string.IsNullOrWhiteSpace(state.TempPath)) ValidateDestination(destination, totalLength);

        if (discovery == null && modern && state.DownloadReadyToFinalize && !File.Exists(partialPath) && File.Exists(finalPath) &&
            new FileInfo(finalPath).Length == layout.Length && PartialDownloadFile.IsDigest(state.DownloadSha256) &&
            (checksums == null || state.DownloadSha256 == checksums.Sha256) &&
            state.Chunks.Count == parts.Count && state.Chunks.All(chunk => chunk.Downloaded) &&
            await FileChecksums.MatchesAsync(new FileInfo(finalPath), state.DownloadSha256!, token))
        {
            ReportVerification(layout.Length);
            var recovered = new FileInfo(finalPath);
            if (encryption != null) EnsureExpectedPlaintextLength(recovered, expectedLength);
            return recovered;
        }

        var previous = state.Chunks.ToDictionary(chunk => chunk.Index);
        var initialParts = parts.ToDictionary(part => part.Index);
        state.Chunks = references.OrderBy(part => part.Index).Select(reference =>
        {
            var part = initialParts.GetValueOrDefault(reference.Index) ?? reference;
            var region = part.Index < layout.Regions.Count ? layout.Regions[part.Index] : null;
            previous.TryGetValue(part.Index, out var old);
            if (modern && (old == null || (region != null && old.Offset.HasValue &&
                    (old.Offset != region.Offset || old.LogicalSizeBytes != region.Length)) ||
                old.MessageId != part.MessageId || (old.AttachmentId ?? "") != part.AttachmentId))
                throw new InvalidDataException("Os intervalos ou referências do checkpoint foram alterados.");
            var chunk = new TransferChunkState
            {
                Index = part.Index, MessageId = part.MessageId, AttachmentId = part.AttachmentId,
                Offset = old?.Offset, LogicalSizeBytes = old?.LogicalSizeBytes ?? -1, SizeBytes = old?.SizeBytes ?? 0,
                Downloaded = old?.Downloaded ?? false, Sha256 = old?.Sha256, TempChunkPath = old?.TempChunkPath
            };
            if (region != null) { chunk.Offset = region.Offset; chunk.LogicalSizeBytes = region.Length; chunk.SizeBytes = part.Size; }
            return chunk;
        }).ToList();
        state.FileName = name; state.DestinationPath = destination.FullName;
        state.PartialFilePath = partialPath; state.FinalFilePath = finalPath;
        state.DownloadIdentity = identity; state.DownloadCheckpointVersion = TransferState.DirectDownloadVersion;
        state.TotalChunks = references.Count; state.Status = TransferStatuses.Downloading;
        state.DownloadReadyToFinalize = false;
        var existed = modern && File.Exists(partialPath);
        using var file = new PartialDownloadFile(partialPath, finalPath, layout, modern || (checkpoint != null && File.Exists(partialPath)));
        using var checkpointGate = new SemaphoreSlim(1, 1);
        var completedSinceSave = 0;
        var dirty = false;
        var awaitingFlush = new HashSet<int>();
        var confirmedThisRun = new HashSet<int>();
        long receivedBytes = 0;
        try
        {
            status("Verificando partes já gravadas...");
            var initial = await PreparePartsAsync(parts);
            dirty = true;
            await SaveCheckpointAsync(token);
            ValidateDestination(destination, totalLength, Math.Min(totalLength, PartialDownloadFile.AllocatedBytes(partialPath)));
            var attempts = encryption == null ? MaxChunkDownloadAttempts : MaxEncryptedPayloadDownloadAttempts;
            for (var pass = 0; pass < attempts; pass++)
            {
                var completed = pass == 0 ? 0 : state.Chunks.Where(chunk => chunk.Downloaded).Sum(chunk => chunk.LogicalSizeBytes);
                var receivedBeforePass = receivedBytes;
                progress?.Invoke(new DownloadProgress(totalLength == 0 ? 0 : Math.Min(99, (int)(completed * 99d / totalLength)),
                    $"{FormatBytes(completed)} / {FormatBytes(totalLength)} baixados", completed, totalLength, RateBytes: receivedBytes));
                try
                {
                    await DownloadPartsAsync(PendingPartsAsync(pass, initial, token), references.Count, file, status, token, item =>
                    {
                        Interlocked.Exchange(ref receivedBytes, receivedBeforePass + (item.RateBytes ?? 0));
                        progress?.Invoke(item with { RateBytes = Interlocked.Read(ref receivedBytes) });
                    }, async (part, digest) =>
                    {
                        await checkpointGate.WaitAsync(token);
                        try
                        {
                            var chunk = state.Chunks[part.Index];
                            var region = layout.Regions[part.Index];
                            chunk.Offset = region.Offset; chunk.LogicalSizeBytes = region.Length;
                            chunk.Sha256 = digest; chunk.Downloaded = true; chunk.SizeBytes = part.Size;
                            confirmedThisRun.Add(chunk.Index);
                            awaitingFlush.Add(chunk.Index);
                            dirty = true;
                            if (++completedSinceSave >= 4) await SaveCheckpointAsync(token);
                        }
                        finally { checkpointGate.Release(); }
                    }, completed, encryption, checksums, totalLength);
                }
                finally
                {
                    await checkpointGate.WaitAsync(CancellationToken.None);
                    try { if (dirty) await SaveCheckpointAsync(CancellationToken.None); }
                    finally { checkpointGate.Release(); }
                }
                discovery = null;
                if (layout.Regions.Count != references.Count) throw new InvalidDataException("A descoberta de partes ficou incompleta.");
                layout.Validate(checksums);

                ReportVerification(0);
                var verified = await file.VerifyAsync(state.Chunks, checksums, null, token);
                if (verified.Invalid.Count != 0)
                {
                    foreach (var index in verified.Invalid) { state.Chunks[index].Downloaded = false; state.Chunks[index].Sha256 = null; }
                    dirty = true; await SaveCheckpointAsync(token);
                    if (pass + 1 == attempts) throw new PayloadChecksumException("O arquivo parcial contém partes inválidas; o progresso íntegro foi preservado.");
                    continue;
                }
                if (checksums != null && verified.Digest != checksums.Sha256)
                    throw new PayloadChecksumException("O checksum do arquivo completo não confere. O destino anterior foi preservado.");
                // Only bytes beyond every authenticated region may be discarded, never metadata-size padding/truncation.
                if (file.Length != layout.Length)
                    throw new InvalidDataException("O tamanho do arquivo parcial diverge das regiões verificadas.");
                file.Flush();
                state.DownloadReadyToFinalize = true; state.DownloadSha256 = verified.Digest;
                if (manager != null) await manager.SaveAsync(state, token);
                file.CloseWriter();
                token.ThrowIfCancellationRequested();
                File.Move(partialPath, finalPath, overwrite: true);
                var output = new FileInfo(finalPath);
                if (encryption != null) EnsureExpectedPlaintextLength(output, expectedLength);
                status("Download concluído.");
                progress?.Invoke(new DownloadProgress(100, "Download concluído.", layout.Length, layout.Length, Assembling: true));
                return output;
            }
            throw new InvalidDataException("Não foi possível validar o download.");
        }
        finally
        {
            // Non-checkpoint preview/API calls have no resumable state to identify an abandoned partial.
            file.CloseWriter();
            if (checkpoint == null && !state.DownloadReadyToFinalize && File.Exists(partialPath)) File.Delete(partialPath);
        }

        async IAsyncEnumerable<Part> PendingPartsAsync(int pass, IReadOnlyList<Part> initial,
            [EnumeratorCancellation] CancellationToken cancellation)
        {
            if (pass > 0)
            {
                foreach (var part in knownParts.Values.OrderBy(part => part.Index))
                    if (!state.Chunks[part.Index].Downloaded) yield return part;
                yield break;
            }
            var batch = initial;
            while (true)
            {
                foreach (var part in batch) yield return part;
                var next = discovery == null ? null : await discovery.NextAsync(cancellation);
                if (next == null) yield break;
                layout.Append(next.Select(part => (part.Index, part.Size)), encryption != null, checksums, expectedLength);
                foreach (var part in next) knownParts[part.Index] = part;
                batch = await PreparePartsAsync(next);
            }
        }

        async Task<IReadOnlyList<Part>> PreparePartsAsync(IReadOnlyList<Part> batch)
        {
            var prepared = new List<Part>(batch.Count);
            foreach (var part in batch)
            {
                var region = layout.Regions[part.Index];
                previous.TryGetValue(part.Index, out var old);
                if (modern && old?.Offset is not null &&
                    (old.Offset != region.Offset || old.LogicalSizeBytes != region.Length))
                    throw new InvalidDataException("Os intervalos do checkpoint foram alterados.");
                var digest = checksums?.Chunks[part.Index] ?? old?.Sha256;
                var valid = existed && old?.Downloaded == true && await file.MatchesAsync(region, digest, token);
                if (!valid && !string.IsNullOrWhiteSpace(old?.TempChunkPath) &&
                    PartialDownloadFile.IsDigest(digest) && File.Exists(old.TempChunkPath) &&
                    new FileInfo(old.TempChunkPath).Length == region.Length)
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[DownloadBufferSize];
                    try
                    {
                        await using var input = new FileStream(old.TempChunkPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            DownloadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using var output = file.OpenRegion(part.Index);
                        int read;
                        while ((read = await input.ReadAsync(buffer, token)) > 0)
                        { hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token); }
                        valid = output.Position == region.Length && Convert.ToHexStringLower(hash.GetHashAndReset()) == digest;
                    }
                    finally { CryptographicOperations.ZeroMemory(buffer); }
                }
                prepared.Add(part with { VerifiedDigest = valid ? digest : null });
            }
            return prepared;
        }

        async Task SaveCheckpointAsync(CancellationToken cancellation)
        {
            try { file.Flush(); }
            catch
            {
                foreach (var index in awaitingFlush) { state.Chunks[index].Downloaded = false; state.Chunks[index].Sha256 = null; }
                throw;
            }
            finally
            {
                awaitingFlush.Clear();
                state.DownloadedChunks = state.Chunks.Where(chunk => chunk.Downloaded).Select(chunk => chunk.Index).ToList();
                state.PendingChunks = state.Chunks.Where(chunk => !chunk.Downloaded).Select(chunk => chunk.Index).ToList();
            }
            if (manager != null) await manager.SaveAsync(state, cancellation);
            dirty = false; completedSinceSave = 0;
            CleanupLegacyParts(state, knownParts, confirmedThisRun);
        }
        void ReportVerification(long verified)
        {
            const string message = "Verificando integridade do arquivo...";
            status(message);
            progress?.Invoke(new DownloadProgress(99, message, layout.Length, layout.Length, Assembling: true));
        }
    }

    private static void CleanupLegacyParts(TransferState state, IReadOnlyDictionary<int, Part> parts, IReadOnlySet<int> confirmed)
    {
        if (string.IsNullOrWhiteSpace(state.TempPath)) return;
        var directory = new DirectoryInfo(state.TempPath);
        if (!directory.Name.Equals(".organessonium_parts_" + state.TransferId[..Math.Min(12, state.TransferId.Length)], StringComparison.Ordinal) ||
            !directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return;
        foreach (var chunk in state.Chunks.Where(chunk => chunk.Downloaded && confirmed.Contains(chunk.Index) && !string.IsNullOrWhiteSpace(chunk.TempChunkPath)))
        {
            if (!parts.TryGetValue(chunk.Index, out var part)) continue;
            var expected = Path.Combine(directory.FullName, TempPartFileName(part));
            if (!SameFilePath(expected, chunk.TempChunkPath!) || !File.Exists(expected) ||
                (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0) continue;
            TryDelete(new FileInfo(expected));
            if (!File.Exists(expected)) chunk.TempChunkPath = null;
        }
        if (!directory.EnumerateFileSystemInfos().Any()) { directory.Delete(); state.TempPath = ""; }
    }
}
