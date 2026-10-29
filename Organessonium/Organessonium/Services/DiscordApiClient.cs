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

public sealed partial class DiscordApiClient : IDisposable
{
    public const int MaxActiveThreadsCode = 160006;

    private readonly HttpClient _http;
    private readonly string _storageAccountKey;
    private readonly DiscordRequestLimiter _rateLimiter;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _routeLocks = new(StringComparer.Ordinal);

public DiscordApiClient(string botToken)
        : this(botToken, NetworkConfig.Current.CreateClient())
    {
    }

    public DiscordApiClient(string botToken, HttpClient http)
    {
        _http = http;
        _storageAccountKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(botToken)));
        _rateLimiter = DiscordRequestLimiter.ForToken(botToken);
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
        Func<IReadOnlyList<DiscordMessageDto>, Task>? onBatch = null,
        bool waitForTransientNetwork = false)
    {
        var messages = new List<DiscordMessageDto>();
        string? before = null;

        while (!limit.HasValue || messages.Count < limit.Value)
        {
            var take = limit.HasValue ? Math.Min(DiscordLimits.MessagesPerPage, limit.Value - messages.Count) : DiscordLimits.MessagesPerPage;
            var url = $"channels/{channelId}/messages?limit={take}";
            if (!string.IsNullOrWhiteSpace(before))
            {
                url += "&before=" + before;
            }

            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url),
                cancellationToken,
                waitForTransientNetwork: waitForTransientNetwork);
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
        if (workers == 1)
        {
            return await GetMessagesAsync(channelId, null, cancellationToken, onBatch);
        }

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"channels/{channelId}/messages?limit=1"),
            cancellationToken,
            serializeRoute: false);
        var newest = await ReadJsonAsync<List<DiscordMessageDto>>(response, cancellationToken);
        if (newest.Count == 0 || !ulong.TryParse(newest[0].Id, out var newestId))
        {
            return [];
        }

        // Channel snowflakes are created before every message in that channel. Using this
        // boundary avoids partitioning the entire history of Discord since 2015.
        var firstId = ulong.TryParse(channelId, out var channelIdValue) && channelIdValue < newestId
            ? channelIdValue
            : 0UL;
        if (newestId <= firstId)
        {
            return await GetMessagesAsync(channelId, null, cancellationToken, onBatch);
        }

        var span = newestId - firstId + 1;
        var divisor = (ulong)workers;
        var quotient = span / divisor;
        var remainder = span % divisor;
        ulong WindowBoundary(int worker)
        {
            var position = (ulong)worker;
            return firstId + quotient * position + Math.Min(remainder, position);
        }

        var gate = new SemaphoreSlim(1);
        var tasks = new List<Task<List<DiscordMessageDto>>>(workers);
        for (var worker = 0; worker < workers; worker++)
        {
            var windowStartId = WindowBoundary(worker);
            var windowEndId = worker == workers - 1
                ? newestId + 1
                : WindowBoundary(worker + 1);
            tasks.Add(FetchWindowAsync(channelId, windowStartId, windowEndId, gate, onBatch, cancellationToken));
        }

        var windows = await Task.WhenAll(tasks);
        var seen = new HashSet<ulong>();
        var merged = new List<DiscordMessageDto>();
        foreach (var window in windows)
        {
            foreach (var message in window)
            {
                if (ulong.TryParse(message.Id, out var id) && seen.Add(id))
                {
                    merged.Add(message);
                }
            }
        }

        merged.Sort((a, b) => ulong.Parse(b.Id).CompareTo(ulong.Parse(a.Id)));
        return merged;
    }

    private async Task<List<DiscordMessageDto>> FetchWindowAsync(
        string channelId,
        ulong windowStartId,
        ulong windowEndId,
        SemaphoreSlim gate,
        Func<IReadOnlyList<DiscordMessageDto>, Task>? onBatch,
        CancellationToken cancellationToken)
    {
        var results = new List<DiscordMessageDto>();
        var before = windowEndId;

        while (true)
        {
            var url = $"channels/{channelId}/messages?limit={DiscordLimits.MessagesPerPage}&before={before}";

            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url),
                cancellationToken,
                serializeRoute: false);
            var batch = await ReadJsonAsync<List<DiscordMessageDto>>(response, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var clipped = batch
                .Where(message => ulong.TryParse(message.Id, out var id) &&
                                  id >= windowStartId &&
                                  id < windowEndId)
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

            if (clipped.Count < batch.Count || batch.Count < DiscordLimits.MessagesPerPage)
            {
                break;
            }

            if (!ulong.TryParse(batch[^1].Id, out before))
            {
                break;
            }
        }

        return results;
    }

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
            var url = $"channels/{channelId}/messages?limit={DiscordLimits.MessagesPerPage}";
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

            if (batch.Count < DiscordLimits.MessagesPerPage)
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
        CancellationToken cancellationToken = default,
        bool waitForTransientNetwork = false)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"channels/{channelId}/messages/{messageId}"),
            cancellationToken,
            waitForTransientNetwork: waitForTransientNetwork);
        return await ReadJsonAsync<DiscordMessageDto>(response, cancellationToken);
    }

    public async Task<DiscordMessageDto> CreateMessageAsync(
        string channelId,
        string content,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        DiscordLimits.ValidateMessage(content);
        var payload = JsonSerializer.Serialize(new { content }, JsonOptions.Web);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return request;
        }, cancellationToken, onRateLimitWait);
        return await ReadJsonAsync<DiscordMessageDto>(response, cancellationToken);
    }

    public async Task<DiscordMessageDto> UpdateMessageAsync(
        string channelId,
        string messageId,
        string content,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool clearAttachments = false)
    {
        DiscordLimits.ValidateMessage(content);
        var payload = clearAttachments
            ? JsonSerializer.Serialize(new { content, attachments = Array.Empty<object>() }, JsonOptions.Web)
            : JsonSerializer.Serialize(new { content }, JsonOptions.Web);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return request;
        }, cancellationToken, onRateLimitWait);
        InvalidatePayloadMessage(channelId, messageId);
        return await ReadJsonAsync<DiscordMessageDto>(response, cancellationToken);
    }

    public async Task DeleteMessageAsync(string channelId, string messageId, CancellationToken cancellationToken = default)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{channelId}/messages/{messageId}"),
            cancellationToken, serializeRoute: false);
        InvalidatePayloadMessage(channelId, messageId);
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

    public Task<DiscordForumPostResult> CreateForumPostWithFileDetailedAsync(
        string forumId,
        string title,
        string content,
        FileInfo file,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool serializeRoute = true,
        Action<bool>? onTransferActivity = null,
        bool waitForTransientNetwork = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        return CreateForumPostWithFileDetailedAsync(
            forumId,
            title,
            content,
            file.Name,
            _ => BuildFileContent(file.OpenRead),
            cancellationToken,
            onRateLimitWait,
            serializeRoute,
            onTransferActivity,
            waitForTransientNetwork);
    }

    internal Task<DiscordForumPostResult> CreateForumPostWithFileDetailedAsync(
        string forumId,
        string title,
        string content,
        PreparedUploadPart file,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool serializeRoute = true,
        Action<bool>? onTransferActivity = null,
        bool waitForTransientNetwork = false,
        AdaptiveUploadGate? uploadBodyGate = null,
        SemaphoreSlim? uploadBodySharedGate = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        return CreateForumPostWithFileDetailedAsync(
            forumId,
            title,
            content,
            file.FileName,
            slot => BuildUploadPartContent(file, slot == null ? null : slot.Complete),
            cancellationToken,
            onRateLimitWait,
            serializeRoute,
            onTransferActivity,
            waitForTransientNetwork,
            uploadBodyGate == null && uploadBodySharedGate == null ? null : () => new UploadBodySlot(uploadBodyGate, uploadBodySharedGate, file.Length));
    }

    private async Task<DiscordForumPostResult> CreateForumPostWithFileDetailedAsync(
        string forumId,
        string title,
        string content,
        string fileName,
        Func<UploadBodySlot?, HttpContent> createContent,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait,
        bool serializeRoute,
        Action<bool>? onTransferActivity,
        bool waitForTransientNetwork,
        Func<UploadBodySlot>? bodySlotFactory = null)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await CreateForumPostWithFileDetailedOnceAsync(
                    forumId,
                    title,
                    content,
                    fileName,
                    createContent,
                    cancellationToken,
                    onRateLimitWait,
                    serializeRoute,
                    onTransferActivity,
                    waitForTransientNetwork, bodySlotFactory);
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

    public async Task<DiscordForumPostResult> CreateForumPostAsync(
        string forumId,
        string title,
        string content,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool replayUnknownResult = true)
    {
        DiscordLimits.ValidateMessage(content);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await SendAsync(() =>
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        name = Truncate(title, DiscordLimits.ThreadNameCharacters),
                        message = new { content }
                    }, JsonOptions.Web);
                    var request = new HttpRequestMessage(HttpMethod.Post, $"channels/{forumId}/threads");
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    return request;
                }, cancellationToken, onRateLimitWait, replayUnknownResult: replayUnknownResult);
                var channel = await ReadJsonAsync<DiscordChannelDto>(response, cancellationToken);
                var messageId = channel.Message?.Id ?? "";
                if (string.IsNullOrWhiteSpace(messageId))
                {
                    var messages = await GetMessagesAsync(channel.Id, 5, cancellationToken);
                    messageId = messages
                        .Where(message => string.Equals(message.Content, content, StringComparison.Ordinal))
                        .OrderBy(message => Snowflake(message.Id))
                        .Select(message => message.Id)
                        .FirstOrDefault() ?? "";
                }

                if (!string.IsNullOrWhiteSpace(messageId))
                {
                    return new DiscordForumPostResult(channel.Id, messageId);
                }

                throw new InvalidOperationException("Não foi possível localizar a mensagem inicial do índice da pasta.");
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

        throw new DiscordApiException(HttpStatusCode.BadRequest, "Discord recusou a criação da thread de índice.");
    }

    private async Task<DiscordForumPostResult> CreateForumPostWithFileDetailedOnceAsync(
        string forumId,
        string title,
        string content,
        string fileName,
        Func<UploadBodySlot?, HttpContent> createContent,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait,
        bool serializeRoute,
        Action<bool>? onTransferActivity,
        bool waitForTransientNetwork,
        Func<UploadBodySlot>? bodySlotFactory = null)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"channels/{forumId}/threads");
            var slot = bodySlotFactory?.Invoke();
            if (slot != null) request.Options.Set(UploadBodySlot.RequestKey, slot);
            request.Content = BuildForumThreadContent(title, content, fileName, () => createContent(slot));
            return request;
        }, cancellationToken, onRateLimitWait, serializeRoute, onTransferActivity, waitForTransientNetwork);
        var channel = await ReadJsonAsync<DiscordChannelDto>(response, cancellationToken);
        var initialMessage = channel.Message;
        var messageId = channel.Message?.Id ?? "";
        if (string.IsNullOrWhiteSpace(messageId))
        {
            var messages = await GetMessagesAsync(channel.Id, 5, cancellationToken);
            initialMessage = messages
                .Where(message => message.Attachments.Any(a => a.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(message => Snowflake(message.Id))
                .FirstOrDefault();
            messageId = initialMessage?.Id ?? "";
        }

        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new InvalidOperationException("Não foi possível localizar a mensagem inicial do arquivo.");
        }

        return new DiscordForumPostResult(channel.Id, messageId, initialMessage?.Attachments);
    }

    public async Task<IReadOnlyList<DiscordChannelDto>> GetActiveThreadsAsync(
        string channelId,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        var channel = await GetChannelAsync(channelId, cancellationToken);
        if (string.IsNullOrWhiteSpace(channel.GuildId))
        {
            return [];
        }

        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"guilds/{channel.GuildId}/threads/active"),
            cancellationToken,
            onRateLimitWait);
        var result = await ReadJsonAsync<DiscordActiveThreadsDto>(response, cancellationToken);
        return result.Threads
            .Where(thread => string.Equals(thread.ParentId, channelId, StringComparison.Ordinal))
            .ToList();
    }

    public async Task<IReadOnlyList<DiscordChannelDto>> GetForumThreadsAsync(
        string channelId,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        var threads = new Dictionary<string, DiscordChannelDto>(StringComparer.Ordinal);
        foreach (var thread in await GetActiveThreadsAsync(channelId, cancellationToken, onRateLimitWait))
        {
            threads[thread.Id] = thread;
        }

        string? before = null;
        while (true)
        {
            var url = $"channels/{channelId}/threads/archived/public?limit={DiscordLimits.MessagesPerPage}";
            if (!string.IsNullOrWhiteSpace(before))
            {
                url += "&before=" + Uri.EscapeDataString(before);
            }

            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, url),
                cancellationToken,
                onRateLimitWait);
            var page = await ReadJsonAsync<DiscordArchivedThreadsDto>(response, cancellationToken);
            foreach (var thread in page.Threads.Where(thread =>
                         string.Equals(thread.ParentId, channelId, StringComparison.Ordinal)))
            {
                threads[thread.Id] = thread;
            }

            var next = page.Threads.LastOrDefault()?.ThreadMetadata?.ArchiveTimestamp;
            if (!page.HasMore || string.IsNullOrWhiteSpace(next) || next.Equals(before, StringComparison.Ordinal))
            {
                break;
            }

            before = next;
        }

        return threads.Values.ToList();
    }

    public async Task ArchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        if (HasThreadWriter(threadId)) return;
        await SetThreadArchivedAsync(threadId, archived: true, cancellationToken, onRateLimitWait);
    }

    public async Task UnarchiveThreadAsync(
        string threadId,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        if (HasThreadWriter(threadId)) return;
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
                     .Where(thread => !string.IsNullOrWhiteSpace(thread.Id) && !HasThreadWriter(thread.Id))
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

    public Task<DiscordMessageDto> SendFileMessageAsync(
        string channelId,
        string content,
        FileInfo file,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool serializeRoute = true,
        Action<bool>? onTransferActivity = null,
        bool waitForTransientNetwork = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        return SendFileMessageAsync(
            channelId,
            content,
            file.Name,
            file.OpenRead,
            cancellationToken,
            onRateLimitWait,
            serializeRoute,
            onTransferActivity,
            waitForTransientNetwork);
    }

    internal Task<DiscordMessageDto> SendFileMessageAsync(
        string channelId,
        string content,
        PreparedUploadPart file,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool serializeRoute = true,
        Action<bool>? onTransferActivity = null,
        bool waitForTransientNetwork = false)
    {
        ArgumentNullException.ThrowIfNull(file);
        return SendFilesMessageAsync(channelId, content, [file], cancellationToken,
            onRateLimitWait, serializeRoute, onTransferActivity, waitForTransientNetwork);
    }

    private async Task<DiscordMessageDto> SendFileMessageAsync(
        string channelId,
        string content,
        string fileName,
        Func<Stream> openRead,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait,
        bool serializeRoute,
        Action<bool>? onTransferActivity,
        bool waitForTransientNetwork)
    {
        return await SendFilesMessageAsync(channelId, content, [(fileName, _ => BuildFileContent(openRead))], cancellationToken,
            onRateLimitWait, serializeRoute, onTransferActivity, waitForTransientNetwork);
    }

    public Task<DiscordMessageDto> SendFilesMessageAsync(
        string channelId,
        string content,
        IReadOnlyList<PreparedUploadPart> files,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool serializeRoute = true,
        Action<bool>? onTransferActivity = null,
        bool waitForTransientNetwork = false,
        AdaptiveUploadGate? uploadBodyGate = null,
        SemaphoreSlim? uploadBodySharedGate = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ValidateAttachmentCount(files.Count);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file);
            if (file.Length > DiscordLimits.AttachmentBytes)
                throw new ArgumentException("Um attachment excede o limite individual configurado para o Discord.", nameof(files));
        }
        return SendFilesMessageAsync(channelId, content,
            files.Select((file, index) => (file.FileName, (Func<UploadBodySlot?, HttpContent>)(slot =>
                BuildUploadPartContent(file, index == files.Count - 1 && slot != null ? slot.Complete : null)))).ToArray(), cancellationToken,
            onRateLimitWait, serializeRoute, onTransferActivity, waitForTransientNetwork,
            uploadBodyGate == null && uploadBodySharedGate == null ? null : () => new UploadBodySlot(uploadBodyGate, uploadBodySharedGate, files.Sum(file => file.Length)));
    }

    private async Task<DiscordMessageDto> SendFilesMessageAsync(
        string channelId, string content, IReadOnlyList<(string FileName, Func<UploadBodySlot?, HttpContent> CreateContent)> files,
        CancellationToken cancellationToken, Func<TimeSpan, Task>? onRateLimitWait,
        bool serializeRoute, Action<bool>? onTransferActivity, bool waitForTransientNetwork,
        Func<UploadBodySlot>? bodySlotFactory = null)
    {
        ValidateAttachmentCount(files.Count);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"channels/{channelId}/messages");
            var slot = bodySlotFactory?.Invoke();
            if (slot != null) request.Options.Set(UploadBodySlot.RequestKey, slot);
            request.Content = BuildMessageFilesContent(content, files.Select(file =>
                (file.FileName, (Func<HttpContent>)(() => file.CreateContent(slot)))).ToArray());
            return request;
        }, cancellationToken, onRateLimitWait, serializeRoute, onTransferActivity, waitForTransientNetwork);
        return await ReadJsonAsync<DiscordMessageDto>(response, cancellationToken);
    }

    public async Task<DiscordMessageDto> ReplaceFileMessageAsync(
        string channelId,
        string messageId,
        string content,
        FileInfo file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"channels/{channelId}/messages/{messageId}");
            request.Content = BuildMessageFileContent(content, file.Name, file.OpenRead);
            return request;
        }, cancellationToken);
        InvalidatePayloadMessage(channelId, messageId);
        return await ReadJsonAsync<DiscordMessageDto>(response, cancellationToken);
    }

    public async Task<byte[]> DownloadAttachmentBytesAsync(
        DiscordAttachmentDto attachment,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
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
                        $"Download do anexo retornou HTTP {(int)response.StatusCode}.",
                        inner: null,
                        statusCode: response.StatusCode);
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

            if (lastError == null || !NetworkRetryPolicy.IsTransient(lastError))
            {
                throw new InvalidOperationException("Não foi possível baixar o anexo do serial.", lastError);
            }

            AppLogger.Error("Discord.DownloadAttachmentRetry", lastError, $"{attachment.FileName}:attempt {attempt}");
            await NetworkRetryPolicy.DelayAsync(attempt, cancellationToken);
        }
    }

    public async Task DeleteChannelAsync(string channelId, CancellationToken cancellationToken = default)
    {
        if (IsSharedThread(channelId)) throw new InvalidOperationException("Uma thread compartilhada não pode ser excluída como payload de um arquivo.");
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"channels/{channelId}"), cancellationToken);
    }

    public async Task RenameThreadAsync(
        string threadId,
        string title,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        var payload = JsonSerializer.Serialize(new { name = Truncate(title, DiscordLimits.ThreadNameCharacters) }, JsonOptions.Web);
        using var _ = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, $"channels/{threadId}");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            return request;
        }, cancellationToken, onRateLimitWait);
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
        _payloadLifetime.Cancel();
        _http.Dispose();
        foreach (var gate in _routeLocks.Values)
        {
            gate.Dispose();
        }
    }

    private static MultipartFormDataContent BuildForumThreadContent(
        string title,
        string content,
        string fileName,
        Func<HttpContent> createContent)
    {
        DiscordLimits.ValidateMessage(content);
        var payload = JsonSerializer.Serialize(new
        {
            name = Truncate(title, DiscordLimits.ThreadNameCharacters),
            message = new
            {
                content,
                attachments = new[] { new { id = "0", filename = fileName } }
            }
        }, JsonOptions.Web);

        var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
        AddFileContent(multipart, fileName, createContent);
        return multipart;
    }

    private static MultipartFormDataContent BuildMessageFileContent(
        string content,
        string fileName,
        Func<Stream> openRead)
        => BuildMessageFilesContent(content, [(fileName, () => BuildFileContent(openRead))]);

    private static MultipartFormDataContent BuildMessageFilesContent(
        string content, IReadOnlyList<(string FileName, Func<HttpContent> CreateContent)> files)
    {
        ValidateAttachmentCount(files.Count);
        DiscordLimits.ValidateMessage(content);
        var payload = JsonSerializer.Serialize(new
        {
            content,
            attachments = files.Select((file, index) => new
                { id = index.ToString(CultureInfo.InvariantCulture), filename = file.FileName }).ToArray()
        }, JsonOptions.Web);

        var multipart = new MultipartFormDataContent();
        try
        {
            multipart.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
            for (var index = 0; index < files.Count; index++)
                AddFileContent(multipart, files[index].FileName, files[index].CreateContent, index);
            return multipart;
        }
        catch { multipart.Dispose(); throw; }
    }

    private static void ValidateAttachmentCount(int count)
    {
        if (count is < 1 || count > DiscordLimits.AttachmentsPerMessage)
            throw new ArgumentException($"A mensagem deve conter entre 1 e {DiscordLimits.AttachmentsPerMessage} attachments.");
    }

    private static void AddFileContent(MultipartFormDataContent multipart, string fileName, Func<HttpContent> createContent, int index = 0)
    {
        try
        {
            multipart.Add(createContent(), $"files[{index}]", fileName);
        }
        catch
        {
            multipart.Dispose();
            throw;
        }
    }

    private static HttpContent BuildUploadPartContent(PreparedUploadPart file, Action? onCopied = null) =>
        file.Deferred || onCopied != null ? UploadPartContent.Create(file, onCopied) : BuildFileContent(file.OpenRead);

    private static StreamContent BuildFileContent(Func<Stream> openRead)
    {
        var stream = openRead();
        if (stream.CanSeek && stream.Length - stream.Position > DiscordLimits.AttachmentBytes)
        {
            stream.Dispose();
            throw new ArgumentException($"O anexo excede o limite de {DiscordLimits.AttachmentBytes} bytes configurado para o Discord.");
        }
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        Func<TimeSpan, Task>? onRateLimitWait = null,
        bool serializeRoute = true,
        Action<bool>? onTransferActivity = null,
        bool waitForTransientNetwork = false,
        bool replayUnknownResult = true)
    {
        var shouldWaitForTransientNetwork = waitForTransientNetwork || onRateLimitWait != null || NetworkRetryPolicy.WaitingForTransferConnection;
        for (var attempt = 0; attempt < 12 || shouldWaitForTransientNetwork; attempt++)
        {
            using var request = await Task.Run(requestFactory, cancellationToken).ConfigureAwait(false);
            using var diagnosticRequest = Diagnostics.DevelopmentDiagnostics.Http(request);
            using var bodySlot = request.Options.TryGetValue(UploadBodySlot.RequestKey, out var slot) ? slot : null;
            if (bodySlot != null) await bodySlot.EnterAsync(cancellationToken).ConfigureAwait(false);
            var routeKey = DiscordRequestLimiter.RouteKey(request);
            var routeLock = serializeRoute
                ? _routeLocks.GetOrAdd(routeKey, _ => new SemaphoreSlim(1, 1))
                : null;
            if (routeLock != null)
            {
                await routeLock.WaitAsync(cancellationToken);
            }
            try
            {
                using var reservation = await _rateLimiter.WaitAsync(routeKey, onRateLimitWait, cancellationToken,
                    uploadSnapshots: !serializeRoute && request.Content is MultipartFormDataContent);

                HttpResponseMessage response;
                var transferActivityStarted = false;
                try
                {
                    onTransferActivity?.Invoke(true);
                    transferActivityStarted = onTransferActivity != null;
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    diagnosticRequest?.HttpStatus((int)response.StatusCode);
                    reservation.Observe(response);
                }
                catch (HttpRequestException) when (replayUnknownResult && (shouldWaitForTransientNetwork || attempt < 11))
                {
                    bodySlot?.Dispose();
                    await WaitTransientRetryAsync(attempt, onRateLimitWait, cancellationToken);
                    continue;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && replayUnknownResult &&
                                                         (shouldWaitForTransientNetwork || attempt < 11))
                {
                    bodySlot?.Dispose();
                    await WaitTransientRetryAsync(attempt, onRateLimitWait, cancellationToken);
                    continue;
                }
                catch (HttpRequestException ex)
                {
                    throw new DiscordApiException(HttpStatusCode.ServiceUnavailable, "Falha de rede ao falar com o Discord: " + ex.Message, ex);
                }
                finally
                {
                    bodySlot?.Dispose();
                    if (transferActivityStarted)
                    {
                        onTransferActivity!(false);
                    }
                }

                if ((int)response.StatusCode != 429)
                {
                    if (response.IsSuccessStatusCode)
                    {
                        return response;
                    }

                    var statusCode = response.StatusCode;
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    response.Dispose();
                    if (replayUnknownResult && IsTransientStatus(statusCode) && (shouldWaitForTransientNetwork || attempt < 11))
                    {
                        await WaitTransientRetryAsync(attempt, onRateLimitWait, cancellationToken);
                        continue;
                    }

                    throw new DiscordApiException(statusCode, body);
                }

                var (retry, global) = await ResolveRetryAfterAsync(response, cancellationToken);
                _rateLimiter.Block(reservation, retry, global);
                response.Dispose();
                await WaitWithProgressAsync(retry, onRateLimitWait, cancellationToken);
            }
            finally
            {
                routeLock?.Release();
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
            or HttpStatusCode.GatewayTimeout
            || (int)statusCode == 520;
    }

    private static Task WaitTransientRetryAsync(
        int attempt,
        Func<TimeSpan, Task>? onRateLimitWait,
        CancellationToken cancellationToken)
    {
        var delayMs = Math.Min(8000, 500 * Math.Pow(2, Math.Min(attempt, 4)));
        return WaitWithProgressAsync(TimeSpan.FromMilliseconds(delayMs), onRateLimitWait, cancellationToken);
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

    private static async Task<(TimeSpan Delay, bool Global)> ResolveRetryAfterAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var seconds = TryReadHeaderDouble(response, "Retry-After", out var headerSeconds) ? headerSeconds : 0;
        var global = response.Headers.Contains("X-RateLimit-Global") ||
            (response.Headers.TryGetValues("X-RateLimit-Scope", out var scopes) && scopes.Contains("global"));
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("retry_after", out var retry) && retry.TryGetDouble(out var bodySeconds))
                seconds = Math.Max(seconds, bodySeconds);
            if (doc.RootElement.TryGetProperty("global", out var flag) && flag.ValueKind == JsonValueKind.True)
                global = true;
        }
        catch (JsonException)
        {
            // Some proxies return non-JSON 429 responses; retain the HTTP headers.
        }
        return (double.IsFinite(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMilliseconds(DiscordLimits.RateLimitFallbackMilliseconds), global);
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

public sealed record DiscordForumPostResult(string ThreadId, string MessageId,
    IReadOnlyList<DiscordAttachmentDto>? Attachments = null);

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

public sealed class DiscordArchivedThreadsDto
{
    [JsonPropertyName("threads")]
    public List<DiscordChannelDto> Threads { get; set; } = [];

    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }
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

    [JsonPropertyName("thread_metadata")]
    public DiscordThreadMetadataDto? ThreadMetadata { get; set; }
}

public sealed class DiscordThreadMetadataDto
{
    [JsonPropertyName("archived")]
    public bool Archived { get; set; }
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }
    [JsonPropertyName("archive_timestamp")]
    public string ArchiveTimestamp { get; set; } = "";
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
