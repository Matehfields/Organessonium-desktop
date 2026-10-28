using System.Security.Cryptography;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private Task<List<DiscordMessageDto>>? _reservationWarmup;
    private string CatalogHintsKey => "storageCatalogHints:" + OwnerId + ":" + _settings.NamesChannelId + ":" + _settings.FoldersForumId + ":" + _settings.AppsForumId;

    // These encrypted local locators are hints, never a quota or ownership decision.
    // Reserve/verify always reload the chosen remote catalogue before using it.
    private bool UseReservationHints()
    {
        if (_initialized || PendingCreations().Count > 0) return false;
        if (_catalog.Count > 0) return true;
        try
        {
            var stored = ((IEncryptionSecretStore)_store).ReadEncryptionSecret(CatalogHintsKey);
            if (stored.Length > 0)
                foreach (var hint in JsonSerializer.Deserialize<List<CatalogHint>>(
                    ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser), JsonOptions.Web) ?? [])
                {
                    if (hint.OwnerId != OwnerId || string.IsNullOrEmpty(hint.CatalogMessageId)) continue;
                    var row = new StorageThreadRecord
                    {
                        OwnerId = hint.OwnerId, StorageId = hint.StorageId, ThreadId = hint.ThreadId,
                        ControlMessageId = hint.ControlMessageId, CatalogMessageId = hint.CatalogMessageId,
                        CatalogChannelId = hint.CatalogChannelId, State = hint.State,
                        Members = hint.MemberIds.Select(id => new StorageMember { FileId = id }).ToList()
                    };
                    ValidateCatalog(row);
                    _catalog[row.StorageId] = row;
                    _api.RegisterSharedThread(row.ThreadId);
                }
        }
        catch (Exception error) { _catalog.Clear(); AppLogger.Error("SharedStorage.CatalogHints", error); }
        // Pending creations were ruled out before accepting even a populated cache.
        return true;
    }

    private void SaveCatalogHints()
    {
        try
        {
            var current = _store.Load();
            if (current.BotToken != _settings.BotToken || current.NamesChannelId != _settings.NamesChannelId ||
                current.FoldersForumId != _settings.FoldersForumId || current.AppsForumId != _settings.AppsForumId) return;
            var hints = _catalog.Values.Where(row => row.OwnerId == OwnerId && row.CatalogMessageId.Length > 0)
                .Select(row => new CatalogHint(row.StorageId, row.OwnerId, row.ThreadId, row.ControlMessageId,
                    row.CatalogMessageId, row.CatalogChannelId, row.State, row.Members.Select(member => member.FileId).ToArray())).ToList();
            var protectedHints = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(hints, JsonOptions.Web),
                null, DataProtectionScope.CurrentUser);
            ((IEncryptionSecretStore)_store).WriteEncryptionSecret(CatalogHintsKey, Convert.ToBase64String(protectedHints));
        }
        catch (Exception error) { AppLogger.Error("SharedStorage.SaveCatalogHints", error); }
    }

    private void WarmReservationJournal(CancellationToken token)
    {
        if (_initialized || _reservationWarmup != null) return;
        _reservationWarmup = Task.Run(() => ReadJournalMessagesAsync(null, token), token);
        _ = _reservationWarmup.ContinueWith(task => _ = task.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task AwaitReservationJournalAsync(CancellationToken token)
    {
        var warmup = _reservationWarmup;
        if (warmup != null) await warmup.WaitAsync(token).ConfigureAwait(false);
    }

    private sealed record CatalogHint(string StorageId, string OwnerId, string ThreadId, string ControlMessageId,
        string CatalogMessageId, string CatalogChannelId, string State, string[] MemberIds);
}
