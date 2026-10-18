using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class TransferManager
{
    private readonly SemaphoreSlim _downloadCreationGate = new(1, 1);

    internal static string DownloadIdentity(AppEntry entry) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { entry.FileId, entry.ThreadId, entry.BatchId, entry.Name, entry.VersionId,
            entry.SizeBytes, entry.Encryption, entry.ChunkReferences, entry.ChunkMessageIds, entry.Checksums }, JsonOptions.Web))));

    public Task RecordDownloadFailureAsync(TransferState state, Exception error, CancellationToken token = default) =>
        error is DownloadAlreadyActiveException ? Task.CompletedTask :
        state.TransferType == TransferTypes.Download ? PauseAsync(state, error, token) : FailAsync(state, error, token);

    private static void CleanupDownload(TransferState state)
    {
        if (state.DownloadCheckpointVersion == TransferState.DirectDownloadVersion)
        {
            if (!PartialDownloadFile.OwnsPartial(state)) throw new InvalidDataException("O caminho parcial não pertence a esta transferência.");
            if (PartialDownloadFile.IsActive(state.FinalFilePath!)) throw new DownloadAlreadyActiveException();
            if (File.Exists(state.PartialFilePath) && (File.GetAttributes(state.PartialFilePath!) & FileAttributes.ReparsePoint) == 0)
                File.Delete(state.PartialFilePath!);
        }
        if (string.IsNullOrWhiteSpace(state.TempPath)) return;
        var directory = new DirectoryInfo(state.TempPath);
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 ||
            directory.Name != ".organessonium_parts_" + state.TransferId[..Math.Min(12, state.TransferId.Length)]) return;
        foreach (var chunk in state.Chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk.TempChunkPath)) continue;
            var file = new FileInfo(chunk.TempChunkPath);
            if (!string.Equals(file.DirectoryName, directory.FullName, StringComparison.OrdinalIgnoreCase) ||
                !file.Name.StartsWith($"part_{chunk.Index:000000}_", StringComparison.Ordinal) ||
                !file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            file.Delete();
        }
        if (!directory.EnumerateFileSystemInfos().Any()) directory.Delete();
    }
}
