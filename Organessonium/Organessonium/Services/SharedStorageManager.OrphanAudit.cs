using Organessonium.Models;

namespace Organessonium.Services;

internal sealed partial class SharedStorageManager
{
    internal bool IsForeignPool(string thread) => _catalog.Values.Any(row => row.ThreadId == thread && row.OwnerId != OwnerId);

    internal async Task AuditOrphansAsync(HashSet<string> payloadThreads, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await RefreshJournalAsync(token);
            var live = await ReadRemoteReferencesAsync(token);
            var files = live.Select(payload => payload.FileId).ToHashSet(StringComparer.Ordinal);
            var referencedThreads = live.Select(payload => payload.ThreadId).ToHashSet(StringComparer.Ordinal);
            var active = await new TransferStateRepository(_store.TransfersDirectory).LoadRecoverableAsync(token);
            var activeFiles = active.Select(state => state.FileId).ToHashSet(StringComparer.Ordinal);
            var retainedThreads = active.Select(state => state.ThreadId).Concat(_catalog.Values.Select(row => row.ThreadId)).ToHashSet(StringComparer.Ordinal);
            var protectedFiles = _references.Values.Where(reference => reference.State == "live" && !reference.Scope.StartsWith("file:"))
                .Select(reference => reference.FileId).ToHashSet(StringComparer.Ordinal);
            var orphanMembers = _catalog.Values.Where(row => row.OwnerId == OwnerId).SelectMany(row => row.Members)
                .Count(member => !files.Contains(member.FileId) && !activeFiles.Contains(member.FileId) && !protectedFiles.Contains(member.FileId) &&
                    !_mutations.Values.Any(mutation => mutation.Pending && mutation.FileId == member.FileId));
            // Absence from a serial/cache never authorizes deletion. Keep candidates and
            // uncertain metadata intact; only the established durable cleanup path deletes payloads.
            Diagnostics.DevelopmentDiagnostics.Current?.Record("janitor.orphans", "maintenance", new
            {
                storageMembers = orphanMembers,
                payloadThreads = payloadThreads.Count(thread => !referencedThreads.Contains(thread) && !retainedThreads.Contains(thread)),
                metadataWithoutPayload = live.Where(payload => !payloadThreads.Contains(payload.ThreadId)).Select(payload => payload.FileId).Distinct().Count()
            });
        }
        finally { _gate.Release(); }
    }
}
