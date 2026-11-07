namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private TaskCompletionSource<bool>? _closeConfirmationResponse;
    internal bool HasTransfersInProgress => !_transferOperations.IsEmpty;

    internal async Task<bool> ConfirmCloseAsync(bool continueInBackground)
    {
        if (!HasTransfersInProgress) return true;
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _closeConfirmationResponse, response, null) is { } previous)
            return await previous.Task;
        try
        {
            await _bridge.CallAsync("_onConfirmAppClose", continueInBackground);
            return await response.Task.WaitAsync(TimeSpan.FromMinutes(5));
        }
        catch (TimeoutException) { return false; }
        finally { Interlocked.CompareExchange(ref _closeConfirmationResponse, null, response); }
    }
}
