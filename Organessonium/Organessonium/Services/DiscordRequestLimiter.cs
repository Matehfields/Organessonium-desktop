using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Organessonium.Services;

// Shared by clients using the same bot in this process. Never holds a lock during HTTP or delays.
internal sealed class DiscordRequestLimiter
{
    private static readonly ConcurrentDictionary<string, DiscordRequestLimiter> Clients = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, Budget> _routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Budget> _buckets = new(StringComparer.Ordinal);
    private long _nextRequestAt;
    private long _globalReadyAt;

    public static DiscordRequestLimiter ForToken(string token) => Clients.GetOrAdd(
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), _ => new());

    public static string RouteKey(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
        var segments = path.Trim('/').Split('/');
        // Preserve the major resource (channel/guild); message IDs share a route quota.
        for (var i = 2; i < segments.Length; i++)
            if (ulong.TryParse(segments[i], out _) || segments[i - 1] == "messages")
                segments[i] = ":id";
        return request.Method.Method + " " + string.Join('/', segments);
    }

    public async Task<Reservation> WaitAsync(string route, Func<TimeSpan, Task>? onWait, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            long delay;
            lock (_sync)
            {
                var now = Environment.TickCount64;
                var budget = GetBudget(route);
                if (budget.ResetAt > 0 && budget.ResetAt <= now)
                {
                    budget.Remaining = budget.Limit.HasValue ? Math.Max(0, budget.Limit.Value - budget.InFlight) : null;
                    budget.ResetAt = 0;
                }
                var readyAt = Math.Max(_nextRequestAt, _globalReadyAt);
                if (budget.Remaining == 0)
                    readyAt = Math.Max(readyAt, budget.ResetAt > now ? budget.ResetAt : now + 10);
                delay = readyAt - now;
                if (delay <= 0)
                {
                    _nextRequestAt = now + (long)Math.Ceiling(1000.0 / Math.Min(
                        DiscordLimits.TargetRequestsPerSecond, DiscordLimits.ApiRequestsPerSecond));
                    if (budget.Remaining.HasValue) budget.Remaining--;
                    budget.InFlight++;
                    return new Reservation(this, route, budget);
                }
            }
            if (onWait != null && delay >= 100) await onWait(TimeSpan.FromMilliseconds(delay));
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, 1000)), token);
        }
    }

    public void Block(string route, TimeSpan retry, bool global)
    {
        lock (_sync)
        {
            var readyAt = Environment.TickCount64 + (long)Math.Ceiling(retry.TotalMilliseconds)
                + DiscordLimits.RateLimitSafetyMilliseconds;
            if (global) _globalReadyAt = Math.Max(_globalReadyAt, readyAt);
            else
            {
                var budget = GetBudget(route);
                budget.Remaining = 0;
                budget.ResetAt = Math.Max(budget.ResetAt, readyAt);
            }
        }
    }

    private Budget GetBudget(string route)
    {
        if (!_routes.TryGetValue(route, out var budget)) _routes[route] = budget = new();
        return Root(budget);
    }

    private static Budget Root(Budget budget)
    {
        while (budget.Parent != null) budget = budget.Parent;
        return budget;
    }

    private void Observe(Reservation reservation, HttpResponseMessage? response)
    {
        lock (_sync)
        {
            if (reservation.Completed) return;
            reservation.Completed = true;
            var budget = Root(reservation.Budget);
            if (response != null && response.Headers.TryGetValues("X-RateLimit-Bucket", out var values))
            {
                var major = reservation.Route.Split(' ', 2)[1].Split('/').Take(2);
                var key = string.Join('/', major) + ":" + values.First();
                if (_buckets.TryGetValue(key, out var shared) && Root(shared) != budget)
                {
                    shared = Root(shared);
                    shared.InFlight += budget.InFlight;
                    if (budget.Remaining.HasValue)
                        shared.Remaining = Math.Min(shared.Remaining ?? int.MaxValue, budget.Remaining.Value);
                    shared.ResetAt = Math.Max(shared.ResetAt, budget.ResetAt);
                    budget.Parent = shared;
                    budget = shared;
                }
                else _buckets[key] = budget;
                _routes[reservation.Route] = budget;
            }
            budget.InFlight--;
            if (response == null) return;
            if (ReadNumber(response, "X-RateLimit-Limit", out var limit)) budget.Limit = (int)limit;
            if (!ReadNumber(response, "X-RateLimit-Reset-After", out var reset) &&
                ReadNumber(response, "X-RateLimit-Reset", out var epoch))
                reset = epoch - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            if (reset > 0)
            {
                var now = Environment.TickCount64;
                if (budget.ResetAt <= now) budget.Remaining = null;
                budget.ResetAt = Math.Max(budget.ResetAt, now + (long)Math.Ceiling(reset * 1000)
                    + DiscordLimits.RateLimitSafetyMilliseconds);
            }
            if (ReadNumber(response, "X-RateLimit-Remaining", out var remaining))
                budget.Remaining = Math.Min(budget.Remaining ?? int.MaxValue,
                    Math.Max(0, (int)remaining - budget.InFlight));
            if (budget.Remaining == 0 && budget.ResetAt <= Environment.TickCount64)
                budget.ResetAt = Environment.TickCount64 + DiscordLimits.RateLimitFallbackMilliseconds;
        }
    }

    private static bool ReadNumber(HttpResponseMessage response, string header, out double value)
    {
        value = 0;
        return response.Headers.TryGetValues(header, out var values) && double.TryParse(
            values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    internal sealed class Budget
    {
        public Budget? Parent;
        public int? Limit;
        public int? Remaining;
        public int InFlight;
        public long ResetAt;
    }

    internal sealed class Reservation(DiscordRequestLimiter owner, string route, Budget budget) : IDisposable
    {
        internal string Route { get; } = route;
        internal Budget Budget { get; } = budget;
        internal bool Completed { get; set; }
        public void Observe(HttpResponseMessage response) => owner.Observe(this, response);
        public void Dispose() => owner.Observe(this, null);
    }
}
