using System.Collections.Concurrent;
using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class StorageReadContext
{
    internal SharedStorageManager Manager { get; }
    internal StorageReadContext(SharedStorageManager manager) => Manager = manager;
}

internal sealed partial class SharedStorageManager
{
    private static readonly ConcurrentDictionary<string, byte> ActiveReads = new(StringComparer.Ordinal);

    internal async Task<StorageReadLease> BeginReadAsync(string scope, string fileId, List<StoragePayloadVersion> payloads, CancellationToken token)
    {
        await AwaitReservationJournalAsync(token);
        ActiveReads[scope] = 0;
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            var continuing = _references.TryGetValue(scope, out var previous) && previous.State == "live" && previous.OwnerId == OwnerId && previous.FileId == fileId &&
                previous.Payloads.Count == payloads.Count && previous.Payloads.SelectMany(payload => payload.Attachments)
                    .ToHashSet().SetEquals(payloads.SelectMany(payload => payload.Attachments));
            await PublishReferenceAsync(scope, fileId, payloads, "", "", token);
            await RefreshJournalAsync(token);
            if (!continuing && (_deletionFences.Contains(fileId) || _cleanups.Values.Any(cleanup => !_appliedCleanups.Contains(cleanup.OperationId) &&
                cleanup.Payloads.Any(candidate => payloads.Any(payload => payload.ThreadId == candidate.ThreadId &&
                    (payload.VersionId == candidate.VersionId || payload.Attachments.Intersect(candidate.Attachments).Any()))))))
            {
                await ReleaseReferenceAsync(scope, fileId, CancellationToken.None);
                throw new IOException("Esse conteúdo está sendo excluído. Atualize a lista antes de tentar novamente.");
            }
            return new StorageReadLease(this, scope, fileId);
        }
        catch { ActiveReads.TryRemove(scope, out _); throw; }
        finally { _gate.Release(); }
    }

    private async Task RecoverOwnedReadsAsync(CancellationToken token)
    {
        var transfers = (await new TransferStateRepository(_store.TransfersDirectory).LoadActiveAsync(token))
            .Where(state => TransferStatuses.IsRecoverable(state.Status))
            .Select(state => (state.TransferType == TransferTypes.Download ? "download:" : "upload:") + state.TransferId).ToHashSet(StringComparer.Ordinal);
        foreach (var reference in _references.Values.Where(reference => reference.OwnerId == OwnerId && reference.State == "live" &&
            (reference.Scope.StartsWith("preview:", StringComparison.Ordinal) || reference.Scope.StartsWith("download:", StringComparison.Ordinal) ||
                reference.Scope.StartsWith("upload:", StringComparison.Ordinal)) &&
            !ActiveReads.ContainsKey(reference.Scope) && !transfers.Contains(reference.Scope)).ToArray())
        {
            await ReleaseReferenceAsync(reference.Scope, reference.FileId, token);
            reference.State = "released";
        }
    }

    internal sealed class StorageReadLease(SharedStorageManager manager, string scope, string fileId) : IAsyncDisposable
    {
        private int _disposed;
        internal void LeaveRecoverable() { ActiveReads.TryRemove(scope, out _); }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            await manager.ReleaseReferenceAsync(scope, fileId, CancellationToken.None);
            ActiveReads.TryRemove(scope, out _);
            manager.ResumeDeferredMaintenance();
        }
    }
}
