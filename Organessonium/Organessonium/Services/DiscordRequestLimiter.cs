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
    private long _sequence;

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
            int pollMilliseconds;
            lock (_sync)
            {
                var now = Environment.TickCount64;
                var budget = GetBudget(route);
                if (budget.ResetAt > 0 && budget.ResetAt <= now)
                {
                    budget.Remaining = budget.Limit;
                    budget.ResetAt = 0;
                }
                var readyAt = Math.Max(Math.Max(_nextRequestAt, _globalReadyAt), budget.BlockedUntil);
                var available = budget.Remaining.HasValue ? Math.Max(0, budget.Remaining.Value - budget.InFlight) : (int?)null;
                if (available == 0)
                    readyAt = Math.Max(readyAt, budget.ResetAt > now ? budget.ResetAt : now + 10);
                if (budget.PendingRetries > 0) readyAt = Math.Max(readyAt, now + 10);
                delay = readyAt - now;
                pollMilliseconds = budget.InFlight > 0 || budget.PendingRetries > 0
                    ? DiscordLimits.RateLimitSafetyMilliseconds : 1000;
                if (delay <= 0)
                {
                    _nextRequestAt = now + (long)Math.Ceiling(1000.0 / Math.Min(
                        DiscordLimits.TargetRequestsPerSecond, DiscordLimits.ApiRequestsPerSecond));
                    var soleRequest = budget.InFlight == 0 && budget.Remaining.HasValue;
                    budget.InFlight++;
                    return new Reservation(this, route, budget, ++_sequence, soleRequest);
                }
            }
            if (onWait != null && delay >= 100) await onWait(TimeSpan.FromMilliseconds(delay));
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(delay, pollMilliseconds)), token);
        }
    }

    public void Block(Reservation reservation, TimeSpan retry, bool global)
    {
        lock (_sync)
        {
            var readyAt = Environment.TickCount64 + (long)Math.Ceiling(retry.TotalMilliseconds)
                + DiscordLimits.RateLimitSafetyMilliseconds;
            var budget = Root(reservation.Budget);
            FinishPendingRetry(reservation, budget);
            if (global) _globalReadyAt = Math.Max(_globalReadyAt, readyAt);
            else
            {
                budget.BlockedUntil = Math.Max(budget.BlockedUntil, readyAt);
                budget.Remaining = 0;
                budget.ResetAt = Math.Max(budget.ResetAt, readyAt);
            }
        }
    }

    private static void FinishPendingRetry(Reservation reservation, Budget budget)
    {
        if (!reservation.PendingRetry) return;
        reservation.PendingRetry = false;
        budget.PendingRetries--;
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
            if (reservation.Completed)
            {
                if (reservation.PendingRetry && response == null)
                {
                    var pendingBudget = Root(reservation.Budget);
                    FinishPendingRetry(reservation, pendingBudget);
                    pendingBudget.BlockedUntil = Math.Max(pendingBudget.BlockedUntil,
                        Environment.TickCount64 + DiscordLimits.RateLimitFallbackMilliseconds);
                }
                return;
            }
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
                    shared.PendingRetries += budget.PendingRetries;
                    if (budget.Remaining.HasValue)
                        shared.Remaining = Math.Min(shared.Remaining ?? int.MaxValue, budget.Remaining.Value);
                    shared.ResetAt = Math.Max(shared.ResetAt, budget.ResetAt);
                    shared.BlockedUntil = Math.Max(shared.BlockedUntil, budget.BlockedUntil);
                    shared.LatestSequence = Math.Max(shared.LatestSequence, budget.LatestSequence);
                    budget.Parent = shared;
                    budget = shared;
                }
                else _buckets[key] = budget;
                _routes[reservation.Route] = budget;
            }
            budget.InFlight--;
            var now = Environment.TickCount64;
            if (response == null)
            {
                // No response cannot prove that the server refunded this request.
                if (budget.Remaining.HasValue) budget.Remaining = Math.Max(0, budget.Remaining.Value - 1);
            }
            else
            {
                var limited = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests;
                if (limited)
                {
                    reservation.PendingRetry = true;
                    budget.PendingRetries++;
                }
                var hasEpoch = ReadNumber(response, "X-RateLimit-Reset", out var epoch);
                var hasReset = ReadNumber(response, "X-RateLimit-Reset-After", out var reset);
                if (!hasReset && hasEpoch) reset = epoch - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                var deadline = reset > 0 ? now + (long)Math.Ceiling(reset * 1000) : 0;
                var date = response.Headers.Date;
                var sequential = reservation.SoleRequest && budget.InFlight == 0 && reservation.Sequence >= budget.LatestSequence;
                var newerDate = date.HasValue && (!budget.HeaderDate.HasValue || date.Value > budget.HeaderDate.Value);
                var older = date.HasValue && budget.HeaderDate.HasValue && date.Value < budget.HeaderDate.Value;
                if (!newerDate && !sequential && hasEpoch && budget.ResetEpoch.HasValue && epoch < budget.ResetEpoch.Value) older = true;
                if (!older)
                {
                    var fresh = budget.Remaining == null || sequential || newerDate ||
                        (hasEpoch && (!budget.ResetEpoch.HasValue || epoch > budget.ResetEpoch.Value)) ||
                        (!hasEpoch && deadline > budget.HeaderResetAt + DiscordLimits.RateLimitSafetyMilliseconds);
                    if (ReadNumber(response, "X-RateLimit-Limit", out var limit)) budget.Limit = (int)limit;
                    if (ReadNumber(response, "X-RateLimit-Remaining", out var remaining))
                    {
                        // Store the server snapshot. In-flight reservations are discounted only during admission.
                        var reported = Math.Max(0, (int)remaining);
                        budget.Remaining = fresh ? reported : Math.Min(budget.Remaining ?? int.MaxValue, reported);
                    }
                    else if (budget.Remaining.HasValue) budget.Remaining = Math.Max(0, budget.Remaining.Value - 1);
                    if (deadline > 0)
                    {
                        budget.ResetAt = fresh || budget.ResetAt <= now ? deadline + DiscordLimits.RateLimitSafetyMilliseconds :
                            Math.Min(budget.ResetAt, deadline + DiscordLimits.RateLimitSafetyMilliseconds);
                        budget.HeaderResetAt = fresh ? deadline : Math.Min(budget.HeaderResetAt, deadline);
                    }
                    if (hasEpoch) budget.ResetEpoch = epoch;
                    if (date.HasValue) budget.HeaderDate = date;
                    budget.LatestSequence = Math.Max(budget.LatestSequence, reservation.Sequence);
                }
                if (limited) budget.Remaining = 0;
            }
            if (budget.Remaining == 0 && budget.ResetAt <= now)
                budget.ResetAt = now + DiscordLimits.RateLimitFallbackMilliseconds;
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
        public long BlockedUntil;
        public int PendingRetries;
        public long HeaderResetAt;
        public double? ResetEpoch;
        public DateTimeOffset? HeaderDate;
        public long LatestSequence;
    }

    internal sealed class Reservation(DiscordRequestLimiter owner, string route, Budget budget, long sequence, bool soleRequest) : IDisposable
    {
        internal string Route { get; } = route;
        internal Budget Budget { get; } = budget;
        internal bool Completed { get; set; }
        internal bool PendingRetry { get; set; }
        internal long Sequence { get; } = sequence;
        internal bool SoleRequest { get; } = soleRequest;
        public void Observe(HttpResponseMessage response) => owner.Observe(this, response);
        public void Dispose() => owner.Observe(this, null);
    }
}
