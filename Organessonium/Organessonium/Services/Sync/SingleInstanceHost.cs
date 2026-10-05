using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Organessonium.Services.Sync;

public sealed class SingleInstanceHost : IAsyncDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _listener;
    public bool IsPrimary { get; }
    public event Action? OpenRequested;

    public SingleInstanceHost(string? scope = null)
    {
        var user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        _pipeName = "Organessonium-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + scope)))[..20];
        _mutex = new Mutex(true, "Local\\" + _pipeName, out var created);
        IsPrimary = created;
    }

    public async Task NotifyPrimaryAsync(bool open)
    {
        if (!open) return;
        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await client.WriteAsync("open\n"u8.ToArray());
        await client.FlushAsync();
    }

    public void Listen() => _listener = Task.Run(async () =>
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_lifetime.Token);
                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(_lifetime.Token);
                if (command == "open") OpenRequested?.Invoke();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
            catch (IOException) { }
        }
    });

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_listener != null) await _listener;
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
        _lifetime.Dispose();
    }
}
