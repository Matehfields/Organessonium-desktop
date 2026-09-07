using System.Text.Json.Serialization;

namespace Organessonium.Models;

public static class TransferStatuses
{
    public const string Queued = "queued";
    public const string Preparing = "preparing";
    public const string Uploading = "uploading";
    public const string Downloading = "downloading";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Recovering = "recovering";

    public static bool IsRecoverable(string? status)
    {
        return string.Equals(status, Queued, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, Preparing, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, Uploading, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, Downloading, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, Paused, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, Recovering, StringComparison.OrdinalIgnoreCase);
    }
}

public static class TransferTypes
{
    public const string Upload = "upload";
    public const string Download = "download";
}

public sealed class TransferState
{
    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("transferType")]
    public string TransferType { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = TransferStatuses.Queued;

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = Now();

    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get; set; } = Now();

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = "";

    [JsonPropertyName("destinationPath")]
    public string DestinationPath { get; set; } = "";

    [JsonPropertyName("tempPath")]
    public string TempPath { get; set; } = "";

    [JsonPropertyName("fileId")]
    public string FileId { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";

    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
[JsonPropertyName("targetPath")]
    public string TargetPath { get; set; } = "/";

    [JsonPropertyName("targetFolderId")]
    public string? TargetFolderId { get; set; }

    [JsonPropertyName("threadId")]
    public string ThreadId { get; set; } = "";

    [JsonPropertyName("batchId")]
    public string BatchId { get; set; } = "";

    [JsonPropertyName("chunkSizeBytes")]
    public long ChunkSizeBytes { get; set; }

    [JsonPropertyName("totalChunks")]
    public int TotalChunks { get; set; }

    [JsonPropertyName("uploadedChunks")]
    public List<TransferChunkState> UploadedChunks { get; set; } = [];

    [JsonPropertyName("chunks")]
    public List<TransferChunkState> Chunks { get; set; } = [];

    [JsonPropertyName("downloadedChunks")]
    public List<int> DownloadedChunks { get; set; } = [];

    [JsonPropertyName("pendingChunks")]
    public List<int> PendingChunks { get; set; } = [];

    [JsonPropertyName("thumbnailMessageId")]
    public string? ThumbnailMessageId { get; set; }

    [JsonPropertyName("videoPreview480pChunks")]
    public List<string> VideoPreview480pChunks { get; set; } = [];

    [JsonPropertyName("videoPreview480pSizeBytes")]
    public long VideoPreview480pSizeBytes { get; set; }

    [JsonPropertyName("metadataMessageId")]
    public string? MetadataMessageId { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("retryCount")]
    public int RetryCount { get; set; }

    public void Touch()
    {
        UpdatedAt = Now();
    }

    public void Normalize()
    {
        TransferId = string.IsNullOrWhiteSpace(TransferId) ? Guid.NewGuid().ToString("N") : TransferId;
        Status = string.IsNullOrWhiteSpace(Status) ? TransferStatuses.Queued : Status;
        CreatedAt = string.IsNullOrWhiteSpace(CreatedAt) ? Now() : CreatedAt;
        UpdatedAt = string.IsNullOrWhiteSpace(UpdatedAt) ? CreatedAt : UpdatedAt;
        SourcePath ??= "";
        DestinationPath ??= "";
        TempPath ??= "";
        FileId ??= "";
        FileName ??= "";
        MimeType ??= "";
        TargetPath = PathRules.Normalize(TargetPath);
        ThreadId ??= "";
        BatchId ??= "";
        UploadedChunks ??= [];
        Chunks ??= [];
        DownloadedChunks ??= [];
        PendingChunks ??= [];
        VideoPreview480pChunks ??= [];
    }

    public static string Now()
    {
        return DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }
}

public sealed class TransferChunkState
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }
[JsonPropertyName("downloaded")]
    public bool Downloaded { get; set; }

    [JsonPropertyName("tempChunkPath")]
    public string? TempChunkPath { get; set; }
}
