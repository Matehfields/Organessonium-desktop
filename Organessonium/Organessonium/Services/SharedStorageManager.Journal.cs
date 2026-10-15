using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private readonly Dictionary<string, ulong> _journalOrigins = new(StringComparer.Ordinal);
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
        foreach (var message in messages.Where(message => !_initialized || Snowflake(message.Id) > _frontier))
        {
            using var json = await _metadata.ReadJsonAsync(message, token);
            if (json?.RootElement.TryGetProperty("legacyNamesMessageId", out var origin) == true)
                _journalOrigins[message.Id] = Snowflake(origin.GetString()!);
        }
        // Read-only fallback for the first V2 writer. Never create or update storage records in names.
        foreach (var message in await ReadAsync(_settings.NamesChannelId))
        {
            using var json = await _metadata.ReadJsonAsync(message, token);
            if (json?.RootElement.TryGetProperty("entryType", out var type) != true ||
                type.GetString()?.StartsWith("storage", StringComparison.Ordinal) != true) continue;
            if (type.GetString() == "storageThread") _legacyCatalogIds.Add(message.Id);
            messages.Add(message);
        }
        return messages;
    }
}
