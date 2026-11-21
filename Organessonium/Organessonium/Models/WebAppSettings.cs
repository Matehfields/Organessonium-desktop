using System.Text.Json.Serialization;
using Organessonium.Services;

namespace Organessonium.Models;

/// <summary>
/// Dados de configuração publicados na interface WebView2. Credenciais nunca
/// fazem parte deste contrato; a interface recebe apenas indicadores de presença.
/// </summary>
public sealed class WebAppSettings
{
    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("closeToTray")]
    public bool CloseToTray { get; set; }

    [JsonPropertyName("syncPaused")]
    public bool SyncPaused { get; set; }

    [JsonPropertyName("botTokenConfigured")]
    public bool BotTokenConfigured { get; set; }

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

    [JsonPropertyName("accentColorMode")]
    public string AccentColorMode { get; set; } = "system";

    [JsonPropertyName("accentColor")]
    public string AccentColor { get; set; } = "#0078D4";

    [JsonPropertyName("cardSize")]
    public string CardSize { get; set; } = "medium";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "system";

    [JsonPropertyName("downloadThreads")]
    public int DownloadThreads { get; set; } = 4;

    [JsonPropertyName("uploadThreads")]
    public int UploadThreads { get; set; } = 4;

    [JsonPropertyName("forceUploadParallelism")]
    public bool ForceUploadParallelism { get; set; }

    [JsonPropertyName("previewThreads")]
    public int PreviewThreads { get; set; } = 4;

    [JsonPropertyName("videoPrefetchChunks")]
    public int VideoPrefetchChunks { get; set; } = 1;

    [JsonPropertyName("downloadPath")]
    public string DownloadPath { get; set; } = "";

    [JsonPropertyName("dataPath")]
    public string DataPath { get; set; } = "";

    [JsonPropertyName("previewEnabled")]
    public bool PreviewEnabled { get; set; } = true;

    [JsonPropertyName("generateVideoPreviews")]
    public bool GenerateVideoPreviews { get; set; } = true;

    [JsonPropertyName("videoPreviewQuality")]
    public string VideoPreviewQuality { get; set; } = "low";

    [JsonPropertyName("animationsEnabled")]
    public bool AnimationsEnabled { get; set; } = true;

    [JsonPropertyName("hardwareAcceleration")]
    public bool HardwareAcceleration { get; set; } = true;

    [JsonPropertyName("maxParallelUploads")]
    public int MaxParallelUploads { get; set; } = 3;

    [JsonPropertyName("encryptionMode")]
    public string EncryptionMode { get; set; } = EncryptionModes.Standard;

    [JsonPropertyName("customKeyAvailable")]
    public bool CustomKeyAvailable { get; set; }

    [JsonPropertyName("proxy")]
    public WebProxySettings Proxy { get; set; } = new();

    public static WebAppSettings FromSettings(AppSettings settings)
    {
        return new WebAppSettings
        {
            StartWithWindows = settings.StartWithWindows,
            CloseToTray = settings.CloseToTray,
            SyncPaused = settings.SyncPaused,
            BotTokenConfigured = !string.IsNullOrWhiteSpace(settings.BotToken),
            NamesChannelId = settings.NamesChannelId,
            FoldersForumId = settings.FoldersForumId,
            AppsForumId = settings.AppsForumId,
            SerialChannelId = settings.SerialChannelId,
            Theme = settings.Theme,
            AccentColorMode = settings.AccentColorMode,
            AccentColor = settings.AccentColor,
            CardSize = settings.CardSize,
            Language = settings.Language,
            DownloadThreads = settings.DownloadThreads,
            UploadThreads = settings.UploadThreads,
            ForceUploadParallelism = settings.ForceUploadParallelism,
            PreviewThreads = settings.PreviewThreads,
            VideoPrefetchChunks = settings.VideoPrefetchChunks,
            DownloadPath = settings.DownloadPath,
            DataPath = settings.DataPath,
            PreviewEnabled = settings.PreviewEnabled,
            GenerateVideoPreviews = settings.GenerateVideoPreviews,
            VideoPreviewQuality = settings.VideoPreviewQuality,
            AnimationsEnabled = settings.AnimationsEnabled,
            HardwareAcceleration = settings.HardwareAcceleration,
            MaxParallelUploads = settings.MaxParallelUploads,
            EncryptionMode = settings.EncryptionMode,
            CustomKeyAvailable = settings.CustomKeyAvailable,
            Proxy = WebProxySettings.FromSettings(settings.Proxy)
        };
    }
}

public sealed class WebProxySettings
{
    [JsonPropertyName("proxyMode")]
    public string Mode { get; set; } = "automatic";

    [JsonPropertyName("proxyHost")]
    public string Host { get; set; } = "";

    [JsonPropertyName("proxyPort")]
    public int Port { get; set; }

    [JsonPropertyName("proxyProtocol")]
    public string Protocol { get; set; } = "auto";

    [JsonPropertyName("proxyAuthenticationEnabled")]
    public bool AuthenticationEnabled { get; set; }

    [JsonPropertyName("proxyUsername")]
    public string Username { get; set; } = "";

    [JsonPropertyName("proxyPasswordConfigured")]
    public bool PasswordConfigured { get; set; }

    public static WebProxySettings FromSettings(ProxySettings proxy)
    {
        return new WebProxySettings
        {
            Mode = SettingsStore.ProxyModeLabel(proxy.Mode),
            Host = proxy.Host,
            Port = proxy.Port,
            Protocol = SettingsStore.ProxyProtocolLabel(proxy.Protocol),
            AuthenticationEnabled = proxy.AuthenticationEnabled,
            Username = proxy.Username,
            PasswordConfigured = !string.IsNullOrEmpty(proxy.Password)
        };
    }
}
