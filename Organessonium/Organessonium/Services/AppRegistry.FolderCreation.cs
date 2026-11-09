using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    internal async Task<AppEntry> CreateQueuedFolderAsync(LibraryMutation job, Action checkpoint, CancellationToken token)
    {
        var requested = job.Changes.Single().Source;
        var creation = job.FolderCreation!;
        var folder = creation.Published ?? requested.AtLocation(NormalizeFolderId(requested.ParentFolderId), requested.Path);
        var recovering = creation.Attempted;
        if (recovering && creation.Published == null) await RefreshMappingsAsync(token);
        string? publishingParent = null;
        try
        {
            await _entryMutationGate.WaitAsync(token);
            try
            {
                if (creation.Published == null)
                {
                    creation.Attempted = true;
                    checkpoint();
                    await AddEntryCoreAsync(folder, token, null, synchronizeSerials: false,
                        creation, recovering, checkpoint);
                    creation.Published = folder;
                    checkpoint();
                }
                if (folder.FolderId != requested.FolderId) return folder;
                publishingParent = NormalizeFolderId(folder.ParentFolderId);
                _publishingLibraryFolders[publishingParent] = 0;
                Interlocked.Increment(ref _libraryPublicationRevision);
                var siblings = _store.LoadCachedEntriesForFolder(publishingParent);
                ReplaceEntry(siblings, folder);
                _store.CacheFolderEntries(publishingParent, siblings);
                RecordDescriptors(siblings);
            }
            finally { _entryMutationGate.Release(); }

            if (_mappings.TryGetValue(folder.FolderId, out var mapping) &&
                string.IsNullOrWhiteSpace(mapping.SerialMessageId))
            {
                await SynchronizeFolderSerialAsync(folder.FolderId, PathRules.ChildPath(folder),
                    _store.LoadCachedEntriesForFolder(folder.FolderId), token, publishImmediately: true);
            }
            _deferredFolderSerials.TryRemove(folder.FolderId, out _);
            await SynchronizeFolderSerialAsync(publishingParent, folder.Path,
                _store.LoadCachedEntriesForFolder(publishingParent), token, publishImmediately: true);
            return folder;
        }
        finally
        {
            if (publishingParent != null)
            {
                _publishingLibraryFolders.TryRemove(publishingParent, out _);
                Interlocked.Increment(ref _libraryPublicationRevision);
            }
        }
    }

    private async Task<AppEntry?> FindQueuedFolderAsync(AppEntry folder, FolderIndexMapping parent, CancellationToken token)
    {
        var cached = _store.LoadCachedEntriesForFolder(parent.FolderId);
        var existing = cached.FirstOrDefault(entry => IsSameFolderIdentity(entry, folder));
        if (existing != null || _deferredFolderSerials.ContainsKey(parent.FolderId)) return existing;

        var serial = await _serials.LoadFolderAsync(parent, token);
        if (serial != null)
        {
            var frontier = serial.Children.Select(entry => Snowflake(entry.NamesMessageId)).DefaultIfEmpty().Max();
            var complete = cached.Where(entry => entry.Type == "folder" || entry.LogicalChunkCount > 0)
                .Select(entry => entry.MetadataMessageId).ToHashSet(StringComparer.Ordinal);
            // Use the serial to bound the duplicate check when all indexed entries
            // are cached with details. A cold cache still needs the full metadata.
            if (frontier > 0 && serial.Children.All(entry => complete.Contains(entry.NamesMessageId)))
            {
                var messages = await _discord.GetMessagesAsync(parent.ThreadId, null, token, stopAtMessageId: frontier);
                var envelopes = await _metadata.ReadEnvelopesInParallelAsync(
                    messages.Where(message => Snowflake(message.Id) > frontier).ToList(), _settings.SearchWorkers, token);
                var newer = _metadata.ToFolderEntries(envelopes, parent.FolderId, folder.Path, MappingSnapshot());
                if (newer.Count > 0)
                {
                    foreach (var entry in newer) ReplaceEntry(cached, entry);
                    _store.CacheFolderEntries(parent.FolderId, cached);
                    RecordDescriptors(newer);
                }
                return cached.FirstOrDefault(entry => IsSameFolderIdentity(entry, folder));
            }
        }

        var siblings = await RefreshFolderAsync(parent.FolderId, folder.Path, token);
        return siblings.FirstOrDefault(entry => IsSameFolderIdentity(entry, folder));
    }

    private async Task<string?> RecoverFolderIndexThreadAsync(AppEntry folder, FolderCreationCheckpoint creation, CancellationToken token)
    {
        var threadId = creation.ThreadId;
        if (threadId == null)
        {
            var threads = await _discord.GetForumThreadsAsync(_settings.FoldersForumId, token);
            threadId = threads.FirstOrDefault(thread => thread.Name == creation.PostTitle)?.Id;
        }
        if (threadId == null) return null;

        try
        {
            foreach (var message in await _discord.GetMessagesAsync(threadId, null, token))
            {
                var envelope = await _metadata.ReadEnvelopeAsync(message, token);
                if (envelope?.Entry is FolderIndexHeaderMetadata header && header.FolderId == folder.FolderId &&
                    header.ParentFolderId == folder.ParentFolderId)
                    return threadId;
            }
        }
        catch (DiscordApiException error) when ((int)error.StatusCode == 404) { return null; }
        throw new InvalidOperationException("O índice recuperado não identifica a pasta solicitada.");
    }
}
