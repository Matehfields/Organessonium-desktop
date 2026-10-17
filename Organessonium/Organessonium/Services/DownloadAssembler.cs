using System.IO;
using System.Security.Cryptography;
using Organessonium.Models;

namespace Organessonium.Services;

internal static class DownloadAssembler
{
    internal static async Task AssembleAsync(IEnumerable<FileInfo> chunks, FileInfo target, Action<string> status,
        Action<DownloadProgress>? progress, CancellationToken token, PayloadChecksums? checksums = null)
    {
        var files = chunks.ToArray();
        var total = files.Sum(file => file.Length);
        checksums?.Validate(total, files.Length);
        long assembled = 0;
        var lastPercent = -1;
        var buffer = new byte[1024 * 1024];
        Report(80);
        var staging = checksums == null ? target.FullName :
            Path.Combine(target.DirectoryName!, ".organessonium-" + Guid.NewGuid().ToString("N") + ".assembling");
        using var wholeHash = checksums == null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var chunkHash = checksums == null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var output = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (var index = 0; index < files.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    await using var input = new FileStream(files[index].FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                        buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        wholeHash?.AppendData(buffer, 0, read);
                        chunkHash?.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        assembled += read;
                        Report(total <= 0 ? 99 : 80 + (int)Math.Min(19, assembled * 20d / total));
                    }
                    if (checksums != null && Convert.ToHexStringLower(chunkHash!.GetHashAndReset()) != checksums.Chunks[index])
                        throw new PayloadChecksumException("O checksum de uma parte mudou durante a montagem. O arquivo não foi concluído.");
                }
                if (checksums != null && Convert.ToHexStringLower(wholeHash!.GetHashAndReset()) != checksums.Sha256)
                    throw new PayloadChecksumException("O checksum do arquivo completo não confere. O arquivo não foi concluído.");
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (checksums != null) File.Move(staging, target.FullName, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (checksums != null) { try { File.Delete(staging); } catch { } }
        }

        void Report(int percent)
        {
            if (percent == lastPercent) return;
            lastPercent = percent;
            var message = checksums == null ? "Montando arquivo final..." : "Montando e verificando checksums...";
            status(message);
            progress?.Invoke(new DownloadProgress(percent, message, total, total, Assembling: true));
        }
    }
}
