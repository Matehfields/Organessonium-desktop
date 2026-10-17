using System.IO;
using System.Security.Cryptography;
using Organessonium.Models;

namespace Organessonium.Services;

internal static class FileChecksums
{
    // One bounded sequential read of the exact representation that will be uploaded, including virtual faststart.
    internal static Task<PayloadChecksums> CalculateAsync(Func<Stream> open, long size, int chunkBytes,
        CancellationToken token) => Task.Run(async () =>
    {
        chunkBytes = Math.Max(1, chunkBytes);
        await using var input = open();
        if (input.Length != size) throw new IOException("O tamanho da origem mudou durante o cálculo do checksum.");
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var chunk = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var result = new PayloadChecksums { ChunkSizeBytes = chunkBytes };
        var buffer = new byte[Math.Min(chunkBytes, 1024 * 1024)];
        try
        {
            long consumed = 0;
            do
            {
                var remaining = (int)Math.Min(chunkBytes, size - consumed);
                while (remaining > 0)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("A origem mudou durante o cálculo do checksum.");
                    whole.AppendData(buffer, 0, read);
                    chunk.AppendData(buffer, 0, read);
                    remaining -= read;
                    consumed += read;
                }
                result.Chunks.Add(Convert.ToHexStringLower(chunk.GetHashAndReset()));
            } while (consumed < size);
            if (await input.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                throw new IOException("A origem mudou durante o cálculo do checksum.");
            result.Sha256 = Convert.ToHexStringLower(whole.GetHashAndReset());
            result.Validate(size, result.Chunks.Count);
            return result;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }, token);

    internal static async Task<bool> MatchesAsync(FileInfo file, string hash, CancellationToken token)
    {
        await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)) == hash;
    }
}
