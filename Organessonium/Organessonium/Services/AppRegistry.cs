using Organessonium.Models;

namespace Organessonium.Services;

public sealed class AppRegistry
{
    private readonly DiscordApiClient _discord;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly MetadataService _metadata = new();

    public AppRegistry(DiscordApiClient discord, SettingsStore store, AppSettings settings)
    {
        _discord = discord;
        _store = store;
        _settings = settings;
    }

    public async Task<List<AppEntry>> LoadAsync(
        CancellationToken cancellationToken = default,
        Func<int, Task>? onProgress = null,
        Func<IReadOnlyList<AppEntry>, Task>? onBatch = null)
    {
var envelopes = new List<MetadataEnvelope>();
        var sentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
        await _discord.GetMessagesInParallelAsync(
            _settings.NamesChannelId,
            workers,
            cancellationToken,
            async batch =>
            {
                var botMessages = batch.Where(message => message.Author?.Bot == true).ToArray();
                var resolved = await _metadata.ReadEnvelopesInParallelAsync(botMessages, workers, cancellationToken);
                envelopes.AddRange(resolved);

                if (onBatch != null)
                {
                    var current = _metadata.ToAppEntries(envelopes);
                    var added = ReadyForProgressiveSend(current, sentIds);
                    if (added.Count > 0)
                    {
                        await onBatch(added);
                    }
                }

                if (onProgress != null)
                {
                    await onProgress(envelopes.Count);
                }
            });

        var list = _metadata.ToAppEntries(envelopes);
        _store.CacheEntries(list);
        return list;
    }

    private static List<AppEntry> ReadyForProgressiveSend(
        IReadOnlyList<AppEntry> entries,
        HashSet<string> sentIds)
    {
        var byFolderId = entries
            .Where(entry => entry.Type == "folder" && !string.IsNullOrWhiteSpace(entry.FolderId))
            .GroupBy(entry => entry.FolderId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var ready = new List<AppEntry>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.MetadataMessageId))
            {
                continue;
            }

            if (sentIds.Contains(entry.MetadataMessageId))
            {
                continue;
            }

            if (!HasKnownParentChain(entry, byFolderId))
            {
                continue;
            }

            sentIds.Add(entry.MetadataMessageId);
            ready.Add(entry);
        }

        return ready;
    }

    private static bool HasKnownParentChain(
        AppEntry entry,
        IReadOnlyDictionary<string, AppEntry> byFolderId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentId = entry.ParentFolderId;
        while (!string.IsNullOrWhiteSpace(parentId))
        {
            if (!seen.Add(parentId))
            {
                return false;
            }

            if (!byFolderId.TryGetValue(parentId, out var parent))
            {
                return false;
            }

            parentId = parent.ParentFolderId;
        }

        return true;
    }

    public List<AppEntry> LoadCached()
    {
        return _store.LoadCachedEntries();
    }

    public async Task<string> AddEntryAsync(
        AppEntry entry,
        CancellationToken cancellationToken = default,
        Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        entry.Normalize();
        return await _metadata.SendEntryAsync(
            _discord,
            _settings.NamesChannelId,
            entry,
            cancellationToken,
            onRateLimitWait);
    }

    public async Task DeleteEntryAsync(AppEntry entry, CancellationToken cancellationToken = default)
    {
        await _metadata.DeleteEntryAsync(_discord, _settings.NamesChannelId, entry, cancellationToken);
    }
}
