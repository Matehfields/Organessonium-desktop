using System.Diagnostics;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> BrowserMemoryIsSeparateAsync()
    {
        AppMemoryBudget.Initialize();
        var before = AppMemoryBudget.CommittedBytes();
        using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            Arguments = "--memory-budget-child", UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        try
        {
            RequireSync(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "MEMORY_READY",
                "o processo auxiliar de memória não iniciou");
            var used = AppMemoryBudget.CommittedBytes();
            AppMemoryBudget.RegisterWebViewProcesses([process.Id]);
            RequireSync(used - before < 32L * 1024 * 1024 && AppMemoryBudget.WebViewCommittedBytes() >= 80L * 1024 * 1024,
                "o processo do navegador consumiu o limite do backend ou deixou de ser observado separadamente");
            RequireSync(AppMemoryBudget.ApplicationPrivateWorkingSetBytes() > 0, "a memória residente privada do executável não foi medida");
            Console.WriteLine($"MEMORY_SEPARATE backendDelta={used - before} browserBytes={AppMemoryBudget.WebViewCommittedBytes()}");
            return TestOutcome.Pass();
        }
        finally { AppMemoryBudget.RegisterWebViewProcesses([]); if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
