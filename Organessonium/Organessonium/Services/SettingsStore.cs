using Microsoft.Data.Sqlite;
using Organessonium.Models;
using System.IO;

namespace Organessonium.Services;

public sealed class SettingsStore : IEncryptionSecretStore
{
    private const string DataFolderName = "OrganessoniumData";
    private const string DataPointerFileName = "OrganessoniumData.path";
    private const string PreferenceDefaultsVersion = "2";
    private readonly object _lock = new();

    public SettingsStore()
    {
        DataDirectory = ResolveDataDirectory();
        AppLogger.Configure(DataDirectory);
        PreviewCacheDirectory = Directory.CreateDirectory(System.IO.Path.Combine(DataDirectory.FullName, "Previews"));
        SerialCacheDirectory = Directory.CreateDirectory(System.IO.Path.Combine(DataDirectory.FullName, "Serials"));
        ThumbnailCache.Configure(PreviewCacheDirectory);
        WebViewDataDirectory = Directory.CreateDirectory(System.IO.Path.Combine(DataDirectory.FullName, "WebView"));
        TransfersDirectory = Directory.CreateDirectory(System.IO.Path.Combine(DataDirectory.FullName, "Transfers"));
        DatabasePath = System.IO.Path.Combine(DataDirectory.FullName, "organessonium.db");
        Directory.CreateDirectory(DataDirectory.FullName);
        InitializeDatabase();
        MigrateIniIfNeeded();
    }

    public DirectoryInfo DataDirectory { get; }
    public DirectoryInfo PreviewCacheDirectory { get; }
    public DirectoryInfo SerialCacheDirectory { get; }
    public DirectoryInfo WebViewDataDirectory { get; }
    public DirectoryInfo TransfersDirectory { get; }
    public string DatabasePath { get; }

    public string PrepareDataDirectoryForNextStartup(string selectedPath)
    {
        var targetPath = NormalizeDataDirectorySelection(selectedPath);
        var target = Directory.CreateDirectory(targetPath);
        if (!SamePath(DataDirectory.FullName, target.FullName))
        {
            if (SamePath(ReadDataDirectoryPointer(), target.FullName))
            {
                CopyCriticalDataFilesBestEffort(DataDirectory, target);
            }
            else
            {
                CopyDataDirectoryBestEffort(DataDirectory, target);
            }
        }
        if (SamePath(target.FullName, DefaultDataDirectoryPath()))
        {
            ClearDataDirectoryPointer();
        }
        else
        {
            WriteDataDirectoryPointer(target.FullName);
        }
        return target.FullName;
    }

    public static string NormalizeDataDirectorySelection(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return DefaultDataDirectoryPath();
        }

