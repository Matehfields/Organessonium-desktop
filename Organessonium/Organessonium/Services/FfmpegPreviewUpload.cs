using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed record FfmpegPreviewUploadResult(long SizeBytes, ForumUploadResult Upload);

internal static class FfmpegPreviewUpload
{
    internal const string StagingPrefix = ".organessonium-preview-";
    internal static bool IsStagingDirectory(string name) => name.StartsWith(StagingPrefix, StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParseExact(name[StagingPrefix.Length..], "N", out _);

    internal static async Task<FfmpegPreviewUploadResult> UploadAsync(FileInfo source, string previewName,
        string threadId, ForumUploader uploader, EncryptionService encryption, EncryptedPayloadContext? payload,
        bool useAttachmentReferences, bool hardwareAcceleration, Action<string>? status, Action<long, long>? progress,
        CancellationToken token, Func<TimeSpan, Task>? onRateLimitWait = null, string? executable = null,
        Action<int>? generationProgress = null, int maxParallelParts = 4, AdaptiveUploadGate? adaptiveUploadGate = null,
        Action<bool>? onTransferActivity = null, Action<long>? onPreviewSize = null,
        SharedUploadContext? sharedUpload = null, Func<bool, CancellationToken, Task>? generationState = null)
    {
        using var staged = new StagedPreview(source);
        if (generationState != null) await generationState(true, token).ConfigureAwait(false);
        try
        {
            status?.Invoke("Gerando prévia 480p");
            generationProgress?.Invoke(0);
            using (await FfmpegMediaService.EnterAsync(token).ConfigureAwait(false))
            using (FfmpegMediaService.OpenSourceLock(source))
            {
                await FfmpegMediaService.ReadOutputAsync(source, hardwareAcceleration, false,
                    (input, cancellation) => input.CopyToAsync(staged.Output, 128 * 1024, cancellation), token, executable,
                    generationProgress: generationProgress).ConfigureAwait(false);
            }
        }
        finally { if (generationState != null) await generationState(false, token).ConfigureAwait(false); }
        await staged.Output.FlushAsync(token).ConfigureAwait(false);
        var length = staged.Output.Length;
        if (length == 0) throw new IOException("O gerador não produziu vídeo para a prévia 480p.");
        staged.File.Refresh();
        generationProgress?.Invoke(100);
        onPreviewSize?.Invoke(length);
        status?.Invoke("Enviando prévia 480p");

        var chunkBytes = payload == null ? DiscordLimits.AttachmentBytes : FfmpegMediaService.PreviewChunkBytes;
        var chunks = ArchiveSplitter.GetPartCount(length, chunkBytes);
        var context = payload?.WithChunkCount(chunks);
        using var memory = new UploadMemoryCoordinator(maxParallelParts);
        using var localGate = adaptiveUploadGate == null ? new AdaptiveUploadGate(maxParallelParts, adaptive: false) : null;
        long sent = 0;
        var bytes = new UploadByteProgress(length, chunkBytes, context != null,
            (logical, _) => progress?.Invoke(Interlocked.Add(ref sent, logical), length));
        var result = await uploader.UploadResumableGeneratedAsync(previewName, chunks,
            index => context != null
                ? memory.PreparePartAsync(encryption, staged.File, length, chunkBytes, context.WithChunkIndex(index), token,
                    deferBuffers: useAttachmentReferences, openSource: staged.OpenRead, isolateSendCancellation: sharedUpload != null)
                : UploadMemoryCoordinator.PreparePlainPartAsync(staged.File, length, chunkBytes, index, token, staged.OpenRead),
            threadId, "", new Dictionary<int, string>(), cancellationToken: token, onRateLimitWait: onRateLimitWait,
            maxParallelParts: 16, encryptedPayload: context, adaptiveUploadGate: adaptiveUploadGate ?? localGate,
            onTransferActivity: onTransferActivity,
            useAttachmentReferences: context != null && useAttachmentReferences,
            batchPreparationGate: memory.BatchPreparationGate,
            streamPreparedParts: context != null && useAttachmentReferences,
            onPartTransferProgress: (index, wire) => bytes.Report(index, wire),
            maxPreparedPartBytes: checked(chunkBytes + (context == null ? 0 : EncryptionService.EnvelopeOverheadBytes)),
            legacyPreviewKind: context == null ? "video-480" : null, sharedUpload: sharedUpload).ConfigureAwait(false);
        status?.Invoke("Concluindo");
        return new FfmpegPreviewUploadResult(length, result);
    }

    private sealed class StagedPreview : IDisposable
    {
        private readonly DirectoryInfo _directory;
        internal FileInfo File { get; }
        internal FileStream Output { get; }

        internal StagedPreview(FileInfo source)
        {
            _directory = Directory.CreateDirectory(Path.Combine(source.DirectoryName!, StagingPrefix + Guid.NewGuid().ToString("N")));
            try
            {
                _directory.Attributes |= FileAttributes.Hidden;
                File = new FileInfo(Path.Combine(_directory.FullName, "preview.mp4"));
                Output = new FileStream(File.FullName, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            }
            catch { try { _directory.Delete(); } catch (IOException) { } throw; }
        }

        internal Stream OpenRead() => new FileStream(File.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

        public void Dispose()
        {
            Output.Dispose();
            try { _directory.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
