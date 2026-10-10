using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Organessonium.Services;

internal sealed class UploadPartContent(PreparedUploadPart part, Action? onCopied) : HttpContent
{
    internal static HttpContent Create(PreparedUploadPart part, Action? onCopied = null)
    {
        var content = new UploadPartContent(part, onCopied);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = part.Length;
        return content;
    }

    protected override bool TryComputeLength(out long length) { length = part.Length; return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
    {
        await part.CopyToAsync(stream, token).ConfigureAwait(false);
        onCopied?.Invoke();
    }
}

// Only a bounded nonce and digest survive a transmitted chunk. No plaintext/ciphertext is retained.
// A retry may reproduce the original cipher only after verifying plaintext, key and authenticated context.
internal sealed class EncryptedUploadReplayState : IDisposable
{
    private readonly object _sync = new();
    private byte[]? _digest;
    private byte[]? _nonce;
    private bool _disposed;

    internal byte[] NonceFor(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> headerPrefix, ReadOnlySpan<byte> fileKey)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(headerPrefix);
        hash.AppendData(fileKey);
        hash.AppendData(plaintext);
        var digest = hash.GetHashAndReset();
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_digest == null)
                {
                    _digest = digest.ToArray();
                    _nonce = RandomNumberGenerator.GetBytes(EncryptionService.NonceBytes);
                }
                else if (!CryptographicOperations.FixedTimeEquals(_digest, digest))
                    throw new InvalidOperationException("O arquivo ou o contexto de criptografia mudou. Não é seguro repetir esse chunk.");
                return _nonce!.ToArray();
            }
        }
        finally { CryptographicOperations.ZeroMemory(digest); }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            if (_digest != null) CryptographicOperations.ZeroMemory(_digest);
            if (_nonce != null) CryptographicOperations.ZeroMemory(_nonce);
            _digest = _nonce = null;
        }
    }
}
