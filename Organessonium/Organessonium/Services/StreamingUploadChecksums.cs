using System.IO;
using System.Security.Cryptography;
using Organessonium.Models;

namespace Organessonium.Services;

// The whole-file scan runs alongside the network, never as a prerequisite for its first byte.
// Chunk proofs come from the plaintext actually encrypted and are durable before transmission.
internal sealed class StreamingUploadChecksums : IAsyncDisposable
{
    private readonly TransferState _state;
    private readonly TransferManager _transfers;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task<PayloadChecksums> _calculation;

    private StreamingUploadChecksums(TransferState state, TransferManager transfers, Func<Stream> open,
        long size, int chunkBytes, CancellationToken token)
    {
        _state = state;
        _transfers = transfers;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _calculation = FileChecksums.CalculateAsync(open, size, chunkBytes, _lifetime.Token);
    }

    internal static async Task<StreamingUploadChecksums?> StartAsync(TransferState state, TransferManager transfers,
        Func<Stream> open, long size, int chunkBytes, CancellationToken token)
    {
        if (!state.UploadChecksumPending && state.Checksums != null)
        {
            state.Checksums.Validate(size, state.TotalChunks);
            return null;
        }
        // Old partial uploads without a manifest retain their original compatibility contract.
        if (state.Checksums == null && state.UploadedChunks.Count > 0) return null;
        if (!state.UploadChecksumPending)
        {
            state.Checksums = new PayloadChecksums
            {
                ChunkSizeBytes = Math.Max(1, chunkBytes),
                Chunks = Enumerable.Repeat("", state.TotalChunks).ToList()
            };
            state.UploadChecksumPending = true;
        }
        state.Normalize();
        if (state.Checksums!.ChunkSizeBytes != Math.Max(1, chunkBytes))
            throw new InvalidDataException("O layout dos checksums não corresponde ao upload.");
        await transfers.SaveAsync(state, token);
        return new StreamingUploadChecksums(state, transfers, open, size, Math.Max(1, chunkBytes), token);
    }

    internal async Task ObserveAsync(int index, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        if (_calculation.IsFaulted) await _calculation.ConfigureAwait(false);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
        await _transfers.RecordUploadChecksumAsync(_state, index, hash, token).ConfigureAwait(false);
    }

    internal async Task CompleteAsync(CancellationToken token)
    {
        var result = await _calculation.WaitAsync(token).ConfigureAwait(false);
        if (!_state.Checksums!.Chunks.SequenceEqual(result.Chunks, StringComparer.Ordinal))
            throw new PayloadChecksumException("A origem mudou durante o upload; os checksums das partes enviadas não conferem.");
        _state.Checksums = result;
        _state.UploadChecksumPending = false;
        await _transfers.SaveAsync(_state, token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try { await _calculation.ConfigureAwait(false); }
        catch (Exception) { /* Observed by the upload, or cancelled while preserving its checkpoint. */ }
        _lifetime.Dispose();
    }
}
