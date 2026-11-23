using System.Net;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

public sealed class FolderIndexAudit(DiscordApiClient discord, MetadataService metadata)
{
    public async Task<IndexInventory> ScanAsync(string threadId, string? folderId, FolderSerial? serial,
        CancellationToken token, int payloadBudget = 0, int payloadOffset = 0, int messageBudget = int.MaxValue)
    {
        var messages = await discord.GetMessagesAsync(threadId, null, token);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var referenceKinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var files = new Dictionary<string, FileMetadata>(StringComparer.Ordinal);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var headers = 0;
        var wrongFolder = 0;
        var duplicates = 0;
        var unreadable = 0;
        var unknown = 0;
        foreach (var message in messages.AsEnumerable().Reverse())
        {
            try
            {
                using var decoded = await metadata.ReadJsonAsync(message, token);
                var root = decoded?.RootElement;
                var kind = root is { } value ? Field(value, "entryType") : "";
                var category = kind switch
                {
                    "file" or "folder" or "folderIndex" or "folderIndexHeader" or "storageThread" or "storageReference" or
                    "storageCleanup" or "storageCleanupApplied" or "storageBatchPlan" or "storageBatchReceipt" or
                    "storageMutation" or "storageControl" or "syncRevision" or "syncApplied" => kind,
                    _ => "outros"
                };
                counts[category] = counts.GetValueOrDefault(category) + 1;
                if (root is not { } record) continue;
                TrackPending(record, kind, pending);
                if (kind == "storageReference")
                {
                    var scope = Field(record, "scope").Split(':', 2)[0];
                    if (scope is not ("preview" or "download" or "upload" or "file" or "sync")) scope = "outros";
                    var state = Field(record, "state") is "released" ? "released" : "live";
                    var referenceKind = scope + "." + state;
                    referenceKinds[referenceKind] = referenceKinds.GetValueOrDefault(referenceKind) + 1;
                }
                if (kind == "folderIndexHeader")
                {
                    headers++;
                    if (folderId != null && !string.Equals(Field(record, "folderId"), folderId, StringComparison.OrdinalIgnoreCase))
                        wrongFolder++;
                }
                if (kind is not ("file" or "folder")) continue;
                var id = Field(record, kind == "file" ? "fileId" : "folderId");
                if (id.Length == 0) { unreadable++; continue; }
                var parent = Field(record, "parentFolderId");
                if (folderId != null && !string.Equals(parent.Length == 0 ? AppRegistry.RootFolderId : parent,
                        folderId, StringComparison.OrdinalIgnoreCase)) wrongFolder++;
                var key = kind + ":" + id;
                if (!entries.TryAdd(key, message.Id)) { duplicates++; entries[key] = message.Id; }
                if (kind == "file" && record.Deserialize<FileMetadata>(JsonOptions.Web) is { } file)
                    files[id] = file;
            }
            catch (Exception error) when (error is MetadataCodecException or JsonException or InvalidOperationException)
            { unreadable++; }
        }
        var serialEntries = serial?.Children
            .GroupBy(child => (child.IsFolder ? "folder:" : "file:") + child.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().NamesMessageId, StringComparer.Ordinal);
        var indexedWithoutMetadata = serialEntries?.Count(pair =>
            !entries.TryGetValue(pair.Key, out var locator) || locator != pair.Value) ?? 0;
        var metadataWithoutIndex = serialEntries == null ? 0 : entries.Count(pair =>
            !serialEntries.TryGetValue(pair.Key, out var locator) || locator != pair.Value);
        var checkedPayloads = 0;
        var orphans = 0;
        var selected = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray();
        if (selected.Length > 0 && payloadBudget > 0)
        {
            var messagesByLocator = new Dictionary<(string Thread, string Message), DiscordMessageDto?>();
            for (var i = 0; i < Math.Min(payloadBudget, selected.Length); i++)
            {
                var file = selected[(int)(((long)payloadOffset + i) % selected.Length)];
                var references = file.ChunkRefs?.Count ?? file.Chunks?.Count ?? 0;
                if (references > messageBudget) { unknown++; continue; }
                messageBudget -= references;
                try
                {
                    var exists = await HasOriginalAsync(file, messagesByLocator, token);
                    if (exists == false) orphans++;
                    if (exists == null) unknown++;
                    else checkedPayloads++;
                }
                catch (DiscordApiException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                { unknown++; }
            }
        }
        return new(messages.Count, files.Count, counts, referenceKinds, headers, wrongFolder, duplicates, unreadable,
            checkedPayloads, orphans, unknown, serial?.Children.Count(child => !child.IsFolder),
            indexedWithoutMetadata, metadataWithoutIndex, pending.Count);
    }

    private async Task<bool?> HasOriginalAsync(FileMetadata file,
        Dictionary<(string Thread, string Message), DiscordMessageDto?> cache, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(file.ThreadId)) return null;
        async Task<DiscordMessageDto?> ReadAsync(string id)
        {
            var key = (file.ThreadId, id);
            if (cache.TryGetValue(key, out var known)) return known;
            DiscordMessageDto? message;
            try { message = await discord.GetMessageAsync(file.ThreadId, id, token); }
            catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { message = null; }
            if (cache.Count == 512) cache.Remove(cache.Keys.First());
            return cache[key] = message;
        }
        if (file.ChunkRefs is { Count: > 0 } references)
        {
            foreach (var reference in references)
                if ((await ReadAsync(reference.MessageId))?.Attachments.Any(item => item.Id == reference.AttachmentId) != true)
                    return false;
            return true;
        }
        if (file.Chunks is not { Count: > 0 } chunks) return null;
        foreach (var chunk in chunks)
            if ((await ReadAsync(chunk))?.Attachments.Count is not > 0) return false;
        return true;
    }

    private static void TrackPending(JsonElement record, string kind, HashSet<string> pending)
    {
        var operation = Field(record, "operationId");
        switch (kind)
        {
            case "storageCleanup" when operation.Length > 0: pending.Add("cleanup:" + operation); break;
            case "storageCleanupApplied": pending.Remove("cleanup:" + operation); break;
            case "storageMutation" when operation.Length > 0:
                if (Field(record, "state") == "pending") pending.Add("mutation:" + operation);
                else if (Field(record, "state") == "completed") pending.Remove("mutation:" + operation);
                break;
        }
    }

    private static string Field(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
}

public sealed record IndexInventory(int Messages, int UniqueFiles, IReadOnlyDictionary<string, int> Counts,
    IReadOnlyDictionary<string, int> ReferenceKinds, int Headers, int WrongFolder, int Duplicates,
    int Unreadable, int CheckedPayloads, int Orphans, int UnknownPayloads,
    int? SerialFiles, int IndexedWithoutMetadata, int MetadataWithoutIndex, int Pending);
