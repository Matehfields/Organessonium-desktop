using System.IO;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private async Task<SharedUploadCompanionPayload?> PrepareImageCompanionAsync(FileInfo source, TransferState state,
        SemaphoreSlim checkpointGate, CancellationToken token, Action<long>? onSize, Func<long, long, Task>? onBytes)
    {
        RamMediaPreview? thumbnail = null;
        PreparedUploadPart? part = null;
        try
        {
            thumbnail = await VideoPreviewGenerator.CreateThumbnailAsync(source, token);
            using var memory = new UploadMemoryCoordinator(1);
            var context = new EncryptedPayloadContext(state.FileId, state.Encryption!, EncryptedPayloadRoles.Preview, 0, 1);
            var virtualFile = new FileInfo(Path.Combine(AppContext.BaseDirectory, "ram-thumbnail.png"));
            part = await memory.PreparePartAsync(_encryption, virtualFile, thumbnail.Length,
                EncryptionService.MaximumPlaintextPayloadBytes, context, token, deferBuffers: true,
                openSource: thumbnail.OpenRead, isolateSendCancellation: true);
            onSize?.Invoke(thumbnail.Length);
            long sent = 0;
            part.TransferProgress = wire =>
            {
                var delta = Math.Max(0, wire - Interlocked.Exchange(ref sent, wire));
                if (onBytes != null) _ = onBytes(delta, delta);
            };
            var image = thumbnail;
            return new SharedUploadCompanionPayload(context, part, async reference =>
            {
                await checkpointGate.WaitAsync(CancellationToken.None);
                try
                {
                    state.ThumbnailRef = new AttachmentReference { MessageId = reference.MessageId, AttachmentId = reference.AttachmentId };
                    state.ThumbnailMessageId = reference.MessageId;
                    await _transferManager.SaveAsync(state, CancellationToken.None);
                }
                finally { checkpointGate.Release(); }
                ThumbnailCache.Store("file-" + state.FileId + "-" + state.TransferId + "-" + reference.MessageId + "-" + reference.AttachmentId, image.Bytes);
            }, image);
        }
        catch (OperationCanceledException) { part?.Dispose(); thumbnail?.Dispose(); throw; }
        catch (TransferMemoryPressureException) { part?.Dispose(); thumbnail?.Dispose(); throw; }
        catch (Exception error)
        {
            part?.Dispose(); thumbnail?.Dispose();
            AppLogger.Error("Upload.ImageThumbnail", error);
            return null;
        }
    }
}
