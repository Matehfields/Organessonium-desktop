using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Organessonium.Models;

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

public sealed class ForumDownloader
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

    public ForumDownloader(DiscordApiClient discord, AppSettings settings, EncryptionService encryption)
    {
        _discord = discord;
        _settings = settings;
        _encryption = encryption;
    }

    public async Task<FileInfo> DownloadAndAssembleAsync(
        string threadId,
        DirectoryInfo destDir,
        Action<string> onProgress,
        CancellationToken cancellationToken = default,
        Action<DownloadProgress>? onDetailedProgress = null)
    {
        onProgress("Recolhendo URLs dos anexos...");
        var parts = await CollectPartsAsync(threadId, cancellationToken);
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("Nenhum anexo encontrado na thread.");
        }

        var expectedBytes = parts.Sum(p => p.Size);
        ValidateDestination(destDir, expectedBytes);
        onProgress($"{parts.Count} URL(s) recolhida(s), {FormatBytes(expectedBytes)}. Baixando com {Math.Clamp(_settings.DownloadThreads, 1, 16)} thread(s)...");

        var tempDir = ResolveTempDir(destDir, threadId);
        Directory.CreateDirectory(tempDir.FullName);
        ValidateDestination(tempDir, expectedBytes);
        onProgress("Pasta temporÃ¡ria: " + tempDir.FullName);

        var downloaded = await DownloadPartsAsync(parts, tempDir, onProgress, cancellationToken, onDetailedProgress);
        onProgress("Montando arquivo final...");

        var firstName = parts[0].Name;
        var archiveName = System.Text.RegularExpressions.Regex.IsMatch(firstName, @".*\.\d{3}$")
            ? System.Text.RegularExpressions.Regex.Replace(firstName, @"\.\d{3}$", "")
            : firstName;

        Directory.CreateDirectory(destDir.FullName);
        var output = new FileInfo(System.IO.Path.Combine(destDir.FullName, archiveName));
        await using (var outStream = new FileStream(output.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true))
        {
            var partNumber = 0;
            foreach (var part in downloaded.Values)
            {
                partNumber++;
                onProgress($"Montando parte {partNumber}/{downloaded.Count}");
                await using var inStream = new FileStream(part.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadBufferSize, true);
                await inStream.CopyToAsync(outStream, cancellationToken);
            }        }

        try { tempDir.Delete(true); } catch { }
        return output;
    }

    public async Task<FileInfo> DownloadAndAssembleAsync(
        AppEntry entry,
        DirectoryInfo destDir,
        Action<string> onProgress,
        CancellationToken cancellationToken = default,
        Action<DownloadProgress>? onDetailedProgress = null)
    {
        try
        {
            if (entry.ChunkMessageIds.Count == 0)
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
                entry.SizeBytes);
        }
        catch (Exception ex) when (CanRecoverByBatch(entry, ex))
        {
            AppLogger.Error("Download.MetadataChunksFailed", ex, entry.ThreadId + " | " + entry.BatchId + " | " + entry.Name);
            return await DownloadBatchAndAssembleAsync(entry, destDir, onProgress, cancellationToken, onDetailedProgress);
        }
    }

    private async Task<FileInfo> DownloadBatchAndAssembleAsync(
        AppEntry entry,
        DirectoryInfo destDir,
        Action<string> onProgress,
        CancellationToken cancellationToken,
        Action<DownloadProgress>? onDetailedProgress = null)
    {
        onProgress("Recuperando partes pelo batch do metadata...");
        var parts = await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId);
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("NÃ£o foi possÃ­vel recuperar os chunks pelo batchId do metadata.json.");
        }

        var expectedBytes = parts.Sum(part => Math.Max(0, part.Size));
        ValidateDestination(destDir, expectedBytes);
        var tempDir = ResolveTempDir(destDir, entry.ThreadId);
        tempDir.Create();
        var downloaded = await DownloadPartsAsync(parts, tempDir, onProgress, cancellationToken, onDetailedProgress);

        destDir.Create();
        var output = new FileInfo(Path.Combine(destDir.FullName, string.IsNullOrWhiteSpace(entry.Name) ? parts[0].Name : entry.Name));
        await using (var outStream = new FileStream(output.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true))
        {
            foreach (var part in downloaded.Values)
            {
                await using var inStream = new FileStream(part.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadBufferSize, true);
                await inStream.CopyToAsync(outStream, cancellationToken);
            }
        }

        try { tempDir.Delete(true); } catch { }
        output.Refresh();
        return output;
    }

    private static bool CanRecoverByBatch(AppEntry entry, Exception ex)
    {
        return ex is not OperationCanceledException &&
               entry.Encryption == null &&
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
    {
        if (entry.ChunkMessageIds.Count == 0)
        {
            return await DownloadAndAssembleAsync(entry, destDir, onProgress, cancellationToken, onDetailedProgress);
        }

        state.Status = TransferStatuses.Downloading;
        state.ThreadId = entry.ThreadId;
        state.FileId = entry.FileId;
        state.FileName = entry.Name;
        state.MimeType = entry.MimeType;
        state.SizeBytes = entry.SizeBytes;
        state.Encryption = entry.Encryption?.Clone();
        state.TotalChunks = entry.ChunkMessageIds.Count;
        state.Chunks = entry.ChunkMessageIds
            .Select((messageId, index) =>
            {
                var existing = state.Chunks.FirstOrDefault(chunk => chunk.Index == index);
                if (existing == null || !string.Equals(existing.MessageId, messageId, StringComparison.OrdinalIgnoreCase))
                {
                    return new TransferChunkState
                    {
                        Index = index,
                        MessageId = messageId
                    };
                }

                existing.MessageId = messageId;
                return existing;
            })
            .ToList();

        onProgress($"Localizando partes 0/{entry.ChunkMessageIds.Count}...");
        var parts = await ResolveMessagePartsAsync(
            entry.ThreadId,
            entry.ChunkMessageIds,
            entry.BatchId,
            cancellationToken,
            (completed, total) => onProgress($"Localizando partes {completed}/{total}..."));

        foreach (var part in parts)
        {
            if (entry.Encryption == null)
            {
                state.Chunks.First(chunk => chunk.Index == part.Index).SizeBytes = part.Size;
            }
        }

        if (parts.Count == 0)
        {
            throw new InvalidOperationException("metadata.json nÃ£o contÃ©m chunks para baixar.");
        }

        var expectedBytes = parts.Sum(part => Math.Max(0, part.Size));
        ValidateDestination(destDir, expectedBytes);
        var tempDir = ResolveTempDir(destDir, state.TransferId);
        tempDir.Create();
        state.TempPath = tempDir.FullName;
        onProgress("Pasta temporÃ¡ria: " + tempDir.FullName);
        var available = new SortedDictionary<int, FileInfo>();
        var existingBytes = 0L;
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = state.Chunks.First(item => item.Index == part.Index);
            if (!chunk.Downloaded || string.IsNullOrWhiteSpace(chunk.TempChunkPath))
            {
                chunk.Downloaded = false;
                chunk.TempChunkPath = null;
                continue;
            }

            var source = new FileInfo(chunk.TempChunkPath);
            source.Refresh();
            if (!source.Exists ||
                (entry.Encryption == null && part.Size > 0 && source.Length != part.Size) ||
                (entry.Encryption != null && chunk.SizeBytes > 0 && source.Length != chunk.SizeBytes))
            {
                chunk.Downloaded = false;
                chunk.TempChunkPath = null;
                TryDelete(source);
                continue;
            }

            var target = new FileInfo(Path.Combine(tempDir.FullName, TempPartFileName(part)));
            var relocated = await RelocateChunkAsync(source, target, cancellationToken);
            chunk.TempChunkPath = relocated.FullName;
            available[part.Index] = relocated;
            existingBytes += relocated.Length;
        }

        state.DownloadedChunks = state.Chunks
            .Where(chunk => chunk.Downloaded && !string.IsNullOrWhiteSpace(chunk.TempChunkPath))
            .Select(chunk => chunk.Index)
            .OrderBy(index => index)
            .ToList();
        state.PendingChunks = state.Chunks
            .Where(chunk => !chunk.Downloaded)
            .Select(chunk => chunk.Index)
            .OrderBy(index => index)
            .ToList();
        await transferManager.SaveAsync(state, cancellationToken);

        var pendingParts = parts.Where(part => !available.ContainsKey(part.Index)).ToList();
        using var stateSaveGate = new SemaphoreSlim(1, 1);
        var completedSinceCheckpoint = 0;
        var checkpointPending = false;
        SortedDictionary<int, FileInfo> downloaded;
        try
        {
            downloaded = await DownloadPartsAsync(
                pendingParts,
                tempDir,
                onProgress,
                cancellationToken,
                onDetailedProgress,
                onPartCompleted: async (part, file) =>
                {
                    await stateSaveGate.WaitAsync(cancellationToken);
                    try
                    {
                        await transferManager.MarkDownloadChunkAsync(
                            state,
                            part.Index,
                            file.FullName,
                            file.Length,
                            cancellationToken,
                            persist: false);
                        checkpointPending = true;
                        completedSinceCheckpoint++;
                        if (completedSinceCheckpoint >= 4)
                        {
                            await transferManager.SaveAsync(state, cancellationToken);
                            checkpointPending = false;
                            completedSinceCheckpoint = 0;
                        }
                    }
                    finally
                    {
                        stateSaveGate.Release();
                    }
                },
                initialDownloadedBytes: existingBytes,
                encryption: EncryptionContext(entry, EncryptedPayloadRoles.Original));
        }
        finally
        {
            await stateSaveGate.WaitAsync(CancellationToken.None);
            try
            {
                if (checkpointPending)
                {
                    await transferManager.SaveAsync(state, CancellationToken.None);
                }
            }
            finally
            {
                stateSaveGate.Release();
            }
        }

        foreach (var part in downloaded)
        {
            available[part.Key] = part.Value;
        }

        onProgress("Montando arquivo final...");
        destDir.Create();
        var outputFile = new FileInfo(Path.Combine(destDir.FullName, string.IsNullOrWhiteSpace(entry.Name) ? parts[0].Name : entry.Name));
        await using (var outStream = new FileStream(outputFile.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true))
        {
            foreach (var part in parts.OrderBy(part => part.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!available.TryGetValue(part.Index, out var chunkFile) || !chunkFile.Exists)
                {
                    throw new IOException("Chunk temporÃ¡rio ausente para montar download: " + part.Index);
                }

                onProgress($"Montando parte {part.Index + 1}/{parts.Count}");
                await using var inStream = new FileStream(chunkFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadBufferSize, true);
                await inStream.CopyToAsync(outStream, cancellationToken);
            }
        }

        try { tempDir.Delete(true); } catch { }
        outputFile.Refresh();
        if (entry.Encryption != null)
        {
            EnsureExpectedPlaintextLength(outputFile, entry.SizeBytes);
        }

        return outputFile;
    }
    public async Task<FileInfo?> DownloadVideoPreview480Async(
        AppEntry entry,
        DirectoryInfo destDir,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (entry.VideoPreviewChunkMessageIds.Count == 0)
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
            entry.VideoPreviewSizeBytes);
    }

    public async Task<FileInfo?> DownloadPreviewAndAssembleAsync(
        string threadId,
        string kind,
        DirectoryInfo destDir,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var parts = await CollectPreviewPartsAsync(threadId, kind, cancellationToken);
        if (parts.Count == 0)
        {
            return null;
        }

        destDir.Create();
        var tempDir = new DirectoryInfo(Path.Combine(destDir.FullName, ".preview_parts_" + SafeKind(kind)));
        tempDir.Create();
        var downloaded = await DownloadPartsAsync(
            parts,
            tempDir,
            _ => { },
            cancellationToken,
            onProgress);
        var firstName = parts[0].Name;
        var outputName = Regex.IsMatch(firstName, @".*\.\d{3}$")
            ? Regex.Replace(firstName, @"\.\d{3}$", "")
            : firstName;
        var output = new FileInfo(Path.Combine(destDir.FullName, outputName));
        await using (var outStream = new FileStream(output.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true))
        {
            foreach (var part in downloaded.Values)
            {
                await using var inStream = new FileStream(part.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadBufferSize, true);
                await inStream.CopyToAsync(outStream, cancellationToken);
            }
        }

        try { tempDir.Delete(true); } catch { }
        return output;
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
        using var http = NetworkConfig.Current.CreateClient();

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
        using var http = NetworkConfig.Current.CreateClient();
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
        EncryptedPayloadContext? encryption = null)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(messageId))
        {
            return null;
        }

        var message = await _discord.GetMessageAsync(threadId, messageId, cancellationToken);
        var attachment = message.Attachments.FirstOrDefault();
        if (attachment == null)
        {
            return null;
        }

        return encryption == null
            ? await DownloadAttachmentToBytesAsync(attachment, onProgress, cancellationToken)
            : await DownloadEncryptedAttachmentBytesAsync(attachment, encryption, onProgress, cancellationToken);
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

        var message = await _discord.GetMessageAsync(threadId, messageId, cancellationToken);
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

        var messages = await _discord.GetMessagesAsync(threadId, null, cancellationToken);
        return messages
            .Where(message => message.Content.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(message => Snowflake(message.Id))
            .SelectMany(message => message.Attachments)
            .FirstOrDefault();
    }

    private static async Task<byte[]> DownloadAttachmentToBytesAsync(
        DiscordAttachmentDto attachment,
        Action<DownloadProgress>? onProgress,
        CancellationToken cancellationToken)
    {
        var total = Math.Max(0, attachment.Size);
        using var http = NetworkConfig.Current.CreateClient();
        using var response = await http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(total > 0 && total <= int.MaxValue ? (int)total : 0);
        var downloaded = await CopyWithProgressAsync(input, output, 0, total, attachment.FileName, onProgress, cancellationToken);
        onProgress?.Invoke(new DownloadProgress(100, "ConcluÃ­do", downloaded, total));
        return output.ToArray();
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
        using var response = await http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(target.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true);
        var downloaded = await CopyWithProgressAsync(input, output, 0, Math.Max(0, attachment.Size), attachment.FileName, onProgress, cancellationToken);
        onProgress?.Invoke(new DownloadProgress(100, "ConcluÃ­do", downloaded, Math.Max(0, attachment.Size)));
        target.Refresh();
        return target.Exists && target.Length > 0 ? target : null;
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
        var messages = await _discord.GetMessagesAsync(threadId, 100, cancellationToken);
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
        var parts = entry.ChunkMessageIds.Count > 0
            ? await ResolveMessagePartsAsync(entry.ThreadId, entry.ChunkMessageIds, entry.BatchId, cancellationToken)
            : await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId);

        if (parts.Count == 0 && entry.ChunkMessageIds.Count > 0)
        {
            parts = await CollectPartsAsync(entry.ThreadId, cancellationToken, entry.BatchId);
        }

        return parts
            .OrderBy(part => part.Index)
            .Select(part => new ProgressiveDownloadPart(part.Index, part.Url, part.Name, part.Size))
            .ToList();
    }
    private async Task<List<Part>> CollectPartsAsync(string threadId, CancellationToken cancellationToken, string? preferredBatchId = null)
    {
        var messages = await _discord.GetMessagesAsync(threadId, null, cancellationToken);
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
            .SelectMany(m => m.Attachments)
            .Select((attachment, index) => new Part(index, attachment.Url, attachment.FileName, attachment.Size))
            .ToList();
        return legacy;
    }

    private async Task<List<Part>> CollectPreviewPartsAsync(string threadId, string kind, CancellationToken cancellationToken)
    {
        var messages = await _discord.GetMessagesAsync(threadId, null, cancellationToken);
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
                return await _discord.GetMessageAsync(threadId, messageId, cancellationToken);
            }
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
        Action<int, int>? onPartResolved = null)
    {
        if (messageIds.Count == 0)
        {
            return [];
        }

        var messages = await _discord.GetMessagesByIdsAsync(
            threadId,
            messageIds,
            cancellationToken,
            onPartResolved);
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
    private async Task<FileInfo> DownloadMessagePartsAsync(
        string threadId,
        IReadOnlyList<string> messageIds,
        string outputName,
        DirectoryInfo destDir,
        Action<string> onProgress,
        CancellationToken cancellationToken,
        Action<DownloadProgress>? onDetailedProgress = null,
        EncryptedPayloadContext? encryption = null,
        long expectedPlaintextLength = -1)
    {
        var parts = await ResolveMessagePartsAsync(threadId, messageIds, "", cancellationToken);

        if (parts.Count == 0)
        {
            throw new InvalidOperationException("metadata.json nÃ£o contÃ©m chunks para baixar.");
        }

        var expectedBytes = parts.Sum(part => Math.Max(0, part.Size));
        ValidateDestination(destDir, expectedBytes);
        var tempDir = ResolveTempDir(destDir, threadId + "_meta");
        tempDir.Create();
        try
        {
            var downloaded = await DownloadPartsAsync(parts, tempDir, onProgress, cancellationToken, onDetailedProgress, encryption: encryption);

            destDir.Create();
            var output = new FileInfo(Path.Combine(destDir.FullName, string.IsNullOrWhiteSpace(outputName) ? downloaded.Values.First().Name : outputName));
            await using (var outStream = new FileStream(output.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true))
            {
                foreach (var part in downloaded.Values)
                {
                    await using var inStream = new FileStream(part.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadBufferSize, true);
                    await inStream.CopyToAsync(outStream, cancellationToken);
                }
            }

            try { tempDir.Delete(true); } catch { }
            output.Refresh();
            if (encryption != null)
            {
                EnsureExpectedPlaintextLength(output, expectedPlaintextLength);
            }

            return output;
        }
        catch
        {
            if (encryption != null)
            {
                try { tempDir.Delete(true); } catch { }
            }

            throw;
        }
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

    private async Task<SortedDictionary<int, FileInfo>> DownloadPartsAsync(
        IReadOnlyList<Part> parts,
        DirectoryInfo tempDir,
        Action<string> onProgress,
        CancellationToken cancellationToken,
        Action<DownloadProgress>? onDetailedProgress = null,
        Func<Part, FileInfo, Task>? onPartCompleted = null,
        long initialDownloadedBytes = 0,
        EncryptedPayloadContext? encryption = null)
    {
        if (parts.Count == 0)
        {
            return new SortedDictionary<int, FileInfo>();
        }

        using var http = NetworkConfig.Current.CreateClient();
        var done = 0;
        var downloadedBytes = initialDownloadedBytes;
        var totalBytes = parts.Sum(part => Math.Max(0, part.Size)) + Math.Max(0, initialDownloadedBytes);
        var lastDetailedPercent = -1;
        var result = new ConcurrentDictionary<int, FileInfo>();
        var parallelism = Math.Min(Math.Clamp(_settings.DownloadThreads, 1, 16), parts.Count);
        using var semaphore = new SemaphoreSlim(parallelism);

        var tasks = parts.Select(async part =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var target = new FileInfo(Path.Combine(tempDir.FullName, TempPartFileName(part)));
                await DownloadPartWithRetryAsync(part, target);
                if (onPartCompleted != null)
                {
                    await onPartCompleted(part, target);
                }

                result[part.Index] = target;
                var current = Interlocked.Increment(ref done);
                onProgress($"[{current}/{parts.Count}] {part.Name}");
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return new SortedDictionary<int, FileInfo>(result);

        async Task DownloadPartWithRetryAsync(Part part, FileInfo target)
        {
            Exception? last = null;
            var maxAttempts = encryption == null ? MaxChunkDownloadAttempts : MaxEncryptedPayloadDownloadAttempts;
            var encryptedTarget = encryption == null ? target : new FileInfo(target.FullName + ".encrypted");
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var countedBytes = 0L;
                var useProxy = attempt % 2 == 0 &&
                               !string.IsNullOrWhiteSpace(part.ProxyUrl) &&
                               !string.Equals(part.ProxyUrl, part.Url, StringComparison.OrdinalIgnoreCase);
                var sourceUrl = useProxy ? part.ProxyUrl : part.Url;
                try
                {
                    encryptedTarget.Directory?.Create();
                    await using (var outStream = new FileStream(encryptedTarget.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true))
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
                        while (true)
                        {
                            var read = await inStream.ReadAsync(buffer, cancellationToken);
                            if (read <= 0)
                            {
                                break;
                            }

                            await outStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            countedBytes += read;
                            var currentBytes = Interlocked.Add(ref downloadedBytes, read);
                            ReportParallelProgress(currentBytes, part);
                        }

                        await outStream.FlushAsync(cancellationToken);
                    }

                    if (part.Size > 0 && countedBytes != part.Size)
                    {
                        throw new IOException($"Chunk {part.Index + 1} incompleto. Esperado {part.Size} bytes, recebido {countedBytes} bytes.");
                    }

                    encryptedTarget.Refresh();
                    if (!encryptedTarget.Exists || encryptedTarget.Length == 0)
                    {
                        throw new IOException("Chunk baixado estÃ¡ vazio: " + part.Name);
                    }

                    if (encryption != null)
                    {
                        await _encryption.DecryptFileAsync(
                            encryptedTarget,
                            target,
                            encryption.WithChunkIndex(part.Index).WithChunkCount(parts.Count),
                            cancellationToken);
                        TryDelete(encryptedTarget);
                    }

                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
                {
                    last = ex;
                    if (countedBytes > 0)
                    {
                        Interlocked.Add(ref downloadedBytes, -countedBytes);
                    }

                    TryDelete(target);
                    TryDelete(encryptedTarget);
                    AppLogger.Error("Download.ChunkRetry", ex, $"{part.Index}:{part.Name}:attempt {attempt}:{(useProxy ? "proxy" : "cdn")}");
                    var isEmptyResponse = ex is IOException io &&
                                          (io.Message.Contains("vazio", StringComparison.OrdinalIgnoreCase) ||
                                           io.Message.Contains("incompleto", StringComparison.OrdinalIgnoreCase));
                    var retryDelayMs = isEmptyResponse
                        ? Math.Min(15000, 750 * Math.Pow(2, attempt - 1))
                        : 250 * attempt * attempt;
                    await Task.Delay(TimeSpan.FromMilliseconds(retryDelayMs), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                    if (countedBytes > 0)
                    {
                        Interlocked.Add(ref downloadedBytes, -countedBytes);
                    }

                    TryDelete(target);
                    TryDelete(encryptedTarget);
                    break;
                }
            }

            if (encryption != null)
            {
                throw new EncryptedPayloadIntegrityException(encryption.Encryption.Mode, last);
            }

            throw new IOException($"NÃ£o foi possÃ­vel baixar a parte {part.Index + 1}/{parts.Count}.", last);
        }

        void ReportParallelProgress(long currentBytes, Part part)
        {
            if (onDetailedProgress == null)
            {
                return;
            }

            var percent = totalBytes <= 0
                ? 0
                : Math.Clamp((int)Math.Round(currentBytes * 100.0 / totalBytes), 0, 100);
            if (Interlocked.Exchange(ref lastDetailedPercent, percent) != percent)
            {
                onDetailedProgress(new DownloadProgress(percent, part.Name, currentBytes, totalBytes));
            }
        }
    }
    private static async Task DownloadSinglePartWithRetryAsync(
        HttpClient http,
        Part part,
        FileInfo target,
        Action<DownloadProgress>? onDetailedProgress,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                target.Directory?.Create();
                using var response = await http.GetAsync(part.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var output = new FileStream(target.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true))
                {
                    await CopyWithProgressAsync(input, output, 0, Math.Max(0, part.Size), part.Name, onDetailedProgress, cancellationToken);
                }

                target.Refresh();
                if (part.Size > 0 && target.Length != part.Size)
                {
                    throw new IOException($"Chunk {part.Index + 1} incompleto. Esperado {part.Size} bytes, recebido {target.Length} bytes.");
                }

                if (!target.Exists || target.Length == 0)
                {
                    throw new IOException("Chunk baixado estÃ¡ vazio: " + part.Name);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 4)
            {
                last = ex;
                TryDelete(target);
                AppLogger.Error("Download.StateChunkRetry", ex, $"{part.Index}:{part.Name}:attempt {attempt}");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                TryDelete(target);
                break;
            }
        }

        throw new IOException($"NÃ£o foi possÃ­vel baixar a parte {part.Index + 1}.", last);
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
            : new EncryptedPayloadContext(entry.FileId, entry.Encryption, role, 0);
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
    private static string SafeKind(string kind)
    {
        return string.Concat((kind ?? "preview").Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_'));
    }

    private DirectoryInfo ResolveTempDir(DirectoryInfo destDir, string threadId)
    {
        var suffix = string.IsNullOrWhiteSpace(threadId)
            ? "download"
            : threadId[..Math.Min(12, threadId.Length)];
        var configured = _settings.TempPath?.Trim();
        var baseDir = !string.IsNullOrWhiteSpace(configured) && SameVolume(configured, destDir.FullName)
            ? configured
            : destDir.FullName;
        return new DirectoryInfo(Path.Combine(baseDir, ".organessonium_parts_" + suffix));
    }

    private static bool SameVolume(string left, string right)
    {
        try
        {
            var leftRoot = Path.GetPathRoot(Path.GetFullPath(left));
            var rightRoot = Path.GetPathRoot(Path.GetFullPath(right));
            return !string.IsNullOrWhiteSpace(leftRoot) &&
                   string.Equals(leftRoot, rightRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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

    private static async Task<FileInfo> RelocateChunkAsync(
        FileInfo source,
        FileInfo target,
        CancellationToken cancellationToken)
    {
        source.Refresh();
        target.Directory?.Create();
        if (SameFilePath(source.FullName, target.FullName))
        {
            return source;
        }

        TryDelete(target);
        if (SameVolume(source.FullName, target.FullName))
        {
            File.Move(source.FullName, target.FullName, true);
        }
        else
        {
            await using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, DownloadBufferSize, true);
            await using var output = new FileStream(target.FullName, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, true);
            await input.CopyToAsync(output, cancellationToken);
            TryDelete(source);
        }

        target.Refresh();
        return target;
    }
    private static void ValidateDestination(DirectoryInfo destDir, long expectedBytes)
    {
        Directory.CreateDirectory(destDir.FullName);
        var root = System.IO.Path.GetPathRoot(destDir.FullName);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        if (expectedBytes > 0 && drive.AvailableFreeSpace > 0 && drive.AvailableFreeSpace < expectedBytes)
        {
            throw new IOException($"EspaÃ§o insuficiente no destino. NecessÃ¡rio: {FormatBytes(expectedBytes)}, disponÃ­vel: {FormatBytes(drive.AvailableFreeSpace)}.");
        }

        if (expectedBytes >= 4L * 1024 * 1024 * 1024 && drive.DriveFormat.Contains("FAT", StringComparison.OrdinalIgnoreCase))
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

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.00} {units[unit]}";
    }

    private sealed record Part(int Index, string Url, string Name, long Size, string MessageId = "", string BatchId = "", string ProxyUrl = "");
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

public sealed record DownloadProgress(int Percent, string Message, long DownloadedBytes, long TotalBytes);






