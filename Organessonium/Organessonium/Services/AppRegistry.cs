using Organessonium.Models;

namespace Organessonium.Services;

public sealed class AppRegistry
{
    private readonly DiscordApiClient _discord;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly MetadataService _metadata = new();
    private readonly SerialIndexService _serials;

    public AppRegistry(DiscordApiClient discord, SettingsStore store, AppSettings settings)
    {
        _discord = discord;
        _store = store;
        _settings = settings;
        _serials = new SerialIndexService(discord, store, settings);
    }

    public async Task<List<AppEntry>> LoadAsync(
        CancellationToken cancellationToken = default,
        Func<int, Task>? onProgress = null,
        Func<IReadOnlyList<AppEntry>, Task>? onBatch = null)
    {
        var cached = _store.LoadCachedEntries();
        if (onProgress != null)
        {
            await onProgress(0);
        }

        try
        {
            var fastEntries = await _serials.LoadFastEntriesAsync(cached, cancellationToken);
            if (fastEntries.Count > 0 && onBatch != null)
            {
                await onBatch(fastEntries);
            }

        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Registry.SerialIndex", ex, _settings.SerialChannelId);
        }

        var seenServerMessages = new HashSet<string>(StringComparer.Ordinal);
        var envelopesByMessageId = new Dictionary<string, MetadataEnvelope>(StringComparer.Ordinal);
        var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
        var metadataBatchSize = Math.Max(100, workers * 100);
        var sentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lastProgressiveEntryCount = 0;
        await _discord.GetMessagesInParallelAsync(
            _settings.NamesChannelId,
            workers,
            cancellationToken,
            async batch =>
            {
                var metadataMessages = batch
                    .Where(message => message.Author?.Bot == true && seenServerMessages.Add(message.Id))
                    .ToArray();
                if (metadataMessages.Length == 0)
                {
                    return;
                }

                var resolved = await _metadata.ReadEnvelopesInParallelAsync(
                    metadataMessages,
                    workers,
                    cancellationToken);
                foreach (var envelope in resolved)
                {
                    envelopesByMessageId[envelope.MessageId] = envelope;
                }

                if (onProgress != null)
                {
                    // The UI must report accepted metadata, not raw Discord messages or a cached floor.
                    await onProgress(envelopesByMessageId.Count);
                }

                if (onBatch != null &&
                    envelopesByMessageId.Count - lastProgressiveEntryCount >= metadataBatchSize)
                {
                    var current = _metadata.ToAppEntries(envelopesByMessageId.Values.ToList());
                    var added = ReadyForProgressiveSend(current, sentIds);
                    if (added.Count > 0)
                    {
                        await onBatch(added);
                    }

                    lastProgressiveEntryCount = envelopesByMessageId.Count;
                }
            });
        var envelopes = envelopesByMessageId.Values
            .OrderByDescending(envelope => ulong.TryParse(envelope.MessageId, out var id) ? id : 0UL)
            .ToList();
        var list = _metadata.ToAppEntries(envelopes);
        if (onBatch != null)
        {
            var added = ReadyForProgressiveSend(list, sentIds);
            if (added.Count > 0)
            {
                await onBatch(added);
            }
        }
        _store.CacheEntries(list);
        try
        {
            await _serials.SynchronizeAsync(list, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Registry.SerialIndexSync", ex, _settings.SerialChannelId);
        }

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

    public void CacheEntries(IEnumerable<AppEntry> entries)
    {
        var list = entries.ToList();
        foreach (var entry in list)
        {
            entry.Normalize();
        }

        _store.CacheEntries(list);
    }

    public Task SynchronizeSerialsAsync(
        IEnumerable<AppEntry> entries,
        CancellationToken cancellationToken = default)
    {
        return _serials.SynchronizeAsync(entries, cancellationToken);
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
