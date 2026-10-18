using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class TransferStateRepository
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public TransferStateRepository(DirectoryInfo rootDirectory)
    {
        RootDirectory = rootDirectory;
        ActiveDirectory = Directory.CreateDirectory(Path.Combine(rootDirectory.FullName, "active"));
        CompletedDirectory = Directory.CreateDirectory(Path.Combine(rootDirectory.FullName, "completed"));
        FailedDirectory = Directory.CreateDirectory(Path.Combine(rootDirectory.FullName, "failed"));
    }

    public DirectoryInfo RootDirectory { get; }
    public DirectoryInfo ActiveDirectory { get; }
    public DirectoryInfo CompletedDirectory { get; }
    public DirectoryInfo FailedDirectory { get; }

    public async Task SaveActiveAsync(TransferState state, CancellationToken cancellationToken = default)
    {
        state.Normalize();
        state.Touch();
        await SaveAsync(ActiveFile(state.TransferId), state, cancellationToken);
    }

    public async Task<IReadOnlyList<TransferState>> LoadActiveAsync(CancellationToken cancellationToken = default)
    {
        return await LoadFromDirectoryAsync(ActiveDirectory, cancellationToken);
    }

    public async Task<IReadOnlyList<TransferState>> LoadRecoverableAsync(CancellationToken cancellationToken = default)
    {
        var active = await LoadActiveAsync(cancellationToken);
        return active.Where(state => TransferStatuses.IsRecoverable(state.Status)).ToList();
    }

    public async Task<TransferState?> LoadActiveByIdAsync(string transferId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transferId))
        {
            return null;
        }

        var file = ActiveFile(transferId);
        return file.Exists ? await ReadStateAsync(file, cancellationToken) : null;
    }

    public async Task MoveToCompletedAsync(TransferState state, bool keepHistory, CancellationToken cancellationToken = default)
    {
        state.Status = TransferStatuses.Completed;
        state.Touch();
        await MoveOutOfActiveAsync(state, keepHistory ? CompletedFile(state.TransferId) : null, cancellationToken);
    }

    public async Task MoveToFailedAsync(TransferState state, string error, CancellationToken cancellationToken = default)
    {
        state.Status = TransferStatuses.Failed;
        state.LastError = error;
        state.RetryCount++;
        state.Touch();
        await MoveOutOfActiveAsync(state, FailedFile(state.TransferId), cancellationToken);
    }

    public async Task MoveToCancelledAsync(TransferState state, bool keepHistory, CancellationToken cancellationToken = default)
    {
        state.Status = TransferStatuses.Cancelled;
        state.Touch();
        await MoveOutOfActiveAsync(state, keepHistory ? FailedFile(state.TransferId) : null, cancellationToken);
    }

    public Task DeleteActiveAsync(string transferId)
    {
        TryDelete(ActiveFile(transferId));
        return Task.CompletedTask;
    }


    private async Task MoveOutOfActiveAsync(TransferState state, FileInfo? destination, CancellationToken cancellationToken)
    {
        var source = ActiveFile(state.TransferId);
        if (destination != null)
        {
            await SaveAsync(destination, state, cancellationToken);
        }

        TryDelete(source);
    }

    private async Task SaveAsync(FileInfo file, TransferState state, CancellationToken cancellationToken)
    {
        var sem = _locks.GetOrAdd(state.TransferId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken);
        try
        {
            file.Directory?.Create();
            var temp = new FileInfo(file.FullName + ".tmp");
            var json = JsonSerializer.Serialize(state, JsonOptions.WebIndented);
            await using (var stream = new FileStream(temp.FullName, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(json.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
                if (state.TransferType == TransferTypes.Download && state.DownloadCheckpointVersion == TransferState.DirectDownloadVersion)
                    stream.Flush(flushToDisk: true);
            }

            File.Move(temp.FullName, file.FullName, overwrite: true);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<IReadOnlyList<TransferState>> LoadFromDirectoryAsync(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        directory.Create();
        var list = new List<TransferState>();
        foreach (var file in directory.EnumerateFiles("*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await ReadStateAsync(file, cancellationToken);
            if (state != null)
            {
                list.Add(state);
            }
        }

        return list;
    }

    private static async Task<TransferState?> ReadStateAsync(FileInfo file, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            var state = await JsonSerializer.DeserializeAsync<TransferState>(stream, JsonOptions.Web, cancellationToken);
            state?.Normalize();
            return state;
        }
        catch (Exception ex)
        {
            AppLogger.Error("TransferState.ReadInvalid", ex, file.FullName);
            return null;
        }
    }

    private FileInfo ActiveFile(string transferId) => new(Path.Combine(ActiveDirectory.FullName, SafeId(transferId) + ".json"));
    private FileInfo CompletedFile(string transferId) => new(Path.Combine(CompletedDirectory.FullName, SafeId(transferId) + ".json"));
    private FileInfo FailedFile(string transferId) => new(Path.Combine(FailedDirectory.FullName, SafeId(transferId) + ".json"));

    private static string SafeId(string transferId)
    {
        var safe = string.Concat((string.IsNullOrWhiteSpace(transferId) ? Guid.NewGuid().ToString("N") : transferId)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            if (file.Exists)
            {
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("TransferState.Delete", ex, file.FullName);
        }
    }
}
