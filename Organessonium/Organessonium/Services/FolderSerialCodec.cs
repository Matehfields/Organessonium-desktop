using System.Buffers.Binary;
using System.IO;
using System.Text;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class FolderSerialCodec
{
    private static readonly byte[] FolderMagic = "OSER"u8.ToArray();
    public const int CurrentVersion = 4;
    public const int PublishedVersion = 3;
    private const int MaxStringBytes = 16 * 1024 * 1024;
    private const int MaxChildren = 10_000_000;

    public FolderSerial DecodeFolder(ReadOnlySpan<byte> bytes)
    {
        var reader = new SerialReader(bytes);
        reader.Expect(FolderMagic);
        var version = reader.UInt16();
        if (version is < 1 or > CurrentVersion)
        {
            throw new FormatException($"Versão de serial não suportada: {version}.");
        }

        var kindValue = reader.Byte();
        if (kindValue > (byte)FolderSerialKind.Favorites)
        {
            throw new FormatException("Tipo de serial inválido.");
        }

        var generatedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.Int64());
        var folderId = reader.String();
        var parentFolderId = reader.String();
        var folderName = reader.String();
        var aggregate = ReadAggregate(ref reader);
        var childCount = reader.UInt32();
        if (childCount > MaxChildren)
        {
            throw new FormatException("Serial possui itens demais.");
        }

        var children = new List<FolderSerialEntry>((int)childCount);
        for (var index = 0; index < childCount; index++)
        {
            var flags = reader.Byte();
            children.Add(new FolderSerialEntry
            {
                IsFolder = (flags & 1) != 0,
                Favorite = (flags & (1 << 1)) != 0,
                DeviceType = version >= 2
                    ? (flags & (1 << 2)) != 0
                        ? "phone"
                        : (flags & (1 << 3)) != 0 ? "pc" : null
                    : null,
                Id = reader.String(),
                NamesMessageId = reader.String(),
                ParentFolderId = reader.String(),
                Name = reader.String(),
                ThreadId = reader.String(),
                MimeType = reader.String(),
                UploadedAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.Int64()),
                PreviewMessageId = reader.String(),
                Encryption = version >= 3 ? ReadEncryption(ref reader) : null,
                FolderColor = reader.String(),
                SizeBytes = version >= 4 ? reader.Int64() : 0
            });
        }

        reader.ExpectEnd();
        return new FolderSerial
        {
            FolderId = folderId,
            ParentFolderId = parentFolderId,
            FolderName = folderName,
            GeneratedAt = generatedAt,
            Aggregate = aggregate,
            Children = children,
            Kind = (FolderSerialKind)kindValue
        };
    }

    public byte[] EncodeFolder(FolderSerial serial, int schemaVersion = CurrentVersion)
    {
        if (schemaVersion is < 1 or > CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        using var stream = new MemoryStream();
        stream.Write(FolderMagic);
        WriteUInt16(stream, checked((ushort)schemaVersion));
        stream.WriteByte((byte)serial.Kind);
        WriteInt64(stream, serial.GeneratedAt.ToUniversalTime().ToUnixTimeMilliseconds());
        WriteString(stream, serial.FolderId);
        WriteString(stream, serial.ParentFolderId);
        WriteString(stream, serial.FolderName);
        WriteAggregate(stream, serial.Aggregate);
        WriteUInt32(stream, checked((uint)serial.Children.Count));

        foreach (var child in serial.Children)
        {
            var flags = child.IsFolder ? 1 : 0;
            if (child.Favorite) flags |= 1 << 1;
            if (schemaVersion >= 2)
            {
                if (child.DeviceType == "phone") flags |= 1 << 2;
                if (child.DeviceType == "pc") flags |= 1 << 3;
            }
            stream.WriteByte((byte)flags);
            WriteString(stream, child.Id);
            WriteString(stream, child.NamesMessageId);
            WriteString(stream, child.ParentFolderId);
            WriteString(stream, child.Name);
            WriteString(stream, child.ThreadId);
            WriteString(stream, child.MimeType);
            WriteInt64(stream, child.UploadedAt.ToUniversalTime().ToUnixTimeMilliseconds());
            WriteString(stream, child.PreviewMessageId);
            if (schemaVersion >= 3)
            {
                WriteEncryption(stream, child.Encryption);
            }
            WriteString(stream, string.IsNullOrWhiteSpace(child.FolderColor) ? "#C4C7C5" : child.FolderColor);
            if (schemaVersion >= 4)
            {
                WriteInt64(stream, Math.Max(0, child.SizeBytes));
            }
        }

        return stream.ToArray();
    }

    public static bool SameContent(FolderSerial left, FolderSerial right)
    {
        if (left.FolderId != right.FolderId ||
            left.ParentFolderId != right.ParentFolderId ||
            left.FolderName != right.FolderName ||
            left.Kind != right.Kind ||
            !SameAggregate(left.Aggregate, right.Aggregate) ||
            left.Children.Count != right.Children.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Children.Count; index++)
        {
            var a = left.Children[index];
            var b = right.Children[index];
            if (a.Id != b.Id || a.NamesMessageId != b.NamesMessageId ||
                a.ParentFolderId != b.ParentFolderId || a.Name != b.Name ||
                a.ThreadId != b.ThreadId || a.IsFolder != b.IsFolder ||
                a.MimeType != b.MimeType || a.UploadedAt != b.UploadedAt || a.SizeBytes != b.SizeBytes ||
                a.PreviewMessageId != b.PreviewMessageId ||
                !SameEncryption(a.Encryption, b.Encryption) ||
                a.FolderColor != b.FolderColor || a.DeviceType != b.DeviceType || a.Favorite != b.Favorite)
            {
                return false;
            }
        }

        return true;
    }

    private static FolderSerialAggregate ReadAggregate(ref SerialReader reader)
    {
        var typeCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var aggregate = new FolderSerialAggregate
        {
            TotalItems = reader.Int64(),
            FileCount = reader.Int64(),
            FolderCount = reader.Int64(),
            TotalBytes = reader.Int64(),
            ImageCount = reader.Int64(),
            VideoCount = reader.Int64(),
            TypeCounts = typeCounts
        };

        var typeCount = reader.UInt32();
        if (typeCount > MaxChildren)
        {
            throw new FormatException("Serial possui extensões demais.");
        }

        for (var index = 0; index < typeCount; index++)
        {
            typeCounts[reader.String()] = reader.Int64();
        }

        return aggregate;
    }

    private static void WriteAggregate(Stream stream, FolderSerialAggregate aggregate)
    {
        WriteInt64(stream, aggregate.TotalItems);
        WriteInt64(stream, aggregate.FileCount);
        WriteInt64(stream, aggregate.FolderCount);
        WriteInt64(stream, aggregate.TotalBytes);
        WriteInt64(stream, aggregate.ImageCount);
        WriteInt64(stream, aggregate.VideoCount);
        var types = aggregate.TypeCounts.OrderBy(item => item.Key, StringComparer.Ordinal).ToList();
        WriteUInt32(stream, checked((uint)types.Count));
        foreach (var type in types)
        {
            WriteString(stream, type.Key);
            WriteInt64(stream, type.Value);
        }
    }

    private static EncryptionMetadata? ReadEncryption(ref SerialReader reader)
    {
        var version = reader.UInt16();
        if (version == 0)
        {
            return null;
        }

        if (version != EncryptionMetadata.CurrentVersion)
        {
            throw new FormatException("Versão de criptografia do serial não suportada.");
        }

        var mode = reader.Byte() switch
        {
            0 => EncryptionModes.Standard,
            1 => EncryptionModes.Custom,
            _ => throw new FormatException("Modo de criptografia do serial inválido.")
        };
        var encryption = new EncryptionMetadata
        {
            Version = version,
            Mode = mode,
            Salt = reader.String(),
            KeyId = mode == EncryptionModes.Custom ? reader.String() : null
        };
        return encryption;
    }

    private static void WriteEncryption(Stream stream, EncryptionMetadata? encryption)
    {
        if (encryption == null || encryption.Version == 0)
        {
            WriteUInt16(stream, 0);
            return;
        }

        if (encryption.Version != EncryptionMetadata.CurrentVersion ||
            !EncryptionModes.IsSupported(encryption.Mode))
        {
            throw new FormatException("Metadados de criptografia do serial inválidos.");
        }

        WriteUInt16(stream, (ushort)encryption.Version);
        stream.WriteByte((byte)(encryption.Mode.Equals(EncryptionModes.Custom, StringComparison.OrdinalIgnoreCase) ? 1 : 0));
        WriteString(stream, encryption.Salt);
        if (encryption.Mode.Equals(EncryptionModes.Custom, StringComparison.OrdinalIgnoreCase))
        {
            WriteString(stream, encryption.KeyId ?? "");
        }
    }

    private static bool SameAggregate(FolderSerialAggregate left, FolderSerialAggregate right)
    {
        return left.TotalItems == right.TotalItems &&
               left.FileCount == right.FileCount &&
               left.FolderCount == right.FolderCount &&
               left.TotalBytes == right.TotalBytes &&
               left.ImageCount == right.ImageCount &&
               left.VideoCount == right.VideoCount &&
               left.TypeCounts.Count == right.TypeCounts.Count &&
               left.TypeCounts.All(item => right.TypeCounts.TryGetValue(item.Key, out var value) && value == item.Value);
    }

    private static bool SameEncryption(EncryptionMetadata? left, EncryptionMetadata? right)
    {
        return left?.Version == right?.Version &&
               string.Equals(left?.Mode, right?.Mode, StringComparison.Ordinal) &&
               string.Equals(left?.Salt, right?.Salt, StringComparison.Ordinal) &&
               string.Equals(left?.KeyId, right?.KeyId, StringComparison.Ordinal);
    }

    private static void WriteString(Stream stream, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private ref struct SerialReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _offset;

        public SerialReader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _offset = 0;
        }

        public byte Byte()
        {
            Ensure(1);
            return _bytes[_offset++];
        }

        public ushort UInt16()
        {
            Ensure(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes[_offset..]);
            _offset += 2;
            return value;
        }

        public uint UInt32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[_offset..]);
            _offset += 4;
            return value;
        }

        public long Int64()
        {
            Ensure(8);
            var value = BinaryPrimitives.ReadInt64LittleEndian(_bytes[_offset..]);
            _offset += 8;
            return value;
        }

        public string String()
        {
            var length = UInt32();
            if (length > MaxStringBytes)
            {
                throw new FormatException("Texto de serial grande demais.");
            }

            Ensure(checked((int)length));
            var value = Encoding.UTF8.GetString(_bytes.Slice(_offset, (int)length));
            _offset += (int)length;
            return value;
        }

        public void Expect(ReadOnlySpan<byte> expected)
        {
            Ensure(expected.Length);
            if (!_bytes.Slice(_offset, expected.Length).SequenceEqual(expected))
            {
                throw new FormatException("Assinatura de serial inválida.");
            }

            _offset += expected.Length;
        }

        public void ExpectEnd()
        {
            if (_offset != _bytes.Length)
            {
                throw new FormatException("Dados extras no serial.");
            }
        }

        private void Ensure(int count)
        {
            if (count < 0 || count > _bytes.Length - _offset)
            {
                throw new FormatException("Serial truncado.");
            }
        }
    }
}
