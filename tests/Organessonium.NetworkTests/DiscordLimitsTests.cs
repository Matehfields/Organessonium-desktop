using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
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

    private sealed class LimitsHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
