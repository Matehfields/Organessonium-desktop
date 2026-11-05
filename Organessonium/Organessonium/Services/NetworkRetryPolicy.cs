using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.IO;

namespace Organessonium.Services;

internal static class NetworkRetryPolicy
{
    private static readonly AsyncLocal<int> TransferDepth = new();
    private static readonly AsyncLocal<CancellationToken> TransferCancellation = new();
    private static readonly AsyncLocal<Action?> NetworkWait = new();
    internal static bool WaitingForTransferConnection => TransferDepth.Value > 0 && !TransferCancellation.Value.IsCancellationRequested;
    internal static IDisposable TransferScope(CancellationToken cancellationToken = default, Action? onNetworkWait = null)
    {
        var previous = TransferCancellation.Value;
        var previousWait = NetworkWait.Value;
        if (onNetworkWait != null) NetworkWait.Value = onNetworkWait;
        if (cancellationToken.CanBeCanceled) TransferCancellation.Value = cancellationToken;
        TransferDepth.Value++;
        return new TransferWaitScope(previous, previousWait);
    }
    private sealed class TransferWaitScope(CancellationToken previous, Action? previousWait) : IDisposable
    {
        public void Dispose()
        {
            TransferDepth.Value = Math.Max(0, TransferDepth.Value - 1);
            TransferCancellation.Value = previous;
            NetworkWait.Value = previousWait;
        }
    }
    public static bool IsTransient(Exception exception)
    {
        if (TransferMemoryPressureException.Find(exception) != null) return false;
        if (exception is AggregateException aggregate)
            return aggregate.InnerExceptions.Count > 0 && aggregate.Flatten().InnerExceptions.All(IsTransient);
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is TimeoutException or OperationCanceledException) return true;
            if (current is SocketException || current is HttpRequestException http &&
                (http.StatusCode == null || IsTransientStatus(http.StatusCode)))
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
        ReportNetworkWait();
        var exponent = Math.Min(Math.Max(0, attempt - 1), 5);
        var delayMs = Math.Min(15000, 500 * Math.Pow(2, exponent));
        return Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
    }

    internal static void ReportNetworkWait()
    {
        if (!TransferCancellation.Value.IsCancellationRequested) NetworkWait.Value?.Invoke();
    }

    internal static async Task<HttpResponseMessage> SendDownloadAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("A conexão não respondeu durante o download.", error); }
    }

    internal static async ValueTask<int> ReadDownloadAsync(Stream input, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("A conexão deixou de receber dados durante o download.", error); }
    }
}
