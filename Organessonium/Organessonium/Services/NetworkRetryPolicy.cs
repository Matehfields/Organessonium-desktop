using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Organessonium.Services;

internal static class NetworkRetryPolicy
{
    private static readonly AsyncLocal<int> TransferDepth = new();
    private static readonly AsyncLocal<CancellationToken> TransferCancellation = new();
    internal static bool WaitingForTransferConnection => TransferDepth.Value > 0 && !TransferCancellation.Value.IsCancellationRequested;
    internal static IDisposable TransferScope(CancellationToken cancellationToken = default)
    {
        var previous = TransferCancellation.Value;
        if (cancellationToken.CanBeCanceled) TransferCancellation.Value = cancellationToken;
        TransferDepth.Value++;
        return new TransferWaitScope(previous);
    }
    private sealed class TransferWaitScope(CancellationToken previous) : IDisposable
    {
        public void Dispose()
        {
            TransferDepth.Value = Math.Max(0, TransferDepth.Value - 1);
            TransferCancellation.Value = previous;
        }
    }
    public static bool IsTransient(Exception exception)
    {
        if (TransferMemoryPressureException.Find(exception) != null) return false;
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TimeoutException or OperationCanceledException) return true;
            if (current is SocketException || current is HttpRequestException http && IsTransientStatus(http.StatusCode))
            {
                return true;
            }

            if (string.Equals(current.GetType().Name, "HttpIOException", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsTransientStatus(HttpStatusCode? statusCode)
    {
        if (!statusCode.HasValue)
        {
            return false;
        }

        var status = (int)statusCode.Value;
        return status is 408 or 429 or 500 or 502 or 503 or 504 or 520;
    }

    public static Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        var exponent = Math.Min(Math.Max(0, attempt - 1), 5);
        var delayMs = Math.Min(15000, 500 * Math.Pow(2, exponent));
        return Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
    }
}
