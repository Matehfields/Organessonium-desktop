using System.Text;
using Organessonium.Services;

namespace NamesScanner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var store = new SettingsStore();
        var settings = store.Load();
        Console.WriteLine("DataDir: " + store.DataDirectory.FullName);
        if (args.Contains("--token", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("TOKEN=" + settings.BotToken);
            return 0;
        }

        Console.WriteLine("Token: " + (settings.BotToken.Length > 0 ? "OK (" + settings.BotToken.Length + " chars)" : "VAZIO"));
        Console.WriteLine("NamesChannel: " + (settings.NamesChannelId.Length > 0 ? settings.NamesChannelId : "VAZIO"));
        Console.Out.Flush();

        if (settings.BotToken.Length == 0 || settings.NamesChannelId.Length == 0)
        {
            Console.WriteLine("FALHA: bot nao configurado.");
            return 2;
        }

        try
        {
            using var discord = new DiscordApiClient(settings.BotToken);
            var sequential = args.Contains("--sequential", StringComparer.OrdinalIgnoreCase);
            Console.WriteLine("modo: " + (sequential ? "SEQUENCIAL (baseline)" : "PARALELO (" + settings.SearchWorkers + " agentes)"));
            Console.Out.Flush();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var scanner = new DebugNamesScanner(discord, store, settings);
            var report = await scanner.ScanAsync(
                CancellationToken.None,
                stage =>
                {
                    Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + stage);
                    Console.Out.Flush();
                },
                !sequential);
            sw.Stop();
            Console.WriteLine("tempo total: " + sw.Elapsed);
            var path = Path.Combine(store.DataDirectory.FullName, "names-scan-report.txt");
            await File.WriteAllTextAsync(path, report, Encoding.UTF8);
            Console.WriteLine("REPORT: " + path);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FALHA: " + ex);
            return 1;
        }
    }
}