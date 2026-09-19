using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Organessonium.Services;

internal static class NetworkRetryPolicy
{
    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
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
