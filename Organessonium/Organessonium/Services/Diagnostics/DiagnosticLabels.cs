namespace Organessonium.Services.Diagnostics;

// Closed labels, never character-filtered copies of potentially private input.
internal static class DiagnosticLabels
{
    private static readonly HashSet<string> BridgeCommands = new(StringComparer.Ordinal)
    {
        "connect", "logout", "openGuide", "loadFolder", "loadApps", "loadFavorites", "loadSynchronizedDevices",
        "setFavorite", "refreshCurrentFolder", "refreshServerIndex", "loadVisibleThumbnails", "loadVisibleFolderStatistics",
        "requestPreview", "videoPlayback", "clearPreviewCache", "getPreviewCacheSize", "openPreviewMedia", "createFolder",
        "moveItems", "moveItem", "updateFolderColor", "renameItem", "requestFileInformation", "copyFile", "downloadVersion",
        "resumeTransfers", "failInterruptedTransfers", "deleteApp", "deleteItems", "restoreItems", "saveMarkdown",
        "readMarkdown", "uploadFiles", "uploadFolder", "downloadApp", "downloadItems", "cancelOperation", "cancelUploadFile",
        "saveSettings", "updateSettings", "syncCreate", "syncLink", "syncRemove", "syncPause", "syncNow", "syncResolve"
    };
    private static readonly HashSet<string> Labels = new(StringComparer.Ordinal)
    { "bootstrap", "runtime", "wpf", "task", "webview", "javascript", "bridge", "work", "test", "http",
        "constructWpf", "runWpf", "App.DispatcherUnhandledException", "App.UnhandledException", "App.UnobservedTaskException",
        "Bootstrap.Failed", "Video.Stream", "Video.FastStart", "Video.Progress", "PreviewUpload", "VideoPreviewUpload",
        "_onAppsLoaded", "_onLocalAppsChanged", "_onLibraryStatistics", "_onError", "_onNonfatalError", "_opsAdd", "_opsUpdate",
        "_opsProgress", "_opsUploadFileProgress", "_opsTransferMetrics", "_opsDone", "_onBackgroundActivity" };
    private static readonly HashSet<string> HttpWords = new(StringComparer.Ordinal)
    { "api", "v10", "channels", "messages", "threads", "guilds", "users", "me", "attachments", "active", "archived", "public", "id" };

    internal static string Bridge(string? label) => label != null && BridgeCommands.Contains(label) ? label : "unknownCommand";
    internal static string Area(string? label)
    {
        if (label == null) return "application";
        if (Labels.Contains(label) || BridgeCommands.Contains(label) || label == "unknownCommand") return label;
        var words = label.Split('.');
        if (words.Length > 1 && words[0] is "GET" or "POST" or "PATCH" or "DELETE" or "PUT" or "HEAD" &&
            words.Skip(1).All(HttpWords.Contains)) return label;
        return "application";
    }
}
