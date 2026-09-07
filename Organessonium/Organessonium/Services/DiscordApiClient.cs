using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace Organessonium.Services;

public sealed class DiscordApiClient : IDisposable
{
    public const int MaxActiveThreadsCode = 160006;

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _routeReadyAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _routeLocks = new(StringComparer.Ordinal);

public DiscordApiClient(string botToken)
        : this(botToken, NetworkConfig.Current.CreateClient())
    {
    }

    public DiscordApiClient(string botToken, HttpClient http)
    {
        _http = http;
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.BaseAddress = new Uri("https://discord.com/api/v10/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", botToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Organessonium-CSharp/1.0");
    }

    public async Task<DiscordChannelDto> GetChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"channels/{channelId}"), cancellationToken);
        return await ReadJsonAsync<DiscordChannelDto>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscordGuildDto>> GetCurrentUserGuildsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "users/@me/guilds"), cancellationToken);
        return await ReadJsonAsync<List<DiscordGuildDto>>(response, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscordChannelDto>> GetGuildChannelsAsync(string guildId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"guilds/{guildId}/channels"), cancellationToken);
        return await ReadJsonAsync<List<DiscordChannelDto>>(response, cancellationToken);
    }

    public async Task<DiscordChannelDto> CreateGuildChannelAsync(
        string guildId,
        string name,
        int type,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new { name, type }, JsonOptions.Web);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"guilds/{guildId}/channels");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return request;
        }, cancellationToken);
        return await ReadJsonAsync<DiscordChannelDto>(response, cancellationToken);
    }

