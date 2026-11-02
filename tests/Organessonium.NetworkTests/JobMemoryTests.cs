using System.Diagnostics;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> JobMemoryIncludesUnregisteredChildrenAsync()
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
            RequireSync(used - before >= 80L * 1024 * 1024 && used <= AppMemoryBudget.LimitBytes,
                "o orçamento ignorou a memória de um filho ainda não listado pelo WebView2");
            Console.WriteLine($"JOB_MEMORY accountedChildBytes={used - before} hardLimit={AppMemoryBudget.LimitBytes}");
            return TestOutcome.Pass();
        }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
