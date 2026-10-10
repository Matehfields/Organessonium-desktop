using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> UploadQuotaSnapshotsKeepSendingAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-upload-quota-");
        try
        {
            var source = new FileInfo(Path.Combine(directory.FullName, "chunk.bin"));
            await File.WriteAllBytesAsync(source.FullName, new byte[4096]);
            using var handler = new UploadQuotaHandler();
            using var api = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(handler, false));
            using var token = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task Send() => api.SendFilesMessageAsync("123", "", [new PreparedUploadPart(source, source.Length)],
                token.Token, serializeRoute: false);
            await Send(); // Learn a quota of two fresh admissions.
            var older = Send();
            await handler.Started[1].Task.WaitAsync(token.Token);
            var first = Send();
            await handler.Started[2].Task.WaitAsync(token.Token);
            handler.Replies[2].TrySetResult(UploadQuotaHandler.Reply(1, 1));
            await first;
            var next = Send();
            await handler.Started[3].Task.WaitAsync(TimeSpan.FromMilliseconds(500));
            RequireSync(!older.IsCompleted, "o cenário não manteve uma resposta anterior pendente");
            var blocked = Send();
            await Task.Delay(100, token.Token);
            RequireSync(!handler.Started[4].Task.IsCompleted, "uma mesma vaga foi reservada duas vezes");
            handler.Replies[1].TrySetResult(UploadQuotaHandler.Reply(0, 0)); // Stale response must not replace the fresh snapshot.
            await older;
            await Task.Delay(100, token.Token);
            RequireSync(!handler.Started[4].Task.IsCompleted, "um header atrasado devolveu uma vaga indevida");
            handler.Replies[3].TrySetResult(UploadQuotaHandler.Reply(1, 2));
            await next;
            await handler.Started[4].Task.WaitAsync(token.Token);
            handler.Replies[4].TrySetResult(UploadQuotaHandler.Reply(1, 3));
            await blocked;
            return TestOutcome.Pass();
        }
        finally { directory.Delete(true); }
    }

    private sealed class UploadQuotaHandler : HttpMessageHandler
    {
        private int _calls;
        internal TaskCompletionSource[] Started = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        internal TaskCompletionSource<HttpResponseMessage>[] Replies = Enumerable.Range(0, 5).Select(_ => new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private static readonly DateTimeOffset Date = DateTimeOffset.UtcNow;
        internal static HttpResponseMessage Reply(int remaining, int seconds)
        {
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"test\",\"attachments\":[]}") };
            reply.Headers.Date = Date.AddSeconds(seconds);
            reply.Headers.Add("X-RateLimit-Bucket", "upload-test");
            reply.Headers.Add("X-RateLimit-Limit", "2");
            reply.Headers.Add("X-RateLimit-Remaining", remaining.ToString(CultureInfo.InvariantCulture));
            reply.Headers.Add("X-RateLimit-Reset-After", "10");
            return reply;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            await request.Content!.CopyToAsync(Stream.Null, token);
            Started[call].TrySetResult();
            return call == 0 ? Reply(2, 0) : await Replies[call].Task.WaitAsync(token);
        }
    }

    // Explicit manual entry point, never part of the automatic test list. Requires user approval.
    private static async Task<int> RunLiveUploadCadenceAsync()
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-cadence-");
        CadenceHttpHandler? trace = null;
        DiscordApiClient? api = null;
        var succeeded = false;
        try
        {
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var pointer = Path.Combine(folder, "OrganessoniumData.path");
            var data = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : Path.Combine(folder, "OrganessoniumData");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(data, "organessonium.db"), Mode = SqliteOpenMode.ReadOnly }.ToString());
            await db.OpenAsync();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT key,value FROM settings WHERE key IN ('botToken','appsForumId','uploadThreads','forceUploadParallelism','proxyMode','proxyHost','proxyPort','proxyProtocol','proxyAuthEnabled','proxyUsername','proxyPassword')";
            var settings = new Dictionary<string, string>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) settings[reader.GetString(0)] = reader.GetString(1);
            string Read(string key, string fallback = "") => settings.GetValueOrDefault(key, fallback);
            var proxy = new ProxySettings
            {
                Mode = (ProxyMode)typeof(SettingsStore).GetMethod("NormalizeProxyMode", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [Read("proxyMode", "automatic")])!,
                Protocol = (ProxyProtocol)typeof(SettingsStore).GetMethod("NormalizeProxyProtocol", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [Read("proxyProtocol", "auto")])!,
                Host = Read("proxyHost"), Port = int.TryParse(Read("proxyPort"), out var port) ? port : 0,
                AuthenticationEnabled = Read("proxyAuthEnabled") is "true" or "1", Username = Read("proxyUsername"), Password = Read("proxyPassword")
            };
            NetworkConfig.Current.Apply(proxy);
            var workers = int.TryParse(Read("uploadThreads"), out var configured) ? Math.Clamp(configured, 1, 16) : 4;
            var force = Read("forceUploadParallelism") == "1" || bool.TryParse(Read("forceUploadParallelism"), out var forced) && forced;
            if (force) workers = 16;
            AppMemoryBudget.Initialize();
            trace = new CadenceHttpHandler(NetworkConfig.Current.CreateHandler());
            api = new DiscordApiClient(Read("botToken"), new HttpClient(trace));
            var crypto = CreateEncryptionService(out _);
            var source = new FileInfo(Path.Combine(directory.FullName, "test-source.bin"));
            await using (var file = source.Create()) file.SetLength(2L * 1024 * 1024 * 1024);
            var count = ArchiveSplitter.GetPartCount(source.Length, EncryptionService.MaximumPlaintextPayloadBytes);
            var context = new EncryptedPayloadContext(Guid.NewGuid().ToString("N"), crypto.CreateFileEncryption(EncryptionModes.Standard), EncryptedPayloadRoles.Original, 0, count);
            using var memory = new UploadMemoryCoordinator(workers, forceParallelism: force);
            using var gate = new AdaptiveUploadGate(workers, !force);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var uploader = new ForumUploader(api, Read("appsForumId"));
            Console.WriteLine($"CADENCE workers={workers} force={force} batchLimit={RamUploadBudget.PipelineBatchChunks(DiscordLimits.AttachmentBytes)}");
            var upload = uploader.UploadResumableGeneratedAsync("test-source.bin", count,
                index => memory.PreparePartAsync(crypto, source, source.Length, EncryptionService.MaximumPlaintextPayloadBytes, context.WithChunkIndex(index), timeout.Token, deferBuffers: true),
                "", "", new Dictionary<int, string>(), cancellationToken: timeout.Token, maxParallelParts: workers,
                adaptiveUploadGate: gate, encryptedPayload: context, useAttachmentReferences: true,
                batchPreparationGate: memory.BatchPreparationGate,
                streamPreparedParts: true,
                onPartTransferProgress: trace.OnWire,
                onRateLimitWait: wait => { Console.WriteLine($"WAIT {wait.TotalSeconds:F2}s"); return Task.CompletedTask; });
            await trace.SampleUntilAsync(upload);
            var result = await upload;
            RequireSync(result.ChunkReferences?.Count == count, "upload incompleto");
            Console.WriteLine($"RESULT bytes={trace.TotalBytes} maxIdle={trace.MaximumIdle.TotalSeconds:F2}s mainIdle={trace.MaximumMainIdle.TotalSeconds:F2}s peakMiB={AppMemoryBudget.PeakCommittedBytes / 1048576d:F1}");
            succeeded = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("DIAGNOSTIC FAILED: " + ex.GetType().Name); // Never print URI, credentials or server content.
        }
        finally
        {
            if (trace?.CreatedThread is { Length: > 0 } thread && api != null)
            {
                try
                {
                    await api.DeleteChannelAsync(thread, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
                    Console.WriteLine("CLEANUP temporary post removed");
                }
                catch (Exception ex) { Console.WriteLine("CLEANUP FAILED: " + ex.GetType().Name); succeeded = false; }
            }
            api?.Dispose();
            directory.Delete(true);
            SqliteConnection.ClearAllPools();
        }
        return succeeded ? 0 : 1;
    }

    private sealed class CadenceHttpHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly object _sync = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Dictionary<int, long> _wire = [];
        private readonly List<CadenceRequest> _requests = [];
        private long _total;
        private double _lastWire;
        private double _maxIdle;
        private double _maxMainIdle;
        private bool _mainHasBytes;
        internal long TotalBytes { get { lock (_sync) return _total; } }
        internal TimeSpan MaximumIdle { get { lock (_sync) return TimeSpan.FromSeconds(_maxIdle); } }
        internal TimeSpan MaximumMainIdle { get { lock (_sync) return TimeSpan.FromSeconds(_maxMainIdle); } }
        internal string CreatedThread { get; private set; } = "";

        internal void OnWire(int index, long bytes)
        {
            lock (_sync)
            {
                var old = _wire.GetValueOrDefault(index);
                var delta = bytes >= old ? bytes - old : Math.Max(0, bytes);
                _wire[index] = bytes;
                if (delta > 0)
                {
                    var now = _clock.Elapsed.TotalSeconds;
                    if (_total > 0) _maxIdle = Math.Max(_maxIdle, now - _lastWire);
                    if (_mainHasBytes) _maxMainIdle = Math.Max(_maxMainIdle, now - _lastWire);
                    if (CreatedThread.Length > 0) _mainHasBytes = true;
                    _lastWire = now;
                    _total += delta;
                }
                foreach (var request in _requests.Where(request => request.BodyEnded == 0))
                    if (request.Parts.All(part => _wire.GetValueOrDefault(part.Index) >= part.Bytes)) request.BodyEnded = _clock.Elapsed.TotalSeconds;
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            CadenceRequest? record = null;
            if (request.Content is MultipartFormDataContent body)
            {
                var parts = body.Where(part => part.Headers.ContentDisposition?.FileName != null).Select(part =>
                {
                    var filename = part.Headers.ContentDisposition!.FileName!.Trim('"');
                    var index = filename.StartsWith("chunk-", StringComparison.Ordinal) ? int.Parse(filename[6..].Split('.')[0]) - 1 : 0;
                    return (Index: index, Bytes: part.Headers.ContentLength!.Value);
                }).ToArray();
                lock (_sync)
                {
                    record = new CadenceRequest(_requests.Count + 1, _clock.Elapsed.TotalSeconds, parts);
                    _requests.Add(record);
                }
                Console.WriteLine($"HTTP {record.Number} start={record.Started:F2} parts={parts.Length}");
            }
            var response = await base.SendAsync(request, token);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/threads", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                CreatedThread = data.RootElement.GetProperty("id").GetString()!;
            }
            if (record != null)
            {
                lock (_sync) record.Responded = _clock.Elapsed.TotalSeconds;
                string Header(string key) => response.Headers.TryGetValues(key, out var values) ? values.First() : "?";
                Console.WriteLine($"HTTP {record.Number} bodyEnd={record.BodyEnded:F2} reply={record.Responded:F2} status={(int)response.StatusCode} http={response.Version} remaining={Header("X-RateLimit-Remaining")} reset={Header("X-RateLimit-Reset-After")}");
            }
            return response;
        }

        internal async Task SampleUntilAsync(Task upload)
        {
            long previous = 0;
            while (!upload.IsCompleted)
            {
                await Task.Delay(500);
                lock (_sync)
                {
                    var pending = _requests.Count(request => request.Responded == 0);
                    var writing = _requests.Count(request => request.Responded == 0 && request.BodyEnded == 0);
                    Console.WriteLine($"T={_clock.Elapsed.TotalSeconds:F2} MiBs={(_total - previous) / 524288d:F2} writing={writing} pending={pending} poolMiB={RamUploadBudget.Pool.AllocatedBytes / 1048576d:F0} managedMiB={GC.GetTotalMemory(false) / 1048576d:F0}");
                    previous = _total;
                }
            }
        }

        private sealed class CadenceRequest(int number, double started, (int Index, long Bytes)[] parts)
        {
            internal int Number = number;
            internal double Started = started, BodyEnded, Responded;
            internal (int Index, long Bytes)[] Parts = parts;
        }
    }
}
