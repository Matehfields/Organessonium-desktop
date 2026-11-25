using System.Text;
using System.Text.Json;

namespace Organessonium.Bridge;

/// <summary>
/// Valida mensagens recebidas do WebView2 antes de encaminhá-las ao controlador.
/// A página local é a única origem aceita e cada comando precisa estar na lista.
/// </summary>
internal static class WebMessagePolicy
{
    internal const int MaximumMessageBytes = 32 * 1024 * 1024;
    private const int MaximumMethodLength = 64;
    private const int MaximumArguments = 64;
    private const string LocalHost = "organessonium.local";

    // S: texto, N: número, B: booleano, X: texto ou nulo. Minúsculas são opcionais.
    private static readonly Dictionary<string, string> Signatures = new(StringComparer.Ordinal)
    {
        ["retryStartupConnect"] = "", ["openSetup"] = "", ["setTitleBarDragRegion"] = "rectangles",
        ["setPreviewVisible"] = "B", ["openExternalLink"] = "S", ["resolveAppClose"] = "B",
        ["loadSyncState"] = "", ["discoverSyncFolders"] = "", ["chooseSyncFolder"] = "", ["openSyncFolder"] = "S",
        ["addSyncFolder"] = "SB", ["updateSyncFolder"] = "S", ["removeSyncFolder"] = "S", ["syncNow"] = "S",
        ["pauseSync"] = "B", ["resolveSyncConflict"] = "SSSB", ["setSyncPreferences"] = "BB",
        ["connect"] = "SSSSSBSS", ["logout"] = "", ["openGuide"] = "", ["setCurrentPath"] = "SSb",
        ["loadApps"] = "", ["loadFavorites"] = "b", ["loadSynchronizedDevices"] = "b", ["setFavorite"] = "SB",
        ["searchLibrary"] = "NS",
        ["refreshCurrentFolder"] = "", ["refreshServerIndex"] = "b", ["loadVisibleThumbnails"] = "SS",
        ["loadVisibleFolderStatistics"] = "S", ["requestPreview"] = "SS", ["requestVideoQuality"] = "SSS",
        ["closePreview"] = "S", ["videoPlayback"] = "SSNNBBB", ["clearPreviewCache"] = "", ["getPreviewCacheSize"] = "",
        ["videoPlayerStats"] = "SS", ["saveVideoPreferences"] = "S", ["prepareVideoFrameDownload"] = "SS",
        ["openPreviewMedia"] = "S", ["requestPdfPages"] = "SNN", ["createFolder"] = "S", ["moveItems"] = "SS",
        ["moveItem"] = "SS", ["updateFolderColor"] = "SS", ["renameItem"] = "SS", ["requestFileInformation"] = "SS",
        ["copyFile"] = "S", ["downloadVersion"] = "SS", ["startUpload"] = "", ["startFolderUpload"] = "",
        ["resolveUploadConflict"] = "SSB", ["cancelTransfers"] = "S", ["cancelUploadFile"] = "SN",
        ["toggleTransferPause"] = "SN", ["resumeTransfers"] = "", ["failInterruptedTransfers"] = "",
        ["startDownload"] = "SS", ["deleteApp"] = "SSb", ["deleteItems"] = "SB", ["emptyTrash"] = "",
        ["restoreTrash"] = "", ["restoreItems"] = "S", ["saveMarkdown"] = "SSSSSS", ["uploadMarkdownImage"] = "SS",
        ["uploadMarkdownImageData"] = "SSSS", ["resolveMarkdownImage"] = "SS", ["resolveEncryptedMarkdownImage"] = "SSS",
        ["resolveEncryptedDriveMarkdownImage"] = "SS", ["configureCustomEncryption"] = "SB", ["setEncryptionMode"] = "S",
        ["exportCustomEncryptionKey"] = "S", ["importCustomEncryptionKey"] = "", ["provideRecoveryPassword"] = "SX",
        ["setupEncryption"] = "Ssb", ["saveSettings"] = "SNSSSBSBBSSNNSSNSBSXNNBSSB", ["testProxy"] = "SNSBSXB",
        ["applyWindowTheme"] = "S", ["setHostFullscreen"] = "B", ["chooseDownloadPath"] = "", ["chooseDataPath"] = ""
    };

