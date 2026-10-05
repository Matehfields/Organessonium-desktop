using Organessonium.Models;

namespace Organessonium.Services.Sync;

public enum SyncAction { None, Accept, Upload, Download, Conflict, RemoteUpdate, LocalDeletion, RemoteDeletion, Unavailable }

public static class SyncPlanner
{
    public static SyncAction Decide(SyncLocalFile? local, SyncFileState? baseline,
        IReadOnlyList<SyncRevision> remote, SyncFolderBinding folder)
    {
        if (remote.Count > 1) return SyncAction.Conflict;
        var head = remote.FirstOrDefault();
        var known = baseline is { BaseRevisionId.Length: > 0 };
        if (head == null)
        {
            if (known) return SyncAction.Unavailable; // Absence is not a tombstone.
            return local == null ? SyncAction.None : SyncAction.Upload;
        }
        if (!known)
        {
            if (head.IsDeleted) return local == null ? SyncAction.Accept : SyncAction.Conflict;
            if (local == null) return folder.Bidirectional ? SyncAction.Download : SyncAction.None;
            return local.Hash == head.Hash && local.Kind == head.Kind ? SyncAction.Accept : SyncAction.Conflict;
        }
        var localChanged = baseline!.BaseDeleted ? local != null : local == null || local.Hash != baseline.BaseHash || local.Kind != baseline.Kind;
        var remoteChanged = head.RevisionId != baseline.BaseRevisionId && head.RevisionId != baseline.IgnoredRevisionId;
        if (!localChanged && !remoteChanged) return SyncAction.None;
        if (localChanged && remoteChanged) return SyncAction.Conflict;
        if (localChanged)
        {
            if (local != null) return SyncAction.Upload;
            return folder.DeletionPolicy == "propagate" ? SyncAction.LocalDeletion :
                folder.DeletionPolicy == "ignore" ? SyncAction.None : SyncAction.Conflict;
        }
        if (!folder.Bidirectional) return SyncAction.None;
        if (head.IsDeleted) return folder.DeletionPolicy == "propagate" ? SyncAction.RemoteDeletion :
            folder.DeletionPolicy == "ignore" ? SyncAction.None : SyncAction.RemoteUpdate;
        return folder.AutomaticRemoteUpdates ? SyncAction.Download : SyncAction.RemoteUpdate;
    }
}
