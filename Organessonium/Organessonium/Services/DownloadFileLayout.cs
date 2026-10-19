using System.IO;
using System.Collections.Immutable;
using Organessonium.Models;

namespace Organessonium.Services;

internal sealed record DownloadRegion(int Index, long Offset, long Length);

internal sealed class DownloadFileLayout
{
    private ImmutableList<DownloadRegion> _regions = ImmutableList<DownloadRegion>.Empty;
    private long _length;
    internal IReadOnlyList<DownloadRegion> Regions => Volatile.Read(ref _regions);
    internal long Length => Interlocked.Read(ref _length);

    internal DownloadFileLayout(IEnumerable<(int Index, long WireLength)> parts, bool encrypted, PayloadChecksums? checksums)
    {
        Append(parts, encrypted);
        Validate(checksums);
    }

    // One discovery producer publishes immutable regions while payload workers read earlier ones.
    internal void Append(IEnumerable<(int Index, long WireLength)> parts, bool encrypted,
        PayloadChecksums? checksums = null, long expectedLength = -1)
    {
        var regions = _regions;
        var offset = Length;
        foreach (var part in parts.OrderBy(part => part.Index))
        {
            var length = checked(part.WireLength - (encrypted ? EncryptionService.EnvelopeOverheadBytes : 0));
            if (part.Index != regions.Count || length < 0)
                throw new InvalidDataException("A ordem ou o tamanho dos chunks do download é inválido.");
            if (checksums != null && length != Math.Min(checksums.ChunkSizeBytes,
                    checked(expectedLength - (long)part.Index * checksums.ChunkSizeBytes)))
                throw new InvalidDataException("Os tamanhos dos chunks divergem do manifesto de checksums.");
            regions = regions.Add(new(part.Index, offset, length));
            offset = checked(offset + length);
        }
        Interlocked.Exchange(ref _length, offset);
        Volatile.Write(ref _regions, regions);
    }

    internal void Validate(PayloadChecksums? checksums)
    {
        if (_regions.Count == 0) throw new InvalidDataException("O download não contém chunks.");
        checksums?.Validate(Length, _regions.Count);
        if (checksums != null && _regions.Any(region => region.Length != Math.Min(checksums.ChunkSizeBytes,
                Length - checked((long)region.Index * checksums.ChunkSizeBytes))))
            throw new InvalidDataException("Os tamanhos dos chunks divergem do manifesto de checksums.");
    }
}
