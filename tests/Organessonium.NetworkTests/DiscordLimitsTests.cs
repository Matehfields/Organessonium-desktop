using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> DiscordNumericChannelIsolationAsync()
    {
        const string payload = "123456789012345678";
        const string journal = "987654321098765432";
        using var handler = new LimitsHandler((request, _) =>
        {
            var response = MessageResponse();
            response.Headers.Add("X-RateLimit-Bucket", "same-endpoint-different-channel");
            response.Headers.Add("X-RateLimit-Limit", "5");
            response.Headers.Add("X-RateLimit-Remaining", request.RequestUri!.AbsolutePath.Contains(payload) ? "0" : "4");
            response.Headers.Add("X-RateLimit-Reset-After", "2");
            return Task.FromResult(response);
        });
        using var discord = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(handler));
        await discord.GetMessageAsync(journal, "100");
        await discord.GetMessageAsync(payload, "101");
        using var quick = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try { await discord.GetMessageAsync(journal, "102", quick.Token); }
        catch (OperationCanceledException) { return TestOutcome.Fail("a cota esgotada do payload bloqueou o journal de outro canal numérico"); }
        // A slow POST must also leave the other channel's route lock available.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var slowHandler = new LimitsHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains(payload))
            { started.TrySetResult(); await release.Task.WaitAsync(token); }
            return MessageResponse();
        });
        using var client = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(slowHandler));
        var pending = client.CreateMessageAsync(payload, "payload");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await client.CreateMessageAsync(journal, "journal", deadline.Token);
        }
        catch (OperationCanceledException) { return TestOutcome.Fail("um POST lento reteve o lock de todos os canais numéricos"); }
        finally { release.TrySetResult(); await pending; }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DiscordLimitsRejectOversizedPayloadsAsync()
    {
        var calls = 0;
        using var handler = new LimitsHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(MessageResponse());
        });
        using var discord = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(handler));
        await discord.CreateMessageAsync("123", new string('x', DiscordLimits.MessageContentCharacters));
        var rejectedText = false;
        try { await discord.UpdateMessageAsync("123", "456", new string('x', DiscordLimits.MessageContentCharacters + 1)); }
        catch (ArgumentException) { rejectedText = true; }
        var path = Path.Combine(Path.GetTempPath(), "organessonium-limit-" + Guid.NewGuid() + ".bin");
        var rejectedFile = false;
        try
        {
            await using (var stream = File.Create(path)) stream.SetLength(DiscordLimits.AttachmentBytes + 1L);
            try { await discord.SendFileMessageAsync("123", "", new FileInfo(path)); }
            catch (ArgumentException) { rejectedFile = true; }
        }
        finally { File.Delete(path); }
        return rejectedText && rejectedFile && calls == 1 &&
            ArchiveSplitter.GetPartCount(DiscordLimits.AttachmentBytes + 1L) == 2 &&
            EncryptionService.MaximumPlaintextPayloadBytes + EncryptionService.EnvelopeOverheadBytes == DiscordLimits.AttachmentBytes
            ? TestOutcome.Pass() : TestOutcome.Fail("texto/anexo excedente chegou ao HTTP ou a divisão não respeitou o orçamento");
    }

    private static async Task<TestOutcome> DiscordGlobalPacingAcrossRoutesAsync()
    {
        var starts = new ConcurrentQueue<long>();
        using var handler = new LimitsHandler((_, _) =>
        {
            starts.Enqueue(Environment.TickCount64);
            return Task.FromResult(MessageResponse());
        });
        var token = Guid.NewGuid().ToString();
        using var first = new DiscordApiClient(token, new HttpClient(handler, false));
        using var second = new DiscordApiClient(token, new HttpClient(handler, false));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Task.WhenAll(Enumerable.Range(0, 55).Select(i => (i % 2 == 0 ? first : second)
            .GetMessageAsync((1000 + i).ToString(), "456", timeout.Token)));
        var times = starts.Order().ToArray();
        var maximum = times.Max(time => times.Count(other => other >= time && other < time + 1000));
        return times.Length == 55 && maximum <= DiscordLimits.TargetRequestsPerSecond
            ? TestOutcome.Pass() : TestOutcome.Fail($"rajada global observada: {maximum} chamadas em 1 segundo");
    }

    private static async Task<TestOutcome> DiscordBucketsReserveParallelRequestsAsync()
    {
        var sync = new object();
        var resetsAt = Environment.TickCount64 + 350;
        var remaining = 3;
        var active = 0;
        var maximumActive = 0;
        var rejected = 0;
        using var handler = new LimitsHandler(async (_, cancellationToken) =>
        {
            long reset;
            int left;
            lock (sync)
            {
                if (Environment.TickCount64 >= resetsAt) { resetsAt = Environment.TickCount64 + 350; remaining = 3; }
                if (remaining <= 0) Interlocked.Increment(ref rejected);
                left = --remaining;
                reset = resetsAt;
                maximumActive = Math.Max(maximumActive, ++active);
            }
            await Task.Delay(80, cancellationToken);
            lock (sync) active--;
            var response = MessageResponse();
            response.Headers.Add("X-RateLimit-Bucket", "test-shared-bucket");
            response.Headers.Add("X-RateLimit-Limit", "3");
            response.Headers.Add("X-RateLimit-Remaining", Math.Max(0, left).ToString());
            response.Headers.Add("X-RateLimit-Reset-After", (Math.Max(1, reset - Environment.TickCount64) / 1000.0).ToString(CultureInfo.InvariantCulture));
            return response;
        });
        using var discord = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(handler));
        var path = Path.Combine(Path.GetTempPath(), "organessonium-bucket-" + Guid.NewGuid() + ".bin");
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            // Learn that GETs for different message IDs and POSTs share this server bucket.
            await discord.GetMessageAsync("123", "1", timeout.Token);
            await discord.SendFileMessageAsync("123", "", new FileInfo(path), timeout.Token, serializeRoute: false);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                discord.SendFileMessageAsync("123", "", new FileInfo(path), timeout.Token, serializeRoute: false)));
            return rejected == 0 && maximumActive > 1
                ? TestOutcome.Pass() : TestOutcome.Fail($"cota excedida {rejected} vezes; paralelismo máximo {maximumActive}");
        }
        finally { File.Delete(path); }
    }

    private static async Task<TestOutcome> DiscordGlobalRetryBlocksOtherRoutesAsync()
    {
        var attempts = 0;
        var limited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedAt = 0L;
        var otherStartedAt = 0L;
        using var handler = new LimitsHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/123/") && Interlocked.Increment(ref attempts) == 1)
            {
                blockedAt = Environment.TickCount64;
                limited.SetResult();
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"retry_after\":0.05,\"global\":true}", Encoding.UTF8, "application/json")
                };
                response.Headers.Add("Retry-After", "1");
                return Task.FromResult(response);
            }
            if (request.RequestUri.AbsolutePath.Contains("/999/")) otherStartedAt = Environment.TickCount64;
            return Task.FromResult(MessageResponse());
        });
        using var discord = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(handler));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = discord.CreateMessageAsync("123", "retry", timeout.Token, _ => { waiting.TrySetResult(); return Task.CompletedTask; });
        await limited.Task.WaitAsync(timeout.Token);
        await waiting.Task.WaitAsync(timeout.Token);
        using var canceled = new CancellationTokenSource(40);
        var cancellationWorked = false;
        try { await discord.GetMessageAsync("777", "1", canceled.Token); }
        catch (OperationCanceledException) { cancellationWorked = true; }
        await discord.GetMessageAsync("999", "1", timeout.Token);
        await first;
        return attempts == 2 && cancellationWorked && otherStartedAt - blockedAt >= 1000
            ? TestOutcome.Pass() : TestOutcome.Fail("o 429 global não bloqueou outra rota, ignorou Retry-After ou não permitiu cancelar a espera");
    }

    private static HttpResponseMessage MessageResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"id\":\"456\",\"content\":\"\",\"attachments\":[]}", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage BucketResponse(int remaining, double reset, double? epoch = null,
        DateTimeOffset? date = null)
    {
        var response = MessageResponse();
        response.Headers.Add("X-RateLimit-Bucket", "quota-recovery");
        response.Headers.Add("X-RateLimit-Limit", "5");
        response.Headers.Add("X-RateLimit-Remaining", remaining.ToString(CultureInfo.InvariantCulture));
        response.Headers.Add("X-RateLimit-Reset-After", reset.ToString(CultureInfo.InvariantCulture));
        if (epoch.HasValue) response.Headers.Add("X-RateLimit-Reset", epoch.Value.ToString(CultureInfo.InvariantCulture));
        response.Headers.Date = date;
        return response;
    }

    private static async Task<TestOutcome> DiscordBucketRefillDoesNotPauseUploadsAsync()
    {
        int[] remaining = [4, 3, 2, 2, 1, 0];
        double[] reset = [1.000, 1.406, 2.016, 2.406, 3.002, 3.500];
        var calls = 0;
        using var handler = new LimitsHandler((_, _) =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            return Task.FromResult(BucketResponse(remaining[index], reset[index]));
        });
        using var discord = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(handler));
        var temporary = Directory.CreateTempSubdirectory("organessonium-quota-refill-");
        try
        {
            var file = new FileInfo(Path.Combine(temporary.FullName, "chunk.bin"));
            await File.WriteAllBytesAsync(file.FullName, [1, 2, 3]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(900));
            for (var i = 0; i < remaining.Length; i++)
                await discord.SendFileMessageAsync("123", "", file, timeout.Token, serializeRoute: false);
            return calls == 6 ? TestOutcome.Pass() : TestOutcome.Fail("a sexta parte perdeu a vaga confirmada pelo servidor");
        }
        finally { temporary.Delete(true); }
    }

    private static async Task<TestOutcome> DiscordParallelRepliesPreserveAvailableQuotaAsync()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var completed = 0;
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 20;
        using var handler = new LimitsHandler(async (_, token) =>
        {
            var index = Interlocked.Increment(ref calls);
            if (index == 1) return BucketResponse(4, 2, epoch);
            if (index <= 4)
            {
                if (index == 4) accepted.TrySetResult();
                await accepted.Task.WaitAsync(token);
                // All three were charged, but the older, higher counters arrive last.
                await Task.Delay((4 - index) * 40, token);
                Interlocked.Increment(ref completed);
                return BucketResponse(5 - index, 2, epoch);
            }
            if (index == 5 && completed == 3) return BucketResponse(0, 2, epoch);
            throw new InvalidOperationException("chamada excedeu a cota disponível");
        });
        using var discord = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(handler));
        var temporary = Directory.CreateTempSubdirectory("organessonium-quota-parallel-");
        try
        {
            var file = new FileInfo(Path.Combine(temporary.FullName, "chunk.bin"));
            await File.WriteAllBytesAsync(file.FullName, [1, 2, 3]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await discord.SendFileMessageAsync("123", "", file, timeout.Token, serializeRoute: false);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
                discord.SendFileMessageAsync("123", "", file, timeout.Token, serializeRoute: false)));
            await discord.SendFileMessageAsync("123", "", file, timeout.Token, serializeRoute: false);
            using var blocked = new CancellationTokenSource(120);
            try
            {
                await discord.SendFileMessageAsync("123", "", file, blocked.Token, serializeRoute: false);
                return TestOutcome.Fail("resposta antiga liberou uma parte sem cota");
            }
            catch (OperationCanceledException) when (blocked.IsCancellationRequested) { }
            return calls == 5 ? TestOutcome.Pass() : TestOutcome.Fail("vaga remota foi perdida ou contada duas vezes");
        }
        finally { temporary.Delete(true); }
    }

    private static async Task<TestOutcome> DiscordPendingRetryBlocksFreshQuotaAsync()
    {
        var parallelStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var limitedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readingRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var date = DateTimeOffset.UtcNow;
        using var handler = new LimitsHandler(async (_, _) =>
        {
            var index = Interlocked.Increment(ref calls);
            if (index == 1) return BucketResponse(4, 1, 100, date);
            if (index == 2)
            {
                limitedStarted.TrySetResult();
                await parallelStarted.Task;
                var limited = BucketResponse(2, 0.2, 100, date);
                limited.StatusCode = HttpStatusCode.TooManyRequests;
                limited.Content = new DeferredRetryContent(readingRetry, releaseRetry);
                return limited;
            }
            if (index == 3)
            {
                parallelStarted.TrySetResult();
                await readingRetry.Task;
                return BucketResponse(4, 0.2, 101, date.AddSeconds(1));
            }
            return BucketResponse(3, 0.2, 102, date.AddSeconds(2));
        });
        using var discord = new DiscordApiClient(Guid.NewGuid().ToString(), new HttpClient(handler));
        var temporary = Directory.CreateTempSubdirectory("organessonium-quota-retry-");
        try
        {
            var file = new FileInfo(Path.Combine(temporary.FullName, "chunk.bin"));
            await File.WriteAllBytesAsync(file.FullName, [1, 2, 3]);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await discord.SendFileMessageAsync("123", "", file, timeout.Token, serializeRoute: false);
            var limited = discord.SendFileMessageAsync("123", "", file, timeout.Token, serializeRoute: false);
            await limitedStarted.Task.WaitAsync(timeout.Token);
            var fresh = discord.SendFileMessageAsync("123", "", file, timeout.Token, serializeRoute: false);
            await readingRetry.Task.WaitAsync(timeout.Token);
            await fresh.WaitAsync(timeout.Token);
            var next = discord.SendFileMessageAsync("123", "", file, timeout.Token, serializeRoute: false);
            await Task.Delay(80, timeout.Token);
            if (calls != 3) return TestOutcome.Fail("headers positivos furaram o 429 enquanto o retry era lido");
            releaseRetry.TrySetResult();
            await Task.WhenAll(limited, next).WaitAsync(timeout.Token);
            return calls == 5 ? TestOutcome.Pass() : TestOutcome.Fail("o bloqueio não retomou as duas partes pendentes");
        }
        finally { parallelStarted.TrySetResult(); releaseRetry.TrySetResult(); temporary.Delete(true); }
    }

    private sealed class DeferredRetryContent(TaskCompletionSource started, TaskCompletionSource released) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            started.TrySetResult();
            await released.Task;
            await stream.WriteAsync("{\"retry_after\":0.1,\"global\":false}"u8.ToArray());
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class LimitsHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
