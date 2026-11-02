using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Organessonium.Services;

public sealed partial class DiscordApiClient
{
    private readonly CancellationTokenSource _payloadLifetime = new();
    internal CancellationToken PayloadLifetime => _payloadLifetime.Token;
    private readonly ConcurrentDictionary<(string Thread, string Message), CachedPayloadMessage> _payloadMessages = new();
    private readonly object _payloadLookupSync = new();
    private static readonly ConcurrentDictionary<string, ThreadWriteState> ThreadWriters = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> AttachmentEditGates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> SharedThreadIds = new(StringComparer.Ordinal);

    internal void RegisterSharedThread(string threadId) => SharedThreadIds[_storageAccountKey + ":" + threadId] = 0;
    internal bool IsSharedThread(string threadId) => SharedThreadIds.ContainsKey(_storageAccountKey + ":" + threadId);
    internal bool HasThreadWriter(string threadId) => ThreadWriters.TryGetValue(_storageAccountKey + ":" + threadId, out var state) && Volatile.Read(ref state.Users) > 0;

    public void InvalidatePayloadMessage(string threadId, string messageId) => _payloadMessages.TryRemove((threadId, messageId), out _);

    public async Task<DiscordMessageDto> ResolvePayloadMessageAsync(string threadId, string messageId,
        CancellationToken token = default, bool refresh = false)
    {
        token.ThrowIfCancellationRequested();
        var key = (threadId, messageId);
        CachedPayloadMessage cached;
        lock (_payloadLookupSync)
        {
            if (_payloadMessages.TryGetValue(key, out var old) && old.Message.IsValueCreated && old.Message.Value.IsCompleted &&
                (refresh || old.Created < DateTimeOffset.UtcNow.AddSeconds(-DiscordLimits.ResolvedMessageCacheSeconds))) _payloadMessages.TryRemove(key, out _);
            if (!_payloadMessages.TryGetValue(key, out cached!))
            {
                if (_payloadMessages.Count >= DiscordLimits.ResolvedMessageCacheEntries)
                    foreach (var item in _payloadMessages.Where(item => item.Value.Message.IsValueCreated && item.Value.Message.Value.IsCompleted)
                        .OrderBy(item => item.Value.Created).Take(Math.Max(1, _payloadMessages.Count / 8))) _payloadMessages.TryRemove(item.Key, out _);
                if (_payloadMessages.Count >= DiscordLimits.ResolvedMessageCacheEntries)
                    throw new IOException("A resolução de mensagens está ocupada; tente novamente após a confirmação das solicitações atuais.");
                cached = new CachedPayloadMessage(DateTimeOffset.UtcNow,
                    new Lazy<Task<DiscordMessageDto>>(() => GetMessageAsync(threadId, messageId, PayloadLifetime, waitForTransientNetwork: true)));
                _payloadMessages[key] = cached;
            }
        }
        try { return await cached.Message.Value.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            if (cached.Message.IsValueCreated && cached.Message.Value.IsCompleted && !cached.Message.Value.IsCompletedSuccessfully)
                _payloadMessages.TryRemove(new KeyValuePair<(string, string), CachedPayloadMessage>(key, cached));
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, DiscordMessageDto>> ResolvePayloadMessagesAsync(string threadId,
        IEnumerable<string> messageIds, CancellationToken token = default)
    {
        var result = new ConcurrentDictionary<string, DiscordMessageDto>(StringComparer.Ordinal);
        var pending = messageIds.Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        foreach (var id in pending.ToArray())
            if (_payloadMessages.TryGetValue((threadId, id), out var cached) &&
                cached.Created >= DateTimeOffset.UtcNow.AddSeconds(-DiscordLimits.ResolvedMessageCacheSeconds) &&
                cached.Message.IsValueCreated && cached.Message.Value.IsCompletedSuccessfully)
            { result[id] = cached.Message.Value.Result; pending.Remove(id); }
        // One page may describe hundreds of logical chunks. Preserve exact IDs and variable attachment sizes.
        while (pending.Count >= 8)
        {
            var anchor = pending.OrderBy(id => id, StringComparer.Ordinal).ElementAt(pending.Count / 2);
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
                $"channels/{threadId}/messages?around={anchor}&limit={DiscordLimits.MessagesPerPage}"), token, waitForTransientNetwork: true);
            var page = await ReadJsonAsync<List<DiscordMessageDto>>(response, token);
            var before = pending.Count;
            foreach (var message in page)
                if (pending.Remove(message.Id)) result[message.Id] = message;
            if (pending.Count == before) break;
        }
        await Parallel.ForEachAsync(pending,
            new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 8 },
            async (id, cancellation) => result[id] = await ResolvePayloadMessageAsync(threadId, id, cancellation));
        return result;
    }

    public async Task<DiscordMessageDto> PreserveMessageAttachmentsAsync(string threadId, string messageId,
        IReadOnlyCollection<string> attachmentIds, CancellationToken token = default,
        IReadOnlyCollection<string>? expectedAttachmentIds = null)
    {
        if (attachmentIds.Distinct(StringComparer.Ordinal).Count() != attachmentIds.Count || attachmentIds.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("A lista de attachments preservados é inválida.");
        var editGate = AttachmentEditGates.GetOrAdd(_storageAccountKey + ":" + threadId + ":" + messageId, _ => new SemaphoreSlim(1, 1));
        await editGate.WaitAsync(token);
        try
        {
            var before = await GetMessageAsync(threadId, messageId, token);
            if (attachmentIds.Any(id => !before.Attachments.Any(attachment => attachment.Id == id)) ||
                (expectedAttachmentIds != null && !before.Attachments.Select(attachment => attachment.Id)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(expectedAttachmentIds)))
                throw new IOException("A mensagem mudou antes da limpeza; os attachments foram preservados.");
            var payload = JsonSerializer.Serialize(new { attachments = attachmentIds.Select(id => new { id }).ToArray() }, JsonOptions.Web);
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, $"channels/{threadId}/messages/{messageId}")
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") }, token);
            var updated = await ReadJsonAsync<DiscordMessageDto>(response, token);
            InvalidatePayloadMessage(threadId, messageId);
            if (!updated.Attachments.Select(attachment => attachment.Id).ToHashSet(StringComparer.Ordinal).SetEquals(attachmentIds) ||
                updated.Content != before.Content)
                throw new IOException("A resposta da limpeza não confirmou os attachments esperados.");
            return updated;
        }
        finally { editGate.Release(); }
    }

    internal async Task<IAsyncDisposable> AcquireThreadWriteAsync(string threadId, CancellationToken token)
    {
        var state = ThreadWriters.GetOrAdd(_storageAccountKey + ":" + threadId, _ => new ThreadWriteState());
        await state.Gate.WaitAsync(token);
        try
        {
            if (state.Users == 0)
            {
                var channel = await GetChannelAsync(threadId, token);
                if (channel.ThreadMetadata?.Locked == true) throw new IOException("A thread está bloqueada; nenhum bloqueio foi removido.");
                if (channel.ThreadMetadata?.Archived == true) await UnarchiveThreadAsync(threadId, token);
            }
            state.Users++;
            return new ThreadWriteLease(this, threadId, state);
        }
        finally { state.Gate.Release(); }
    }

    internal async Task<DiscordMessageDto> WriteEncodedDocumentAsync(string channelId, string encoded,
        string? messageId, CancellationToken token, bool serializeRoute = true)
    {
        if (encoded.Length <= DiscordLimits.MessageContentCharacters)
            return string.IsNullOrEmpty(messageId) ? await CreateMessageAsync(channelId, encoded, token, serializeRoute: serializeRoute) :
                await UpdateMessageAsync(channelId, messageId, encoded, token, clearAttachments: true);
        var bytes = Encoding.UTF8.GetBytes(encoded);
        if (bytes.Length > DiscordLimits.AttachmentBytes) throw new InvalidDataException("O documento excede o limite de metadata.");
        using var response = await SendAsync(() =>
        {
            var payload = JsonSerializer.Serialize(new { content = "metadata", attachments = new[] { new { id = 0, filename = "metadata.json" } } }, JsonOptions.Web);
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
            multipart.Add(new StreamContent(new MemoryStream(bytes, writable: false)), "files[0]", "metadata.json");
            return new HttpRequestMessage(string.IsNullOrEmpty(messageId) ? HttpMethod.Post : HttpMethod.Patch,
                string.IsNullOrEmpty(messageId) ? $"channels/{channelId}/messages" : $"channels/{channelId}/messages/{messageId}")
            { Content = multipart };
        }, token, serializeRoute: serializeRoute);
        if (!string.IsNullOrEmpty(messageId)) InvalidatePayloadMessage(channelId, messageId);
        return await ReadJsonAsync<DiscordMessageDto>(response, token);
    }

    private sealed record CachedPayloadMessage(DateTimeOffset Created, Lazy<Task<DiscordMessageDto>> Message);
    private sealed class ThreadWriteState { internal readonly SemaphoreSlim Gate = new(1, 1); internal int Users; }
    private sealed class ThreadWriteLease(DiscordApiClient api, string threadId, ThreadWriteState state) : IAsyncDisposable
    {
        private int _disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await state.Gate.WaitAsync();
            try
            {
                if (--state.Users == 0)
                {
                    try { await api.ArchiveThreadAsync(threadId); }
                    catch (Exception error) { AppLogger.Error("SharedStorage.Archive", error); }
                }
            }
            finally { state.Gate.Release(); }
        }
    }
}
