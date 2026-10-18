using System.IO;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed record DownloadRegion(int Index, long Offset, long Length);

internal sealed class DownloadFileLayout
{
    internal IReadOnlyList<DownloadRegion> Regions { get; }
    internal long Length { get; }

    internal DownloadFileLayout(IEnumerable<(int Index, long WireLength)> parts, bool encrypted, PayloadChecksums? checksums)
    {
        var regions = new List<DownloadRegion>();
        long offset = 0;
        foreach (var part in parts.OrderBy(part => part.Index))
        {
            var length = checked(part.WireLength - (encrypted ? EncryptionService.EnvelopeOverheadBytes : 0));
            if (part.Index != regions.Count || length < 0)
                throw new InvalidDataException("A ordem ou o tamanho dos chunks do download é inválido.");
            regions.Add(new(part.Index, offset, length));
            offset = checked(offset + length);
        }
        if (regions.Count == 0) throw new InvalidDataException("O download não contém chunks.");
        checksums?.Validate(offset, regions.Count);
        if (checksums != null && regions.Any(region => region.Length != Math.Min(checksums.ChunkSizeBytes,
                offset - checked((long)region.Index * checksums.ChunkSizeBytes))))
            throw new InvalidDataException("Os tamanhos dos chunks divergem do manifesto de checksums.");
        Regions = regions;
        Length = offset;
    }
}
