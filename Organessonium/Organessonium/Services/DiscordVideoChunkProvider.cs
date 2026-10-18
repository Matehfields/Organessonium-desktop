using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed record VideoAttachment(int Index, string MessageId, string Name, string Url, long Size, string AttachmentId = "");

/// <summary>CDN bytes stay in memory; REST lookups reuse DiscordApiClient's rate limiter.</summary>
public sealed class DiscordVideoChunkProvider : IVideoChunkProvider
{
    private readonly DiscordApiClient _discord;
    private readonly string _threadId;
    private readonly VideoAttachment[] _attachments;
    private readonly EncryptionService _encryption;
    private readonly EncryptedPayloadContext? _context;
    private readonly HttpClient _http;
    // Scoped to this video, without the per-thread retention of ArrayPool.Shared.
    private readonly ArrayPool<byte> _encryptedBuffers = ArrayPool<byte>.Create(DiscordLimits.AttachmentBytes, 2);
    internal IAsyncDisposable? ReadLease { get; set; }

    public DiscordVideoChunkProvider(DiscordApiClient discord, string threadId,
        IReadOnlyList<VideoAttachment> attachments, EncryptionService encryption,
        EncryptedPayloadContext? context, long expectedLength = 0, HttpClient? http = null)
    {
        _discord = discord;
        _threadId = threadId;
        _attachments = attachments.OrderBy(a => a.Index).ToArray();
        _encryption = encryption;
        _context = context;
        try
        {
            if (_attachments.Where((a, i) => a.Index != i).Any())
                throw new InvalidDataException("A ordem dos chunks do vídeo é inválida.");
            Index = new VideoChunkIndex(_attachments.Select(a => a.Size - (context == null ? 0 : EncryptionService.EnvelopeOverheadBytes)), expectedLength);
        }
        catch { http?.Dispose(); throw; }
        _http = http ?? NetworkConfig.Current.CreateClient();
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public VideoChunkIndex Index { get; }

    public async Task<byte[]> DownloadAsync(int chunkIndex, CancellationToken cancellationToken)
    {
        var attachment = _attachments[chunkIndex];
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var priority = VideoNetworkPriority.Enter();
                using var response = await _http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    if (string.IsNullOrWhiteSpace(attachment.MessageId))
                        throw new IOException("O anexo do vídeo não está mais disponível.");
                    var message = await _discord.ResolvePayloadMessageAsync(_threadId, attachment.MessageId, cancellationToken).ConfigureAwait(false);
                    if (message.Attachments.Any(item => item.Id == attachment.AttachmentId && item.Url == attachment.Url))
                        message = await _discord.ResolvePayloadMessageAsync(_threadId, attachment.MessageId, cancellationToken, refresh: true).ConfigureAwait(false);
                    var renewed = string.IsNullOrWhiteSpace(attachment.AttachmentId)
                        ? message.Attachments.FirstOrDefault(a => a.FileName == attachment.Name)
                        : message.Attachments.SingleOrDefault(a => a.Id == attachment.AttachmentId);
                    if (renewed == null || renewed.Size != attachment.Size)
                        throw new InvalidDataException("O anexo do chunk foi removido ou alterado.");
                    attachment = attachment with { Url = renewed.Url };
                    _attachments[chunkIndex] = attachment;
                    if (attempt == 4) throw new IOException("Não foi possível renovar o anexo do vídeo.");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long actual && actual != attachment.Size)
                    throw new InvalidDataException("O tamanho do anexo do vídeo é inválido.");
                var payloadLength = checked((int)attachment.Size);
                var payload = _context == null ? new byte[payloadLength] : _encryptedBuffers.Rent(payloadLength);
                try
                {
                    await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await input.ReadExactlyAsync(payload.AsMemory(0, payloadLength), cancellationToken).ConfigureAwait(false);
                    var extra = new byte[1];
                    if (await input.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                        throw new InvalidDataException("O anexo do vídeo contém bytes excedentes.");
                    if (_context == null) return payload;
                    // Authenticate GCM, role, salt, plaintext length and the real chunk ordinal first.
                    return _encryption.DecryptBytes(payload.AsSpan(0, payloadLength), _context.WithChunkIndex(chunkIndex).WithChunkCount(_attachments.Length));
                }
                finally { if (_context != null) _encryptedBuffers.Return(payload, clearArray: true); }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                last = ex;
                if (attempt < 4) await NetworkRetryPolicy.DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException("Não foi possível carregar o chunk do vídeo após quatro tentativas.", last);
    }

    public void Dispose()
    {
        _http.Dispose();
        var lease = ReadLease; ReadLease = null;
        if (lease != null) _ = ReleaseReadAsync(lease);
    }
    private static async Task ReleaseReadAsync(IAsyncDisposable lease)
    {
        try { await lease.DisposeAsync(); }
        catch (Exception error) { AppLogger.Error("Video.ReadRelease", error); }
    }
}
