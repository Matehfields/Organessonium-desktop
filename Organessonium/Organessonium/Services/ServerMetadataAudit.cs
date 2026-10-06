using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.Services;

public sealed record ServerMetadataAuditResult(int MetadataCount, int UnreadableCount, int MissingSerialReferences, int InvalidSerials);

public sealed class ServerMetadataAudit
{
    private readonly DiscordApiClient _discord;
    private readonly AppSettings _settings;
    private readonly MetadataService _metadata;
    private readonly IMetadataCodec _metadataCodec;
    private readonly SerialMetadataTransport _transport;
    private readonly FolderSerialCodec _codec = new();

    public ServerMetadataAudit(DiscordApiClient discord, AppSettings settings, IMetadataCodec? metadataCodec = null)
    {
        _discord = discord;
        _settings = settings;
        _metadataCodec = metadataCodec ?? MetadataCodec.Standard();
        _metadata = new MetadataService(_metadataCodec, discord.DownloadAttachmentBytesAsync);
        _transport = new SerialMetadataTransport(_metadataCodec);
    }

    public async Task<ServerMetadataAuditResult> WriteReportAsync(string reportPath, CancellationToken cancellationToken = default)
    {
        var workers = Math.Clamp(_settings.SearchWorkers, 1, 8);
        var names = await _discord.GetMessagesInParallelAsync(_settings.NamesChannelId, workers, cancellationToken);
        var threads = await _discord.GetForumThreadsAsync(_settings.FoldersForumId, cancellationToken);
        var report = new StringBuilder("Organessonium server metadata audit\n");
        var metadataIds = new HashSet<string>(StringComparer.Ordinal);
        var serialChildren = new List<(string SerialId, FolderSerialEntry Child)>();
        var metadataCount = 0;
        var unreadable = 0;
        var invalidSerials = 0;

        async Task ScanAsync(string source, IReadOnlyList<DiscordMessageDto> messages)
        {
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (message.Author?.Bot != true)
                {
                    continue;
                }

                var candidate = message.Content?.TrimStart().StartsWith('{') == true ||
                    message.Attachments.Any(attachment =>
                        attachment.FileName.Equals(MetadataService.FileName, StringComparison.OrdinalIgnoreCase));
                if (!candidate)
                {
                    continue;
                }

                var envelope = await _metadata.ReadEnvelopeAsync(message, cancellationToken);
                if (envelope == null)
                {
                    using var record = await _metadata.ReadJsonAsync(message, cancellationToken);
                    if (record?.RootElement.TryGetProperty("entryType", out var type) == true &&
                        type.GetString() is "syncRevision" or "syncApplied") continue;
                    unreadable++;
                    report.AppendLine($"UNREADABLE source={source} messageId={message.Id}");
                    continue;
                }

                metadataCount++;
                metadataIds.Add(message.Id);
                report.AppendLine($"METADATA source={source} messageId={message.Id} " +
                    JsonSerializer.Serialize(envelope.Entry, envelope.Entry.GetType(), JsonOptions.Web));
            }
        }

        await ScanAsync("names", names);
        Console.WriteLine($"Audit: names scanned; folder threads={threads.Count}");
        var scannedThreads = 0;
        foreach (var thread in threads)
        {
            try
            {
                var messages = await _discord.GetMessagesInParallelAsync(thread.Id, workers, cancellationToken);
                await ScanAsync("folders/" + thread.Id, messages);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                unreadable++;
                report.AppendLine($"UNREADABLE source=folders/{thread.Id} error={ex.GetType().Name}");
            }
            scannedThreads++;
            if (scannedThreads % 10 == 0 || scannedThreads == threads.Count)
            {
                Console.WriteLine($"Audit: folder threads {scannedThreads}/{threads.Count}");
            }
        }

        Console.WriteLine("Audit: scanning serials");
        var serialMessages = await _discord.GetMessagesInParallelAsync(_settings.SerialChannelId, workers, cancellationToken);
        foreach (var message in serialMessages.Where(message => message.Author?.Bot == true &&
                     SerialMessageEnvelope.TryParse(message.Content, out _, _metadataCodec)))
        {
            SerialMessageEnvelope.TryParse(message.Content, out var serialEnvelope, _metadataCodec);
            try
            {
                var attachment = message.Attachments.First(attachment =>
                    attachment.FileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));
                var bytes = await _discord.DownloadAttachmentBytesAsync(attachment, cancellationToken);
                var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!hash.Equals(serialEnvelope!.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("SHA-256 mismatch");
                }
                bytes = _transport.Decode(bytes);
                var serial = _codec.DecodeFolder(bytes);
                var version = bytes[4] | bytes[5] << 8;
                report.AppendLine($"SERIAL folderId={serial.FolderId} messageId={message.Id} version={version} children={serial.Children.Count}");
                if (version > FolderSerialCodec.PublishedVersion)
                {
                    invalidSerials++;
                    report.AppendLine($"MOBILE_INCOMPATIBLE_SERIAL folderId={serial.FolderId} messageId={message.Id}");
                }
                serialChildren.AddRange(serial.Children.Select(child => (serial.FolderId, child)));
            }
            catch (Exception ex)
            {
                invalidSerials++;
                report.AppendLine($"INVALID_SERIAL folderId={serialEnvelope?.FolderId} messageId={message.Id} error={ex.GetType().Name}");
            }
        }

        var missing = 0;
        foreach (var (serialId, child) in serialChildren)
        {
            if (string.IsNullOrWhiteSpace(child.NamesMessageId) || !metadataIds.Contains(child.NamesMessageId))
            {
                missing++;
                report.AppendLine($"MISSING_METADATA serialId={serialId} childId={child.Id} namesMessageId={child.NamesMessageId}");
            }
        }

        report.Insert(0, $"metadata={metadataCount} unreadable={unreadable} missingReferences={missing} invalidSerials={invalidSerials}\n");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8, cancellationToken);
        return new ServerMetadataAuditResult(metadataCount, unreadable, missing, invalidSerials);
    }
}
