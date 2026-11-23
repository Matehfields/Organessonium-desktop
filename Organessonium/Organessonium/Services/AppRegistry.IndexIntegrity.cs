using System.Collections.Concurrent;
using System.IO;
using System.Net;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    private readonly ConcurrentDictionary<string, string> _verifiedIndexFolders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _indexPayloadOffsets = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastAuditedFolder;
    private DateTimeOffset _nextIndexAudit;

    internal async Task AuditFolderIndexesAsync(CancellationToken token)
    {
        if (DateTimeOffset.UtcNow < _nextIndexAudit) return;
        _nextIndexAudit = DateTimeOffset.UtcNow.AddMinutes(5);
        var mappings = MappingSnapshot().Values.Where(mapping => mapping.FolderId != StorageJournalFolderId)
            .OrderBy(mapping => mapping.FolderId, StringComparer.OrdinalIgnoreCase).ToArray();
        var start = Array.FindIndex(mappings, mapping => string.Compare(mapping.FolderId, _lastAuditedFolder,
            StringComparison.OrdinalIgnoreCase) > 0);
        if (start < 0) start = 0;
        var audit = new FolderIndexAudit(_discord, _metadata);
        for (var i = 0; i < Math.Min(4, mappings.Length); i++)
        {
            var mapping = mappings[(start + i) % mappings.Length];
            _lastAuditedFolder = mapping.FolderId;
            if (_publishingLibraryFolders.ContainsKey(mapping.FolderId) || _uploadIndexThreads.ContainsKey(mapping.ThreadId)) continue;
            try
            {
                var serial = await _serials.LoadFolderAsync(mapping, token);
                var offset = _indexPayloadOffsets.GetValueOrDefault(mapping.FolderId);
                var result = await audit.ScanAsync(mapping.ThreadId, mapping.FolderId, serial, token,
                    payloadBudget: 4, payloadOffset: offset, messageBudget: 32);
                _indexPayloadOffsets[mapping.FolderId] = result.UniqueFiles == 0 ? 0 : (offset + 4) % result.UniqueFiles;
                Diagnostics.DevelopmentDiagnostics.Current?.Record("index.audit", "maintenance", new
                {
                    messages = result.Messages, files = result.UniqueFiles, result.SerialFiles,
                    result.WrongFolder, result.Duplicates, result.Unreadable, result.Orphans, result.UnknownPayloads,
                    result.IndexedWithoutMetadata, result.MetadataWithoutIndex, result.Headers, result.Pending,
                    unreadableSerial = serial == null && mapping.SerialMessageId.Length > 0
                });
                if (result.Headers != 1 || result.WrongFolder != 0) _verifiedIndexFolders.TryRemove(mapping.ThreadId, out _);
                if (result.Headers == 1 && result.WrongFolder == 0 && result.Unreadable == 0 &&
                    _descriptors.TryGetValue(mapping.FolderId, out var descriptor))
                    await RenameVerifiedIndexAsync(mapping, descriptor.Name, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { AppLogger.Error("Index.Audit", error); }
        }
        await SharedStorage.AuditPendingIndexOperationsAsync(token);
    }

    private async Task RenameVerifiedIndexAsync(FolderIndexMapping mapping, string name,
        CancellationToken token, Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        await VerifyFolderDestinationAsync(mapping, mapping.FolderId, token);
        var title = FolderThreadTitle(name, mapping.FolderId);
        var channel = await _discord.GetChannelAsync(mapping.ThreadId, token);
        if (channel.Name == title) return;
        await using var lease = await _discord.AcquireThreadWriteAsync(mapping.ThreadId, token);
        await _discord.RenameThreadAsync(mapping.ThreadId, title, token, onRateLimitWait);
    }

    private async Task<bool> VerifyMovedSerialFilesAsync(FolderIndexMapping source, FolderSerial serial, CancellationToken token)
    {
        foreach (var child in serial.Children)
        {
            if (child.IsFolder) return false;
            var cached = _store.LoadCachedFileByRequestId(child.Id, source.FolderId);
            if (cached == null || cached.FileId != child.Id || cached.ThreadId != child.ThreadId ||
                !_mappings.TryGetValue(NormalizeFolderId(cached.ParentFolderId), out var destination) ||
                destination.ThreadId == source.ThreadId || string.IsNullOrWhiteSpace(cached.MetadataMessageId))
                return false;
            try
            {
                await VerifyFolderDestinationAsync(destination, cached.ParentFolderId, token);
                var message = await _discord.GetMessageAsync(destination.ThreadId, cached.MetadataMessageId, token);
                if ((await _metadata.ReadEnvelopeAsync(message, token))?.Entry is not FileMetadata file ||
                    file.FileId != child.Id || file.ThreadId != child.ThreadId ||
                    !string.Equals(file.ParentFolderId, destination.FolderId, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            catch (InvalidDataException) { return false; }
            catch (DiscordApiException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            { return false; }
        }
        return true;
    }

    private async Task VerifyFolderDestinationAsync(FolderIndexMapping mapping, string? expectedFolderId, CancellationToken token)
    {
        var expected = NormalizeFolderId(expectedFolderId);
        if (string.IsNullOrWhiteSpace(mapping.ThreadId) ||
            !string.Equals(expected, mapping.FolderId, StringComparison.OrdinalIgnoreCase) ||
            !_mappings.TryGetValue(expected, out var current) || current.ThreadId != mapping.ThreadId ||
            _mappings.Values.Any(other => other.ThreadId == mapping.ThreadId &&
                !other.FolderId.Equals(expected, StringComparison.OrdinalIgnoreCase)))
            throw IndexDestinationMismatch();

        if (!_verifiedIndexFolders.TryGetValue(mapping.ThreadId, out var actual))
        {
            actual = (await ReadFolderIndexHeaderAsync(mapping.ThreadId, token))?.FolderId ?? "";
            if (actual.Length > 0) _verifiedIndexFolders.TryAdd(mapping.ThreadId, actual);
        }
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !_mappings.TryGetValue(expected, out current) || current.ThreadId != mapping.ThreadId)
            throw IndexDestinationMismatch();
    }

    private static InvalidDataException IndexDestinationMismatch()
    {
        Diagnostics.DevelopmentDiagnostics.Current?.Record("index.destination_mismatch", "maintenance",
            new { count = 1 }, critical: true);
        return new InvalidDataException("A thread de destino não corresponde ao índice da pasta. Os metadados foram preservados.");
    }

    private async Task<FolderIndexHeaderMetadata?> ReadFolderIndexHeaderAsync(string threadId, CancellationToken token)
    {
        // On Discord forum posts the starter message has the thread's ID.
        try
        {
            var starter = await _discord.GetMessageAsync(threadId, threadId, token);
            return (await _metadata.ReadEnvelopeAsync(starter, token))?.Entry as FolderIndexHeaderMetadata;
        }
        catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // Accept an older index whose starter has a different ID only when its header can be proved.
            foreach (var message in (await _discord.GetMessagesAsync(threadId, 100, token)).Reverse())
                if ((await _metadata.ReadEnvelopeAsync(message, token))?.Entry is FolderIndexHeaderMetadata header)
                    return header;
            return null;
        }
    }

    private async Task<string> SendFolderEntryAsync(FolderIndexMapping mapping, AppEntry entry,
        CancellationToken token, Func<TimeSpan, Task>? onRateLimitWait = null, bool replayUnknownResult = true)
    {
        await VerifyFolderDestinationAsync(mapping, entry.ParentFolderId, token);
        return await _metadata.SendEntryAsync(_discord, mapping.ThreadId, entry, token, onRateLimitWait, replayUnknownResult);
    }

    private async Task<string> UpsertFolderEntryAsync(FolderIndexMapping mapping, AppEntry entry,
        CancellationToken token, Func<TimeSpan, Task>? onRateLimitWait = null)
    {
        await VerifyFolderDestinationAsync(mapping, entry.ParentFolderId, token);
        return await _metadata.UpsertEntryAsync(_discord, mapping.ThreadId, entry, token, onRateLimitWait);
    }
}
