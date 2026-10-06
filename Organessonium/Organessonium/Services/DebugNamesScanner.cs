using System.Net.Http;
using System.Text;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

public sealed class DebugNamesScanner
{
    private readonly DiscordApiClient _discord;
    private readonly SettingsStore _store;
    private readonly AppSettings _settings;
    private readonly MetadataService _metadata;
    private readonly int _parallelWorkers;

    public enum Verdict { Accepted, Skipped }

    public sealed class MessageResult
    {
        public required string Id { get; init; }
        public required bool AuthorIsBot { get; init; }
        public required string ContentPreview { get; init; }
        public required string AttachmentNames { get; init; }
        public required Verdict Verdict { get; set; }
        public string Reason = "";
        public string Source = "";
        public string SchemaVersion = "";
        public string EntryType = "";
        public string EntryName = "";
        public string CacheStatus = "";
    }

    public DebugNamesScanner(
        DiscordApiClient discord,
        SettingsStore store,
        AppSettings settings)
    {
        _discord = discord;
        _store = store;
        _settings = settings;
        _metadata = new MetadataService(MetadataCodec.ForStore(store), discord.DownloadAttachmentBytesAsync);
        _parallelWorkers = Math.Clamp(settings.SearchWorkers, 1, 8);
    }

    public async Task<string> ScanAsync(
        CancellationToken cancellationToken = default,
        Action<string>? onProgress = null,
        bool useParallel = true)
    {
        var messages = new List<DiscordMessageDto>();
        if (useParallel)
        {
            onProgress?.Invoke("start: fetch paralelo (" + _parallelWorkers + " agentes) de " + _settings.NamesChannelId);
            await _discord.GetMessagesInParallelAsync(_settings.NamesChannelId, _parallelWorkers, cancellationToken, batch =>
            {
                messages.AddRange(batch);
                onProgress?.Invoke($"batch {messages.Count} mensagens ate agora");
                return Task.CompletedTask;
            });
        }
        else
        {
            onProgress?.Invoke("start: fetch sequencial de " + _settings.NamesChannelId);
            await _discord.GetMessagesAsync(_settings.NamesChannelId, null, cancellationToken, batch =>
            {
                messages.AddRange(batch);
                onProgress?.Invoke($"batch {messages.Count} mensagens ate agora");
                return Task.CompletedTask;
            });
        }

        onProgress?.Invoke("fetch completo: " + messages.Count + " mensagens");

        var cached = new Dictionary<string, FolderIndexMapping>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in _store.LoadFolderIndexMappings().Values)
        {
            if (!string.IsNullOrWhiteSpace(mapping.NamesMessageId))
            {
                cached[mapping.NamesMessageId] = mapping;
            }
        }

        var results = new List<MessageResult>(messages.Count);
        var index = 0;
        foreach (var message in messages)
        {
            var result = await AnalyzeAsync(message, cancellationToken);
            result.CacheStatus = cached.ContainsKey(result.Id) ? "no-cache" : "ausente-no-cache";

            results.Add(result);
            index++;
            if (index % 25 == 0 || index == messages.Count)
            {
                onProgress?.Invoke($"analisadas {index}/{messages.Count}");
            }
        }

