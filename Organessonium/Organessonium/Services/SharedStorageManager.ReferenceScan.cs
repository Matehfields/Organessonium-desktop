using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    // Destructive operations use fresh remote metadata, including older records that have no ledger.
    // An unreadable index aborts cleanup. SQLite and a stale/released reference are not proof of absence.
    private async Task<List<StoragePayloadVersion>> ReadRemoteReferencesAsync(CancellationToken token)
    {
        var names = await _api.GetMessagesAsync(_settings.NamesChannelId, null, token);
        var mappings = await _metadata.ReadFolderIndexMappingsInParallelAsync(names, 4, token);
        var live = new ConcurrentBag<StoragePayloadVersion>();
        async Task InspectAsync(DiscordMessageDto message)
        {
            using var json = await _metadata.ReadJsonAsync(message, token);
            if (json?.RootElement.TryGetProperty("entryType", out var type) != true) return;
            if (type.GetString() == "file")
            {
                ChunkReference.ValidateJsonPresence(json.RootElement);
                var file = json.RootElement.Deserialize<FileMetadata>(JsonOptions.Web) ?? throw new InvalidDataException("Metadados remotos incompletos.");
                MetadataService.Validate(file);
                foreach (var item in Payloads(file)) live.Add(item);
            }
            else if (type.GetString() == "syncRevision" && json.RootElement.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                ChunkReference.ValidateJsonPresence(payload);
                var file = payload.Deserialize<FileMetadata>(JsonOptions.Web) ?? throw new InvalidDataException("Revisão remota inválida.");
                MetadataService.Validate(file);
                foreach (var item in Payloads(file)) live.Add(item);
            }
        }
        foreach (var message in names) await InspectAsync(message);
        await Parallel.ForEachAsync(mappings.DistinctBy(mapping => mapping.ThreadId),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (mapping, cancellation) =>
            {
                foreach (var message in await _api.GetMessagesAsync(mapping.ThreadId, null, cancellation))
                    await InspectAsync(message);
            });
        return live.ToList();
    }

    internal async Task<StorageCleanupRecord> BeginPayloadCleanupAsync(string scope, string fileId,
        List<StoragePayloadVersion> payloads, CancellationToken token, string metadataThreadId = "", string metadataMessageId = "")
    {
        var request = new StorageCleanupRecord
        {
            Scope = scope,
            FileId = fileId,
            Payloads = payloads,
            MetadataThreadId = metadataThreadId,
            MetadataMessageId = metadataMessageId
        };
        await WriteRecordAsync(request, null, token);
        return request;
    }

    internal async Task<string> LocateMetadataAsync(AppEntry entry, CancellationToken token)
    {
        // A local mapping is only a locator hint, never proof that a payload is unreferenced.
        if (_store.LoadFolderIndexMappings().TryGetValue(entry.ParentFolderId ?? AppRegistry.RootFolderId, out var cached) &&
            !string.IsNullOrWhiteSpace(cached.ThreadId))
        {
            try { await _api.GetMessageAsync(cached.ThreadId, entry.MetadataMessageId, token); return cached.ThreadId; }
            catch (DiscordApiException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }
        var names = await _api.GetMessagesAsync(_settings.NamesChannelId, null, token);
        var mappings = await _metadata.ReadFolderIndexMappingsInParallelAsync(names, 4, token);
        foreach (var channel in new[] { _settings.NamesChannelId }.Concat(mappings.Where(mapping => mapping.FolderId == entry.ParentFolderId).Select(mapping => mapping.ThreadId)))
        {
            try { await _api.GetMessageAsync(channel, entry.MetadataMessageId, token); return channel; }
            catch (DiscordApiException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }
        throw new IOException("Os metadados do arquivo não foram encontrados; os attachments foram preservados.");
    }
}
