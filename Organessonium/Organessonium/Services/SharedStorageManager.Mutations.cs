using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private readonly Dictionary<string, (string FileId, string OwnerId, bool Pending, ulong ClaimId)> _mutations = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveMetadataMutations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletionFences = new(StringComparer.Ordinal);
    private const string MutationConflictMessage = "Há uma reserva remota de edição para este registro no Discord. Isso não é um bloqueio de leitura do arquivo local. Use Manter ambos para enviar um item independente, ou finalize a edição pendente.";
    internal sealed class MetadataMutationConflictException() : IOException(MutationConflictMessage);

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

    internal async Task<MetadataMutationLease?> AcquireMetadataMutationAsync(AppEntry entry, CancellationToken token,
        string? pendingOperation = null)
    {
        if (entry.Type != "file" || !Payloads(MetadataService.CreateFileMetadata(entry)).Any(payload => payload.Storage?.Shared == true)) return null;
        await AwaitReservationJournalAsync(token);
        var operation = pendingOperation ?? Guid.NewGuid().ToString("N");
        var journalWrite = await _api.AcquireThreadWriteAsync(await GetJournalThreadAsync(token), token);
        try
        {
            ActiveMetadataMutations[operation] = 0;
            if (pendingOperation == null) await WriteMutationAsync(operation, entry.FileId, true, token);
            await _gate.WaitAsync(token);
            try
            {
                await RefreshJournalAsync(token);
                await RecoverOwnedMutationsAsync(token, entry.FileId);
                if (_deletionFences.Contains(entry.FileId))
                    throw new IOException("Há uma exclusão definitiva pendente para esse arquivo; seus dados foram preservados.");
                if (pendingOperation != null && !_mutations.ContainsKey(operation))
                {
                    if (_mutations.Any(pair => pair.Value.FileId == entry.FileId && pair.Value.Pending))
                        throw new MetadataMutationConflictException();
                    await WriteMutationAsync(operation, entry.FileId, true, token);
                    await RefreshJournalAsync(token);
                }
                if (!_mutations.TryGetValue(operation, out var claim) || !claim.Pending || claim.FileId != entry.FileId)
                    throw new IOException("A reserva de alteração ainda não foi confirmada no servidor.");
                if (_mutations.Any(pair => pair.Key != operation && pair.Value.FileId == entry.FileId && pair.Value.Pending && pair.Value.ClaimId < claim.ClaimId))
                    throw new MetadataMutationConflictException();
            }
            finally { _gate.Release(); }
            return new MetadataMutationLease(this, operation, entry.FileId, journalWrite, pendingOperation != null);
        }
        catch
        {
            try
            {
                if (pendingOperation == null) await CompleteMutationAsync(operation, entry.FileId);
                else ActiveMetadataMutations.TryRemove(operation, out _);
            }
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

    internal async Task CancelPendingLibraryClaimsAsync(IEnumerable<(string OperationId, string FileId)> claims, CancellationToken token)
    {
        var requested = claims.ToList();
        if (requested.Count == 0) return;
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            foreach (var (operationId, fileId) in requested)
                if (_mutations.TryGetValue(operationId, out var claim) && claim.Pending && claim.FileId == fileId)
                    await WriteMutationAsync(operationId, fileId, false, token);
        }
        finally { _gate.Release(); }
    }

    private async Task RecoverOwnedMutationsAsync(CancellationToken token, string? fileId = null)
    {
        var abandoned = _mutations.Where(pair => pair.Value.Pending && pair.Value.OwnerId == OwnerId &&
            !pair.Key.StartsWith("library-", StringComparison.Ordinal) &&
            (fileId == null || pair.Value.FileId == fileId) && !ActiveMetadataMutations.ContainsKey(pair.Key)).ToArray();
        if (abandoned.Length == 0) return;
        await using var journalWrite = await _api.AcquireThreadWriteAsync(await GetJournalThreadAsync(token), token);
        foreach (var (id, mutation) in abandoned)
        {
            await WriteMutationAsync(id, mutation.FileId, false, token);
            _mutations.Remove(id);
        }
    }

    internal sealed class MetadataMutationLease(SharedStorageManager manager, string operationId, string fileId,
        IAsyncDisposable journalWrite, bool durable) : IAsyncDisposable
    {
        private bool _applied;
        internal void MarkApplied() => _applied = true;

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!durable || _applied) await manager.CompleteMutationAsync(operationId, fileId);
                else ActiveMetadataMutations.TryRemove(operationId, out _);
            }
            finally { await journalWrite.DisposeAsync(); }
        }
    }
}
