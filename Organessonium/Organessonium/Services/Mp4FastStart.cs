using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Organessonium.Services;

/// <summary>Conservative ISO-BMFF relocation. Sample bytes and codec descriptors are copied verbatim.</summary>
public static class Mp4FastStart
{
    private const int MaximumMoovBytes = 32 * 1024 * 1024;
    private static readonly HashSet<string> TopLevelTypes = ["ftyp", "free", "skip", "wide", "mdat", "moov"];
    private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl", "edts", "dinf", "udta", "meta"];

    public static async Task<PreparedVideoUpload> PrepareAsync(FileInfo original, CancellationToken cancellationToken = default)
    {
        if (!original.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) return new(original, null, false);
        DirectoryInfo? temporary = null;
        try
        {
            await using var input = new FileStream(original.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            var boxes = await ReadTopLevelAsync(input, cancellationToken).ConfigureAwait(false);
            var moovs = boxes.Where(b => b.Type == "moov").ToArray();
            var mdats = boxes.Where(b => b.Type == "mdat").ToArray();
            if (moovs.Length != 1 || mdats.Length == 0 || boxes.Count == 0 || boxes[0].Type != "ftyp")
                throw new InvalidDataException("Estrutura MP4 não suportada para faststart.");
            var moov = moovs[0];
            if (moov.Offset < mdats[0].Offset) return new(original, null, false);
            if (boxes.Any(b => !TopLevelTypes.Contains(b.Type)) || moov.Size > MaximumMoovBytes || moov.ExtendsToEnd)
                throw new InvalidDataException("MP4 fragmentado ou estrutura não suportada para faststart.");
            input.Position = moov.Offset;
            var metadata = new byte[checked((int)moov.Size)];
            await input.ReadExactlyAsync(metadata, cancellationToken).ConfigureAwait(false);
            var ordered = boxes.Where(b => b != moov).ToList();
            ordered.Insert(ordered.FindIndex(b => b.Type == "mdat"), moov);
            var relocated = new Dictionary<long, long>();
            long position = 0;
            foreach (var box in ordered) { relocated[box.Offset] = position; position = checked(position + box.Size); }
            var offsetTables = PatchContainer(metadata, moov.Header, metadata.Length, mdats, relocated, 0);
            if (offsetTables == 0) throw new InvalidDataException("MP4 sem tabela de offsets de amostras.");

            temporary = Directory.CreateTempSubdirectory("organessonium-faststart-");
            var outputFile = new FileInfo(Path.Combine(temporary.FullName, original.Name));
            await using (var output = new FileStream(outputFile.FullName, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                var buffer = new byte[128 * 1024];
                foreach (var box in ordered)
                {
                    if (box == moov) { await output.WriteAsync(metadata, cancellationToken).ConfigureAwait(false); continue; }
                    input.Position = box.Offset;
                    var remaining = box.Size;
                    while (remaining > 0)
                    {
                        var take = (int)Math.Min(remaining, buffer.Length);
                        await input.ReadExactlyAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
                        await output.WriteAsync(buffer.AsMemory(0, take), cancellationToken).ConfigureAwait(false);
                        remaining -= take;
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (output.Length != input.Length) throw new InvalidDataException("Tamanho inesperado após faststart.");
            }
            return new PreparedVideoUpload(outputFile, temporary, true);
        }
        catch (OperationCanceledException) { Cleanup(temporary); throw; }
        catch (Exception ex) when (ex is IOException or OverflowException or ArgumentException or UnauthorizedAccessException)
        {
            Cleanup(temporary);
            // Do not persist a local source path or private media name in diagnostics.
            AppLogger.Error("Video.FastStart", new IOException("Não foi possível preparar o container MP4; upload original preservado.",
                new InvalidDataException(ex is InvalidDataException ? ex.Message : ex.GetType().Name)));
            return new PreparedVideoUpload(original, null, false);
        }
    }

    private static async Task<List<Box>> ReadTopLevelAsync(Stream file, CancellationToken token)
    {
        var boxes = new List<Box>();
        var header = new byte[16];
        long offset = 0;
        while (offset < file.Length)
        {
            if (file.Length - offset < 8) throw new InvalidDataException("Header MP4 truncado.");
            file.Position = offset;
            await file.ReadExactlyAsync(header.AsMemory(0, 8), token).ConfigureAwait(false);
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = size32 == 1 ? 16 : 8;
            if (size32 == 1) await file.ReadExactlyAsync(header.AsMemory(8, 8), token).ConfigureAwait(false);
            var size = size32 == 0 ? file.Length - offset : size32 == 1
                ? checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8))) : size32;
            if (size < headerSize || size > file.Length - offset) throw new InvalidDataException("Tamanho inválido de box MP4.");
            boxes.Add(new Box(type, offset, size, headerSize, size32 == 0));
            offset = checked(offset + size);
            if (boxes.Count > 100_000) throw new InvalidDataException("Quantidade inválida de boxes MP4.");
        }
        return boxes;
    }

    private static int PatchContainer(byte[] bytes, int start, int end, IReadOnlyList<Box> mdats,
        IReadOnlyDictionary<long, long> relocated, int depth)
    {
        if (depth > 32) throw new InvalidDataException("Estrutura MP4 excessivamente aninhada.");
        var tables = 0;
        for (var position = start; position < end;)
        {
            if (end - position < 8) throw new InvalidDataException("Box MP4 interno truncado.");
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(position, 4));
            var type = Encoding.ASCII.GetString(bytes, position + 4, 4);
            var header = size32 == 1 ? 16 : 8;
            if (end - position < header) throw new InvalidDataException("Header MP4 estendido truncado.");
            var size = size32 == 0 ? end - position : size32 == 1
                ? checked((int)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(position + 8, 8))) : checked((int)size32);
            if (size < header || size > end - position) throw new InvalidDataException("Box MP4 interno inválido.");
            var payload = position + header;
            var boxEnd = position + size;
            if (type is "cmov" or "mvex" or "saio" or "iloc" or "tfhd" or "trun")
                throw new InvalidDataException("MP4 comprimido, fragmentado ou com offsets auxiliares não suportados.");
            if (type is "stco" or "co64")
            {
                var entrySize = type == "stco" ? 4 : 8;
                if (boxEnd - payload < 8 || bytes[payload] != 0)
                    throw new InvalidDataException("Tabela de offsets MP4 inválida.");
                var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(payload + 4, 4));
                if ((long)count * entrySize != boxEnd - payload - 8) throw new InvalidDataException("Tabela MP4 truncada.");
                for (var index = 0; index < count; index++)
                {
                    var entry = checked(payload + 8 + index * entrySize);
                    var old = entrySize == 4 ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry, 4))
                        : BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(entry, 8));
                    var mdat = mdats.FirstOrDefault(b => old >= (ulong)(b.Offset + b.Header) && old < (ulong)(b.Offset + b.Size))
                        ?? throw new InvalidDataException("Offset MP4 fora dos dados de mídia.");
                    var updated = checked((ulong)(checked((long)old) + relocated[mdat.Offset] - mdat.Offset));
                    if (entrySize == 4) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry, 4), checked((uint)updated));
                    else BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(entry, 8), updated);
                }
                tables++;
            }
            else if (Containers.Contains(type))
                tables += PatchContainer(bytes, payload + (type == "meta" ? 4 : 0), boxEnd, mdats, relocated, depth + 1);
            position = boxEnd;
        }
        return tables;
    }
    private sealed record Box(string Type, long Offset, long Size, int Header, bool ExtendsToEnd);
    internal static void Cleanup(DirectoryInfo? temporary)
    {
        if (temporary != null) try { temporary.Delete(true); } catch { }
    }
}

public sealed class PreparedVideoUpload(FileInfo file, DirectoryInfo? temporary, bool applied) : IDisposable
{
    public FileInfo File { get; } = file;
    public bool Applied { get; } = applied;
    public void Dispose() => Mp4FastStart.Cleanup(temporary);
}
