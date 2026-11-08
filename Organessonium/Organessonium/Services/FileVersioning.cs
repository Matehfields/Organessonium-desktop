using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

/// <summary>Immutable payload snapshots; organization remains on the current file.</summary>
public static class FileVersioning
{
    public static FileReplacement PrepareReplacement(AppEntry current)
    {
        if (current.Type != "file" || current.LogicalChunkCount == 0 || string.IsNullOrWhiteSpace(current.MetadataMessageId))
            throw new InvalidDataException("Os metadados completos do arquivo não estão disponíveis para criar uma versão.");
        return new FileReplacement
        {
            MetadataMessageId = current.MetadataMessageId,
            Path = current.Path,
            Metadata = Clone(MetadataService.CreateFileMetadata(current))
        };
    }

    public static AppEntry ReadBase(FileReplacement replacement, MetadataService metadata)
    {
        var entry = metadata.ToAppEntry(new MetadataEnvelope(replacement.MetadataMessageId, Clone(replacement.Metadata)))
            ?? throw new InvalidDataException("Metadados da versão anterior inválidos.");
        entry.Path = replacement.Path;
        return entry;
    }

    public static void PreservePrevious(AppEntry previous, AppEntry next, string now)
    {
        if (previous.FileId != next.FileId)
            throw new InvalidDataException("A nova versão deve manter o fileId do arquivo.");
        var snapshot = Clone(MetadataService.CreateFileMetadata(previous));
        var history = snapshot.Versions ?? [];
        snapshot.Versions = null;
        snapshot.Trash = null;
        snapshot.Sync = null;
        snapshot.Favorite = false;
        var previousId = string.IsNullOrEmpty(previous.VersionId)
            ? "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(snapshot, JsonOptions.Web)))).ToLowerInvariant()[..24]
            : previous.VersionId;
        snapshot.VersionId = previousId;
        if (!history.Any(version => version.VersionId == previousId))
            history.Add(new FileVersion { VersionId = previousId, ArchivedAt = now, Payload = snapshot });
        next.Versions = history;
        next.MetadataMessageId = previous.MetadataMessageId;
        next.UploadedAt = previous.UploadedAt;
        next.VersionUploadedAt = now;
        next.ModifiedAt = now;
        next.ParentFolderId = previous.ParentFolderId;
        next.Path = previous.Path;
        next.Name = previous.Name;
        next.Favorite = previous.Favorite;
        next.Trash = previous.Trash;
        next.Sync = previous.Sync;
    }

    public static long CurrentStorageBytes(AppEntry entry) => AddBytes(entry.SizeBytes, entry.VideoPreviewSizeBytes);

    public static long StorageBytes(AppEntry entry) => entry.Versions.Aggregate(CurrentStorageBytes(entry),
        (total, version) => AddBytes(total, AddBytes(version.Payload.SizeBytes, version.Payload.Preview?.Video480p?.SizeBytes ?? 0)));

    private static long AddBytes(long size, long extra)
    {
        size = Math.Max(0, size);
        extra = Math.Max(0, extra);
        return size > long.MaxValue - extra ? long.MaxValue : size + extra;
    }

    private static FileMetadata Clone(FileMetadata metadata) =>
        JsonSerializer.Deserialize<FileMetadata>(JsonSerializer.Serialize(metadata, JsonOptions.Web), JsonOptions.Web)
        ?? throw new InvalidDataException("Metadados da versão inválidos.");
}