        onProgress?.Invoke("analise completa");
        return BuildReport(results, cached, messages.Count);
    }

    private async Task<MessageResult> AnalyzeAsync(
        DiscordMessageDto message,
        CancellationToken cancellationToken)
    {
        var preview = message.Content.Length > 100
            ? message.Content[..100] + "..."
            : message.Content;
        var attachments = string.Join(", ", message.Attachments.Select(a => a.FileName));

        var result = new MessageResult
        {
            Id = message.Id,
            AuthorIsBot = message.Author?.Bot == true,
            ContentPreview = preview.Replace("\n", "\\n"),
            AttachmentNames = attachments,
            Verdict = Verdict.Skipped
        };

        if (message.Author?.Bot != true)
        {
            result.Reason = "nao-bot (author=" + (message.Author == null ? "ausente" : "bot=" + message.Author.Bot) + ")";
            return result;
        }

        var envelope = await _metadata.ReadEnvelopeAsync(message, cancellationToken);
        if (envelope != null)
        {
            result.Source = envelope.Entry.EntryType;
            result.EntryType = envelope.Entry.EntryType;
            result.EntryName = envelope.Entry switch
            {
                FileMetadata file => file.Name,
                FolderMetadata folder => folder.Name,
                FolderIndexMetadata index => index.FolderId + " -> " + index.ThreadId,
                _ => ""
            };
            if (envelope.Entry is FolderIndexMetadata)
            {
                result.Verdict = Verdict.Accepted;
            }
            else
            {
                result.Reason = "metadata de arquivo ou pasta não pertence ao canal names";
            }

            return result;
        }

        var (inlineReason, schemaVersion, entryType) = DiagnoseInline(message.Content);
        result.SchemaVersion = schemaVersion;
        result.EntryType = entryType;

        var attachmentReason = await DiagnoseAttachmentAsync(message, cancellationToken);
        result.Reason = $"inline: {inlineReason} | anexo: {attachmentReason}";
        return result;
    }

    private static (string Reason, string SchemaVersion, string EntryType) DiagnoseInline(string content)
    {
        var trimmed = content.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return ("conteudo-vazio", "", "");
        }

        if (!trimmed.StartsWith('{'))
        {
            return (trimmed.StartsWith("\uFEFF{", StringComparison.Ordinal)
                ? "comeca-com-BOM-\\uFEFF"
                : $"nao-comeca-com-{{ (primeiros: {Preview(trimmed, 40)})}}", "", "");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed);
        }
        catch (Exception ex)
        {
            return ($"json-invalido: {ex.Message}", "", "");
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var version))
            {
                return ("schemaVersion-ausente", "-", "-");
            }

            string schema = version.ValueKind == JsonValueKind.String
                ? version.GetString() ?? "?"
                : version.GetRawText();
            if (!version.TryGetInt32(out var parsed) || parsed != MetadataService.SchemaVersion)
            {
                return ($"schemaVersion!={MetadataService.SchemaVersion}", schema, "-");
            }

            if (!root.TryGetProperty("entryType", out var entryTypeElement))
            {
                return ($"entryType-ausente (schema={schema})", schema, "-");
            }

            var entryType = entryTypeElement.GetString() ?? "?";
            if (entryType != "file" && entryType != "folder" && entryType != "folderIndex")
            {
                return ($"entryType-desconhecido (schema={schema})", schema, entryType);
            }

            var validation = entryType switch
            {
                "file" => DiagnoseFileValidation(root, schema),
                "folder" => DiagnoseFolderValidation(root, schema),
                _ => DiagnoseFolderIndexValidation(root, schema)
            };
            if (validation != null)
            {
                return (validation, schema, entryType);
            }

            return ($"json-ok-mas-nao-aceito (schema={schema}, entryType={entryType})", schema, entryType);
        }
    }

    private static string? DiagnoseFileValidation(JsonElement root, string schema)
    {
        FileMetadata file;
        try
        {
            file = root.Deserialize<FileMetadata>(JsonOptions.Web) ?? new FileMetadata();
        }
        catch (Exception ex)
        {
            return $"deserialize-file-falhou: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(file.FileId)) return "fileId-ausente";
        if (string.IsNullOrWhiteSpace(file.ThreadId)) return "threadId-ausente";
        if (string.IsNullOrWhiteSpace(file.Name)) return "name-ausente";
        if (file.SizeBytes < 0) return $"sizeBytes-invalido ({file.SizeBytes})";
        if (file.Chunks == null || file.Chunks.Count == 0) return "chunks-ausentes (arquivo vazio?)";
        if (file.Preview != null)
        {
            if (file.Preview.Video480p != null &&
                (!file.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
                 file.Preview.Video480p.SizeBytes <= 0 ||
                 file.Preview.Video480p.Chunks == null ||
                 file.Preview.Video480p.Chunks.Count == 0))
            {
                return "preview-480p-descartada (video480p invalido)";
            }
        }

        return $"json-ok-mas-nao-aceito (schema={schema}, entryType=file)";
    }

    private static string? DiagnoseFolderValidation(JsonElement root, string schema)
    {
        FolderMetadata folder;
        try
        {
            folder = root.Deserialize<FolderMetadata>(JsonOptions.Web) ?? new FolderMetadata();
        }
        catch (Exception ex)
        {
            return $"deserialize-folder-falhou: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(folder.FolderId)) return "folderId-ausente";
        if (string.IsNullOrWhiteSpace(folder.Name)) return "name-ausente";
        return $"json-ok-mas-nao-aceito (schema={schema}, entryType=folder)";
    }

    private static string? DiagnoseFolderIndexValidation(JsonElement root, string schema)
    {
        FolderIndexMetadata index;
        try
        {
            index = root.Deserialize<FolderIndexMetadata>(JsonOptions.Web) ?? new FolderIndexMetadata();
        }
        catch (Exception ex)
        {
            return $"deserialize-folderIndex-falhou: {ex.Message}";
        }

        if (string.IsNullOrWhiteSpace(index.FolderId)) return "folderId-ausente";
        if (string.IsNullOrWhiteSpace(index.ThreadId)) return "threadId-ausente";
        return $"json-ok-mas-nao-aceito (schema={schema}, entryType=folderIndex)";
    }

    private static async Task<string> DiagnoseAttachmentAsync(
        DiscordMessageDto message,
        CancellationToken cancellationToken)
    {
        var attachment = message.Attachments.FirstOrDefault(a =>
            a.FileName.Equals(MetadataService.FileName, StringComparison.OrdinalIgnoreCase));
        if (attachment == null)
        {
            return "sem-anexo-metadata.json";
        }

        if (attachment.Size > MetadataService.MaxMetadataBytes)
        {
            return $"anexo-grande ({attachment.Size} bytes)";
        }

        try
        {
            using var http = NetworkConfig.Current.CreateClient();
            using var response = await http.GetAsync(attachment.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return DiagnoseInline(document.RootElement.GetRawText())
                .Reason.Replace("inline:", "anexo-json:");
        }
        catch (Exception ex)
        {
            return $"anexo-download/parse-falhou: {ex.Message}";
        }
    }

    private string BuildReport(
        IReadOnlyList<MessageResult> results,
        IReadOnlyDictionary<string, FolderIndexMapping> cached,
        int totalMessages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== SCAN CANAL DE NAMES ===");
        sb.AppendLine($"canal: {_settings.NamesChannelId}");
        sb.AppendLine($"total de mensagens no canal: {totalMessages}");
        sb.AppendLine($"mensagens de bot: {results.Count(r => r.AuthorIsBot)} | nao-bot: {results.Count(r => !r.AuthorIsBot)}");
        sb.AppendLine();

        var accepted = results.Where(r => r.Verdict == Verdict.Accepted).ToList();
        var skipped = results.Where(r => r.Verdict == Verdict.Skipped).ToList();
        sb.AppendLine($"ACEITAS: {accepted.Count} (mappings folderIndex)");
        sb.AppendLine($"IGNORADAS: {skipped.Count}");
        sb.AppendLine();

        sb.AppendLine("--- IGNORADAS POR MOTIVO (inline motivo | anexo motivo) ---");
        foreach (var group in skipped
                     .GroupBy(r => r.Source.Length == 0 ? "não reconhecido" : "tipo reconhecido")
                     .OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"{group.Count(),4}  {group.Key}");
        }

        sb.AppendLine();
        sb.AppendLine("--- DETALHE DE CADA MENSAGEM IGNORADA ---");
        sb.AppendLine("id | authorBot | schema | entryType | motivo | cache | content | anexos");
        foreach (var r in skipped.OrderBy(r => r.Id))
        {
            sb.AppendLine($"{r.Id} | bot={r.AuthorIsBot} | schema={r.SchemaVersion} | conteúdo omitido");
        }

        sb.AppendLine();
        sb.AppendLine("--- DETALHE DE CADA MENSAGEM ACEITA (id | tipo | nome) ---");
        foreach (var r in accepted.OrderBy(r => r.Id))
        {
            sb.AppendLine($"{r.Id} | schema={r.SchemaVersion} | conteúdo omitido");
        }

        sb.AppendLine();
        var cachedIds = cached.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var acceptedIds = accepted.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inCacheNotInScan = cachedIds.Where(id => !acceptedIds.Contains(id)).OrderBy(id => id).ToList();
        var inScanNotInCache = acceptedIds.Where(id => !cachedIds.Contains(id)).OrderBy(id => id).ToList();
        sb.AppendLine($"--- COMPARACAO COM CACHE DO BANCO ({cachedIds.Count} entries) ---");
        sb.AppendLine($"no banco mas ausentes no scan atual: {inCacheNotInScan.Count}");
        foreach (var id in inCacheNotInScan)
        {
            sb.AppendLine($"  {id}");
        }

        sb.AppendLine($"no scan atual mas ausentes no banco: {inScanNotInCache.Count}");
        foreach (var id in inScanNotInCache)
        {
            sb.AppendLine($"  {id}");
        }

        return sb.ToString();
    }

    private static string Preview(string value, int max)
    {
        return value.Length <= max ? value : value[..max] + "...";
    }
}
