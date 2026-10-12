using System.IO;

namespace Organessonium.Services;

internal static class DownloadAssembler
{
    internal static async Task AssembleAsync(IEnumerable<FileInfo> chunks, FileInfo target, Action<string> status,
        Action<DownloadProgress>? progress, CancellationToken token)
    {
        var files = chunks.ToArray();
        var total = files.Sum(file => file.Length);
        long assembled = 0;
        var lastPercent = -1;
        var buffer = new byte[1024 * 1024];
        Report(80);
        await using var output = new FileStream(target.FullName, FileMode.Create, FileAccess.Write, FileShare.None,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            await using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                assembled += read;
                Report(total <= 0 ? 99 : 80 + (int)Math.Min(19, assembled * 20d / total));
            }
        }
        await output.FlushAsync(token).ConfigureAwait(false);

        void Report(int percent)
        {
            if (percent == lastPercent) return;
            lastPercent = percent;
            var message = "Montando arquivo final...";
            status(message);
            progress?.Invoke(new DownloadProgress(percent, message, total, total, Assembling: true));
        }
    }
}
