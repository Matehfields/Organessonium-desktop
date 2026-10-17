using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace Organessonium.Models;

public sealed class PayloadChecksums
{
    [JsonPropertyName("version")]
    [JsonRequired]
    public int Version { get; set; } = 1;
    [JsonPropertyName("algorithm")]
    [JsonRequired]
    public string Algorithm { get; set; } = "SHA-256";
    [JsonPropertyName("sha256")]
    [JsonRequired]
    public string Sha256 { get; set; } = "";
    [JsonPropertyName("chunkSizeBytes")]
    [JsonRequired]
    public int ChunkSizeBytes { get; set; }
    [JsonPropertyName("chunks")]
    [JsonRequired]
    public List<string> Chunks { get; set; } = [];

    public void Validate(long size, int count)
    {
        if (Version != 1 || Algorithm != "SHA-256")
            throw new InvalidDataException("Formato de checksum ainda não suportado.");
        if (size < 0 || ChunkSizeBytes <= 0 || ChunkSizeBytes > 64 * 1024 * 1024 ||
            Chunks == null || Chunks.Count != count ||
            count != Math.Max(1, (size + ChunkSizeBytes - 1) / ChunkSizeBytes) ||
            !IsHash(Sha256) || Chunks.Any(hash => !IsHash(hash)))
            throw new InvalidDataException("Metadados de checksum incompletos ou inválidos.");
    }

    private static bool IsHash(string? hash) => hash is { Length: 64 } &&
        hash.All(value => value is >= '0' and <= '9' or >= 'a' and <= 'f');

    public void VerifyChunk(int index, ReadOnlySpan<byte> bytes)
    {
        if (index < 0 || index >= Chunks.Count ||
            !Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(Chunks[index], StringComparison.Ordinal))
            throw new PayloadChecksumException("O checksum de uma parte do arquivo não confere. Os dados não foram aceitos.");
    }
}

public sealed class PayloadChecksumException(string message) : IOException(message);
