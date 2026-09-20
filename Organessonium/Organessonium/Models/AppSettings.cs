using System.Text.Json.Serialization;

namespace Organessonium.Models;

public sealed class AppSettings
{
    [JsonPropertyName("botToken")]
    public string BotToken { get; set; } = "";

    [JsonPropertyName("namesChannelId")]
    public string NamesChannelId { get; set; } = "";

    [JsonPropertyName("foldersForumId")]
    public string FoldersForumId { get; set; } = "";

    [JsonPropertyName("appsForumId")]
    public string AppsForumId { get; set; } = "";

    [JsonPropertyName("serialChannelId")]
    public string SerialChannelId { get; set; } = "";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("cardSize")]
    public string CardSize { get; set; } = "medium";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "system";

    [JsonPropertyName("downloadThreads")]
    public int DownloadThreads { get; set; } = 4;

    [JsonPropertyName("uploadThreads")]
    public int UploadThreads { get; set; } = 4;

    [JsonPropertyName("previewThreads")]
    public int PreviewThreads { get; set; } = 4;

    [JsonPropertyName("downloadPath")]
    public string DownloadPath { get; set; } = "";

    [JsonPropertyName("tempPath")]
    public string TempPath { get; set; } = "";

    [JsonPropertyName("dataPath")]
    public string DataPath { get; set; } = "";

    [JsonPropertyName("previewEnabled")]
    public bool PreviewEnabled { get; set; } = true;

    [JsonPropertyName("videoPreviewQuality")]
    public string VideoPreviewQuality { get; set; } = "low";

    [JsonPropertyName("animationsEnabled")]
    public bool AnimationsEnabled { get; set; } = true;

    [JsonPropertyName("hardwareAcceleration")]
    public bool HardwareAcceleration { get; set; } = true;

    [JsonPropertyName("autoResumeTransfers")]
    public bool AutoResumeTransfers { get; set; }

    [JsonPropertyName("keepTransferHistory")]
    public bool KeepTransferHistory { get; set; }

    [JsonPropertyName("cleanupTempOnCancel")]
    public bool CleanupTempOnCancel { get; set; } = true;

    [JsonPropertyName("maxParallelUploads")]
    public int MaxParallelUploads { get; set; } = 3;

[JsonPropertyName("maxParallelDownloads")]
    public int MaxParallelDownloads { get; set; } = 4;

    [JsonPropertyName("searchWorkers")]
    public int SearchWorkers { get; set; } = 4;

    [JsonPropertyName("encryptionMode")]
    public string EncryptionMode { get; set; } = EncryptionModes.Standard;

    [JsonPropertyName("customKeyAvailable")]
    public bool CustomKeyAvailable { get; set; }

    [JsonPropertyName("proxy")]
    public ProxySettings Proxy { get; set; } = new();

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BotToken) &&
        !string.IsNullOrWhiteSpace(NamesChannelId) &&
        !string.IsNullOrWhiteSpace(FoldersForumId) &&
        !string.IsNullOrWhiteSpace(AppsForumId) &&
        !string.IsNullOrWhiteSpace(SerialChannelId);

    public AppSettings Clone()
    {
        return new AppSettings
        {
            BotToken = BotToken,
            NamesChannelId = NamesChannelId,
            FoldersForumId = FoldersForumId,
            AppsForumId = AppsForumId,
            SerialChannelId = SerialChannelId,
            Theme = Theme,
            CardSize = CardSize,
            Language = Language,
            DownloadThreads = DownloadThreads,
            UploadThreads = UploadThreads,
            PreviewThreads = PreviewThreads,
            DownloadPath = DownloadPath,
            TempPath = TempPath,
            DataPath = DataPath,
            PreviewEnabled = PreviewEnabled,
            VideoPreviewQuality = VideoPreviewQuality,
            AnimationsEnabled = AnimationsEnabled,
            HardwareAcceleration = HardwareAcceleration,
            AutoResumeTransfers = AutoResumeTransfers,
            KeepTransferHistory = KeepTransferHistory,
            CleanupTempOnCancel = CleanupTempOnCancel,
MaxParallelUploads = MaxParallelUploads,
            MaxParallelDownloads = MaxParallelDownloads,
            SearchWorkers = SearchWorkers,
            EncryptionMode = EncryptionMode,
            CustomKeyAvailable = CustomKeyAvailable,
            Proxy = Proxy?.Clone() ?? new ProxySettings()
        };
    }
}

