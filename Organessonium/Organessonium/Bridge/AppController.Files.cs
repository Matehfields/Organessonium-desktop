using System.IO;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.Bridge;

public sealed partial class AppController
{
    private async Task<AppEntry> FindDetailedFileAsync(string metadataMessageId, CancellationToken token = default)
    {
        var entry = LoadedEntriesForLookup().FirstOrDefault(item => item.MetadataMessageId == metadataMessageId)
            ?? throw new InvalidOperationException("O arquivo não está disponível na lista atual.");
        var detailed = await Registry().LoadFileDetailsAsync(entry, token, forceRefresh: true);
        if (detailed.Type == "file" && detailed.LogicalChunkCount == 0)
            throw new InvalidDataException("Não foi possível carregar os metadados completos do arquivo.");
        return detailed;
    }

    private async Task SendFileInformationAsync(string metadataMessageId, string requestId)
    {
        try
        {
            var entry = await FindDetailedFileAsync(metadataMessageId);
            await _bridge.CallAsync("_onFileInformation", requestId, new
            {
                entry, storageBytes = FileVersioning.StorageBytes(entry), chunkCount = entry.LogicalChunkCount
            });
        }
        catch (Exception ex)
        {
            await _bridge.CallAsync("_onFileInformationError", requestId, ex.Message);
        }
    }

    private async Task<AppEntry> PublishFileVersionAsync(AppRegistry registry, FileReplacement replacement,
        AppEntry next, CancellationToken token = default, Func<TimeSpan, Task>? onWait = null)
    {
        var previous = FileVersioning.ReadBase(replacement, new MetadataService());
        var authoritative = await registry.LoadFileDetailsAsync(previous, token, forceRefresh: true);
        // An acknowledged metadata update may have been followed by a failed serial/checkpoint write.
        // Retrying that transfer must not archive the same payload a second time.
        if (authoritative.VersionId == next.VersionId) return authoritative;
        if (authoritative.VersionId != previous.VersionId || authoritative.BatchId != previous.BatchId ||
            authoritative.LogicalChunkCount != previous.LogicalChunkCount)
            throw new IOException("O arquivo recebeu outra versão durante o envio. As versões existentes foram preservadas.");
        FileVersioning.PreservePrevious(authoritative, next, next.VersionUploadedAt);
        await registry.UpdateEntryAsync(next, token, onWait);
        ClosePreview(next.ThreadId);
        ThumbnailCache.Remove(next.ThreadId);
        _store.RemoveFilePreviewCache(next.ThreadId);
        await _bridge.CallAsync("_onFileVersionPublished", next.ThreadId);
        return next;
    }

    private async Task StartVersionDownloadAsync(string metadataMessageId, string versionId)
    {
        var current = await FindDetailedFileAsync(metadataMessageId);
        var version = current.Versions.FirstOrDefault(version => version.VersionId == versionId)
            ?? throw new InvalidOperationException("A versão selecionada não está disponível.");
        var entry = new MetadataService().ToAppEntry(new MetadataEnvelope(metadataMessageId, version.Payload))
            ?? throw new InvalidDataException("Metadados da versão inválidos.");
        entry.Path = current.Path;
        await StartDownloadAsync(entry.ThreadId, entry.Name, selectedVersion: entry);
    }

    private async Task CopyFileAsync(string metadataMessageId)
    {
        var original = await FindDetailedFileAsync(metadataMessageId);
        if (original.Type != "file") throw new InvalidOperationException("Fazer cópia está disponível para arquivos.");
        var opId = NewId();
        var token = BeginTransferOperation(opId);
        var directory = Directory.CreateTempSubdirectory("organessonium_copy_");
        try
        {
            await OpsAddAsync(opId, "upload", "Cópia de " + original.Name);
            var downloaded = await Task.Run(() => Downloader().DownloadAndAssembleAsync(original, directory,
                message => _ = OpsUpdateAsync(opId, "active", message), token), token);
            var entries = CurrentLocalEntries();
            var extension = Path.GetExtension(original.Name);
            var stem = Path.GetFileNameWithoutExtension(original.Name);
            var copyName = "Cópia de " + stem + extension;
            for (var number = 2; entries.Any(item => item.Path == original.Path && item.Name.Equals(copyName, StringComparison.OrdinalIgnoreCase)); number++)
                copyName = $"Cópia de {stem} ({number}){extension}";
            var source = new FileInfo(Path.Combine(directory.FullName, PathRules.CleanFileName(copyName)));
            await Task.Run(() => File.Move(downloaded.FullName, source.FullName), token);
            var settings = _store.Load();
            var uploader = new ForumUploader(RequireApi(), settings.AppsForumId,
                Organessonium.Services.MetadataEncryption.MetadataCodec.ForEncryption(_encryption, () => _store.Load().EncryptionMode));
            var registry = Registry();
            var copy = await Task.Run(() => UploadSingleAsync(opId, 0, 1, source, original.Path, entries, uploader,
                registry, settings, new UiThrottle(), token), token);
            ReplaceLocalEntry(entries, copy);
            await PublishLocalRegistryAsync(registry, entries);
            await OpsUpdateAsync(opId, "done", "Cópia criada");
        }
        catch (OperationCanceledException) { await OpsUpdateAsync(opId, "cancelled", "Cancelado"); }
        catch (Exception ex) { await OpsUpdateAsync(opId, "error", ex.Message); }
        finally
        {
            EndTransferOperation(opId);
            CleanDir(directory);
        }
    }
}
