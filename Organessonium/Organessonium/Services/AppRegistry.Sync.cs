using System.IO;
using System.Security.Cryptography;
using System.Text;
using Organessonium.Models;
using Organessonium.Services.Sync;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    private DateTime _syncMappingsChecked;
    public async Task<AppEntry> EnsureComputerAsync(string deviceId, string deviceName, CancellationToken token)
    {
        await RefreshMappingsAsync(token);
        if (!_mappings.ContainsKey(SynchronizedRootFolderId))
        {
            // Preserve roots published by the gallery client when introducing a mapped index.
            var devices = await LoadSynchronizedDevicesAsync(token);
            await EnsureRootIndexAsync(token);
            await CreateFolderIndexAsync(SynchronizedRootFolderId, RootFolderId, "synchronized", SynchronizedRootPath, token, false);
            var root = await GetMappingAsync(SynchronizedRootFolderId, token);
            foreach (var device in devices)
                await _metadata.SendEntryAsync(_discord, root.ThreadId, device, token);
            _store.CacheFolderEntries(SynchronizedRootFolderId, devices);
        }
        var id = "pc-" + deviceId;
        var siblings = await RefreshFolderAsync(SynchronizedRootFolderId, SynchronizedRootPath, token);
        var existing = siblings.FirstOrDefault(e => e.FolderId == id);
        if (existing != null)
        {
            if (existing.Name != deviceName)
            {
                existing.Name = deviceName;
                existing.SyncDevice = new() { DeviceId = deviceId, DeviceName = deviceName };
                await UpdateEntryAsync(existing, token);
            }
            return existing;
        }
        var computer = AppEntry.Folder(deviceName, SynchronizedRootPath);
        computer.FolderId = id;
        computer.ParentFolderId = SynchronizedRootFolderId;
        computer.DeviceType = "pc";
        computer.SyncDevice = new() { DeviceId = deviceId, DeviceName = deviceName };
        await AddSyncFolderEntryAsync(computer, token);
        return computer;
    }

    public async Task<AppEntry> EnsureSyncRootAsync(SyncFolderBinding binding, string deviceId, string deviceName, bool link, CancellationToken token)
    {
        var computer = await EnsureComputerAsync(deviceId, deviceName, token);
        var parentPath = PathRules.ChildPath(computer);
        var id = link ? "sync-link-" + deviceId + "-" + binding.SyncFolderId : "sync-" + binding.SyncFolderId;
        var siblings = await RefreshFolderAsync(computer.FolderId, parentPath, token);
        var existing = siblings.FirstOrDefault(e => e.FolderId == id);
        if (existing != null) return existing;
        var folder = AppEntry.Folder(binding.Name, parentPath);
        folder.FolderId = id;
        folder.ParentFolderId = computer.FolderId;
        folder.Sync = new SyncMetadata
        {
            SyncFolderId = binding.SyncFolderId, DeviceId = deviceId, DeviceName = deviceName,
            CanonicalFolderId = link ? binding.CloudFolderId : id
        };
        await AddSyncFolderEntryAsync(folder, token);
        return folder;
    }

    private async Task AddSyncFolderEntryAsync(AppEntry folder, CancellationToken token)
    {
        // Device names are labels, not identities. Do not reuse a same-named PC/folder.
        await _entryMutationGate.WaitAsync(token);
        try
        {
            folder.ThreadId = (await EnsureFolderIndexAsync(folder, token)).ThreadId;
            var parent = await GetMappingAsync(NormalizeFolderId(folder.ParentFolderId), token);
            await _discord.UnarchiveThreadAsync(parent.ThreadId, token);
            await _metadata.SendEntryAsync(_discord, parent.ThreadId, folder, token);
            var entries = _store.LoadCachedEntriesForFolder(NormalizeFolderId(folder.ParentFolderId));
            ReplaceEntry(entries, folder);
            _store.CacheFolderEntries(NormalizeFolderId(folder.ParentFolderId), entries);
            RecordDescriptors(entries);
            await SynchronizeFolderSerialAsync(NormalizeFolderId(folder.ParentFolderId), folder.Path, entries, token);
            await ArchiveFolderThreadAsync(parent.ThreadId, token);
        }
        finally { _entryMutationGate.Release(); }
    }

    public async Task<IReadOnlyList<AppEntry>> DiscoverSyncFoldersAsync(CancellationToken token)
    {
        await RefreshMappingsAsync(token);
        var computers = await LoadSynchronizedDevicesAsync(token, true);
        var result = new List<AppEntry>();
        foreach (var computer in computers.Where(e => e.DeviceType == "pc" && _mappings.ContainsKey(e.FolderId)))
        {
            var folders = await RefreshFolderAsync(computer.FolderId, PathRules.ChildPath(computer), token);
            result.AddRange(folders.Where(e => e.Sync is { Version: 1, SyncFolderId.Length: > 0 }));
        }
        return result;
    }

    public async Task<FolderIndexMapping> SyncMappingAsync(string folderId, CancellationToken token)
    {
        if (!_mappings.ContainsKey(folderId) || DateTime.UtcNow - _syncMappingsChecked > TimeSpan.FromMinutes(5))
        {
            await RefreshMappingsAsync(token);
            _syncMappingsChecked = DateTime.UtcNow;
        }
        return await GetMappingAsync(folderId, token);
    }

    public async Task<string> ProjectSyncRevisionAsync(SyncFolderBinding binding, SyncRevision revision, CancellationToken token)
    {
        var relative = SyncPaths.Relative(revision.RelativePath);
        var parts = relative.Split('/');
        var parentId = binding.CloudFolderId;
        var parentPath = binding.CloudPath;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var directoryPath = string.Join('/', parts.Take(i + 1));
            var id = SyncDirectoryId(binding.SyncFolderId, directoryPath);
            var siblings = await RefreshFolderAsync(parentId, parentPath, token);
            var directory = siblings.FirstOrDefault(e => e.FolderId == id);
            if (directory == null)
            {
                directory = AppEntry.Folder(parts[i], parentPath);
                directory.FolderId = id;
                directory.ParentFolderId = parentId;
                directory.Sync = new SyncMetadata { SyncFolderId = binding.SyncFolderId, RelativePath = directoryPath };
                await AddSyncFolderEntryAsync(directory, token);
            }
            parentId = id;
            parentPath = PathRules.ChildPath(directory);
        }
        var entries = await RefreshFolderAsync(parentId, parentPath, token);
        var existing = entries.FirstOrDefault(e => revision.Kind == "directory" ?
            e.FolderId == revision.FileId : e.FileId == revision.FileId);
        if (revision.IsDeleted)
        {
            if (existing != null)
            {
                if (existing.Type == "folder")
                {
                    var children = await RefreshFolderAsync(existing.FolderId, PathRules.ChildPath(existing), token);
                    if (children.Count != 0) throw new IOException("A pasta remota ainda contém itens. A exclusão aguardará a reconciliação dos filhos.");
                }
                await DeleteEntryAsync(existing, token);
            }
            return parentId;
        }
        AppEntry entry;
        if (revision.Kind == "directory")
        {
            entry = existing ?? AppEntry.Folder(parts[^1], parentPath);
            entry.FolderId = revision.FileId;
        }
        else
        {
            if (revision.Payload == null) throw new InvalidDataException("Revisão sem metadata de payload.");
            entry = _metadata.ToAppEntry(new MetadataEnvelope(existing?.MetadataMessageId ?? "", revision.Payload))
                ?? throw new InvalidDataException("Metadata da revisão inválido.");
            entry.Favorite = existing?.Favorite ?? entry.Favorite;
        }
        entry.Name = parts[^1];
        entry.Path = parentPath;
        entry.ParentFolderId = parentId;
        entry.Sync = new SyncMetadata
        {
            SyncFolderId = binding.SyncFolderId, RelativePath = relative, Hash = revision.Hash,
            ModifiedUtc = revision.ModifiedUtc, Revision = revision.Revision, RevisionId = revision.RevisionId,
            DeviceId = revision.DeviceId, DeviceName = revision.DeviceName
        };
        if (existing == null)
        {
            if (entry.Type == "folder") await AddSyncFolderEntryAsync(entry, token);
            else await AddEntryAsync(entry, token);
        }
        else if (existing.Sync?.RevisionId != revision.RevisionId)
            await UpdateEntryAsync(entry, token);
        return parentId;
    }

    public async Task RemoveSyncProjectionAsync(SyncFolderBinding binding, SyncRevision previous, CancellationToken token)
    {
        var parts = SyncPaths.Relative(previous.RelativePath).Split('/');
        var parentId = parts.Length == 1 ? binding.CloudFolderId :
            SyncDirectoryId(binding.SyncFolderId, string.Join('/', parts.Take(parts.Length - 1)));
        if (!_mappings.ContainsKey(parentId)) return;
        var path = parts.Length == 1 ? binding.CloudPath : PathRules.Normalize(binding.CloudPath + "/" + string.Join('/', parts.Take(parts.Length - 1)));
        var siblings = await RefreshFolderAsync(parentId, path, token);
        var entry = siblings.FirstOrDefault(e => e.FileId == previous.FileId || e.FolderId == previous.FileId);
        if (entry != null) await DeleteEntryAsync(entry, token);
    }

    public static string SyncDirectoryId(string syncFolderId, string relative) => "sd-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(syncFolderId + "/" + relative.ToUpperInvariant())))[..32].ToLowerInvariant();
}
