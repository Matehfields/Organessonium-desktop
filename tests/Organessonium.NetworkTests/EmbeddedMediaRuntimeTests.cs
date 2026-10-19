using System.Diagnostics;
using System.Security.Cryptography;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> EmbeddedMediaRuntimeAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-media-deployment-");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var executable = await Task.Run(() => EmbeddedMediaRuntime.PrepareAsync(directory.FullName, timeout.Token));
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(executable, timeout.Token));
            var timestamp = File.GetLastWriteTimeUtc(executable);
            var second = await EmbeddedMediaRuntime.PrepareAsync(directory.FullName, timeout.Token);
            RequireSync(second == executable && File.GetLastWriteTimeUtc(second) == timestamp,
                "o runtime validado foi regravado em vez de reutilizado");
            RequireSync(File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "GPL-3.0.txt")) &&
                File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "THIRD-PARTY-NOTICES.txt")), "avisos não extraídos");
            await File.WriteAllBytesAsync(executable, [0, 1, 2], timeout.Token);
            var repaired = await EmbeddedMediaRuntime.PrepareAsync(directory.FullName, timeout.Token);
            var repairedHash = SHA256.HashData(await File.ReadAllBytesAsync(repaired, timeout.Token));
            RequireSync(hash.AsSpan().SequenceEqual(repairedHash),
                "um runtime corrompido não foi reparado");
            var info = new ProcessStartInfo(repaired) { UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-v", "error", "-f", "lavfi", "-i", "color=s=64x64:d=0.1", "-frames:v", "1", "-f", "null", "-" })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errors; await output;
            RequireSync(process.ExitCode == 0, "o motor extraído não executou com as dependências embutidas");
            RequireSync(!Directory.EnumerateFiles(directory.FullName, ".extract-*", SearchOption.AllDirectories).Any(),
                "a extração deixou arquivos parciais");
            return TestOutcome.Pass();
        }
        finally { directory.Delete(recursive: true); }
    }
}
