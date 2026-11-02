using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> OwnerGates = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, MaintenancePriority> OwnerMaintenance = new(StringComparer.Ordinal);
    private static readonly object OwnerIdentityGate = new();
    private readonly DiscordApiClient _api;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly MetadataService _metadata;
    private readonly SemaphoreSlim _gate;
    private readonly MaintenancePriority _maintenance;
    private readonly Dictionary<string, StorageThreadRecord> _catalog = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StorageReferenceRecord> _references = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StorageCleanupRecord> _cleanups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _appliedCleanups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StorageBatchPlan> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _legacyCatalogIds = new(StringComparer.Ordinal);
    private readonly Func<CancellationToken, Task<FolderIndexMapping>> _journalIndex;
    private readonly SemaphoreSlim _journalIndexGate = new(1, 1);
    private string? _journalThreadId;
    private ulong _frontier;
    private bool _initialized;
    internal string OwnerId { get; }
    internal SharedPayloadBatcher Batcher { get; }

    internal SharedStorageManager(DiscordApiClient api, SettingsStore store, AppSettings settings, string? ownerId = null,
        Func<CancellationToken, Task<FolderIndexMapping>>? journalIndex = null)
    {
        _api = api; _store = store; _settings = settings.Clone();
        _metadata = new MetadataService(MetadataCodec.ForStore(store), api.DownloadAttachmentBytesAsync);
        _journalIndex = journalIndex ?? (token => new AppRegistry(api, store, settings).GetStorageJournalIndexAsync(token));
        OwnerId = ownerId ?? ResolveOwner(store);
        var ownerKey = OwnerId + ":" + settings.NamesChannelId + ":" + settings.AppsForumId;
        _gate = OwnerGates.GetOrAdd(ownerKey, _ => new SemaphoreSlim(1, 1));
        _maintenance = OwnerMaintenance.GetOrAdd(ownerKey, _ => new MaintenancePriority());
        Batcher = new SharedPayloadBatcher(this, api, _metadata);
    }

    private static string ResolveOwner(SettingsStore store)
    {
        lock (OwnerIdentityGate)
        {
            using var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                (machine?.GetValue("MachineGuid") as string ?? Environment.MachineName) + ":" +
                WindowsIdentity.GetCurrent().User?.Value))).ToLowerInvariant();
            var secrets = (IEncryptionSecretStore)store;
            var key = "sharedStorageWriter:" + binding;
            var id = secrets.ReadEncryptionSecret(key);
            if (!Guid.TryParseExact(id, "N", out _))
            {
                id = Guid.NewGuid().ToString("N");
                secrets.WriteEncryptionSecret(key, id);
            }
            return id;
        }
    }

    internal async Task<IReadOnlyDictionary<string, SharedStorageReservation>> ReserveManyAsync(
        IReadOnlyList<(string FileId, string VersionId)> files, CancellationToken token, bool onePoolOnly = false)
    {
        if (files.Count == 0) return new Dictionary<string, SharedStorageReservation>();
        await _gate.WaitAsync(token);
        try
        {
            await LoadJournalSnapshotAsync(token);
            if (!UseReservationHints()) await RefreshJournalAsync(token);
            var result = new Dictionary<string, SharedStorageReservation>(StringComparer.Ordinal);
            var remaining = files.Distinct().ToList();
            while (remaining.Count > 0)
            {
                var first = remaining[0];
                var selected = _catalog.Values.FirstOrDefault(row => row.OwnerId == OwnerId && row.State == "open" && row.Members.Any(member => member.FileId == first.FileId))
                    ?? _catalog.Values.FirstOrDefault(row => row.OwnerId == OwnerId && row.State == "open" && row.Members.Count < DiscordLimits.MaxFilesPerSharedThread);
                if (selected == null)
                {
                    foreach (var known in _catalog.Values.Where(row => row.OwnerId == OwnerId && row.State == "open").ToArray())
                    {
                        var current = await ReloadCatalogAsync(known, token);
                        if (current.Members.Count < DiscordLimits.MaxFilesPerSharedThread || current.Members.Any(member => member.FileId == first.FileId))
                        { selected = current; break; }
                    }
                }
                try
                {
                    selected = selected == null ? await CreateStorageAsync(token) : await ReloadCatalogAsync(selected, token);
                    await _api.GetChannelAsync(selected.ThreadId, token);
                }
                catch (DiscordApiException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound && selected != null)
                {
                    await RetireMissingPoolAsync(selected, token);
                    continue;
                }
                if (selected.State != "open" || selected.Members.Count >= DiscordLimits.MaxFilesPerSharedThread &&
                    !selected.Members.Any(member => member.FileId == first.FileId)) continue;
                var assigned = new List<(string FileId, string VersionId)>();
                foreach (var file in remaining)
                {
                    if (string.IsNullOrWhiteSpace(file.FileId) || string.IsNullOrEmpty(file.VersionId))
                        throw new InvalidDataException("A reserva exige fileId e versão persistentes.");
                    var member = selected.Members.FirstOrDefault(member => member.FileId == file.FileId);
                    if (member == null)
                    {
                        if (selected.Members.Count == DiscordLimits.MaxFilesPerSharedThread) continue;
                        member = new StorageMember { FileId = file.FileId }; selected.Members.Add(member);
                    }
                    if (!member.PendingVersions.Contains(file.VersionId, StringComparer.Ordinal)) member.PendingVersions.Add(file.VersionId);
                    assigned.Add(file);
                    result[file.VersionId] = new(file.FileId, file.VersionId, selected.ThreadId,
                        new StorageLayout { StorageId = selected.StorageId });
                }
                await SaveCatalogAsync(selected, token);
                foreach (var assignedFile in assigned) remaining.Remove(assignedFile);
                if (onePoolOnly) break;
            }
            WarmReservationJournal(token);
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<SharedStorageReservation> ReserveAsync(string fileId, string versionId, CancellationToken token) =>
        (await ReserveManyAsync([(fileId, versionId)], token))[versionId];

    internal async Task<bool> ValidateUploadPoolAsync(IReadOnlyList<TransferState> transfers, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var first = transfers[0];
            if (!UseReservationHints() || !_catalog.ContainsKey(first.Storage!.StorageId!)) await RefreshJournalAsync(token);
            _catalog.TryGetValue(first.Storage!.StorageId!, out var row);
            var missingThread = false;
            try { await _api.GetChannelAsync(first.ThreadId, token); }
            catch (DiscordApiException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { missingThread = true; }
            if (row != null && (row.ThreadId != first.ThreadId || row.OwnerId != OwnerId))
                throw new IOException("A identidade do destino remoto mudou; o checkpoint foi preservado.");
            if (!missingThread)
            {
                if (row == null) throw new IOException("O catálogo remoto não foi encontrado; o checkpoint foi preservado.");
                row = await ReloadCatalogAsync(row, token);
                if (row.OwnerId != OwnerId || transfers.Any(state => !row.Members.Any(member => member.FileId == state.FileId)))
                    throw new IOException("A reserva remota mudou; nenhuma identidade foi substituída.");
                return true;
            }
            if (transfers.Any(state => state.UploadedChunks.Count > 0 || state.ThumbnailRef != null || state.VideoPreview480pChunkRefs is { Count: > 0 }))
                throw new IOException("O destino de uma transferência com partes confirmadas foi removido. Os checkpoints foram preservados.");
            if (row != null) await RetireMissingPoolAsync(row, token);
            return false;
        }
        finally { _gate.Release(); }
    }

    private async Task RetireMissingPoolAsync(StorageThreadRecord row, CancellationToken token)
    {
        row.State = "closed";
        try { await SaveCatalogAsync(row, token); }
        catch (DiscordApiException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        { _catalog.Remove(row.StorageId); SaveCatalogHints(); }
    }

    internal async Task<IAsyncDisposable> HoldJournalWriteAsync(CancellationToken token) =>
        await _api.AcquireThreadWriteAsync(await GetJournalThreadAsync(token), token);

    internal async Task VerifyReservationAsync(SharedStorageReservation reservation, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!UseReservationHints() || !_catalog.ContainsKey(reservation.Layout.StorageId!)) await RefreshJournalAsync(token);
            if (!_catalog.TryGetValue(reservation.Layout.StorageId!, out var record)) throw new IOException("Armazenamento compartilhado ausente do catálogo.");
            record = await ReloadCatalogAsync(record, token);
            if (record.OwnerId != OwnerId || !record.Members.Any(member => member.FileId == reservation.FileId))
                throw new IOException("Essa transferência deve ser retomada pela instalação que reservou o armazenamento.");
            WarmReservationJournal(token);
        }
        finally { _gate.Release(); }
    }

    private async Task<StorageThreadRecord> CreateStorageAsync(CancellationToken token)
    {
        var row = new StorageThreadRecord { OwnerId = OwnerId };
        // Persist the creation intent before Discord; a lost reply can be reconciled by storageId.
        StoreCreation(row);
        var content = _metadata.EncodeJson(JsonSerializer.Serialize(new
        { schemaVersion = 1, entryType = "storageControl", layoutVersion = 2, storageId = row.StorageId, ownerId = OwnerId }, JsonOptions.Web));
        var created = await _api.CreateForumPostAsync(_settings.AppsForumId, "storage-" + row.StorageId, content, token, replayUnknownResult: false);
        row.ThreadId = created.ThreadId; row.ControlMessageId = created.MessageId;
        StoreCreation(row);
        _api.RegisterSharedThread(row.ThreadId);
        var message = await WriteRecordAsync(row, null, token);
        row.CatalogMessageId = message.Id; _catalog[row.StorageId] = row;
        StoreCreation(row, remove: true);
        return row;
    }

    private async Task<StorageThreadRecord> ReloadCatalogAsync(StorageThreadRecord row, CancellationToken token)
    {
        var channel = string.IsNullOrEmpty(row.CatalogChannelId) ? await GetJournalThreadAsync(token) : row.CatalogChannelId;
        using var document = await _metadata.ReadJsonAsync(await _api.GetMessageAsync(channel, row.CatalogMessageId, token), token)
            ?? throw new IOException("O catálogo remoto está incompleto.");
        var current = document.RootElement.Deserialize<StorageThreadRecord>(JsonOptions.Web) ?? throw new InvalidDataException("Catálogo inválido.");
        ValidateCatalog(current);
        if (current.StorageId != row.StorageId || current.OwnerId != OwnerId || current.ThreadId != row.ThreadId)
            throw new IOException("A identidade do catálogo mudou; nenhuma reserva foi sobrescrita.");
        current.CatalogMessageId = row.CatalogMessageId; current.CatalogChannelId = channel; _catalog[current.StorageId] = current;
        return current;
    }

    private async Task SaveCatalogAsync(StorageThreadRecord row, CancellationToken token)
    {
        ValidateCatalog(row);
        row.Revision++;
        var updated = await WriteRecordAsync(row,
            row.CatalogChannelId == _settings.NamesChannelId ? null : row.CatalogMessageId, token);
        row.CatalogMessageId = updated.Id; row.CatalogChannelId = await GetJournalThreadAsync(token); _catalog[row.StorageId] = row;
        SaveCatalogHints();
    }

    private static void ValidateCatalog(StorageThreadRecord row)
    {
        if (row.SchemaVersion != 1 || row.EntryType != "storageThread" || row.LayoutVersion != 2 ||
            !Guid.TryParseExact(row.StorageId, "N", out _) || string.IsNullOrEmpty(row.OwnerId) ||
            string.IsNullOrEmpty(row.ThreadId) || string.IsNullOrEmpty(row.ControlMessageId) || row.State is not ("open" or "closed") ||
            row.Members == null || row.Members.Any(member => member == null || string.IsNullOrWhiteSpace(member.FileId) ||
                member.PendingVersions == null || member.PendingVersions.Any(string.IsNullOrWhiteSpace)) ||
            row.Members.Count > DiscordLimits.MaxFilesPerSharedThread || row.Members.Select(member => member.FileId).Distinct().Count() != row.Members.Count)
            throw new InvalidDataException("O catálogo compartilhado está inválido ou possui uma versão não suportada.");
    }

    internal async Task<DiscordMessageDto> WriteRecordAsync<T>(T record, string? messageId, CancellationToken token)
    {
        var channel = await GetJournalThreadAsync(token);
        await using var lease = await _api.AcquireThreadWriteAsync(channel, token);
        return await _api.WriteEncodedDocumentAsync(channel,
            _metadata.EncodeJson(JsonSerializer.Serialize(record, JsonOptions.Web)), messageId, token, serializeRoute: false);
    }

    private async Task RefreshJournalAsync(CancellationToken token)
    {
        await LoadJournalSnapshotAsync(token);
        if (!_initialized && _reservationWarmup != null)
        {
            var warmup = _reservationWarmup;
            try { await ApplyJournalMessagesAsync(await warmup.WaitAsync(token), token); }
            finally { _reservationWarmup = null; }
        }
        var messages = await ReadJournalMessagesAsync(_initialized ? 100 : null, token);
        if (_initialized && messages.Any(message => Snowflake(message.Id) > _frontier && JournalOrder(message) <= _frontier))
        {
            // A newly copied event can have an older logical order. Replay the complete journal, not a stale tail.
            messages = await ReadJournalMessagesAsync(null, token);
            _catalog.Clear(); _references.Clear(); _cleanups.Clear(); _appliedCleanups.Clear(); _plans.Clear();
            _mutations.Clear(); _deletionFences.Clear(); _initialized = false; _frontier = 0;
        }
        await ApplyJournalMessagesAsync(messages, token);
        await SaveJournalSnapshotAsync(token);
    }

    private async Task ApplyJournalMessagesAsync(List<DiscordMessageDto> messages, CancellationToken token)
    {
        if (!_initialized)
        {
            _catalog.Clear(); _references.Clear(); _cleanups.Clear(); _appliedCleanups.Clear(); _plans.Clear();
            _mutations.Clear(); _deletionFences.Clear(); _frontier = 0;
        }
        foreach (var message in messages.Where(message => !_initialized || Snowflake(message.Id) > _frontier)
                     .OrderBy(JournalOrder).ThenBy(message => Snowflake(message.Id)))
        {
            using var document = await _metadata.ReadJsonAsync(message, token);
            if (document == null || !document.RootElement.TryGetProperty("entryType", out var type)) continue;
            var root = document.RootElement;
            var kind = type.GetString();
            if (kind?.StartsWith("storage", StringComparison.Ordinal) != true) continue;
            if (!root.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1)
                throw new InvalidDataException("Versão do catálogo de armazenamento não suportada.");
            switch (kind)
            {
                case "storageThread":
                    var row = root.Deserialize<StorageThreadRecord>(JsonOptions.Web)!; ValidateCatalog(row);
                    row.CatalogMessageId = message.Id;
                    row.CatalogChannelId = _legacyCatalogIds.ContainsKey(message.Id) ? _settings.NamesChannelId : await GetJournalThreadAsync(token);
                    _catalog[row.StorageId] = row; _api.RegisterSharedThread(row.ThreadId); break;
                case "storageReference":
                    var reference = root.Deserialize<StorageReferenceRecord>(JsonOptions.Web)!;
                    if (reference.State is not ("live" or "released") || string.IsNullOrEmpty(reference.Scope)) throw new InvalidDataException("Referência remota inválida.");
                    _references[reference.Scope] = reference; break;
                case "storageCleanup":
                    var cleanup = root.Deserialize<StorageCleanupRecord>(JsonOptions.Web)!; _cleanups[cleanup.OperationId] = cleanup;
                    if (cleanup.Scope == "file:" + cleanup.FileId) _deletionFences.Add(cleanup.FileId);
                    break;
                case "storageCleanupApplied": _appliedCleanups.Add(root.GetProperty("operationId").GetString()!); break;
                case "storageBatchPlan":
                    var plan = root.Deserialize<StorageBatchPlan>(JsonOptions.Web)!;
                    _plans[plan.BatchId] = plan;
                    while (_plans.Count > DiscordLimits.SharedPlanCacheEntries) _plans.Remove(_plans.Keys.First());
                    break;
                case "storageBatchReceipt": break;
                case "storageMutation":
                    var state = root.GetProperty("state").GetString();
                    if (state is not ("pending" or "completed")) throw new InvalidDataException("Estado de alteração remota inválido.");
                    var operation = root.GetProperty("operationId").GetString()!;
                    if (state == "completed") _mutations.Remove(operation);
                    else _mutations[operation] = (root.GetProperty("fileId").GetString()!, root.GetProperty("ownerId").GetString()!, true,
                        _mutations.TryGetValue(operation, out var priorClaim) ? Math.Min(priorClaim.ClaimId, JournalOrder(message)) : JournalOrder(message));
                    break;
                default: throw new InvalidDataException("Registro de armazenamento ainda não suportado.");
            }
        }
        if (messages.Count > 0) _frontier = Math.Max(_frontier, messages.Max(message => Snowflake(message.Id)));
        _initialized = true;
        SaveCatalogHints();
        await RecoverCreationsAsync(token);
    }

    internal async Task PublishFileReferenceAsync(AppEntry entry, string metadataThread, CancellationToken token)
    {
        if (entry.Type != "file") return;
        await PublishReferenceAsync("file:" + entry.FileId, entry.FileId,
            Payloads(MetadataService.CreateFileMetadata(entry)), metadataThread, entry.MetadataMessageId, token);
    }

    internal async Task PublishReferenceAsync(string scope, string fileId, List<StoragePayloadVersion> payloads,
        string metadataThread, string metadataMessage, CancellationToken token)
    {
        var record = new StorageReferenceRecord
        {
            Scope = scope,
            FileId = fileId,
            Payloads = payloads,
            OwnerId = OwnerId,
            MetadataThreadId = metadataThread,
            MetadataMessageId = metadataMessage
        };
        await WriteRecordAsync(record, null, token);
    }

    internal Task ReleaseReferenceAsync(string scope, string fileId, CancellationToken token) =>
        WriteRecordAsync(new StorageReferenceRecord { Scope = scope, FileId = fileId, State = "released", OwnerId = OwnerId }, null, token);

    internal async Task ReconcileReservationsAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            var committed = _references.Values.Where(reference => reference.State == "live")
                .SelectMany(reference => reference.Payloads).Select(payload => payload.VersionId).ToHashSet(StringComparer.Ordinal);
            foreach (var known in _catalog.Values.Where(row => row.OwnerId == OwnerId).ToArray())
            {
                var row = await ReloadCatalogAsync(known, token);
                var changed = false;
                foreach (var member in row.Members) changed |= member.PendingVersions.RemoveAll(committed.Contains) > 0;
                if (changed) await SaveCatalogAsync(row, token);
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task<IAsyncDisposable> HoldWritesAsync(IEnumerable<string> threads, CancellationToken token)
    {
        var leases = new List<IAsyncDisposable>();
        try
        {
            var targets = threads.Distinct(StringComparer.Ordinal).ToList();
            if (targets.Count > 0) targets.Add(await GetJournalThreadAsync(token));
            foreach (var thread in targets.Distinct(StringComparer.Ordinal)) leases.Add(await _api.AcquireThreadWriteAsync(thread, token));
            return new WriteLeaseGroup(leases);
        }
        catch { foreach (var lease in leases) await lease.DisposeAsync(); throw; }
    }

    private sealed class WriteLeaseGroup(List<IAsyncDisposable> leases) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { foreach (var lease in leases) await lease.DisposeAsync(); }
    }

    internal static List<StoragePayloadVersion> Payloads(FileMetadata metadata)
    {
        var result = new List<StoragePayloadVersion>();
        foreach (var file in new[] { metadata }.Concat(metadata.Versions?.Select(version => version.Payload) ?? []))
        {
            var attachments = (file.ChunkRefs ?? []).Select(reference => new AttachmentReference
            { MessageId = reference.MessageId, AttachmentId = reference.AttachmentId }).ToList();
            if (file.Preview?.ThumbnailRef != null) attachments.Add(file.Preview.ThumbnailRef);
            attachments.AddRange((file.Preview?.Video480p?.ChunkRefs ?? []).Select(reference => new AttachmentReference
            { MessageId = reference.MessageId, AttachmentId = reference.AttachmentId }));
            result.Add(new StoragePayloadVersion
            {
                FileId = file.FileId,
                VersionId = file.VersionId ?? "",
                ThreadId = file.ThreadId,
                Storage = file.Storage,
                Attachments = attachments.Distinct().ToList()
            });
        }
        return result;
    }

    private static ulong Snowflake(string id) => ulong.TryParse(id, out var value) ? value : throw new InvalidDataException("ID remoto inválido.");
}