public async Task<IReadOnlyList<DiscordMessageDto>> GetMessagesAsync(
        string channelId,
        int? limit = 500,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<DiscordMessageDto>, Task>? onBatch = null)
    {
        var messages = new List<DiscordMessageDto>();
        string? before = null;

        while (!limit.HasValue || messages.Count < limit.Value)
        {
            var take = limit.HasValue ? Math.Min(100, limit.Value - messages.Count) : 100;
            var url = $"channels/{channelId}/messages?limit={take}";
            if (!string.IsNullOrWhiteSpace(before))
            {
                url += "&before=" + before;
            }

            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            var batch = await ReadJsonAsync<List<DiscordMessageDto>>(response, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            messages.AddRange(batch);
            if (onBatch != null)
            {
                await onBatch(batch);
            }

            before = batch[^1].Id;
            if (batch.Count < take)
            {
                break;
            }
        }

        return messages;
    }

    public async Task<IReadOnlyList<DiscordMessageDto>> GetMessagesInParallelAsync(
        string channelId,
        int workers,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<DiscordMessageDto>, Task>? onBatch = null)
    {
        workers = Math.Clamp(workers, 1, 16);

        using (var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"channels/{channelId}/messages?limit=1"), cancellationToken))
        {
            var newest = await ReadJsonAsync<List<DiscordMessageDto>>(response, cancellationToken);
            if (newest.Count == 0)
            {
                return [];
            }

            var newestMs = (ulong.Parse(newest[0].Id) >> 22) + DiscordEpochMilliseconds;
            var windowMs = (double)(newestMs + 1 - DiscordEpochMilliseconds) / workers;
            var gate = new SemaphoreSlim(1);
            var tasks = new List<Task<List<DiscordMessageDto>>>(workers);
            for (var worker = 0; worker < workers; worker++)
            {
                var startMs = DiscordEpochMilliseconds + (ulong)(windowMs * worker);
                var endMs = worker == workers - 1
                    ? newestMs + 1
                    : DiscordEpochMilliseconds + (ulong)(windowMs * (worker + 1));
                tasks.Add(FetchWindowAsync(channelId, startMs, endMs, gate, onBatch, cancellationToken));
            }

            var windows = await Task.WhenAll(tasks);
            var seen = new HashSet<ulong>();
            var merged = new List<DiscordMessageDto>();
            foreach (var window in windows)
            {
                foreach (var message in window)
                {
                    if (seen.Add(ulong.Parse(message.Id)))
                    {
                        merged.Add(message);
                    }
                }
            }

            merged.Sort((a, b) => ulong.Parse(b.Id).CompareTo(ulong.Parse(a.Id)));
            return merged;
        }
    }

    private async Task<List<DiscordMessageDto>> FetchWindowAsync(
        string channelId,
        ulong startMs,
        ulong endMs,
        SemaphoreSlim gate,
        Func<IReadOnlyList<DiscordMessageDto>, Task>? onBatch,
        CancellationToken cancellationToken)
    {
        var results = new List<DiscordMessageDto>();
        var windowStartId = (startMs - DiscordEpochMilliseconds) << 22;
        var before = (endMs - DiscordEpochMilliseconds) << 22;

        while (true)
        {
            var url = $"channels/{channelId}/messages?limit=100&before={before}";

            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            var batch = await ReadJsonAsync<List<DiscordMessageDto>>(response, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var clipped = batch
                .Where(message => ulong.Parse(message.Id) >= windowStartId)
                .ToList();
            results.AddRange(clipped);
            if (onBatch != null && clipped.Count > 0)
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await onBatch(clipped);
                }
                finally
                {
                    gate.Release();
                }
            }

            if (clipped.Count < batch.Count || batch.Count < 100)
            {
                break;
            }

            before = ulong.Parse(batch[^1].Id);
        }

        return results;
    }

    private const ulong DiscordEpochMilliseconds = 1420070400000UL;

    public async Task<IReadOnlyDictionary<string, DiscordMessageDto>> GetMessagesByIdsAsync(
        string channelId,
        IReadOnlyCollection<string> messageIds,
        CancellationToken cancellationToken = default,
        Action<int, int>? onResolved = null)
    {
        var wanted = messageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return new Dictionary<string, DiscordMessageDto>(StringComparer.Ordinal);
        }

        var found = new Dictionary<string, DiscordMessageDto>(StringComparer.Ordinal);
        var total = wanted.Count;
        string? before = null;
        while (wanted.Count > 0)
        {
            var url = $"channels/{channelId}/messages?limit=100";
            if (!string.IsNullOrWhiteSpace(before))
            {
                url += "&before=" + before;
            }

            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), cancellationToken);
            var batch = await ReadJsonAsync<List<DiscordMessageDto>>(response, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var previousCount = found.Count;
            foreach (var message in batch)
            {
                if (wanted.Remove(message.Id))
                {
                    found[message.Id] = message;
                }
            }

            if (found.Count != previousCount)
            {
                onResolved?.Invoke(found.Count, total);
            }

            if (batch.Count < 100)
            {
                break;
            }

            before = batch[^1].Id;
        }

        return found;
    }
    public async Task<DiscordMessageDto> GetMessageAsync(
        string channelId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"channels/{channelId}/messages/{messageId}"),
            cancellationToken);
        return await ReadJsonAsync<DiscordMessageDto>(response, cancellationToken);
    }

    public async Task<DiscordMessageDto> CreateMessageAsync(
        string channelId,
        string content,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        var payload = JsonSerializer.Serialize(new { content }, JsonOptions.Web);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return request;
        }, cancellationToken, onRateLimitWait);
        return await ReadJsonAsync<DiscordMessageDto>(response, cancellationToken);
    }

    public async Task DeleteMessageAsync(string channelId, string messageId, CancellationToken cancellationToken = default)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{channelId}/messages/{messageId}"), cancellationToken);
    }

    public async Task<string> CreateForumPostWithFileAsync(
        string forumId,
        string title,
        string content,
        FileInfo file,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        var result = await CreateForumPostWithFileDetailedAsync(
            forumId,
            title,
            content,
            file,
            cancellationToken,
            onRateLimitWait);
        return result.ThreadId;
    }

    public async Task<DiscordForumPostResult> CreateForumPostWithFileDetailedAsync(
        string forumId,
        string title,
        string content,
        FileInfo file,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await CreateForumPostWithFileDetailedOnceAsync(
                    forumId,
                    title,
                    content,
                    file,
                    cancellationToken,
                    onRateLimitWait);
            }
            catch (DiscordApiException ex) when (ex.DiscordCode == MaxActiveThreadsCode && attempt < 2)
            {
                var archived = await ArchiveActiveThreadsAsync(
                    forumId,
                    attempt == 0 ? 100 : 500,
                    cancellationToken,
                    onRateLimitWait);
                if (archived <= 0)
                {
                    throw;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken);
            }
        }

        throw new DiscordApiException(HttpStatusCode.BadRequest, "Discord recusou a criacao da thread do arquivo.");
    }

    private async Task<DiscordForumPostResult> CreateForumPostWithFileDetailedOnceAsync(
        string forumId,
        string title,
        string content,
        FileInfo file,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"channels/{forumId}/threads");
            request.Content = BuildForumThreadContent(title, content, file);
            return request;
        }, cancellationToken, onRateLimitWait);
        var channel = await ReadJsonAsync<DiscordChannelDto>(response, cancellationToken);
        var messageId = channel.Message?.Id ?? "";
        if (string.IsNullOrWhiteSpace(messageId))
        {
            var messages = await GetMessagesAsync(channel.Id, 5, cancellationToken);
            messageId = messages
                .Where(message => message.Attachments.Any(a => a.FileName.Equals(file.Name, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(message => Snowflake(message.Id))
                .Select(message => message.Id)
                .FirstOrDefault() ?? "";
        }

        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new InvalidOperationException("Não foi possível localizar a mensagem inicial do arquivo.");
        }

        return new DiscordForumPostResult(channel.Id, messageId);
    }

    public async Task<IReadOnlyList<DiscordChannelDto>> GetActiveThreadsAsync(
        string channelId,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"channels/{channelId}/threads/active"),
            cancellationToken,
            onRateLimitWait);
        var result = await ReadJsonAsync<DiscordActiveThreadsDto>(response, cancellationToken);
        return result.Threads;
    }

    public async Task ArchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        await SetThreadArchivedAsync(threadId, archived: true, cancellationToken, onRateLimitWait);
    }

    public async Task UnarchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        await SetThreadArchivedAsync(threadId, archived: false, cancellationToken, onRateLimitWait);
    }

    public async Task<int> ArchiveActiveThreadsAsync(
        string channelId,
        int maxToArchive = 100,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        var active = await GetActiveThreadsAsync(channelId, cancellationToken, onRateLimitWait);
        var archived = 0;
        foreach (var thread in active
                     .Where(thread => !string.IsNullOrWhiteSpace(thread.Id))
                     .OrderBy(thread => Snowflake(thread.Id))
                     .Take(Math.Max(1, maxToArchive)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ArchiveThreadAsync(thread.Id, cancellationToken, onRateLimitWait);
                archived++;
            }
            catch (DiscordApiException ex) when ((int)ex.StatusCode is 403 or 404)
            {
                AppLogger.Error("Discord.ArchiveActiveThread", ex, thread.Id);
            }
        }

        return archived;
    }

    public async Task<DiscordMessageDto> SendFileMessageAsync(
        string channelId,
        string content,
        FileInfo file,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages");
            request.Content = BuildMessageFileContent(content, file);
            return request;
        }, cancellationToken, onRateLimitWait);
        return await ReadJsonAsync<DiscordMessageDto>(response, cancellationToken);
    }

    public async Task<byte[]> DownloadAttachmentBytesAsync(
        DiscordAttachmentDto attachment,
        CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        foreach (var url in new[] { attachment.Url, attachment.ProxyUrl }
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                using var response = await _http.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync(cancellationToken);
                }

                lastError = new HttpRequestException(
                    $"Download do anexo retornou HTTP {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("Não foi possível baixar o anexo do serial.", lastError);
    }

    public async Task DeleteChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{channelId}"), cancellationToken);
    }

    private async Task SetThreadArchivedAsync(
        string threadId,
        bool archived,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait)
    {
        var payload = JsonSerializer.Serialize(new { archived }, JsonOptions.Web);
        using var _ = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"channels/{threadId}");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return request;
        }, cancellationToken, onRateLimitWait);
    }

    public void Dispose()
    {
        _http.Dispose();
        foreach (var gate in _routeLocks.Values)
        {
            gate.Dispose();
        }
    }

    private static MultipartFormDataContent BuildForumThreadContent(string title, string content, FileInfo file)
    {
        var payload = JsonSerializer.Serialize(new
        {
            name = Truncate(title, 100),
            message = new
            {
                content,
                attachments = new[] { new { id = "0", filename = file.Name } }
            }
        }, JsonOptions.Web);

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
        multipart.Add(BuildFileContent(file), "files[0]", file.Name);
        return multipart;
    }

    private static MultipartFormDataContent BuildMessageFileContent(string content, FileInfo file)
    {
        var payload = JsonSerializer.Serialize(new
        {
            content,
            attachments = new[] { new { id = "0", filename = file.Name } }
        }, JsonOptions.Web);

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
        multipart.Add(BuildFileContent(file), "files[0]", file.Name);
        return multipart;
    }

    private static StreamContent BuildFileContent(FileInfo file)
    {
        var content = new StreamContent(file.OpenRead());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            using var request = requestFactory();
            var routeKey = BuildRouteKey(request);
            var routeLock = _routeLocks.GetOrAdd(routeKey, _ => new SemaphoreSlim(1, 1));
            await routeLock.WaitAsync(cancellationToken);
            try
            {
                await WaitForRouteAsync(routeKey, onRateLimitWait, cancellationToken);

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (HttpRequestException) when (attempt < 11)
                {
                    await WaitTransientRetryAsync(attempt, cancellationToken);
                    continue;
                }
                catch (HttpRequestException ex)
                {
                    throw new DiscordApiException(HttpStatusCode.ServiceUnavailable, "Falha de rede ao falar com o Discord: " + ex.Message, ex);
                }

                if ((int)response.StatusCode != 429)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        TrackRouteLimit(routeKey, response);
                        return response;
                    }

                    var statusCode = response.StatusCode;
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    response.Dispose();
                    if (IsTransientStatus(statusCode) && attempt < 11)
                    {
                        await WaitTransientRetryAsync(attempt, cancellationToken);
                        continue;
                    }

                    throw new DiscordApiException(statusCode, body);
                }

                var retry = await ResolveRetryAfterAsync(response, cancellationToken);
                _routeReadyAt[routeKey] = DateTimeOffset.UtcNow.Add(retry);
                response.Dispose();
                await WaitWithProgressAsync(retry, onRateLimitWait, cancellationToken);
            }
            finally
            {
                routeLock.Release();
            }
        }

        throw new DiscordApiException(HttpStatusCode.TooManyRequests, "Discord rate limit persistiu após várias tentativas.");
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    private static Task WaitTransientRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delayMs = Math.Min(8000, 500 * Math.Pow(2, Math.Min(attempt, 4)));
        return Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
    }

    private static string BuildRouteKey(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        var path = uri == null
            ? string.Empty
            : uri.IsAbsoluteUri
                ? uri.AbsolutePath
                : uri.OriginalString.Split('?', 2)[0];
        return request.Method.Method + " " + path;
    }

    private async Task WaitForRouteAsync(
        string routeKey,
        Func<TimeSpan, Task>? onRateLimitWait,
        CancellationToken cancellationToken)
    {
        if (!_routeReadyAt.TryGetValue(routeKey, out var readyAt))
        {
            return;
        }

        var delay = readyAt - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            _routeReadyAt.TryRemove(routeKey, out _);
            return;
        }

        await WaitWithProgressAsync(delay, onRateLimitWait, cancellationToken);
        _routeReadyAt.TryRemove(routeKey, out _);
    }

    private void TrackRouteLimit(string routeKey, HttpResponseMessage response)
    {
        if (!TryReadHeaderInt(response, "X-RateLimit-Remaining", out var remaining) || remaining > 0)
        {
            return;
        }

        if (TryReadHeaderDouble(response, "X-RateLimit-Reset-After", out var seconds) && seconds > 0)
        {
            _routeReadyAt[routeKey] = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
    }

    private static async Task WaitWithProgressAsync(
        TimeSpan delay,
        Func<TimeSpan, Task>? onRateLimitWait,
        CancellationToken cancellationToken)
    {
        var readyAt = DateTimeOffset.UtcNow.Add(delay);
        while (true)
        {
            var remaining = readyAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            if (onRateLimitWait != null)
            {
                await onRateLimitWait(remaining);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, remaining.TotalMilliseconds)), cancellationToken);
        }
    }

    private static bool TryReadHeaderInt(HttpResponseMessage response, string name, out int value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) &&
               int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadHeaderDouble(HttpResponseMessage response, string name, out double value)
    {
        value = 0;
        return response.Headers.TryGetValues(name, out var values) &&
               double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions.Web, cancellationToken);
        return value ?? throw new DiscordApiException(response.StatusCode, "Resposta vazia do Discord.");
    }

    private static async Task<TimeSpan> ResolveRetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("retry_after", out var retry) && retry.TryGetDouble(out var seconds))
            {
                return TimeSpan.FromMilliseconds(Math.Max(250, seconds * 1000.0));
            }
        }
        catch
        {
            // Fall back below.
        }

        return TimeSpan.FromSeconds(1);
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "arquivo";
        }

        return value.Length <= max ? value : value[..max];
    }

    private static ulong Snowflake(string id)
    {
        return ulong.TryParse(id, out var parsed) ? parsed : 0;
    }
}

