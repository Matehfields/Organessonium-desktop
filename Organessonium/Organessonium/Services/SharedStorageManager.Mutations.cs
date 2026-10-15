using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    private readonly Dictionary<string, (string FileId, string OwnerId, bool Pending, ulong ClaimId)> _mutations = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ActiveMetadataMutations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deletionFences = new(StringComparer.Ordinal);

    internal async Task<IAsyncDisposable?> AcquireMetadataMutationAsync(AppEntry entry, CancellationToken token)
    {
        if (entry.Type != "file" || !Payloads(MetadataService.CreateFileMetadata(entry)).Any(payload => payload.Storage?.Shared == true)) return null;
        var operation = Guid.NewGuid().ToString("N");
        ActiveMetadataMutations[operation] = 0;
        await WriteMutationAsync(operation, entry.FileId, true, token);
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                await RefreshJournalAsync(token);
                if (_deletionFences.Contains(entry.FileId))
                    throw new IOException("Há uma exclusão definitiva pendente para esse arquivo; seus dados foram preservados.");
                if (!_mutations.TryGetValue(operation, out var claim) || !claim.Pending)
                    throw new IOException("A reserva de alteração ainda não foi confirmada no servidor.");
                if (_mutations.Any(pair => pair.Key != operation && pair.Value.FileId == entry.FileId && pair.Value.Pending && pair.Value.ClaimId < claim.ClaimId))
                    throw new IOException("Outro dispositivo está alterando esse arquivo. Atualize a lista antes de tentar novamente.");
            }
            finally { _gate.Release(); }
            return new MetadataMutationLease(this, operation, entry.FileId);
        }
        catch { await CompleteMutationAsync(operation, entry.FileId); throw; }
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
        await WriteMutationAsync(operationId, fileId, false, CancellationToken.None);
        ActiveMetadataMutations.TryRemove(operationId, out _);
    }

    private async Task RecoverOwnedMutationsAsync(CancellationToken token)
    {
        foreach (var (id, mutation) in _mutations.Where(pair => pair.Value.Pending && pair.Value.OwnerId == OwnerId && !ActiveMetadataMutations.ContainsKey(pair.Key)).ToArray())
        {
            await WriteMutationAsync(id, mutation.FileId, false, token);
            _mutations.Remove(id);
        }
    }

    private sealed class MetadataMutationLease(SharedStorageManager manager, string operationId, string fileId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(manager.CompleteMutationAsync(operationId, fileId));
    }
}
