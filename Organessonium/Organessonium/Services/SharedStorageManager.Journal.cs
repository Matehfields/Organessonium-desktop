using System.Collections.Concurrent;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private readonly ConcurrentDictionary<string, ulong> _journalOrigins = new(StringComparer.Ordinal);
    private ulong JournalOrder(DiscordMessageDto message) => _journalOrigins.GetValueOrDefault(message.Id, Snowflake(message.Id));

    private async Task<string> GetJournalThreadAsync(CancellationToken token)
    {
        if (_journalThreadId != null) return _journalThreadId;
        await _journalIndexGate.WaitAsync(token);
        try { return _journalThreadId ??= (await _journalIndex(token)).ThreadId; }
        finally { _journalIndexGate.Release(); }
    }

    private async Task<List<DiscordMessageDto>> ReadJournalMessagesAsync(int? limit, CancellationToken token)
    {
        async Task<IReadOnlyList<DiscordMessageDto>> ReadAsync(string channel)
        {
            var rows = await _api.GetMessagesAsync(channel, limit, token);
            if (limit.HasValue && rows.Count == limit && rows.All(row => Snowflake(row.Id) > _frontier))
                rows = await _api.GetMessagesAsync(channel, null, token);
            return rows;
        }

        var messages = (await ReadAsync(await GetJournalThreadAsync(token))).ToList();
        long materializedBytes = 0;
        var candidates = Enumerable.Range(0, messages.Count)
            .Where(index => !limit.HasValue || !_initialized || Snowflake(messages[index].Id) > _frontier).ToArray();
        async ValueTask MaterializeAsync(int index, CancellationToken cancellation)
        {
            var message = messages[index];
            using var json = await _metadata.ReadJsonAsync(message, cancellation);
            if (json?.RootElement.TryGetProperty("legacyNamesMessageId", out var origin) == true)
                _journalOrigins[message.Id] = Snowflake(origin.GetString()!);
            if (json == null) return;
            if (!json.RootElement.TryGetProperty("schemaVersion", out _) && !json.RootElement.TryGetProperty("schema", out _)) return;
            // This is an authenticated, operation-local snapshot, never a remote rewrite or disk cache.
            // Replaying it must not download the same metadata attachment again.
            var body = json.RootElement.GetRawText();
            var bytes = body.Length * 2L;
            if (Interlocked.Add(ref materializedBytes, bytes) <= 32L * 1024 * 1024)
                messages[index] = new DiscordMessageDto { Id = message.Id, Content = body,
                    Author = message.Author, Attachments = message.Attachments };
            else Interlocked.Add(ref materializedBytes, -bytes);
        }
        // Large documents retain sequential admission; eight maximum-size blobs must not fill RAM.
        var small = candidates.Where(index => messages[index].Attachments.All(attachment => attachment.Size <= 256 * 1024)).ToArray();
        await Parallel.ForEachAsync(small, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, MaterializeAsync);
        foreach (var index in candidates.Except(small)) await MaterializeAsync(index, token);
        // Read-only fallback for the first V2 writer. Never create or update storage records in names.
        foreach (var message in await ReadAsync(_settings.NamesChannelId))
        {
            using var json = await _metadata.ReadJsonAsync(message, token);
            if (json?.RootElement.TryGetProperty("entryType", out var type) != true ||
                type.GetString()?.StartsWith("storage", StringComparison.Ordinal) != true) continue;
            if (type.GetString() == "storageThread") _legacyCatalogIds.TryAdd(message.Id, 0);
            messages.Add(message);
        }
        return messages;
    }
}
