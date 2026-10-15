using System.IO;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private bool _creationsRecovered;
    private string CreationKey => "storageCreations:" + OwnerId;

    private List<StorageThreadRecord> PendingCreations()
    {
        var value = ((IEncryptionSecretStore)_store).ReadEncryptionSecret(CreationKey);
        return value.Length == 0 ? [] : JsonSerializer.Deserialize<List<StorageThreadRecord>>(value, JsonOptions.Web)
            ?? throw new InvalidDataException("Estado de criação de armazenamento inválido.");
    }

    private void StoreCreation(StorageThreadRecord row, bool remove = false)
    {
        var pending = PendingCreations();
        pending.RemoveAll(item => item.StorageId == row.StorageId);
        if (!remove) { pending.Add(row); _creationsRecovered = false; }
        ((IEncryptionSecretStore)_store).WriteEncryptionSecret(CreationKey, JsonSerializer.Serialize(pending, JsonOptions.Web));
    }

    private async Task RecoverCreationsAsync(CancellationToken token)
    {
        if (_creationsRecovered) return;
        foreach (var pending in PendingCreations())
        {
            if (_catalog.ContainsKey(pending.StorageId)) { StoreCreation(pending, remove: true); continue; }
            if (pending.OwnerId != OwnerId) throw new InvalidDataException("A criação pendente pertence a outra instalação.");
            if (pending.ThreadId.Length == 0)
            {
                var threads = await _api.GetForumThreadsAsync(_settings.AppsForumId, token);
                foreach (var thread in threads.Where(thread => thread.Name == "storage-" + pending.StorageId))
                {
                    var messages = await _api.GetMessagesAsync(thread.Id, 5, token);
                    foreach (var message in messages)
                    {
                        using var control = await _metadata.ReadJsonAsync(message, token);
                        if (control?.RootElement.TryGetProperty("entryType", out var kind) != true || kind.GetString() != "storageControl") continue;
                        if (message.Attachments.Count != 0 || control.RootElement.GetProperty("schemaVersion").GetInt32() != 1 ||
                            control.RootElement.GetProperty("layoutVersion").GetInt32() != 2) throw new InvalidDataException("Controle remoto inválido.");
                        if (control.RootElement.GetProperty("storageId").GetString() != pending.StorageId ||
                            control.RootElement.GetProperty("ownerId").GetString() != OwnerId) continue;
                        pending.ThreadId = thread.Id; pending.ControlMessageId = message.Id; break;
                    }
                    if (pending.ThreadId.Length > 0) break;
                }
            }
            if (pending.ThreadId.Length == 0) { StoreCreation(pending, remove: true); continue; }
            _api.RegisterSharedThread(pending.ThreadId);
            var created = await WriteRecordAsync(pending, null, token);
            pending.CatalogMessageId = created.Id; _catalog[pending.StorageId] = pending;
            StoreCreation(pending, remove: true);
        }
        _creationsRecovered = true;
    }

    internal async Task<IReadOnlyList<ChunkReference>> RecoverChunksAsync(SharedStorageReservation reservation,
        string role, int count, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            var recovered = await RecoverPayloadReferencesAsync(reservation.FileId, reservation.VersionId, reservation.ThreadId, token);
            return ChunkReference.Validate(recovered.Where(item => item.Part.Role == role && item.Part.Count == count)
                .GroupBy(item => item.Part.Index).Select(group => group.Last().Reference).ToArray(), totalChunks: count, partial: true);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<(StorageBatchPart Part, ChunkReference Reference)>> RecoverPayloadReferencesAsync(
        string fileId, string versionId, string threadId, CancellationToken token)
    {
        // Plans are durable remotely; the bounded in-memory cache is never the recovery authority.
        var plans = new Dictionary<string, StorageBatchPlan>(StringComparer.Ordinal);
        foreach (var message in await _api.GetMessagesAsync(_settings.NamesChannelId, null, token))
        {
            using var json = await _metadata.ReadJsonAsync(message, token);
            if (json?.RootElement.TryGetProperty("entryType", out var kind) != true || kind.GetString() != "storageBatchPlan") continue;
            var plan = json.RootElement.Deserialize<StorageBatchPlan>(JsonOptions.Web) ?? throw new InvalidDataException("Plano remoto inválido.");
            if (plan.SchemaVersion != 1 || !Guid.TryParseExact(plan.BatchId, "N", out _) || plan.Parts is not { Count: > 0 and <= 10 } ||
                plan.Parts.Any(part => part.Count < 1 || part.Index < 0 || part.Index >= part.Count || string.IsNullOrWhiteSpace(part.Filename)))
                throw new InvalidDataException("Plano remoto incompleto ou não suportado.");
            if (plan.ThreadId == threadId && plan.Parts.Any(part => part.FileId == fileId && part.VersionId == versionId)) plans[plan.BatchId] = plan;
        }
        var result = new List<(StorageBatchPart, ChunkReference)>();
        if (plans.Count == 0) return result;
        foreach (var message in await _api.GetMessagesAsync(threadId, null, token))
        {
            if (message.Attachments.Count == 0) continue;
            var label = _metadata.DecodePayloadReference(message.Content);
            const string prefix = "shared:v2 batch:";
            if (!label.StartsWith(prefix, StringComparison.Ordinal) || !plans.TryGetValue(label[prefix.Length..], out var plan)) continue;
            foreach (var part in plan.Parts.Where(part => part.FileId == fileId && part.VersionId == versionId))
            {
                var attachment = message.Attachments.SingleOrDefault(attachment => attachment.FileName == part.Filename && attachment.Size == part.SizeBytes);
                if (attachment == null) continue;
                if (string.IsNullOrEmpty(attachment.Id)) throw new InvalidDataException("Uma confirmação remota está incompleta.");
                result.Add((part, new ChunkReference { Index = part.Index, MessageId = message.Id, AttachmentId = attachment.Id }));
            }
        }
        return result;
    }
}
