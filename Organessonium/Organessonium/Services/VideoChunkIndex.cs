using System.IO;

namespace Organessonium.Services;

public sealed record VideoChunk(int Index, long Start, int Length);

/// <summary>Offsets refer to authenticated plaintext, not the size limit of new uploads.</summary>
public sealed class VideoChunkIndex
{
    public VideoChunkIndex(IEnumerable<long> lengths, long expectedLength = 0)
    {
        var chunks = new List<VideoChunk>();
        long start = 0;
        foreach (var length in lengths)
        {
            if (length <= 0 || length > int.MaxValue)
                throw new InvalidDataException("Tamanho inválido de chunk de vídeo.");
            chunks.Add(new VideoChunk(chunks.Count, start, (int)length));
            start = checked(start + length);
        }
        if (chunks.Count == 0 || expectedLength > 0 && start != expectedLength)
            throw new InvalidDataException("Os tamanhos dos chunks não correspondem aos metadados do vídeo.");
        Chunks = chunks.AsReadOnly();
        Length = start;
    }

    public IReadOnlyList<VideoChunk> Chunks { get; }
    public long Length { get; }

    public VideoChunk Find(long offset)
    {
        if (offset < 0 || offset >= Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var low = 0;
        var high = Chunks.Count - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (Chunks[mid].Start <= offset) low = mid;
            else high = mid - 1;
        }
        return Chunks[low];
    }
}

public interface IVideoChunkProvider : IDisposable
{
    VideoChunkIndex Index { get; }
    Task<byte[]> DownloadAsync(int chunkIndex, CancellationToken cancellationToken);
}

public interface IVirtualFile
{
    long Length { get; }
    ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default);
}