public sealed record DiscordForumPostResult(string ThreadId, string MessageId);

public sealed class DiscordApiException : Exception
{
    public DiscordApiException(HttpStatusCode statusCode, string body)
        : this(statusCode, body, null)
    {
    }

    public DiscordApiException(HttpStatusCode statusCode, string body, Exception? innerException)
        : base(BuildMessage(statusCode, body), innerException)
    {
        StatusCode = statusCode;
        Body = body;
        DiscordCode = ParseDiscordCode(body);
    }

    public HttpStatusCode StatusCode { get; }
    public string Body { get; }
    public int? DiscordCode { get; }

    private static string BuildMessage(HttpStatusCode statusCode, string body)
    {
        if (body.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return "Token inválido ou expirado.";
        }

        if (ParseDiscordCode(body) == DiscordApiClient.MaxActiveThreadsCode)
        {
            return "O fórum do Discord atingiu o limite de threads ativas.";
        }

        return $"Discord HTTP {(int)statusCode}: {body}";
    }

    private static int? ParseDiscordCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("code", out var code) && code.TryGetInt32(out var value))
            {
                return value;
            }
        }
        catch
        {
            // Non-JSON responses are handled by the status code/message.
        }

        return null;
    }
}

public sealed class DiscordActiveThreadsDto
{
    [JsonPropertyName("threads")]
    public List<DiscordChannelDto> Threads { get; set; } = [];
}

public sealed class DiscordChannelDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("guild_id")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("parent_id")]
    public string ParentId { get; set; } = "";

    [JsonPropertyName("message")]
    public DiscordMessageDto? Message { get; set; }
}

public sealed class DiscordGuildDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class DiscordMessageDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("author")]
    public DiscordUserDto? Author { get; set; }

    [JsonPropertyName("attachments")]
    public List<DiscordAttachmentDto> Attachments { get; set; } = [];
}

public sealed class DiscordUserDto
{
    [JsonPropertyName("bot")]
    public bool Bot { get; set; }
}

public sealed class DiscordAttachmentDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("filename")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("proxy_url")]
    public string ProxyUrl { get; set; } = "";

    [JsonPropertyName("size")]
    public long Size { get; set; }
}
