using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private readonly Dictionary<string, (string FileId, string OwnerId, bool Pending, ulong ClaimId)> _mutations = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveMetadataMutations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletionFences = new(StringComparer.Ordinal);
    private const string MutationConflictMessage = "Há uma reserva remota de edição para este registro no Discord. Isso não é um bloqueio de leitura do arquivo local. Use Manter ambos para enviar um item independente, ou finalize a edição pendente.";

    internal async Task EnsureMetadataAvailableAsync(string fileId, CancellationToken token)
    {
        await AwaitReservationJournalAsync(token);
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            await RecoverOwnedMutationsAsync(token, fileId);
            if (_deletionFences.Contains(fileId))
                throw new IOException("Há uma exclusão definitiva pendente para esse arquivo; seus dados foram preservados.");
            if (_mutations.Values.Any(claim => claim.Pending && claim.FileId == fileId))
                throw new IOException(MutationConflictMessage);
        }
        finally { _gate.Release(); }
    }

    internal async Task<IAsyncDisposable?> AcquireMetadataMutationAsync(AppEntry entry, CancellationToken token)
    {
        if (entry.Type != "file" || !Payloads(MetadataService.CreateFileMetadata(entry)).Any(payload => payload.Storage?.Shared == true)) return null;
        await AwaitReservationJournalAsync(token);
        var operation = Guid.NewGuid().ToString("N");
        var journalWrite = await _api.AcquireThreadWriteAsync(await GetJournalThreadAsync(token), token);
        try
        {
            ActiveMetadataMutations[operation] = 0;
            await WriteMutationAsync(operation, entry.FileId, true, token);
            await _gate.WaitAsync(token);
            try
            {
                await RefreshJournalAsync(token);
                await RecoverOwnedMutationsAsync(token, entry.FileId);
                if (_deletionFences.Contains(entry.FileId))
                    throw new IOException("Há uma exclusão definitiva pendente para esse arquivo; seus dados foram preservados.");
                if (!_mutations.TryGetValue(operation, out var claim) || !claim.Pending)
                    throw new IOException("A reserva de alteração ainda não foi confirmada no servidor.");
                if (_mutations.Any(pair => pair.Key != operation && pair.Value.FileId == entry.FileId && pair.Value.Pending && pair.Value.ClaimId < claim.ClaimId))
                    throw new IOException(MutationConflictMessage);
            }
            finally { _gate.Release(); }
            return new MetadataMutationLease(this, operation, entry.FileId, journalWrite);
        }
        catch
        {
            try { await CompleteMutationAsync(operation, entry.FileId); }
            catch (Exception error) { AppLogger.Error("SharedStorage.ReleaseMutation", error); }
            finally { await journalWrite.DisposeAsync(); }
            throw;
        }
    }

    private Task WriteMutationAsync(string operationId, string fileId, bool pending, CancellationToken token) =>
        WriteRecordAsync(new
        {
            schemaVersion = 1,
            entryType = "storageMutation",
            operationId,
            fileId,
            ownerId = OwnerId,
            state = pending ? "pending" : "completed"
        }, null, token);

    private async Task CompleteMutationAsync(string operationId, string fileId)
    {
        try { await WriteMutationAsync(operationId, fileId, false, CancellationToken.None); }
        finally { ActiveMetadataMutations.TryRemove(operationId, out _); }
    }

    private async Task RecoverOwnedMutationsAsync(CancellationToken token, string? fileId = null)
    {
        var abandoned = _mutations.Where(pair => pair.Value.Pending && pair.Value.OwnerId == OwnerId &&
            (fileId == null || pair.Value.FileId == fileId) && !ActiveMetadataMutations.ContainsKey(pair.Key)).ToArray();
        if (abandoned.Length == 0) return;
        await using var journalWrite = await _api.AcquireThreadWriteAsync(await GetJournalThreadAsync(token), token);
        foreach (var (id, mutation) in abandoned)
        {
            await WriteMutationAsync(id, mutation.FileId, false, token);
            _mutations.Remove(id);
        }
    }

    private sealed class MetadataMutationLease(SharedStorageManager manager, string operationId, string fileId,
        IAsyncDisposable journalWrite) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await manager.CompleteMutationAsync(operationId, fileId); }
            finally { await journalWrite.DisposeAsync(); }
        }
    }
}
