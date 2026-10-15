using System.Text;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace NamesScanner;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var store = new SettingsStore();
        var settings = store.Load();
        if (args.Contains("--migrate-storage-journal", StringComparer.OrdinalIgnoreCase))
        {
            if (System.Diagnostics.Process.GetProcessesByName("Organessonium").Length != 0)
            { Console.WriteLine("Migration blocked: close Organessonium and pause other clients first."); return 3; }
            try
            {
                using var api = new DiscordApiClient(settings.BotToken);
                var registry = new AppRegistry(api, store, settings);
                var result = await registry.MigrateNamesStorageJournalAsync((stage, done, total) =>
                {
                    if (System.Diagnostics.Process.GetProcessesByName("Organessonium").Length != 0)
                        throw new IOException("Organessonium was opened during maintenance.");
                    if (stage != "removing" && (done == 1 || done % 25 == 0 || done == total)) Console.WriteLine(stage + ": " + done + "/" + total);
                });
                Console.WriteLine($"Candidates: {result.Candidates}; copied: {result.Copied}; removed: {result.Removed}; preserved: {result.Preserved}");
                return 0;
            }
            catch (Exception error) { Console.WriteLine("Migration stopped safely: " + error.GetType().Name); return 1; }
        }
        var auditBatches = args.Contains("--audit-storage-batches", StringComparer.OrdinalIgnoreCase);
        var auditJournal = args.Contains("--audit-storage-journal", StringComparer.OrdinalIgnoreCase);
        if (auditBatches || auditJournal || args.Contains("--audit-metadata-layout", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var api = new DiscordApiClient(settings.BotToken);
                var metadata = new MetadataService(MetadataCodec.ForStore(store), api.DownloadAttachmentBytesAsync);
                var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var messages = (await api.GetMessagesAsync(settings.NamesChannelId, null)).ToList();
                if (auditBatches || auditJournal)
                {
                    var mappings = await metadata.ReadFolderIndexMappingsInParallelAsync(messages, 4);
                    foreach (var mapping in mappings.Where(mapping => mapping.FolderId == "storage-v2"))
                        messages.AddRange(await api.GetMessagesAsync(mapping.ThreadId, null));
                }
                var plans = new Dictionary<string, string[]>();
                var receipts = new Dictionary<string, int>();
                foreach (var message in messages)
                {
                    using var json = await metadata.ReadJsonAsync(message);
                    var kind = json?.RootElement.TryGetProperty("entryType", out var type) == true ? type.GetString() ?? "unknown" : "unknown";
                    counts[kind] = counts.GetValueOrDefault(kind) + 1;
                    if (auditBatches && kind == "storageBatchPlan") plans[json!.RootElement.GetProperty("batchId").GetString()!] =
                        json.RootElement.GetProperty("parts").EnumerateArray().Select(part => part.GetProperty("role").GetString()!).ToArray();
                    if (auditBatches && kind == "storageBatchReceipt") receipts[json!.RootElement.GetProperty("batchId").GetString()!] =
                        json.RootElement.GetProperty("attachments").GetArrayLength();
                }
                if (!auditBatches) foreach (var pair in counts) Console.WriteLine(pair.Key + ": " + pair.Value);
                else
                {
                    foreach (var group in receipts.Values.GroupBy(count => count).OrderBy(group => group.Key))
                        Console.WriteLine("Attachments " + group.Key + ": " + group.Count() + " messages");
                    foreach (var group in receipts.Where(pair => pair.Value == 1 && plans.ContainsKey(pair.Key))
                                 .Select(pair => string.Join(",", plans[pair.Key])).GroupBy(role => role))
                        Console.WriteLine("Single attachment roles " + group.Key + ": " + group.Count());
                }
                return 0;
            }
            catch (Exception error) { Console.WriteLine("Audit failed: " + error.GetType().Name); return 1; }
        }
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
