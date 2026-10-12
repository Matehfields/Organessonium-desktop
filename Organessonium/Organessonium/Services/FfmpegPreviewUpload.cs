using System.IO;
using System.Security.Cryptography;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed record FfmpegPreviewUploadResult(long SizeBytes, ForumUploadResult Upload);

internal static class FfmpegPreviewUpload
{
    internal static async Task<FfmpegPreviewUploadResult> UploadAsync(FileInfo source, string previewName,
        string threadId, ForumUploader uploader, EncryptionService encryption, EncryptedPayloadContext? payload,
        bool useAttachmentReferences, bool hardwareAcceleration, Action<string>? status, Action<long, long>? progress,
        CancellationToken token, Func<TimeSpan, Task>? onRateLimitWait = null, string? executable = null)
    {
        using var gate = await FfmpegMediaService.EnterAsync(token).ConfigureAwait(false);
        using var sourceLock = FfmpegMediaService.OpenSourceLock(source);
        status?.Invoke("Analisando prévia 480p");
        long length = 0;
        byte[] expectedHash;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await FfmpegMediaService.ReadOutputAsync(source, hardwareAcceleration, false, async (input, cancellation) =>
            {
                var buffer = new byte[64 * 1024];
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                    {
                        length = checked(length + read);
                        hash.AppendData(buffer, 0, read);
                    }
                }
                finally { CryptographicOperations.ZeroMemory(buffer); }
            }, token, executable, time => status?.Invoke("Analisando prévia 480p: " + FormatTime(time))).ConfigureAwait(false);
            expectedHash = hash.GetHashAndReset();
        }
        if (length == 0) throw new IOException("O gerador não produziu vídeo para a prévia 480p.");
        var chunks = ArchiveSplitter.GetPartCount(length, FfmpegMediaService.PreviewChunkBytes);
        var context = payload?.WithChunkCount(chunks);
        ForumUploadResult? result = null;
        long sent = 0;
        var bytes = new UploadByteProgress(length, FfmpegMediaService.PreviewChunkBytes, context != null,
            (logical, _) => progress?.Invoke(Interlocked.Add(ref sent, logical), length));
        using var actualHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            // The existing v1 header authenticates ChunkCount. Count without retaining media, then
            // repeat the same deterministic encode and verify it before publishing preview metadata.
            await FfmpegMediaService.ReadOutputAsync(source, hardwareAcceleration, false, async (input, cancellation) =>
            {
                var next = 0;
                async Task<PreparedUploadPart> Prepare(int index)
                {
                    if (index != next) throw new InvalidDataException("A prévia foi solicitada fora da ordem de geração.");
                    var count = checked((int)Math.Min(FfmpegMediaService.PreviewChunkBytes,
                        length - (long)index * FfmpegMediaService.PreviewChunkBytes));
                    var encryptedLength = checked(count + (context == null ? 0 : EncryptionService.EnvelopeOverheadBytes));
                    await RamUploadBudget.Preparation.WaitAsync(cancellation).ConfigureAwait(false);
                    UploadChunkBufferLease? lease = null;
                    var slot = false;
                    try
                    {
                        await RamUploadBudget.Chunks.WaitAsync(cancellation).ConfigureAwait(false);
                        slot = true;
                        lease = RamUploadBudget.Pool.Rent(context == null ? 0 : count, encryptedLength,
                            () => RamUploadBudget.Chunks.Release(), exactSize: true);
                        var plaintext = context == null ? lease.Ciphertext[..count] : lease.Plaintext[..count];
                        var read = 0;
                        while (read < count)
                        {
                            var received = await input.ReadAsync(plaintext[read..], cancellation).ConfigureAwait(false);
                            if (received == 0)
                                throw new EndOfStreamException($"Bloco {index + 1}/{chunks}: {read}/{count} bytes recebidos.");
                            read += received;
                        }
                        actualHash.AppendData(plaintext.Span);
                        if (index == chunks - 1)
                        {
                            var tail = new byte[1];
                            if (await input.ReadAsync(tail, cancellation).ConfigureAwait(false) != 0 ||
                                !CryptographicOperations.FixedTimeEquals(actualHash.GetHashAndReset(), expectedHash))
                                throw new InvalidDataException("A geração da prévia mudou entre as etapas. O arquivo original foi preservado.");
                        }
                        if (context != null)
                            encryption.EncryptBytesInto(plaintext.Span, lease.Ciphertext.Span, context.WithChunkIndex(index));
                        lease.ReleasePlaintext();
                        next++;
                        var name = context == null ? (chunks == 1 ? previewName : $"{previewName}.{index + 1:D3}")
                            : EncryptedPayloadProtocol.FileName(context.Role, index, chunks);
                        return new PreparedUploadPart(name, encryptedLength, count, lease);
                    }
                    catch
                    {
                        if (lease != null) lease.Dispose(); else if (slot) RamUploadBudget.Chunks.Release();
                        throw;
                    }
                    finally { RamUploadBudget.Preparation.Release(); }
                }
                status?.Invoke("Gerando e enviando prévia 480p");
                result = await uploader.UploadResumableGeneratedAsync(previewName, chunks, Prepare, threadId, "",
                    new Dictionary<int, string>(), cancellationToken: cancellation, onRateLimitWait: onRateLimitWait,
                    // Forward-only encoder. The bounded producer prepares batches in index order.
                    maxParallelParts: 1, encryptedPayload: context,
                    useAttachmentReferences: context != null && useAttachmentReferences,
                    batchPreparationGate: RamUploadBudget.Batches,
                    onPartTransferProgress: (index, wire) => bytes.Report(index, wire),
                    maxPreparedPartBytes: FfmpegMediaService.PreviewChunkBytes + EncryptionService.EnvelopeOverheadBytes,
                    legacyPreviewKind: context == null ? "video-480" : null).ConfigureAwait(false);
            }, token, executable).ConfigureAwait(false);
            return new FfmpegPreviewUploadResult(length, result ?? throw new IOException("A prévia não foi confirmada."));
        }
        finally { CryptographicOperations.ZeroMemory(expectedHash); }
    }

    private static string FormatTime(TimeSpan value) => value.ToString(value.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss");
}