        var expanded = Environment.ExpandEnvironmentVariables(selectedPath.Trim().Trim('"'));
        var full = Path.GetFullPath(expanded);
        var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return leaf.Equals(DataFolderName, StringComparison.OrdinalIgnoreCase)
            ? full
            : Path.Combine(full, DataFolderName);
    }
    public AppSettings Load()
    {
        lock (_lock)
        {
            var settings = new AppSettings();
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM settings";
            using var reader = cmd.ExecuteReader();
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }

            reader.Dispose();
            settings.BotToken = Get(values, "botToken", settings.BotToken);
            settings.NamesChannelId = Get(values, "namesChannelId", settings.NamesChannelId);
            settings.AppsForumId = Get(values, "appsForumId", settings.AppsForumId);
            settings.SerialChannelId = Get(values, "serialChannelId", settings.SerialChannelId);
            settings.Theme = Get(values, "theme", settings.Theme);
            settings.CardSize = Get(values, "cardSize", settings.CardSize);
            settings.Language = Get(values, "language", settings.Language);
            settings.DownloadThreads = Math.Clamp(GetInt(values, "downloadThreads", settings.DownloadThreads), 1, 16);
            settings.UploadThreads = Math.Clamp(GetInt(values, "uploadThreads", settings.UploadThreads), 1, 16);
            settings.DownloadPath = Get(values, "downloadPath", settings.DownloadPath);
            settings.TempPath = Get(values, "tempPath", settings.TempPath);
            settings.DataPath = DataDirectory.FullName;
            settings.PreviewEnabled = GetBool(values, "previewEnabled", settings.PreviewEnabled);
            settings.VideoPreviewQuality = NormalizeVideoPreviewQuality(Get(values, "videoPreviewQuality", settings.VideoPreviewQuality));
            settings.AnimationsEnabled = GetBool(values, "animationsEnabled", settings.AnimationsEnabled);
            settings.HardwareAcceleration = GetBool(values, "hardwareAcceleration", settings.HardwareAcceleration);
            settings.AutoResumeTransfers = GetBool(values, "autoResumeTransfers", settings.AutoResumeTransfers);
            settings.KeepTransferHistory = GetBool(values, "keepTransferHistory", settings.KeepTransferHistory);
            settings.CleanupTempOnCancel = GetBool(values, "cleanupTempOnCancel", settings.CleanupTempOnCancel);
            settings.MaxParallelUploads = Math.Clamp(GetInt(values, "maxParallelUploads", settings.MaxParallelUploads), 1, 8);
            settings.MaxParallelDownloads = Math.Clamp(GetInt(values, "maxParallelDownloads", settings.MaxParallelDownloads), 1, 16);
            settings.SearchWorkers = Math.Clamp(GetInt(values, "searchWorkers", settings.SearchWorkers), 1, 8);
            settings.EncryptionMode = EncryptionModes.Normalize(Get(values, "encryptionMode", settings.EncryptionMode));
            settings.CustomKeyAvailable = !string.IsNullOrWhiteSpace(Get(values, EncryptionSettingKeys.ProtectedMasterKey, ""));
            settings.Proxy.Mode = NormalizeProxyMode(Get(values, "proxyMode", ProxyModeLabel(settings.Proxy.Mode)));
            settings.Proxy.Host = Get(values, "proxyHost", settings.Proxy.Host);
            settings.Proxy.Port = Math.Clamp(GetInt(values, "proxyPort", settings.Proxy.Port), 0, 65535);
            settings.Proxy.Protocol = NormalizeProxyProtocol(Get(values, "proxyProtocol", ProxyProtocolLabel(settings.Proxy.Protocol)));
            settings.Proxy.AuthenticationEnabled = GetBool(values, "proxyAuthEnabled", settings.Proxy.AuthenticationEnabled);
            settings.Proxy.Username = Get(values, "proxyUsername", settings.Proxy.Username);
            settings.Proxy.Password = Get(values, "proxyPassword", settings.Proxy.Password);
            NormalizePreferenceDefaults(values, connection, settings);
            return settings;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            Upsert(connection, "botToken", settings.BotToken);
            Upsert(connection, "namesChannelId", settings.NamesChannelId);
            Upsert(connection, "appsForumId", settings.AppsForumId);
            Upsert(connection, "serialChannelId", settings.SerialChannelId);
            Upsert(connection, "theme", settings.Theme);
            Upsert(connection, "cardSize", settings.CardSize);
            Upsert(connection, "language", settings.Language);
            Upsert(connection, "downloadThreads", Math.Clamp(settings.DownloadThreads, 1, 16).ToString());
            Upsert(connection, "uploadThreads", Math.Clamp(settings.UploadThreads, 1, 16).ToString());
            Upsert(connection, "downloadPath", settings.DownloadPath);
            Upsert(connection, "tempPath", settings.TempPath);
            Upsert(connection, "dataPath", settings.DataPath);
            Upsert(connection, "previewEnabled", settings.PreviewEnabled ? "true" : "false");
            Upsert(connection, "videoPreviewQuality", NormalizeVideoPreviewQuality(settings.VideoPreviewQuality));
            Upsert(connection, "animationsEnabled", settings.AnimationsEnabled ? "true" : "false");
            Upsert(connection, "hardwareAcceleration", settings.HardwareAcceleration ? "true" : "false");
            Upsert(connection, "autoResumeTransfers", settings.AutoResumeTransfers ? "true" : "false");
            Upsert(connection, "keepTransferHistory", settings.KeepTransferHistory ? "true" : "false");
            Upsert(connection, "cleanupTempOnCancel", settings.CleanupTempOnCancel ? "true" : "false");
            Upsert(connection, "maxParallelUploads", Math.Clamp(settings.MaxParallelUploads, 1, 8).ToString());
            Upsert(connection, "maxParallelDownloads", Math.Clamp(settings.MaxParallelDownloads, 1, 16).ToString());
            Upsert(connection, "searchWorkers", Math.Clamp(settings.SearchWorkers, 1, 8).ToString());
            Upsert(connection, "encryptionMode", EncryptionModes.Normalize(settings.EncryptionMode));
            Upsert(connection, "proxyMode", ProxyModeLabel(settings.Proxy.Mode));
            Upsert(connection, "proxyHost", settings.Proxy.Host);
            Upsert(connection, "proxyPort", settings.Proxy.Port.ToString());
            Upsert(connection, "proxyProtocol", ProxyProtocolLabel(settings.Proxy.Protocol));
            Upsert(connection, "proxyAuthEnabled", settings.Proxy.AuthenticationEnabled ? "true" : "false");
            Upsert(connection, "proxyUsername", settings.Proxy.Username);
            Upsert(connection, "proxyPassword", settings.Proxy.Password);
            tx.Commit();
        }
    }

    public List<AppEntry> LoadCachedEntries()
    {
        lock (_lock)
        {
            var entries = new List<AppEntry>();
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT names_message_id, name, thread_id, uploaded_at, size_bytes, path, folder_color, type,
                       mime_type, chunk_message_ids, chunk_count, batch_id, preview_message_id,
                       video_preview_message_id, video_preview_chunk_message_ids, video_preview_size_bytes,
                       file_id, folder_id, COALESCE(parent_folder_id, ''), encryption_version,
                       encryption_mode, encryption_salt
                FROM app_entries
                ORDER BY rowid
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    var entry = new AppEntry
                    {
                        MetadataMessageId = reader.GetString(0),
                        Name = reader.GetString(1),
                        ThreadId = reader.GetString(2),
                        UploadedAt = reader.GetString(3),
                        SizeBytes = Math.Max(0, reader.GetInt64(4)),
                        Path = reader.GetString(5),
                        FolderColor = reader.GetString(6),
                        Type = reader.GetString(7),
                        MimeType = reader.GetString(8),
                        BatchId = reader.GetString(11),
                        PreviewMessageId = reader.GetString(12),
                        VideoPreviewMessageId = reader.GetString(13),
                        VideoPreviewSizeBytes = Math.Max(0, reader.GetInt64(15)),
                        FileId = reader.GetString(16),
                        FolderId = reader.GetString(17),
                        ParentFolderId = reader.GetString(18)
                    };
                    var encryptionVersion = reader.GetInt32(19);
                    if (encryptionVersion > 0)
                    {
                        entry.Encryption = new EncryptionMetadata
                        {
                            Version = encryptionVersion,
                            Mode = reader.GetString(20),
                            Salt = reader.GetString(21)
                        };
                    }
                    entry.ChunkMessageIds = DeserializeCachedList(reader.GetString(9));
                    entry.ChunkCount = Math.Max(reader.GetInt32(10), entry.ChunkMessageIds.Count);
                    entry.VideoPreviewChunkMessageIds = DeserializeCachedList(reader.GetString(14));
                    if (string.IsNullOrWhiteSpace(entry.VideoPreviewMessageId))
                    {
                        entry.VideoPreviewMessageId = entry.VideoPreviewChunkMessageIds.FirstOrDefault() ?? "";
                    }

                    entry.Normalize();
                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Settings.LoadCachedEntry", ex);
                }
            }

            return entries;
        }
    }

    private static List<string> DeserializeCachedList(string json)
    {
        try
        {
            return (System.Text.Json.JsonSerializer.Deserialize<List<string>>(json, JsonOptions.Web) ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }
    public void ClearPreviewCache()
    {
        lock (_lock)
        {
            if (PreviewCacheDirectory.Exists)
            {
                foreach (var file in PreviewCacheDirectory.EnumerateFiles())
                {
                    TryDelete(file);
                }

                foreach (var dir in PreviewCacheDirectory.EnumerateDirectories())
                {
                    TryDelete(dir);
                }
            }

            Directory.CreateDirectory(PreviewCacheDirectory.FullName);
            ThumbnailCache.Configure(PreviewCacheDirectory);
        }
    }

    public long GetPreviewCacheSizeBytes()
    {
        lock (_lock)
        {
            return DirectorySize(PreviewCacheDirectory);
        }
    }

    public FileInfo? GetCachedPreviewFile(string threadId, string slot, string name)
    {
        lock (_lock)
        {
            var file = PreviewCacheFile(threadId, slot, name, false);
            if (file is not { Exists: true, Length: > 0 })
            {
                return null;
            }

            if (IsEncryptedPreviewCache(file))
            {
                TryDelete(file);
                return null;
            }

            return file;
        }
    }

    public FileInfo GetPreviewCacheFile(string threadId, string slot, string name)
    {
        lock (_lock)
        {
            return PreviewCacheFile(threadId, slot, name, true);
        }
    }

    public void CacheEntries(IEnumerable<AppEntry> entries)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM app_entries";
                delete.ExecuteNonQuery();
            }

            foreach (var entry in entries)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO app_entries
                    (name, thread_id, uploaded_at, size_bytes, names_message_id, path, folder_color, type,
                     mime_type, chunk_message_ids, chunk_count, batch_id, preview_message_id, video_preview_message_id,
                     video_preview_chunk_message_ids, video_preview_size_bytes, file_id, folder_id, parent_folder_id,
                     encryption_version, encryption_mode, encryption_salt)
                    VALUES ($name, $threadId, $uploadedAt, $sizeBytes, $metadataMessageId, $path, $folderColor, $type,
                     $mimeType, $chunkMessageIds, $chunkCount, $batchId, $previewMessageId, $videoPreviewMessageId,
                     $videoPreviewChunkMessageIds, $videoPreviewSizeBytes, $filedd, $folderdd, $parentFolderId,
                     $encryptionVersion, $encryptionMode, $encryptionSalt)
                    """;
                insert.Parameters.AddWithValue("$name", entry.Name);
                insert.Parameters.AddWithValue("$threadId", entry.ThreadId);
                insert.Parameters.AddWithValue("$uploadedAt", entry.UploadedAt);
                insert.Parameters.AddWithValue("$sizeBytes", entry.SizeBytes);
                insert.Parameters.AddWithValue("$metadataMessageId", entry.MetadataMessageId);
                insert.Parameters.AddWithValue("$path", entry.Path);
                insert.Parameters.AddWithValue("$folderColor", entry.FolderColor);
                insert.Parameters.AddWithValue("$type", entry.Type);
                insert.Parameters.AddWithValue("$mimeType", entry.MimeType);
                insert.Parameters.AddWithValue("$chunkMessageIds", System.Text.Json.JsonSerializer.Serialize(entry.ChunkMessageIds, JsonOptions.Web));
                insert.Parameters.AddWithValue("$chunkCount", entry.ChunkCount);
                insert.Parameters.AddWithValue("$batchId", entry.BatchId);
                insert.Parameters.AddWithValue("$previewMessageId", entry.PreviewMessageId);
                insert.Parameters.AddWithValue("$videoPreviewMessageId", entry.VideoPreviewMessageId);
                insert.Parameters.AddWithValue("$videoPreviewChunkMessageIds", System.Text.Json.JsonSerializer.Serialize(entry.VideoPreviewChunkMessageIds, JsonOptions.Web));
                insert.Parameters.AddWithValue("$videoPreviewSizeBytes", entry.VideoPreviewSizeBytes);
                insert.Parameters.AddWithValue("$filedd", entry.FileId);
                insert.Parameters.AddWithValue("$folderdd", entry.FolderId);
                insert.Parameters.AddWithValue("$parentFolderId", (object?)entry.ParentFolderId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$encryptionVersion", entry.Encryption?.Version ?? 0);
                insert.Parameters.AddWithValue("$encryptionMode", entry.Encryption?.Mode ?? "");
                insert.Parameters.AddWithValue("$encryptionSalt", entry.Encryption?.Salt ?? "");
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private void InitializeDatabase()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS app_entries (
                names_message_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                thread_id TEXT NOT NULL,
                uploaded_at TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                path TEXT NOT NULL,
                folder_color TEXT NOT NULL,
                type TEXT NOT NULL,
                mime_type TEXT NOT NULL DEFAULT '',
                chunk_message_ids TEXT NOT NULL DEFAULT '[]',
                chunk_count INTEGER NOT NULL DEFAULT 0,
                batch_id TEXT NOT NULL DEFAULT '',
                preview_message_id TEXT NOT NULL DEFAULT '',
                video_preview_message_id TEXT NOT NULL DEFAULT '',
                video_preview_chunk_message_ids TEXT NOT NULL DEFAULT '[]',
                video_preview_size_bytes INTEGER NOT NULL DEFAULT 0,
                file_id TEXT NOT NULL DEFAULT '',
                folder_id TEXT NOT NULL DEFAULT '',
                parent_folder_id TEXT,
                encryption_version INTEGER NOT NULL DEFAULT 0,
                encryption_mode TEXT NOT NULL DEFAULT '',
                encryption_salt TEXT NOT NULL DEFAULT ''
            );
            """;
        cmd.ExecuteNonQuery();

        EnsureColumn(connection, "settings", "updated_at", "TEXT NOT NULL DEFAULT ''");
        using (var repairSettings = connection.CreateCommand())
        {
            repairSettings.CommandText = "UPDATE settings SET updated_at = CURRENT_TIMESTAMP WHERE updated_at = ''";
            repairSettings.ExecuteNonQuery();
        }

        EnsureColumn(connection, "app_entries", "mime_type", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "app_entries", "chunk_message_ids", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "app_entries", "chunk_count", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "app_entries", "batch_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "app_entries", "preview_message_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "app_entries", "video_preview_message_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "app_entries", "video_preview_chunk_message_ids", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(connection, "app_entries", "video_preview_size_bytes", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "app_entries", "file_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "app_entries", "folder_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "app_entries", "parent_folder_id", "TEXT");
        EnsureColumn(connection, "app_entries", "encryption_version", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "app_entries", "encryption_mode", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "app_entries", "encryption_salt", "TEXT NOT NULL DEFAULT ''");
    }

    private void MigrateIniIfNeeded()
    {
        if (Load().IsConfigured)
        {
            return;
        }

        var candidates = new List<string>
        {
            System.IO.Path.Combine(DataDirectory.FullName, "config.ini")
        };
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(System.IO.Path.Combine(appData, "Organessonium", "config.ini"));
        }

        var ini = candidates.FirstOrDefault(File.Exists);
        if (ini == null)
        {
            return;
        }

        var values = ReadIni(ini);
        var settings = Load();
        settings.BotToken = Get(values, "botToken", settings.BotToken);
        settings.NamesChannelId = Get(values, "namesChannelId", settings.NamesChannelId);
        settings.AppsForumId = Get(values, "appsForumId", settings.AppsForumId);
        settings.SerialChannelId = Get(values, "serialChannelId", settings.SerialChannelId);
        settings.Theme = Get(values, "theme", settings.Theme);
        settings.CardSize = Get(values, "cardSize", settings.CardSize);
        settings.Language = Get(values, "language", settings.Language);
        settings.DownloadThreads = Math.Clamp(GetInt(values, "downloadThreads", settings.DownloadThreads), 1, 16);
        settings.UploadThreads = Math.Clamp(GetInt(values, "uploadThreads", settings.UploadThreads), 1, 16);
        settings.DownloadPath = Get(values, "downloadPath", settings.DownloadPath);
        settings.TempPath = Get(values, "tempPath", settings.TempPath);
        settings.DataPath = DataDirectory.FullName;
        settings.PreviewEnabled = GetBool(values, "previewEnabled", settings.PreviewEnabled);
        settings.VideoPreviewQuality = NormalizeVideoPreviewQuality(Get(values, "videoPreviewQuality", settings.VideoPreviewQuality));
        settings.AnimationsEnabled = GetBool(values, "animationsEnabled", settings.AnimationsEnabled);
        settings.HardwareAcceleration = GetBool(values, "hardwareAcceleration", settings.HardwareAcceleration);
        settings.AutoResumeTransfers = GetBool(values, "autoResumeTransfers", settings.AutoResumeTransfers);
        settings.KeepTransferHistory = GetBool(values, "keepTransferHistory", settings.KeepTransferHistory);
        settings.CleanupTempOnCancel = GetBool(values, "cleanupTempOnCancel", settings.CleanupTempOnCancel);
        settings.MaxParallelUploads = Math.Clamp(GetInt(values, "maxParallelUploads", settings.MaxParallelUploads), 1, 8);
        settings.MaxParallelDownloads = Math.Clamp(GetInt(values, "maxParallelDownloads", settings.MaxParallelDownloads), 1, 16);
        Save(settings);
    }

    string IEncryptionSecretStore.ReadEncryptionSecret(string key)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key LIMIT 1";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string ?? "";
        }
    }

    void IEncryptionSecretStore.WriteEncryptionSecret(string key, string value)
    {
        lock (_lock)
        {
            using var connection = Open();
            Upsert(connection, key, value);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        return connection;
    }

    private static DirectoryInfo ResolveDataDirectory()
    {
        var configured = ReadDataDirectoryPointer();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                var configuredPath = NormalizeDataDirectorySelection(configured);
                if (Directory.Exists(configuredPath))
                {
                    return new DirectoryInfo(configuredPath);
                }

                ClearDataDirectoryPointer();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Settings.DataPath", ex, configured);
                ClearDataDirectoryPointer();
            }
        }

        return Directory.CreateDirectory(DefaultDataDirectoryPath());
    }
    private static string DefaultDataDirectoryPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Combine(appData, DataFolderName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", DataFolderName);
    }

    private static string DataPointerPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            return Path.Combine(appData, DataPointerFileName);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", DataPointerFileName);
    }

    private static string ReadDataDirectoryPointer()
    {
        try
        {
            var path = DataPointerPath();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static void WriteDataDirectoryPointer(string dataPath)
    {
        var pointer = DataPointerPath();
        Directory.CreateDirectory(Path.GetDirectoryName(pointer) ?? ".");
        File.WriteAllText(pointer, dataPath.Trim());
    }

    private static void ClearDataDirectoryPointer()
    {
        try { File.Delete(DataPointerPath()); } catch { }
    }
    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('['))
            {
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        return values;
    }

    private static void Upsert(SqliteConnection connection, string key, string value)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value, updated_at)
            VALUES ($key, $value, CURRENT_TIMESTAMP)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value ?? "");
        cmd.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        if (!IsSqlIdentifier(table) || !IsSqlIdentifier(column))
        {
            throw new InvalidOperationException("Identificador SQLite invÃ¡lido.");
        }

        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = $"PRAGMA table_info({table})";
            using var reader = exists.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    private static bool IsSqlIdentifier(string value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    }

    private static void NormalizePreferenceDefaults(
        IReadOnlyDictionary<string, string> values,
        SqliteConnection connection,
        AppSettings settings)
    {
        settings.Theme = NormalizeTheme(settings.Theme);
        settings.Language = NormalizeLanguage(settings.Language);

        if (!values.TryGetValue("preferenceDefaultsVersion", out var version) ||
            !version.Equals(PreferenceDefaultsVersion, StringComparison.Ordinal))
        {
            if (settings.Theme == "dark" && settings.Language == "pt-BR")
            {
                settings.Theme = "system";
                settings.Language = "system";
                Upsert(connection, "theme", settings.Theme);
                Upsert(connection, "language", settings.Language);
            }

            Upsert(connection, "preferenceDefaultsVersion", PreferenceDefaultsVersion);
        }
    }

    private static string NormalizeTheme(string value)
    {
        return value.Equals("dark", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("light", StringComparison.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : "system";
    }

    private static string NormalizeLanguage(string value)
    {
        if (value.Equals("pt-BR", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("pt", StringComparison.OrdinalIgnoreCase))
        {
            return "pt-BR";
        }

        return value.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en" : "system";
    }

    private static string NormalizeVideoPreviewQuality(string value)
    {
        return value.Equals("original", StringComparison.OrdinalIgnoreCase) ? "original" : "low";
    }

    internal static string ProxyModeLabel(ProxyMode mode)
    {
        return mode switch
        {
            ProxyMode.Disabled => "disabled",
            ProxyMode.Manual => "manual",
            _ => "automatic"
        };
    }

    internal static ProxyMode NormalizeProxyMode(string value)
    {
        if (value.Equals("disabled", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyMode.Disabled;
        }

        if (value.Equals("manual", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyMode.Manual;
        }

        return ProxyMode.Automatic;
    }

    internal static string ProxyProtocolLabel(ProxyProtocol protocol)
    {
        return protocol switch
        {
            ProxyProtocol.Http => "http",
            ProxyProtocol.Https => "https",
            ProxyProtocol.Socks5 => "socks5",
            ProxyProtocol.Socks4a => "socks4a",
            ProxyProtocol.Socks4 => "socks4",
            _ => "auto"
        };
    }

    internal static ProxyProtocol NormalizeProxyProtocol(string value)
    {
        if (value.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyProtocol.Http;
        }

        if (value.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyProtocol.Https;
        }

        if (value.Equals("socks5", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyProtocol.Socks5;
        }

        if (value.Equals("socks4a", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyProtocol.Socks4a;
        }

        if (value.Equals("socks4", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyProtocol.Socks4;
        }

        return ProxyProtocol.Auto;
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) ? value : fallback;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int fallback)
    {
        return values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }


    private static void CopyDataDirectoryBestEffort(DirectoryInfo source, DirectoryInfo target)
    {
        source.Refresh();
        if (!source.Exists || SamePath(source.FullName, target.FullName))
        {
            return;
        }

        var sourceRoot = EnsureTrailingSeparator(source.FullName);
        var targetRoot = EnsureTrailingSeparator(target.FullName);
        if (targetRoot.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Escolha uma pasta de dados fora da pasta atual do OrganessoniumData.");
        }

        target.Create();
        foreach (var file in source.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source.FullName, file.FullName);
            if (relative.StartsWith("WebView" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                relative.Equals("WebView", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = new FileInfo(Path.Combine(target.FullName, relative));
            CopyFileBestEffort(file, destination);
        }
    }


    private static void CopyCriticalDataFilesBestEffort(DirectoryInfo source, DirectoryInfo target)
    {
        source.Refresh();
        if (!source.Exists || SamePath(source.FullName, target.FullName))
        {
            return;
        }

        target.Create();
        foreach (var file in source.EnumerateFiles())
        {
            CopyFileBestEffort(file, new FileInfo(Path.Combine(target.FullName, file.Name)));
        }

        var transfers = new DirectoryInfo(Path.Combine(source.FullName, "Transfers"));
        if (!transfers.Exists)
        {
            return;
        }

        foreach (var file in transfers.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source.FullName, file.FullName);
            CopyFileBestEffort(file, new FileInfo(Path.Combine(target.FullName, relative)));
        }
    }
    private static void CopyFileBestEffort(FileInfo source, FileInfo destination)
    {
        try
        {
            destination.Directory?.Create();
            using var input = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = new FileStream(destination.FullName, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Settings.CopyDataFile", ex, source.FullName);
        }
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string path)
    {
        path = Path.GetFullPath(path);
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }
    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
            // Cache cleanup should never block the app.
        }
    }

    private static bool IsEncryptedPreviewCache(FileInfo file)
    {
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[8];
            return stream.Read(header, 0, header.Length) == header.Length &&
                   EncryptionService.LooksLikeEncryptedEnvelope(header);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(DirectoryInfo dir)
    {
        try
        {
            dir.Delete(true);
        }
        catch
        {
            // Cache cleanup should never block the app.
        }
    }

    private FileInfo PreviewCacheFile(string threadId, string slot, string name, bool createDirectory)
    {
        if (createDirectory)
        {
            PreviewCacheDirectory.Create();
        }

        var safeThread = SafeCachePart(threadId, "item");
        var safeSlot = SafeCachePart(slot, "preview");
        var safeName = PathRules.CleanFileName(string.IsNullOrWhiteSpace(name) ? "preview.bin" : name);
        return new FileInfo(System.IO.Path.Combine(PreviewCacheDirectory.FullName, "file_" + safeThread + "_" + safeSlot + "_" + safeName));
    }

    private static string SafeCachePart(string value, string fallback)
    {
        var safe = string.Concat((string.IsNullOrWhiteSpace(value) ? fallback : value)
            .Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        return string.IsNullOrWhiteSpace(safe) ? fallback : safe[..Math.Min(64, safe.Length)];
    }

    private static long DirectorySize(DirectoryInfo dir)
    {
        if (!dir.Exists)
        {
            return 0;
        }

        var total = 0L;
        try
        {
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try
                {
                    total += Math.Max(0, file.Length);
                }
                catch
                {
                    // Ignore inaccessible cache files.
                }
            }
        }
        catch
        {
            return total;
        }

        return total;
    }
}



