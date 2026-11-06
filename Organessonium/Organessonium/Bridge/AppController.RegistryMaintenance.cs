using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private readonly object _registryMaintenanceSync = new();
    private RegistryMaintenance? _registryMaintenance;

    private sealed record RegistryMaintenance(AppRegistry Registry, SemaphoreSlim Wake);

    private void QueueRegistryMaintenance(AppRegistry registry)
    {
        RegistryMaintenance state;
        lock (_registryMaintenanceSync)
        {
            if (_registryMaintenance?.Registry == registry)
            {
                try { _registryMaintenance.Wake.Release(); } catch (SemaphoreFullException) { }
                return;
            }
            state = _registryMaintenance = new RegistryMaintenance(registry, new SemaphoreSlim(0, 1));
        }
        var api = RequireApi();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!api.PayloadLifetime.IsCancellationRequested)
                {
                    try
                    {
                        await registry.SharedStorage.RunBackgroundAsync(async token =>
                        {
                            await Task.WhenAll(registry.SharedStorage.WarmReadIndexAsync(token), registry.RefreshServerIndexAsync(false, token, async (id, entries) =>
                            {
                                if (_registry != registry || _loggingOut) return;
                                ReplaceLoadedFolder(id, entries);
                                await _bridge.CallAsync("_onFolderIndexReconciled", id, entries);
                            }));
                            if (_registry == registry && !_loggingOut) await PublishLibraryFileCountAsync(registry);
                            await registry.RunJanitorAsync(token);
                        }, api.PayloadLifetime);
                    }
                    catch (OperationCanceledException) when (api.PayloadLifetime.IsCancellationRequested) { break; }
                    catch (Exception error) { AppLogger.Error("Registry.BackgroundMaintenance", error); }
                    registry.SharedStorage.ResumePendingCleanup();
                    await state.Wake.WaitAsync(TimeSpan.FromMinutes(5), api.PayloadLifetime);
                }
            }
            catch (OperationCanceledException) when (api.PayloadLifetime.IsCancellationRequested) { }
        });
    }
}
