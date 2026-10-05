using System.IO;
using System.Security.Cryptography;
using Organessonium.Models;

namespace Organessonium.Services.Sync;

public static class SyncPaths
{
    public static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/') ||
            Path.IsPathRooted(path) || path.Contains(':'))
            throw new InvalidDataException("Caminho remoto inválido.");
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException("Caminho remoto não pode ser escrito com segurança no Windows.");
        }
        return string.Join('/', parts);
    }

    public static string Resolve(string root, string relative)
    {
        relative = Relative(relative);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O caminho sai da pasta sincronizada.");
        // Check existing ancestors too: a valid textual path can still escape through a junction.
        var cursor = new DirectoryInfo(Path.GetDirectoryName(target)!);
        while (cursor != null && (cursor.FullName + Path.DirectorySeparatorChar).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (cursor.Exists && cursor.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Links e junctions não são sincronizados.");
            cursor = cursor.Parent;
        }
        if ((File.Exists(target) || Directory.Exists(target)) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Links e junctions não são sincronizados.");
        return target;
    }

    public static bool Excluded(string name) => name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.StartsWith("~$", StringComparison.Ordinal) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".organessonium-", StringComparison.OrdinalIgnoreCase);
}

public sealed class SyncScanner
{
    public async Task<SyncScan> ScanAsync(string root, IReadOnlyDictionary<string, SyncFileState> previous,
        CancellationToken cancellationToken = default, IReadOnlySet<string>? changedPaths = null)
    {
        var entries = changedPaths == null ? new Dictionary<string, SyncLocalFile>(StringComparer.OrdinalIgnoreCase) :
            previous.Values.Where(p => p.LocalExists).ToDictionary(p => p.RelativePath,
                p => new SyncLocalFile(p.RelativePath, p.Kind, p.LocalSize, p.LocalWriteTicks, p.LocalHash), StringComparer.OrdinalIgnoreCase);
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return new SyncScan(entries, busy, false);
        var queue = new Stack<FileSystemInfo>();
        if (changedPaths == null) queue.Push(directory);
        else
        {
            var scopes = changedPaths.Select(SyncPaths.Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in changedPaths)
            {
                var parent = relative;
                while (parent.Contains('/'))
                {
                    parent = parent[..parent.LastIndexOf('/')];
                    if (!previous.ContainsKey(parent)) scopes.Add(parent);
                }
            }
            foreach (var relative in scopes.OrderBy(p => p.Count(c => c == '/')))
            {
                if (scopes.Any(p => relative.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))) continue;
                foreach (var key in entries.Keys.Where(p => p.Equals(relative, StringComparison.OrdinalIgnoreCase) ||
                             p.StartsWith(relative + "/", StringComparison.OrdinalIgnoreCase)).ToList()) entries.Remove(key);
                var target = SyncPaths.Resolve(root, relative);
                try
                {
                    var attributes = File.GetAttributes(target);
                    queue.Push(attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(target) : new FileInfo(target));
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new SyncScan(entries, busy, false); }
            }
        }
        var complete = true;
        while (queue.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemInfo[] children;
            try
            {
                if (current != directory && current is DirectoryInfo sub && !current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var path = SyncPaths.Relative(Path.GetRelativePath(root, current.FullName).Replace('\\', '/'));
                    if (path.Split('/').Any(SyncPaths.Excluded)) continue;
                    entries[path] = new(path, "directory", 0, 0, "directory");
                }
                children = current is DirectoryInfo dir ? dir.GetFileSystemInfos() : [current];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { complete = false; continue; }
            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Attributes.HasFlag(FileAttributes.ReparsePoint) || SyncPaths.Excluded(child.Name)) continue;
                var relative = SyncPaths.Relative(Path.GetRelativePath(root, child.FullName).Replace('\\', '/'));
                if (child is DirectoryInfo subdirectory)
                {
                    entries[relative] = new SyncLocalFile(relative, "directory", 0, 0, "directory");
                    queue.Push(subdirectory);
                    continue;
                }
                var file = (FileInfo)child;
                try
                {
                    file.Refresh();
                    var size = file.Length;
                    var ticks = file.LastWriteTimeUtc.Ticks;
                    previous.TryGetValue(relative, out var old);
                    if ((changedPaths == null || !changedPaths.Contains(relative)) && old is { LocalExists: true } &&
                        old.LocalSize == size && old.LocalWriteTicks == ticks && old.LocalHash.Length > 0)
                    {
                        entries[relative] = new SyncLocalFile(relative, "file", size, ticks, old.LocalHash);
                        continue;
                    }
                    if (DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromSeconds(2)) { busy.Add(relative); continue; }
                    await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
                    file.Refresh();
                    if (file.Length != size || file.LastWriteTimeUtc.Ticks != ticks) { busy.Add(relative); continue; }
                    entries[relative] = new SyncLocalFile(relative, "file", size, ticks, hash);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { busy.Add(relative); }
            }
        }
        return new SyncScan(entries, busy, complete);
    }
}
