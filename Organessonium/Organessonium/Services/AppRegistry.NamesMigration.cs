using System.IO;
using System.Text.Json.Nodes;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed record NamesStorageMigrationResult(int Candidates, int Copied, int Removed, int Preserved);

public sealed partial class AppRegistry
{
    private static readonly HashSet<string> MigratableStorageTypes = new(StringComparer.Ordinal)
    {
        "storageThread", "storageReference", "storageMutation", "storageCleanup", "storageCleanupApplied",
        "storageBatchPlan", "storageBatchReceipt"
    };

    // Explicit maintenance operation. Call only with library writers stopped; never run on ordinary reads.
    public async Task<NamesStorageMigrationResult> MigrateNamesStorageJournalAsync(
        Action<string, int, int>? progress = null, CancellationToken token = default)
    {
        var names = await _discord.GetMessagesAsync(_settings.NamesChannelId, null, token);
        var candidates = new List<(DiscordMessageDto Message, JsonObject Json)>();
        foreach (var message in names.OrderBy(message => ulong.Parse(message.Id)))
        {
            using var json = await _metadata.ReadJsonAsync(message, token);
            if (json?.RootElement.TryGetProperty("entryType", out var type) != true) continue;
            var kind = type.GetString() ?? "";
            if (!kind.StartsWith("storage", StringComparison.Ordinal)) continue;
            if (!MigratableStorageTypes.Contains(kind) || json.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidDataException("A migração encontrou um tipo/versão de registro não suportado; nenhuma origem foi removida.");
            var payload = JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
            if (payload.ContainsKey("legacyNamesMessageId"))
                throw new InvalidDataException("O registro de origem já contém um marcador de migração; nenhuma origem foi removida.");
            candidates.Add((message, payload));
        }
        if (candidates.Count == 0) return new(0, 0, 0, names.Count);
        var index = await GetStorageJournalIndexAsync(token);
        var previousCopies = new Dictionary<string, (DiscordMessageDto Message, JsonObject Json)>(StringComparer.Ordinal);
        foreach (var message in await _discord.GetMessagesAsync(index.ThreadId, null, token))
        {
            using var json = await _metadata.ReadJsonAsync(message, token);
            if (json?.RootElement.TryGetProperty("legacyNamesMessageId", out var origin) == true)
                previousCopies[origin.GetString()!] = (message, JsonNode.Parse(json.RootElement.GetRawText())!.AsObject());
        }
        var verified = new List<(DiscordMessageDto Source, JsonObject Original, DiscordMessageDto Copy)>();
        var copied = 0;
        await using var lease = await _discord.AcquireThreadWriteAsync(index.ThreadId, token);
        foreach (var (message, original) in candidates)
        {
            var expected = (JsonObject)original.DeepClone();
            expected["legacyNamesMessageId"] = message.Id;
            DiscordMessageDto destination;
            if (previousCopies.TryGetValue(message.Id, out var previous))
            {
                if (!JsonNode.DeepEquals(previous.Json, expected))
                    throw new InvalidDataException("Uma cópia anterior não corresponde à origem; nenhuma origem foi removida.");
                destination = previous.Message;
            }
            else
            {
                destination = await _discord.WriteEncodedDocumentAsync(index.ThreadId,
                    _metadata.EncodeJson(expected.ToJsonString()), null, token);
                copied++;
            }
            using var confirmed = await _metadata.ReadJsonAsync(
                await _discord.GetMessageAsync(index.ThreadId, destination.Id, token), token);
            if (confirmed == null || !JsonNode.DeepEquals(JsonNode.Parse(confirmed.RootElement.GetRawText()), expected))
                throw new InvalidDataException("A confirmação do destino falhou; nenhuma origem foi removida.");
            verified.Add((message, original, destination));
            progress?.Invoke("verified", verified.Count, candidates.Count);
        }
        // Verify the complete source snapshot again before the first deletion. Any concurrent writer aborts this run.
        var current = await _discord.GetMessagesAsync(_settings.NamesChannelId, null, token);
        var sourceIds = candidates.Select(candidate => candidate.Message.Id).ToHashSet(StringComparer.Ordinal);
        var currentIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in current)
        {
            using var json = await _metadata.ReadJsonAsync(message, token);
            if (json?.RootElement.TryGetProperty("entryType", out var kind) == true &&
                kind.GetString()?.StartsWith("storage", StringComparison.Ordinal) == true) currentIds.Add(message.Id);
        }
        if (!sourceIds.SetEquals(currentIds))
            throw new IOException("O canal mudou durante a migração. As cópias foram conservadas, sem remover as origens.");
        foreach (var item in verified)
        {
            using var source = await _metadata.ReadJsonAsync(
                await _discord.GetMessageAsync(_settings.NamesChannelId, item.Source.Id, token), token);
            if (source == null || !JsonNode.DeepEquals(JsonNode.Parse(source.RootElement.GetRawText()), item.Original))
                throw new IOException("Um registro foi alterado durante a migração; as origens foram preservadas.");
        }
        var removed = 0;
        foreach (var item in verified)
        {
            progress?.Invoke("removing", removed, candidates.Count);
            using var source = await _metadata.ReadJsonAsync(
                await _discord.GetMessageAsync(_settings.NamesChannelId, item.Source.Id, token), token);
            if (source == null || !JsonNode.DeepEquals(JsonNode.Parse(source.RootElement.GetRawText()), item.Original))
                throw new IOException("A origem mudou; esse registro não foi removido.");
            // Revalidate each destination immediately before removing that source. No payload DELETE is used.
            using var destination = await _metadata.ReadJsonAsync(
                await _discord.GetMessageAsync(index.ThreadId, item.Copy.Id, token), token);
            var expected = (JsonObject)item.Original.DeepClone(); expected["legacyNamesMessageId"] = item.Source.Id;
            if (destination == null || !JsonNode.DeepEquals(JsonNode.Parse(destination.RootElement.GetRawText()), expected))
                throw new IOException("O destino mudou; a origem desse registro foi preservada.");
            await _discord.DeleteMessageAsync(_settings.NamesChannelId, item.Source.Id, token);
            removed++;
            progress?.Invoke("removed", removed, candidates.Count);
        }
        return new(candidates.Count, copied, removed, names.Count - candidates.Count);
    }
}