    internal static bool IsAllowedMethod(string method) => Signatures.ContainsKey(method);

    internal static bool IsLocalDocument(Uri? uri)
    {
        return uri != null &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, LocalHost, StringComparison.OrdinalIgnoreCase) &&
            uri.IsDefaultPort &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            (uri.AbsolutePath.Equals("/index.html", StringComparison.Ordinal) ||
             uri.AbsolutePath.Equals("/setup.html", StringComparison.Ordinal));
    }

    internal static bool IsLocalDocument(string? uri)
    {
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && IsLocalDocument(parsed);
    }

    /// <summary>
    /// Compara a origem da mensagem com o documento de nível superior. Frames e
    /// qualquer outro documento local que não seja a página atual são rejeitados.
    /// </summary>
    internal static bool IsTrustedSource(string? source, string? topLevelSource)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) ||
            !Uri.TryCreate(topLevelSource, UriKind.Absolute, out var topUri) ||
            !IsLocalDocument(sourceUri) ||
            !IsLocalDocument(topUri))
        {
            return false;
        }

        return sourceUri.Scheme == topUri.Scheme &&
            sourceUri.Host == topUri.Host &&
            sourceUri.Port == topUri.Port &&
            sourceUri.AbsolutePath == topUri.AbsolutePath &&
            sourceUri.Query == topUri.Query;
    }

    internal static bool TryValidate(string json, out string method, out string error)
    {
        method = "";
        error = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty message";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes)
        {
            error = "message too large";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "message is not an object";
                return false;
            }

            string? parsedMethod = null;
            JsonElement arguments = default;
            var hasArguments = false;
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("method"))
                {
                    if (parsedMethod != null || property.Value.ValueKind != JsonValueKind.String)
                    {
                        error = "method is not a string";
                        return false;
                    }

                    parsedMethod = property.Value.GetString();
                }
                else if (property.NameEquals("args"))
                {
                    if (hasArguments || property.Value.ValueKind != JsonValueKind.Array)
                    {
                        error = "args is not an array";
                        return false;
                    }

                    hasArguments = true;
                    arguments = property.Value;
                }
                else
                {
                    error = "unknown message property";
                    return false;
                }
            }

            if (string.IsNullOrEmpty(parsedMethod) ||
                parsedMethod.Length > MaximumMethodLength ||
                !IsAllowedMethod(parsedMethod))
            {
                error = "unknown method";
                return false;
            }

            if (!hasArguments || arguments.GetArrayLength() > MaximumArguments ||
                !ValidateArguments(arguments, Signatures[parsedMethod]))
            {
                error = "invalid arguments";
                return false;
            }

            method = parsedMethod;
            return true;
        }
        catch (JsonException)
        {
            error = "invalid json";
            return false;
        }
    }

    private static bool ValidateArguments(JsonElement arguments, string signature)
    {
        var count = arguments.GetArrayLength();
        if (signature == "rectangles")
        {
            return count % 4 == 0 && arguments.EnumerateArray().All(value => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _));
        }
        var required = signature.Count(char.IsUpper);
        if (count < required || count > signature.Length) return false;
        for (var index = 0; index < count; index++)
        {
            var kind = arguments[index].ValueKind;
            var valid = char.ToUpperInvariant(signature[index]) switch
            {
                'S' => kind == JsonValueKind.String,
                'X' => kind is JsonValueKind.String or JsonValueKind.Null,
                'N' => kind == JsonValueKind.Number && arguments[index].TryGetDouble(out var number) && double.IsFinite(number),
                'B' => kind is JsonValueKind.True or JsonValueKind.False,
                _ => false
            };
            if (!valid) return false;
        }
        return true;
    }
}
