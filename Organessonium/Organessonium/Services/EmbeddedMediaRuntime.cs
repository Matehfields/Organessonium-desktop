using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Organessonium.Services;

// Only tools and notices enter this cache, never user media.
internal static class EmbeddedMediaRuntime
{
    private const string ArchiveResource = "media/runtime-package.zip";
    private static readonly Assembly Resources = typeof(EmbeddedMediaRuntime).Assembly;
    private static readonly SemaphoreSlim Preparation = new(1, 1);
    private static string? _prepared;

    internal static bool Available => Resources.GetManifestResourceInfo(ArchiveResource) != null ||
        File.Exists(Path.Combine(AppContext.BaseDirectory, "media", "ffmpeg.exe"));

    internal static async Task<int> VerifyAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var executable = await GetExecutableAsync(timeout.Token).ConfigureAwait(false);
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "color=s=64x64:d=0.1", "-frames:v", "1", "-f", "null", "-" })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Motor de mídia indisponível.");
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            await errors.ConfigureAwait(false); await output.ConfigureAwait(false);
            return process.ExitCode == 0 ? 0 : 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Media runtime verification failed ({exception.GetType().Name}).");
            return 1;
        }
    }

    internal static async Task<string> GetExecutableAsync(CancellationToken token)
    {
        await Preparation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_prepared != null && File.Exists(_prepared)) return _prepared;
            if (Resources.GetManifestResourceInfo(ArchiveResource) == null)
                return Path.Combine(AppContext.BaseDirectory, "media", "ffmpeg.exe");
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Organessonium", "Media");
            // Directory creation and verification must not run on WPF.
            _prepared = await Task.Run(() => PrepareAsync(cache, token), token).ConfigureAwait(false);
            return _prepared;
        }
        finally { Preparation.Release(); }
    }

    // Deployment regression exercises the actual resource, including repair.
    internal static async Task<string> PrepareAsync(string cacheRoot, CancellationToken token)
    {
        using var manifestStream = Resources.GetManifestResourceStream("media/source-manifest.json") ??
            throw new IOException("O manifesto do motor de mídia está ausente.");
        using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: token).ConfigureAwait(false);
        var root = manifest.RootElement;
        var archiveHash = root.GetProperty("runtimeArchive").GetProperty("sha256").GetString()!;
        if (archiveHash.Length != 64 || !archiveHash.All(Uri.IsHexDigit))
            throw new IOException("A identidade do motor de mídia é inválida.");
        var directory = Path.Combine(cacheRoot, archiveHash);
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("O cache do motor de mídia não pode ser um link.");

        // FileShare.None coordinates processes. Windows releases the handle on
        // exit, so a leftover file cannot keep the cache permanently locked.
        await using var lease = await OpenLeaseAsync(Path.Combine(directory, ".prepare.lock"), token).ConfigureAwait(false);
        using var package = Resources.GetManifestResourceStream(ArchiveResource) ??
            throw new IOException("O pacote do motor de mídia está ausente.");
        if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(package, token).ConfigureAwait(false)),
            archiveHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("O pacote do motor de mídia está corrompido.");
        package.Position = 0;
        using var archive = new ZipArchive(package, ZipArchiveMode.Read);
        foreach (var file in root.GetProperty("runtimeFiles").EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            var relative = file.GetProperty("path").GetString()!;
            var expectedHash = file.GetProperty("sha256").GetString()!;
            if (string.IsNullOrWhiteSpace(relative) || Path.GetFileName(relative) != relative ||
                relative.IndexOfAny(['/', '\\', ':']) >= 0 || relative is "." or "..")
                throw new IOException("O pacote contém um caminho de mídia inválido.");
            var destination = Path.Combine(directory, relative);
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("O cache contém um link de mídia inválido.");
            if (File.Exists(destination) && await MatchesAsync(destination, expectedHash, token).ConfigureAwait(false)) continue;
            var entry = archive.GetEntry(relative) ?? throw new IOException("Um componente do motor de mídia está ausente.");
            var partial = Path.Combine(directory, ".extract-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var input = entry.Open())
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, 128 * 1024, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                if (!await MatchesAsync(partial, expectedHash, token).ConfigureAwait(false))
                    throw new IOException("Um componente do motor de mídia está corrompido.");
                File.Move(partial, destination, overwrite: true);
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        var executable = Path.Combine(directory, "ffmpeg.exe");
        if (!File.Exists(executable)) throw new IOException("O motor de mídia não pôde ser preparado.");
        return executable;
    }

    private static async Task<bool> MatchesAsync(string path, string expected, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)),
            expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<FileStream> OpenLeaseAsync(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }
}
