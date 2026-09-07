using System.IO;

namespace Organessonium.Services;

public static class ArchiveSplitter
{
    private const int SplitBytes = 10 * 1024 * 1024;

    public static async Task<IReadOnlyList<FileInfo>> SplitAsync(FileInfo file, CancellationToken cancellationToken = default)
    {
        if (file.Length <= SplitBytes)
        {
            return [file];
        }

        var tempDir = Directory.CreateTempSubdirectory("discord_split_");
        var parts = new List<FileInfo>();
        await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, SplitBytes, true);
        var buffer = new byte[SplitBytes];
        var partNumber = 1;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            var partPath = System.IO.Path.Combine(tempDir.FullName, $"{file.Name}.{partNumber:000}");
            await using var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, read, true);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            parts.Add(new FileInfo(partPath));
            partNumber++;
        }

        return parts;
    }

    public static void CleanupParts(IReadOnlyList<FileInfo> parts, FileInfo original)
    {
        if (parts.Count == 0 || (parts.Count == 1 && SamePath(parts[0], original)))
        {
            return;
        }

        var dir = parts[0].Directory;
        foreach (var part in parts)
        {
            try { part.Delete(); } catch { }
        }

        try
        {
            if (dir != null && !SamePath(dir, original.Directory))
            {
                dir.Delete();
            }
        }
        catch
        {
            // Best-effort temp cleanup.
        }
    }

    private static bool SamePath(FileSystemInfo? left, FileSystemInfo? right)
    {
        if (left == null || right == null)
        {
            return false;
        }

        return string.Equals(left.FullName.TrimEnd('\\', '/'), right.FullName.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }
}
