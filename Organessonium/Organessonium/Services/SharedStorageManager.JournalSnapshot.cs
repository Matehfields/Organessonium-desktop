using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private bool _snapshotAttempted;
    private DateTime _snapshotSaved;
    internal async Task WarmReadIndexAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { await RefreshJournalAsync(token); }
        finally { _gate.Release(); }
    }
    private string JournalSnapshotPath => Path.Combine(_store.DataDirectory.FullName, "StorageJournalCache",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OwnerId + ":" + _settings.BotToken + ":" +
            _settings.NamesChannelId + ":" + _settings.FoldersForumId + ":" + _settings.AppsForumId))) + ".bin");

    private async Task LoadJournalSnapshotAsync(CancellationToken token)
    {
        if (_snapshotAttempted || _initialized) return;
        _snapshotAttempted = true;
        var path = JournalSnapshotPath;
        if (!File.Exists(path)) return;
        try
        {
            var channel = await GetJournalThreadAsync(token);
            var snapshot = await Task.Run(() => JsonSerializer.Deserialize<JournalSnapshot>(
                ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser), JsonOptions.Web), token);
            if (snapshot == null || snapshot.Version != 1 || snapshot.Channel != channel || snapshot.Frontier == 0) return;
            foreach (var row in snapshot.Catalog) ValidateCatalog(row);
            _catalog.Clear(); _references.Clear(); _cleanups.Clear(); _appliedCleanups.Clear(); _plans.Clear();
            _mutations.Clear(); _deletionFences.Clear();
            foreach (var row in snapshot.Catalog) { _catalog[row.StorageId] = row; _api.RegisterSharedThread(row.ThreadId); }
            foreach (var row in snapshot.References) _references[row.Scope] = row;
            foreach (var row in snapshot.Cleanups) _cleanups[row.OperationId] = row;
            _appliedCleanups.UnionWith(snapshot.Applied);
            _deletionFences.UnionWith(snapshot.Fences);
            foreach (var row in snapshot.Plans) _plans[row.BatchId] = row;
            foreach (var row in snapshot.Mutations) _mutations[row.Operation] = (row.FileId, row.OwnerId, true, row.ClaimId);
            foreach (var row in snapshot.CatalogLocators)
                if (_catalog.TryGetValue(row.Key, out var catalog))
                { catalog.CatalogChannelId = row.Value.Channel; catalog.CatalogMessageId = row.Value.Message; }
            _frontier = snapshot.Frontier; _initialized = true;
        }
        catch (OperationCanceledException) { _snapshotAttempted = false; throw; }
        catch (Exception error) { AppLogger.Error("SharedStorage.JournalSnapshotRead", error); }
    }

    private async Task SaveJournalSnapshotAsync(CancellationToken token)
    {
        if (!_initialized || _frontier == 0 || DateTime.UtcNow - _snapshotSaved < TimeSpan.FromSeconds(2)) return;
        var snapshot = new JournalSnapshot(1, await GetJournalThreadAsync(token), _frontier, _catalog.Values.ToArray(),
            _catalog.ToDictionary(pair => pair.Key, pair => new JournalLocator(pair.Value.CatalogChannelId, pair.Value.CatalogMessageId)),
            _references.Values.ToArray(), _cleanups.Values.ToArray(), _appliedCleanups.ToArray(), _deletionFences.ToArray(),
            _plans.Values.ToArray(), _mutations.Where(pair => pair.Value.Pending).Select(pair =>
                new JournalMutation(pair.Key, pair.Value.FileId, pair.Value.OwnerId, pair.Value.ClaimId)).ToArray());
        var path = JournalSnapshotPath;
        try
        {
            // Snapshot is an encrypted hint. Every use still reads the remote tail; GC also
            // requires fresh library references, and catalogue writes reload their remote row.
            var body = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions.Web);
            try
            {
                if (body.Length > 32 * 1024 * 1024) return;
                await Task.Run(() =>
                {
                    var encrypted = ProtectedData.Protect(body, null, DataProtectionScope.CurrentUser);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    var temporary = path + ".tmp";
                    File.WriteAllBytes(temporary, encrypted);
                    File.Move(temporary, path, overwrite: true);
                }, token);
            }
            finally { CryptographicOperations.ZeroMemory(body); }
            _snapshotSaved = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { AppLogger.Error("SharedStorage.JournalSnapshotWrite", error); }
    }

    private sealed record JournalLocator(string Channel, string Message);
    private sealed record JournalMutation(string Operation, string FileId, string OwnerId, ulong ClaimId);
    private sealed record JournalSnapshot(int Version, string Channel, ulong Frontier, StorageThreadRecord[] Catalog,
        Dictionary<string, JournalLocator> CatalogLocators, StorageReferenceRecord[] References, StorageCleanupRecord[] Cleanups,
        string[] Applied, string[] Fences, StorageBatchPlan[] Plans, JournalMutation[] Mutations);
}
