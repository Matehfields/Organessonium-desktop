using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

public sealed class VerifiedPayloadSizeMismatchException : IOException
{
    public VerifiedPayloadSizeMismatchException(FileInfo recoveredFile, long expectedLength, long actualLength)
        : base("A integridade criptográfica foi validada, mas o tamanho recuperado não corresponde aos metadados do arquivo.")
    {
        RecoveredFile = recoveredFile;
        ExpectedLength = expectedLength;
        ActualLength = actualLength;
    }

    public FileInfo RecoveredFile { get; }
    public long ExpectedLength { get; }
    public long ActualLength { get; }
}

public sealed class EncryptedPayloadIntegrityException : IOException
{
    public EncryptedPayloadIntegrityException(string encryptionMode, Exception? innerException = null)
        : base(
            EncryptionModes.Normalize(encryptionMode) == EncryptionModes.Custom
                ? "Não foi possível autenticar este arquivo. Verifique se a chave de criptografia correta está configurada."
                : "Não foi possível verificar a integridade deste arquivo.",
            innerException)
    {
        EncryptionMode = EncryptionModes.Normalize(encryptionMode);
    }

    public string EncryptionMode { get; }
}

public sealed partial class ForumDownloader
{
    private const int DownloadBufferSize = 1024 * 1024;
    private const int MaxChunkDownloadAttempts = 8;
    private const int MaxEncryptedPayloadDownloadAttempts = 3;
    private const string PreviewPrefix = "ORG_PREVIEW|";
    private const string DartPreviewPrefix = "PREVIEW|";
    private const string DartVideoPreviewPrefix = "VIDEO_PREVIEW_480P|";
    private readonly DiscordApiClient _discord;
    private readonly AppSettings _settings;
    private readonly EncryptionService _encryption;
    private readonly Func<HttpClient> _downloadClientFactory;
    private readonly MetadataService _metadata;
    private readonly StorageReadContext? _storageReads;

    public ForumDownloader(DiscordApiClient discord, AppSettings settings, EncryptionService encryption, Func<HttpClient>? downloadClientFactory = null,
        StorageReadContext? storageReads = null)
    {
        _discord = discord;
        _settings = settings;
        _encryption = encryption;
        _metadata = new MetadataService(MetadataCodec.ForEncryption(encryption));
        _downloadClientFactory = downloadClientFactory ?? (() => NetworkConfig.Current.CreateClient());
        _storageReads = storageReads;
    }

    private DiscordMessageDto DecodeReference(DiscordMessageDto message)
    {
        message.Content = _metadata.DecodePayloadReference(message.Content);
        return message;
    }

    private async Task<IReadOnlyList<DiscordMessageDto>> GetPayloadMessagesAsync(string threadId, int? limit,
        CancellationToken cancellationToken, bool waitForTransientNetwork = false)
    {
        var messages = await _discord.GetMessagesAsync(threadId, limit, cancellationToken, waitForTransientNetwork: waitForTransientNetwork);
        foreach (var message in messages) DecodeReference(message);
        return messages;
    }

    private async Task<DiscordMessageDto> GetPayloadMessageAsync(string threadId, string messageId,
        CancellationToken cancellationToken, bool waitForTransientNetwork = false) =>
        DecodeReference(await _discord.ResolvePayloadMessageAsync(threadId, messageId, cancellationToken));

    public async Task<FileInfo> DownloadAndAssembleAsync(
        string threadId,
        DirectoryInfo destDir,
        Action<string> onProgress,
        CancellationToken cancellationToken = default,
        Action<DownloadProgress>? onDetailedProgress = null)
        => await Task.Run(() => DownloadThreadCoreAsync(threadId, destDir, onProgress,
            cancellationToken, onDetailedProgress), cancellationToken).ConfigureAwait(false);

    private async Task<FileInfo> DownloadThreadCoreAsync(string threadId, DirectoryInfo destDir,
        Action<string> onProgress, CancellationToken cancellationToken, Action<DownloadProgress>? onDetailedProgress)
    {
        onProgress("Recolhendo URLs dos anexos...");
        var parts = await CollectPartsAsync(threadId, cancellationToken);
        if (parts.Count == 0) throw new InvalidOperationException("Nenhum anexo encontrado na thread.");
        var firstName = parts[0].Name;
        var name = Regex.IsMatch(firstName, @".*\.\d{3}$") ? Regex.Replace(firstName, @"\.\d{3}$", "") : firstName;
        return await DownloadDirectAsync(parts, destDir, name, onProgress, cancellationToken, onDetailedProgress);
    }
    public async Task<FileInfo> DownloadAndAssembleAsync(
        AppEntry entry,
        DirectoryInfo destDir,
        Action<string> onProgress,
        CancellationToken cancellationToken = default,
        Action<DownloadProgress>? onDetailedProgress = null)
        => await DownloadWithReadLeaseAsync(entry, null, () => Task.Run(() => DownloadEntryCoreAsync(entry, destDir, onProgress,
            cancellationToken, onDetailedProgress), cancellationToken), cancellationToken).ConfigureAwait(false);

    private async Task<FileInfo> DownloadEntryCoreAsync(AppEntry entry, DirectoryInfo destDir,
        Action<string> onProgress, CancellationToken cancellationToken, Action<DownloadProgress>? onDetailedProgress)
    {
        entry.Checksums?.Validate(entry.SizeBytes, entry.LogicalChunkCount);
        if (entry.ChunkReferences != null) ChunkReference.Validate(entry.ChunkReferences, entry.ChunkMessageIds);
        try
        {
            if (entry.LogicalChunkCount == 0)
            {
                if (entry.Encryption != null)
                {
                    throw new InvalidOperationException("O metadata criptografado não contém referências aos payloads do arquivo.");
                }

                return await DownloadAndAssembleAsync(entry.ThreadId, destDir, onProgress, cancellationToken, onDetailedProgress);
            }

            return await DownloadMessagePartsAsync(
                entry.ThreadId,
                entry.ChunkMessageIds,
                entry.Name,
                destDir,
                onProgress,
                cancellationToken,
                onDetailedProgress,
                EncryptionContext(entry, EncryptedPayloadRoles.Original),
                entry.SizeBytes,
                entry.ChunkReferences, entry.Checksums);
        }
        catch (Exception ex) when (CanRecoverByBatch(entry, ex))
        {
            AppLogger.Error("Download.MetadataChunksFailed", ex, entry.ThreadId + " | " + entry.BatchId + " | " + entry.Name);
            return await DownloadBatchAndAssembleAsync(entry, destDir, onProgress, cancellationToken, onDetailedProgress);
        }
    }

    private async Task<FileInfo> DownloadBatchAndAssembleAsync(AppEntry entry, DirectoryInfo destDir,
        Action<string> onProgress, CancellationToken cancellationToken, Action<DownloadProgress>? onDetailedProgress = null)
    {
        onProgress("Recuperando partes pelo batch do metadata...");
        var parts = await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId);
        if (parts.Count == 0) throw new InvalidOperationException("Não foi possível recuperar os chunks pelo batchId do metadata.");
        return await DownloadDirectAsync(parts, destDir, string.IsNullOrWhiteSpace(entry.Name) ? parts[0].Name : entry.Name,
            onProgress, cancellationToken, onDetailedProgress);
    }
    private static bool CanRecoverByBatch(AppEntry entry, Exception ex)
    {
        return ex is not OperationCanceledException &&
               entry.Encryption == null &&
               entry.Checksums == null &&
               entry.ChunkReferences == null &&
               !string.IsNullOrWhiteSpace(entry.BatchId);
    }
    public async Task<FileInfo> DownloadAndAssembleAsync(
        AppEntry entry,
        DirectoryInfo destDir,
        TransferState state,
        TransferManager transferManager,
        Action<string> onProgress,
        CancellationToken cancellationToken = default,
        Action<DownloadProgress>? onDetailedProgress = null)
        => await DownloadWithReadLeaseAsync(entry, state, () => Task.Run(() => DownloadResumableCoreAsync(entry, destDir, state, transferManager, onProgress,
            cancellationToken, onDetailedProgress), cancellationToken), cancellationToken).ConfigureAwait(false);

    private async Task<FileInfo> DownloadResumableCoreAsync(
        AppEntry entry, DirectoryInfo destDir, TransferState state, TransferManager transferManager,
        Action<string> onProgress, CancellationToken cancellationToken, Action<DownloadProgress>? onDetailedProgress)
    {
        entry.Checksums?.Validate(entry.SizeBytes, entry.LogicalChunkCount);
        if (entry.ChunkReferences != null) ChunkReference.Validate(entry.ChunkReferences, entry.ChunkMessageIds);
        if (entry.Encryption != null && entry.LogicalChunkCount == 0)
            throw new InvalidDataException("O metadata criptografado não contém referências aos chunks.");
        var finalized = await RecoverFinalizedDownloadAsync(state, entry, onProgress, onDetailedProgress, cancellationToken);
        if (finalized != null) return finalized;
        await using var discovery = entry.LogicalChunkCount == 0 ? null : new PartDiscovery(this, entry.ThreadId,
            entry.ChunkMessageIds, entry.BatchId, entry.ChunkReferences, cancellationToken);
        var useDiscovery = discovery != null;
        List<Part> parts;
        try
        {
            parts = entry.LogicalChunkCount == 0
                ? await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId)
                : (await discovery!.NextAsync(cancellationToken))?.ToList() ?? [];
        }
        catch (Exception error) when (CanRecoverByBatch(entry, error))
        {
            useDiscovery = false;
            parts = await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId);
        }
        if (parts.Count == 0) throw new InvalidDataException("O arquivo não contém chunks para baixar.");
        var identity = TransferManager.DownloadIdentity(entry);
        if (state.DownloadCheckpointVersion != 0 && state.DownloadIdentity != identity)
            throw new InvalidDataException("O checkpoint pertence a outra versão do arquivo.");
        state.ThreadId = entry.ThreadId; state.FileId = entry.FileId; state.SizeBytes = entry.SizeBytes;
        state.MimeType = entry.MimeType; state.Encryption = entry.Encryption?.Clone();
        state.DownloadMetadata ??= MetadataService.CreateFileMetadata(entry);
        state.Checksums = entry.Checksums;
        state.PayloadLayoutVersion = entry.Storage?.Shared == true ? TransferPayloadLayouts.SharedStorage :
            entry.ChunkReferences == null ? TransferPayloadLayouts.SingleAttachment : TransferPayloadLayouts.MultiAttachment;
        try
        {
            return await DownloadDirectAsync(parts, destDir, string.IsNullOrWhiteSpace(entry.Name) ? parts[0].Name : entry.Name,
                onProgress, cancellationToken, onDetailedProgress,
                EncryptionContext(entry, EncryptedPayloadRoles.Original)?.WithChunkCount(useDiscovery ? discovery!.References.Count : parts.Count),
                entry.SizeBytes, entry.Checksums, state, transferManager, identity, useDiscovery ? discovery : null);
        }
        catch (PartDiscoveryException error) when (CanRecoverByBatch(entry, error))
        {
            var recovered = await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId);
            // A legacy batch recovery can change the locator list. Revalidate every region instead of trusting the old layout.
            state.DownloadCheckpointVersion = 0;
            return await DownloadDirectAsync(recovered, destDir, entry.Name, onProgress, cancellationToken,
                onDetailedProgress, expectedLength: entry.SizeBytes, checkpoint: state, manager: transferManager, identity: identity);
        }
    }
    public async Task<FileInfo?> DownloadVideoPreview480Async(
        AppEntry entry,
        DirectoryInfo destDir,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
        => await Task.Run(() => DownloadVideoPreviewCoreAsync(entry, destDir, onProgress,
            cancellationToken), cancellationToken).ConfigureAwait(false);

    private async Task<FileInfo?> DownloadVideoPreviewCoreAsync(AppEntry entry, DirectoryInfo destDir,
        Action<DownloadProgress>? onProgress, CancellationToken cancellationToken)
    {
        if (entry.VideoPreviewChunkReferences == null && entry.VideoPreviewChunkMessageIds.Count == 0)
        {
            return await DownloadPreviewAndAssembleAsync(entry.ThreadId, "video-480", destDir, onProgress, cancellationToken);
        }

        return await DownloadMessagePartsAsync(
            entry.ThreadId,
            entry.VideoPreviewChunkMessageIds,
            "preview_480p_" + entry.Name,
            destDir,
            _ => { },
            cancellationToken,
            onProgress,
            EncryptionContext(entry, EncryptedPayloadRoles.Video480p),
            entry.VideoPreviewSizeBytes,
            entry.VideoPreviewChunkReferences);
    }

    public async Task<FileInfo?> DownloadPreviewAndAssembleAsync(
        string threadId,
        string kind,
        DirectoryInfo destDir,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
        => await Task.Run(() => DownloadPreviewCoreAsync(threadId, kind, destDir, onProgress,
            cancellationToken), cancellationToken).ConfigureAwait(false);

    private async Task<FileInfo?> DownloadPreviewCoreAsync(string threadId, string kind, DirectoryInfo destDir,
        Action<DownloadProgress>? onProgress, CancellationToken cancellationToken)
    {
        var parts = await CollectPreviewPartsAsync(threadId, kind, cancellationToken);
        if (parts.Count == 0) return null;
        var firstName = parts[0].Name;
        var name = Regex.IsMatch(firstName, @".*\.\d{3}$") ? Regex.Replace(firstName, @"\.\d{3}$", "") : firstName;
        return await DownloadDirectAsync(parts, destDir, name, _ => { }, cancellationToken, onProgress);
    }
    public async Task<byte[]?> DownloadPreviewBytesAsync(
        string threadId,
        string kind,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var parts = await CollectPreviewPartsAsync(threadId, kind, cancellationToken);
        if (parts.Count == 0)
        {
            return null;
        }

        var total = parts.Sum(part => Math.Max(0, part.Size));
        var downloadedBytes = 0L;
        var buffers = new ConcurrentDictionary<int, byte[]>();
        var parallelism = Math.Min(Math.Clamp(_settings.DownloadThreads, 1, 16), parts.Count);
        using var semaphore = new SemaphoreSlim(parallelism);
        using var http = _downloadClientFactory();

        var tasks = parts.Select(async part =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                buffers[part.Index] = await DownloadPreviewPartBytesAsync(http, part, cancellationToken);
                var current = Interlocked.Add(ref downloadedBytes, buffers[part.Index].LongLength);
                var percent = total <= 0
                    ? 0
                    : Math.Clamp((int)Math.Round(current * 100.0 / total), 0, 100);
                onProgress?.Invoke(new DownloadProgress(percent, part.Name, current, total));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        using var output = new MemoryStream(total > 0 && total <= int.MaxValue ? (int)total : 0);
        foreach (var part in parts.OrderBy(part => part.Index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteAsync(buffers[part.Index], cancellationToken);
        }

        onProgress?.Invoke(new DownloadProgress(100, "ConcluÃ­do", downloadedBytes, total));
        return output.ToArray();
    }

    private static async Task<byte[]> DownloadPreviewPartBytesAsync(
        HttpClient http,
        Part part,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(part.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (part.Size > 0 && bytes.LongLength != part.Size)
                {
                    throw new IOException($"PrÃ©via incompleta. Esperado {part.Size} bytes, recebido {bytes.LongLength} bytes.");
                }

                return bytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 4)
            {
                last = ex;
                AppLogger.Error("Preview.PartRetry", ex, $"{part.Index}:{part.Name}:attempt {attempt}");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                break;
            }
        }

        throw new IOException("NÃ£o foi possÃ­vel baixar a parte da prÃ©via " + (part.Index + 1) + ".", last);
    }
    public async Task<byte[]?> DownloadMarkedPreviewBytesAsync(
        string threadId,
        string contentPrefix,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var attachment = await FindMarkedPreviewAttachmentAsync(threadId, contentPrefix, cancellationToken);
        if (attachment == null)
        {
            return null;
        }

        var total = Math.Max(0, attachment.Size);
        using var http = _downloadClientFactory();
        using var response = await http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(total > 0 && total <= int.MaxValue ? (int)total : 0);
        var downloaded = await CopyWithProgressAsync(input, output, 0, total, attachment.FileName, onProgress, cancellationToken);
        onProgress?.Invoke(new DownloadProgress(100, "ConcluÃ­do", downloaded, total));
        return output.ToArray();
    }

    public async Task<FileInfo?> DownloadMarkedPreviewFileAsync(
        string threadId,
        string contentPrefix,
        DirectoryInfo destDir,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var attachment = await FindMarkedPreviewAttachmentAsync(threadId, contentPrefix, cancellationToken);
        if (attachment == null)
        {
            return null;
        }

        return await DownloadAttachmentToFileAsync(attachment, destDir, onProgress, cancellationToken);
    }

    public async Task<byte[]?> DownloadMessageAttachmentBytesAsync(
        string threadId,
        string messageId,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default,
        EncryptedPayloadContext? encryption = null,
        string? attachmentId = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        var message = await GetPayloadMessageAsync(threadId, messageId, cancellationToken, waitForTransientNetwork: true);
        if (string.IsNullOrEmpty(attachmentId) && message.Attachments.Count > 1)
            throw new InvalidDataException("A miniatura exige attachmentId nessa mensagem compartilhada.");
        var attachment = string.IsNullOrEmpty(attachmentId) ? message.Attachments.FirstOrDefault() :
            message.Attachments.SingleOrDefault(attachment => attachment.Id == attachmentId)
                ?? throw new InvalidDataException("A miniatura referenciada não está presente na mensagem.");
        if (attachment == null)
        {
            return null;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return encryption == null
                    ? await DownloadAttachmentToBytesAsync(attachment, onProgress, cancellationToken)
                    : await DownloadEncryptedAttachmentBytesAsync(attachment, encryption, onProgress, cancellationToken);
            }
            catch (HttpRequestException error) when (attempt == 0 && error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                var fresh = await _discord.ResolvePayloadMessageAsync(threadId, messageId, cancellationToken, refresh: true);
                attachment = fresh.Attachments.SingleOrDefault(item => item.Id == attachment.Id)
                    ?? throw new InvalidDataException("O attachment referenciado foi removido.");
            }
        }
    }

    public async Task<FileInfo?> DownloadMessageAttachmentAsync(
        string threadId,
        string messageId,
        DirectoryInfo destDir,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default,
        EncryptedPayloadContext? encryption = null,
        string? outputName = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        var message = await GetPayloadMessageAsync(threadId, messageId, cancellationToken, waitForTransientNetwork: true);
        var attachment = message.Attachments.FirstOrDefault();
        if (attachment == null)
        {
            return null;
        }

        if (encryption == null)
        {
            return await DownloadAttachmentToFileAsync(attachment, destDir, onProgress, cancellationToken);
        }

        var bytes = await DownloadEncryptedAttachmentBytesAsync(attachment, encryption, onProgress, cancellationToken);
        try
        {
            destDir.Create();
            var file = new FileInfo(Path.Combine(destDir.FullName, string.IsNullOrWhiteSpace(outputName) ? "preview.bin" : PathRules.CleanFileName(outputName)));
            await File.WriteAllBytesAsync(file.FullName, bytes, cancellationToken);
            file.Refresh();
            return file;
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task<DiscordAttachmentDto?> FindMarkedPreviewAttachmentAsync(
        string threadId,
        string contentPrefix,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(contentPrefix))
        {
            return null;
        }

        var messages = await GetPayloadMessagesAsync(threadId, null, cancellationToken, waitForTransientNetwork: true);
        return messages
            .Where(message => message.Content.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(message => Snowflake(message.Id))
            .SelectMany(message => message.Attachments)
            .FirstOrDefault();
    }

    private async Task<byte[]> DownloadAttachmentToBytesAsync(
        DiscordAttachmentDto attachment,
        Action<DownloadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        using var http = _downloadClientFactory();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var total = Math.Max(0, attachment.Size);
                using var response = await http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var output = new MemoryStream(total > 0 && total <= int.MaxValue ? (int)total : 0);
                var downloaded = await CopyWithProgressAsync(input, output, 0, total, attachment.FileName, onProgress, cancellationToken);
                onProgress?.Invoke(new DownloadProgress(100, "ConcluÃ­do", downloaded, total));
                return output.ToArray();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && NetworkRetryPolicy.IsTransient(ex))
            {
                AppLogger.Error("Download.AttachmentRetry", ex, $"{attachment.FileName}:attempt {attempt}");
                await NetworkRetryPolicy.DelayAsync(attempt, cancellationToken);
            }
        }
    }

    private async Task<byte[]> DownloadEncryptedAttachmentBytesAsync(
        DiscordAttachmentDto attachment,
        EncryptedPayloadContext encryption,
        Action<DownloadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= MaxEncryptedPayloadDownloadAttempts; attempt++)
        {
            byte[]? encryptedBytes = null;
            try
            {
                encryptedBytes = await DownloadAttachmentToBytesAsync(attachment, onProgress, cancellationToken);
                return _encryption.DecryptBytes(encryptedBytes, encryption);
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < MaxEncryptedPayloadDownloadAttempts)
            {
                last = ex;
                AppLogger.Error("Download.EncryptedAttachmentRetry", ex, $"{encryption.FileId}:{encryption.Role}:{encryption.ChunkIndex}:attempt {attempt}");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                break;
            }
            finally
            {
                if (encryptedBytes != null)
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(encryptedBytes);
                }
            }
        }

        throw new EncryptedPayloadIntegrityException(encryption.Encryption.Mode, last);
    }

    private static async Task<FileInfo?> DownloadAttachmentToFileAsync(
        DiscordAttachmentDto attachment,
        DirectoryInfo destDir,
        Action<DownloadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        destDir.Create();
        var fileName = string.IsNullOrWhiteSpace(attachment.FileName) ? "preview.bin" : attachment.FileName;
        var target = new FileInfo(Path.Combine(destDir.FullName, fileName));
        using var http = NetworkConfig.Current.CreateClient();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(target.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true);
                var downloaded = await CopyWithProgressAsync(input, output, 0, Math.Max(0, attachment.Size), attachment.FileName, onProgress, cancellationToken);
                onProgress?.Invoke(new DownloadProgress(100, "ConcluÃ­do", downloaded, Math.Max(0, attachment.Size)));
                target.Refresh();
                return target.Exists && target.Length > 0 ? target : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && NetworkRetryPolicy.IsTransient(ex))
            {
                TryDelete(target);
                AppLogger.Error("Download.AttachmentFileRetry", ex, $"{attachment.FileName}:attempt {attempt}");
                await NetworkRetryPolicy.DelayAsync(attempt, cancellationToken);
            }
        }
    }

    public async Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            await _discord.DeleteChannelAsync(threadId, cancellationToken);
        }
    }

    public async Task<string?> GetFirstAttachmentUrlAsync(string threadId, CancellationToken cancellationToken = default)
    {
        var messages = await GetPayloadMessagesAsync(threadId, 100, cancellationToken, waitForTransientNetwork: true);
        return messages
            .OrderBy(m => Snowflake(m.Id))
            .Where(message => !IsPreviewMessage(message.Content))
            .SelectMany(m => m.Attachments)
            .Select(a => a.Url)
            .FirstOrDefault();
    }


    public async Task<IReadOnlyList<ProgressiveDownloadPart>> ResolveProgressivePartsAsync(
        AppEntry entry,
        CancellationToken cancellationToken = default)
    {
        var parts = entry.ChunkReferences != null || entry.LogicalChunkCount > 0
            ? await ResolveMessagePartsAsync(entry.ThreadId, entry.ChunkMessageIds, entry.BatchId, cancellationToken,
                chunkReferences: entry.ChunkReferences)
            : await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId);

        if (parts.Count == 0 && entry.ChunkReferences == null && entry.ChunkMessageIds.Count > 0)
        {
            parts = await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId);
        }

        return parts
            .OrderBy(part => part.Index)
            .Select(part => new ProgressiveDownloadPart(part.Index, part.Url, part.Name, part.Size))
            .ToList();
    }

    public async Task<DiscordVideoChunkProvider> OpenVideoChunksAsync(AppEntry entry, bool lowQuality,
        CancellationToken cancellationToken = default)
    {
        List<Part> parts;
        if (lowQuality)
        {
            var ids = entry.VideoPreviewChunkMessageIds.Count > 0 ? entry.VideoPreviewChunkMessageIds :
                string.IsNullOrWhiteSpace(entry.VideoPreviewMessageId) ? [] : new List<string> { entry.VideoPreviewMessageId };
            parts = entry.VideoPreviewChunkReferences != null || ids.Count > 0
                ? await ResolveMessagePartsAsync(entry.ThreadId,
                    entry.VideoPreviewChunkReferences == null ? ids : entry.VideoPreviewChunkMessageIds, "", cancellationToken,
                    chunkReferences: entry.VideoPreviewChunkReferences)
                : await CollectPreviewPartsAsync(entry.ThreadId, "video-480", cancellationToken);
            if (parts.Count == 0 && entry.Encryption == null)
            {
                var messages = await GetPayloadMessagesAsync(entry.ThreadId, null, cancellationToken);
                var marked = messages.FirstOrDefault(m => m.Content.StartsWith(DartVideoPreviewPrefix, StringComparison.OrdinalIgnoreCase));
                if (marked?.Attachments.FirstOrDefault() is { } attachment)
                    parts.Add(new Part(0, attachment.Url, attachment.FileName, attachment.Size, marked.Id));
            }
        }
        else
        {
            if (entry.Encryption != null && entry.LogicalChunkCount == 0)
                throw new InvalidDataException("O metadata criptografado não contém referências aos chunks do vídeo.");
            parts = entry.ChunkReferences != null || entry.LogicalChunkCount > 0
                ? await ResolveMessagePartsAsync(entry.ThreadId, entry.ChunkMessageIds, entry.BatchId, cancellationToken,
                    chunkReferences: entry.ChunkReferences)
                : await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId);
        }
        var readLease = _storageReads == null ? null : await _storageReads.Manager.BeginReadAsync("preview:" + Guid.NewGuid().ToString("N"), entry.FileId,
            SharedStorageManager.Payloads(MetadataService.CreateFileMetadata(entry)).Take(1).ToList(), cancellationToken);
        try
        {
        var provider = new DiscordVideoChunkProvider(_discord, entry.ThreadId,
            parts.Select(p => new VideoAttachment(p.Index, p.MessageId, p.Name, p.Url, p.Size, p.AttachmentId)).ToArray(),
            _encryption, EncryptionContext(entry, lowQuality ? EncryptedPayloadRoles.Video480p : EncryptedPayloadRoles.Original),
            lowQuality ? entry.VideoPreviewSizeBytes : entry.SizeBytes, _downloadClientFactory());
        provider.ReadLease = readLease;
        return provider;
        }
        catch { if (readLease != null) await readLease.DisposeAsync(); throw; }
    }
    private async Task<List<Part>> CollectPartsAsync(string threadId, CancellationToken cancellationToken, string? preferredBatchId = null)
    {
        var messages = await GetPayloadMessagesAsync(threadId, null, cancellationToken, waitForTransientNetwork: true);
        var candidates = new Dictionary<string, PartBatch>(StringComparer.OrdinalIgnoreCase);
        var hasPreferredBatch = !string.IsNullOrWhiteSpace(preferredBatchId);
        foreach (var message in messages.OrderByDescending(m => Snowflake(m.Id)))
        {
            if (IsPreviewMessage(message.Content))
            {
                continue;
            }

            if (!TryReadPartInfo(message.Content, out var batchId, out var partIndex, out var totalParts))
            {
                continue;
            }

            var attachment = message.Attachments.FirstOrDefault();
            if (attachment == null)
            {
                continue;
            }

            if (!candidates.TryGetValue(batchId, out var batch))
            {
                batch = new PartBatch(totalParts);
                candidates[batchId] = batch;
            }

            if (batch.TotalParts != totalParts || batch.Parts.ContainsKey(partIndex))
            {
                continue;
            }

            batch.Parts[partIndex] = new Part(partIndex - 1, attachment.Url, attachment.FileName, attachment.Size, message.Id, batchId, attachment.ProxyUrl);
            if (!hasPreferredBatch && batch.Parts.Count == batch.TotalParts)
            {
                return batch.Parts.OrderBy(p => p.Key).Select(p => p.Value).ToList();
            }
        }

        if (hasPreferredBatch)
        {
            return candidates.TryGetValue(preferredBatchId!, out var preferred) && preferred.Parts.Count == preferred.TotalParts
                ? preferred.Parts.OrderBy(p => p.Key).Select(p => p.Value).ToList()
                : [];
        }

        if (candidates.Count > 0)
        {
            return [];
        }

        var legacy = messages
            .Where(message => !IsPreviewMessage(message.Content) &&
                              !message.Content.Contains("Parte", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => Snowflake(m.Id))
            .SelectMany(m => m.Attachments.Select(a => (Message: m, Attachment: a)))
            .Select((item, index) => new Part(index, item.Attachment.Url, item.Attachment.FileName, item.Attachment.Size, item.Message.Id))
            .ToList();
        return legacy;
    }

    private async Task<List<Part>> CollectPreviewPartsAsync(string threadId, string kind, CancellationToken cancellationToken)
    {
        var messages = await GetPayloadMessagesAsync(threadId, null, cancellationToken, waitForTransientNetwork: true);
        var candidates = new Dictionary<string, PartBatch>(StringComparer.OrdinalIgnoreCase);
        foreach (var message in messages.OrderByDescending(message => Snowflake(message.Id)))
        {
            if (!TryReadPreviewPartInfo(message.Content, kind, out var batchId, out var partIndex, out var totalParts))
            {
                continue;
            }

            var attachment = message.Attachments.FirstOrDefault();
            if (attachment == null)
            {
                continue;
            }

            if (!candidates.TryGetValue(batchId, out var batch))
            {
                batch = new PartBatch(totalParts);
                candidates[batchId] = batch;
            }

            if (batch.TotalParts != totalParts || batch.Parts.ContainsKey(partIndex))
            {
                continue;
            }

            batch.Parts[partIndex] = new Part(partIndex - 1, attachment.Url, attachment.FileName, attachment.Size, message.Id, batchId, attachment.ProxyUrl);
            if (batch.Parts.Count == batch.TotalParts)
            {
                return batch.Parts.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToList();
            }
        }

        return [];
    }



    private async Task<DiscordMessageDto> GetMessageWithRetryAsync(
        string threadId,
        string messageId,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                return await GetPayloadMessageAsync(threadId, messageId, cancellationToken, waitForTransientNetwork: true);
            }
            catch (MetadataCodecException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 4)
            {
                last = ex;
                AppLogger.Error("Download.GetChunkMessageRetry", ex, messageId + ":attempt " + attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                break;
            }
        }

        throw new IOException("NÃ£o foi possÃ­vel localizar o chunk " + messageId + ".", last);
    }
    private async Task<List<Part>> ResolveMessagePartsAsync(
        string threadId,
        IReadOnlyList<string> messageIds,
        string batchId,
        CancellationToken cancellationToken,
        Action<int, int>? onPartResolved = null,
        IReadOnlyList<ChunkReference>? chunkReferences = null)
    {
        if (chunkReferences != null)
        {
            var ordered = ChunkReference.Validate(chunkReferences, messageIds);
            var messageKeys = ordered.Select(reference => reference.MessageId).Distinct(StringComparer.Ordinal).ToArray();
            var fetched = await _discord.ResolvePayloadMessagesAsync(threadId, messageKeys, cancellationToken);
            foreach (var message in fetched.Values) DecodeReference(message);
            var parts = new List<Part>(ordered.Count);
            foreach (var reference in ordered)
            {
                if (!fetched.TryGetValue(reference.MessageId, out var message))
                    throw new InvalidDataException("A mensagem referenciada por chunkRefs não foi encontrada.");
                var attachments = message.Attachments.Where(attachment => attachment.Id == reference.AttachmentId).ToArray();
                if (attachments.Length != 1 || attachments[0].Size <= 0)
                    throw new InvalidDataException("O attachment exato referenciado por chunkRefs não existe ou está inválido.");
                var attachment = attachments[0];
                parts.Add(new Part(reference.Index, attachment.Url, attachment.FileName, attachment.Size,
                    reference.MessageId, batchId, attachment.ProxyUrl, reference.AttachmentId));
                onPartResolved?.Invoke(parts.Count, ordered.Count);
            }
            return parts;
        }
        if (messageIds.Count == 0)
        {
            return [];
        }

        var messages = await _discord.GetMessagesByIdsAsync(
            threadId,
            messageIds,
            cancellationToken,
            onPartResolved);
        foreach (var payloadMessage in messages.Values) DecodeReference(payloadMessage);
        var resolved = new Part[messageIds.Count];
        for (var index = 0; index < messageIds.Count; index++)
        {
            var messageId = messageIds[index];
            if (!messages.TryGetValue(messageId, out var message))
            {
                throw new IOException("NÃ£o foi possÃ­vel localizar o chunk " + messageId + " no tÃ³pico.");
            }

            var attachment = message.Attachments.FirstOrDefault();
            if (attachment == null)
            {
                throw new InvalidOperationException("Chunk sem anexo no metadata: " + messageId);
            }

            if (attachment.Size <= 0)
            {
                throw new IOException("O servidor informou uma parte vazia: " + attachment.FileName + ".");
            }

            resolved[index] = new Part(
                index,
                attachment.Url,
                attachment.FileName,
                attachment.Size,
                messageId,
                batchId,
                attachment.ProxyUrl);
        }

        return resolved.ToList();
    }
    private async Task<FileInfo> DownloadMessagePartsAsync(string threadId, IReadOnlyList<string> messageIds,
        string outputName, DirectoryInfo destDir, Action<string> onProgress, CancellationToken cancellationToken,
        Action<DownloadProgress>? onDetailedProgress = null, EncryptedPayloadContext? encryption = null,
        long expectedPlaintextLength = -1, IReadOnlyList<ChunkReference>? chunkReferences = null, PayloadChecksums? checksums = null)
    {
        await using var discovery = expectedPlaintextLength < 0 ? null : new PartDiscovery(this, threadId,
            messageIds, "", chunkReferences, cancellationToken);
        var parts = discovery == null
            ? await ResolveMessagePartsAsync(threadId, messageIds, "", cancellationToken, chunkReferences: chunkReferences)
            : (await discovery.NextAsync(cancellationToken))?.ToList() ?? [];
        if (parts.Count == 0) throw new InvalidDataException("O arquivo não contém chunks para baixar.");
        return await DownloadDirectAsync(parts, destDir, string.IsNullOrWhiteSpace(outputName) ? parts[0].Name : outputName,
            onProgress, cancellationToken, onDetailedProgress, encryption?.WithChunkCount(discovery?.References.Count ?? parts.Count),
            expectedPlaintextLength, checksums, discovery: discovery);
    }
    private static bool IsPreviewMessage(string? content)
    {
        return (content ?? "").StartsWith(PreviewPrefix, StringComparison.OrdinalIgnoreCase) ||
               (content ?? "").StartsWith(DartPreviewPrefix, StringComparison.OrdinalIgnoreCase) ||
               (content ?? "").StartsWith(DartVideoPreviewPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadPartInfo(string content, out string batchId, out int partIndex, out int totalParts)
    {
        batchId = "";
        partIndex = 0;
        totalParts = 0;

        var match = Regex.Match(content ?? "", @"(?:batch:(?<batch>[a-zA-Z0-9_-]+)\s+)?Parte\s+(?<part>\d+)\s*/\s*(?<total>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success ||
            !int.TryParse(match.Groups["part"].Value, out partIndex) ||
            !int.TryParse(match.Groups["total"].Value, out totalParts) ||
            partIndex <= 0 ||
            totalParts <= 0 ||
            partIndex > totalParts)
        {
            return false;
        }

        batchId = match.Groups["batch"].Success && !string.IsNullOrWhiteSpace(match.Groups["batch"].Value)
            ? match.Groups["batch"].Value
            : "legacy:" + totalParts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryReadPreviewPartInfo(
        string content,
        string kind,
        out string batchId,
        out int partIndex,
        out int totalParts)
    {
        batchId = "";
        partIndex = 0;
        totalParts = 0;
        var prefix = PreviewPrefix + kind + "|";
        if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = Regex.Match(
            content[prefix.Length..],
            @"batch:(?<batch>[a-zA-Z0-9_-]+)\|Parte\s+(?<part>\d+)\s*/\s*(?<total>\d+)",
            RegexOptions.IgnoreCase);
        return match.Success &&
               !string.IsNullOrWhiteSpace(batchId = match.Groups["batch"].Value) &&
               int.TryParse(match.Groups["part"].Value, out partIndex) &&
               int.TryParse(match.Groups["total"].Value, out totalParts) &&
               partIndex > 0 && totalParts > 0 && partIndex <= totalParts;
    }

    private async Task DownloadPartsAsync(
        IAsyncEnumerable<Part> parts,
        int partCount,
        PartialDownloadFile destination,
        Action<string> onProgress,
        CancellationToken cancellationToken,
        Action<DownloadProgress>? onDetailedProgress = null,
        Func<Part, string, Task>? onPartCompleted = null,
        long initialDownloadedBytes = 0,
        EncryptedPayloadContext? encryption = null,
        PayloadChecksums? checksums = null,
        long totalLength = -1)
    {
        if (partCount == 0)
        {
            return;
        }

        using var http = _downloadClientFactory();
        var downloadedBytes = initialDownloadedBytes;
        long receivedBytes = 0;
        var totalBytes = totalLength >= 0 ? totalLength : destination.Layout.Length;
        var lastDetailedPercent = -1;
        var parallelism = Math.Min(Math.Clamp(_settings.DownloadThreads, 1, 16), partCount);
        using var semaphore = new SemaphoreSlim(parallelism);
        var tasks = new HashSet<Task>();
        try
        {
            await foreach (var part in parts.WithCancellation(cancellationToken))
            {
                // Checkpoint waiters retain only descriptors/digests, never payload buffers.
                if (tasks.Count >= 64)
                {
                    await Task.WhenAny(tasks);
                    foreach (var completed in tasks.Where(task => task.IsCompleted).ToArray())
                    {
                        await completed;
                        tasks.Remove(completed);
                    }
                }
                await semaphore.WaitAsync(cancellationToken);
                tasks.Add(Task.Run(() => DownloadPartAsync(part)));
            }
        }
        finally { await Task.WhenAll(tasks); }

        async Task DownloadPartAsync(Part part)
        {
            string digest;
            try
            {
                if (part.VerifiedDigest != null)
                {
                    digest = part.VerifiedDigest;
                    ReportParallelProgress(Interlocked.Add(ref downloadedBytes, destination.Layout.Regions[part.Index].Length), part);
                }
                else digest = await DownloadPartWithRetryAsync(part);
            }
            finally
            {
                semaphore.Release();
            }

            // The payload buffers are released. A durability checkpoint must not hold a network slot.
            if (onPartCompleted != null)
            {
                await onPartCompleted(part, digest);
            }

            onProgress($"{FormatBytes(Interlocked.Read(ref downloadedBytes))} / {FormatBytes(totalBytes)} baixados");
        }

        async Task<string> DownloadPartWithRetryAsync(Part part)
        {
            Exception? last = null;
            var maxAttempts = encryption == null ? MaxChunkDownloadAttempts : MaxEncryptedPayloadDownloadAttempts;
            var networkRetryCount = 0;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var countedBytes = 0L;
                var reportedBytes = 0L;
                var logicalLength = destination.Layout.Regions[part.Index].Length;
                var useProxy = (attempt + networkRetryCount) % 2 == 0 &&
                               !string.IsNullOrWhiteSpace(part.ProxyUrl) &&
                               !string.Equals(part.ProxyUrl, part.Url, StringComparison.OrdinalIgnoreCase);
                var sourceUrl = useProxy ? part.ProxyUrl : part.Url;
                try
                {
                    if (encryption != null && (part.Size <= 0 || part.Size > 64L * 1024 * 1024))
                        throw new InvalidDataException("O chunk criptografado excede o limite seguro de memória ou possui tamanho inválido.");
                    using var reservation = encryption == null ? null : await RamUploadBudget.ReserveExternalBytesAsync(
                        checked(part.Size * 2 + DownloadBufferSize), checked(part.Size * 2 + DownloadBufferSize), cancellationToken);
                    using var encryptedMemory = encryption == null ? null : new MemoryStream(checked((int)part.Size));
                    try
                    {
                        using var plainHash = encryption == null ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
                        await using var diskOutput = encryptedMemory == null ? destination.OpenRegion(part.Index) : null;
                        var outStream = (Stream?)encryptedMemory ?? diskOutput!;
                        {
                            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
                            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                            response.EnsureSuccessStatusCode();
                            if (part.Size > 0 && response.Content.Headers.ContentLength == 0)
                            {
                                throw new IOException("Chunk recebido vazio do CDN: " + part.Name);
                            }

                            await using var inStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                            var buffer = new byte[DownloadBufferSize];
                            try
                            {
                                while (true)
                                {
                                    await VideoNetworkPriority.YieldAsync(cancellationToken);
                                    var read = await inStream.ReadAsync(buffer, cancellationToken);
                                    if (read <= 0)
                                    {
                                        break;
                                    }

                                    if (part.Size > 0 && countedBytes + read > part.Size)
                                        throw new IOException("O chunk recebido excedeu o tamanho informado pelo servidor.");
                                    await outStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                                    plainHash?.AppendData(buffer, 0, read);
                                    countedBytes += read;
                                    Interlocked.Add(ref receivedBytes, read);
                                    var logical = encryption == null ? countedBytes : Math.Min(logicalLength, countedBytes * logicalLength / part.Size);
                                    var currentBytes = Interlocked.Add(ref downloadedBytes, logical - reportedBytes);
                                    reportedBytes = logical;
                                    ReportParallelProgress(currentBytes, part);
                                }

                                await outStream.FlushAsync(cancellationToken);
                            }
                            finally { CryptographicOperations.ZeroMemory(buffer); }
                        }

                        if (part.Size > 0 && countedBytes != part.Size)
                        {
                            throw new IOException($"Chunk {part.Index + 1} incompleto. Esperado {part.Size} bytes, recebido {countedBytes} bytes.");
                        }

                        if (countedBytes == 0 && logicalLength != 0)
                        {
                            throw new IOException("Chunk baixado estÃ¡ vazio: " + part.Name);
                        }

                        if (encryption != null)
                        {
                            var plaintext = _encryption.DecryptBytes(encryptedMemory!.GetBuffer().AsSpan(0, checked((int)countedBytes)),
                                encryption.WithChunkIndex(part.Index));
                            try
                            {
                                if (encryption.Checksums == null) checksums?.VerifyChunk(part.Index, plaintext);
                                if (plaintext.LongLength != logicalLength) throw new InvalidDataException("O tamanho autenticado diverge do intervalo calculado.");
                                var digest = checksums?.Chunks[part.Index] ?? Convert.ToHexStringLower(SHA256.HashData(plaintext));
                                await using var output = destination.OpenRegion(part.Index);
                                await output.WriteAsync(plaintext, cancellationToken);
                                return digest;
                            }
                            finally { CryptographicOperations.ZeroMemory(plaintext); }
                        }
                        var plainDigest = Convert.ToHexStringLower(plainHash!.GetHashAndReset());
                        if (checksums != null && plainDigest != checksums.Chunks[part.Index])
                            throw new PayloadChecksumException("O checksum de uma parte baixada não confere.");
                        return plainDigest;
                    }
                    finally
                    {
                        if (encryptedMemory != null) CryptographicOperations.ZeroMemory(encryptedMemory.GetBuffer());
                    }
                }
                catch (Exception ex) when ((ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) &&
                                           (NetworkRetryPolicy.IsTransient(ex) || attempt < maxAttempts))
                {
                    last = ex;
                    if (countedBytes > 0)
                    {
                        Interlocked.Add(ref downloadedBytes, -reportedBytes);
                    }

                    AppLogger.Error("Download.ChunkRetry", ex, $"{part.Index}:{part.Name}:attempt {attempt}:{(useProxy ? "proxy" : "cdn")}");
                    var isEmptyResponse = ex is IOException io &&
                                          (io.Message.Contains("vazio", StringComparison.OrdinalIgnoreCase) ||
                                           io.Message.Contains("incompleto", StringComparison.OrdinalIgnoreCase));
                    var retryDelayMs = isEmptyResponse
                        ? Math.Min(15000, 750 * Math.Pow(2, attempt - 1))
                        : 250 * attempt * attempt;
                    if (NetworkRetryPolicy.IsTransient(ex))
                    {
                        onProgress($"Conexão indisponível; aguardando retomada ({part.Name})...");
                        networkRetryCount++;
                        attempt--;
                        await NetworkRetryPolicy.DelayAsync(networkRetryCount, cancellationToken);
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMs), cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    if (countedBytes > 0)
                    {
                        Interlocked.Add(ref downloadedBytes, -reportedBytes);
                    }

                    break;
                }
            }

            if (last is PayloadChecksumException checksumFailure) throw checksumFailure;
            if (encryption != null)
            {
                throw new EncryptedPayloadIntegrityException(encryption.Encryption.Mode, last);
            }

            throw new IOException($"NÃ£o foi possÃ­vel baixar a parte {part.Index + 1}/{partCount}.", last);
        }

        void ReportParallelProgress(long currentBytes, Part part)
        {
            if (onDetailedProgress == null)
            {
                return;
            }

            var percent = totalBytes <= 0
                ? 0
                : Math.Clamp((int)Math.Round(currentBytes * 99.0 / totalBytes), 0, 99);
            if (Interlocked.Exchange(ref lastDetailedPercent, percent) != percent)
            {
                onDetailedProgress(new DownloadProgress(percent, part.Name, currentBytes, totalBytes, RateBytes: Interlocked.Read(ref receivedBytes)));
            }
        }
    }
    private static async Task<long> CopyWithProgressAsync(
        Stream input,
        Stream output,
        long downloaded,
        long total,
        string name,
        Action<DownloadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[DownloadBufferSize];
        while (true)
        {
            await VideoNetworkPriority.YieldAsync(cancellationToken);
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                return downloaded;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            downloaded += read;
            ReportDetailedProgress(downloaded, total, name, onProgress);
        }
    }

    private static string TempPartFileName(Part part)
    {
        var cleanName = PathRules.CleanFileName(string.IsNullOrWhiteSpace(part.Name) ? "chunk.bin" : part.Name);
        return $"part_{part.Index:000000}_{cleanName}";
    }

    private static EncryptedPayloadContext? EncryptionContext(AppEntry entry, string role)
    {
        return entry.Encryption == null
            ? null
            : new EncryptedPayloadContext(entry.FileId, entry.Encryption, role, 0,
                Math.Max(1, role == EncryptedPayloadRoles.Video480p
                    ? entry.VideoPreviewChunkReferences?.Count ?? entry.VideoPreviewChunkMessageIds.Count
                    : entry.LogicalChunkCount)) { Checksums = role == EncryptedPayloadRoles.Original ? entry.Checksums : null };
    }

    private static void EnsureExpectedPlaintextLength(FileInfo file, long expectedLength)
    {
        if (expectedLength < 0)
        {
            return;
        }

        file.Refresh();
        var actualLength = file.Exists ? file.Length : -1;
        if (actualLength != expectedLength)
        {
            throw new VerifiedPayloadSizeMismatchException(file, expectedLength, actualLength);
        }
    }

    private static void ReportDetailedProgress(long downloaded, long total, string name, Action<DownloadProgress>? onProgress)
    {
        if (onProgress == null)
        {
            return;
        }

        var percent = total <= 0 ? 0 : Math.Clamp((int)Math.Round(downloaded * 100.0 / total), 0, 100);
        onProgress(new DownloadProgress(percent, name, downloaded, total));
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
        catch
        {
            // Partial chunks are retried or surfaced as transfer errors.
        }
    }

    private static bool SameFilePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }


    private static void ValidateDestination(DirectoryInfo destDir, long expectedBytes, long allocatedBytes = 0)
    {
        Directory.CreateDirectory(destDir.FullName);
        var root = System.IO.Path.GetPathRoot(destDir.FullName);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        var neededBytes = Math.Max(0, expectedBytes - allocatedBytes);
        if (neededBytes > 0 && drive.AvailableFreeSpace < neededBytes)
        {
            throw new IOException($"EspaÃ§o insuficiente no destino. NecessÃ¡rio: {FormatBytes(expectedBytes)}, disponÃ­vel: {FormatBytes(drive.AvailableFreeSpace)}.");
        }

        if (expectedBytes >= 4L * 1024 * 1024 * 1024 && drive.DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"O destino usa {drive.DriveFormat} e nÃ£o suporta arquivos maiores que 4 GB. Escolha uma pasta em NTFS/exFAT antes de baixar.");
        }
    }

    private static ulong Snowflake(string id)
    {
        return ulong.TryParse(id, out var parsed) ? parsed : 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "desconhecido";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit == 0 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private sealed record Part(int Index, string Url, string Name, long Size, string MessageId = "", string BatchId = "", string ProxyUrl = "", string AttachmentId = "", string? VerifiedDigest = null);
    private sealed class PartBatch
    {
        public PartBatch(int totalParts)
        {
            TotalParts = totalParts;
        }

        public int TotalParts { get; }
        public Dictionary<int, Part> Parts { get; } = new();
    }
}

public sealed record DownloadProgress(int Percent, string Message, long DownloadedBytes, long TotalBytes, bool Assembling = false, long? RateBytes = null);






