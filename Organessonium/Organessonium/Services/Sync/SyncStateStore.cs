using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Organessonium.Models;

namespace Organessonium.Services.Sync;

public sealed class SyncStateStore
{
    private readonly string _database;
    private readonly object _gate = new();
    public string DeviceId { get; }
    public string DeviceName => Environment.MachineName;

    public SyncStateStore(SettingsStore settings, string? identityPath = null)
    {
        _database = settings.DatabasePath;
        identityPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Organessonium", "installation-id.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(identityPath)!);
        if (File.Exists(identityPath) && Guid.TryParse(File.ReadAllText(identityPath).Trim(), out var id))
            DeviceId = id.ToString("N");
        else
        {
            DeviceId = Guid.NewGuid().ToString("N");
            // CreateNew prevents two starting hosts from silently replacing an installation identity.
            try
            {
                using var file = new FileStream(identityPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(file);
                writer.Write(DeviceId);
            }
            catch (IOException) when (File.Exists(identityPath))
            {
                if (!Guid.TryParse(File.ReadAllText(identityPath).Trim(), out var existing))
                    throw new InvalidDataException("A identidade local de sincronização é inválida.");
                DeviceId = existing.ToString("N");
            }
        }
        EnsureSchema();
    }

    internal void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_folders (id TEXT PRIMARY KEY, account TEXT NOT NULL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sync_state (folder TEXT NOT NULL, path TEXT COLLATE NOCASE NOT NULL,
                json TEXT NOT NULL, PRIMARY KEY(folder,path));
            CREATE TABLE IF NOT EXISTS sync_issues (id TEXT PRIMARY KEY, folder TEXT NOT NULL,
                path TEXT COLLATE NOCASE NOT NULL, json TEXT NOT NULL, UNIQUE(folder,path));
            """;
        command.ExecuteNonQuery();
    }

    public static string AccountKey(AppSettings settings) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('|', settings.NamesChannelId, settings.FoldersForumId,
            settings.AppsForumId, settings.SerialChannelId)))).ToLowerInvariant();

    public List<SyncFolderBinding> Folders(string account)
        => Read<SyncFolderBinding>("SELECT json FROM sync_folders WHERE account=$value", account);
    public Dictionary<string, SyncFileState> Files(string folder)
        => Read<SyncFileState>("SELECT json FROM sync_state WHERE folder=$value", folder)
            .ToDictionary(s => s.RelativePath, StringComparer.OrdinalIgnoreCase);
    public List<SyncIssue> Issues(string folder)
        => Read<SyncIssue>("SELECT json FROM sync_issues WHERE folder=$value", folder);

    public void SaveFolder(SyncFolderBinding binding) => Write("""
        INSERT INTO sync_folders(id,account,json) VALUES($id,$folder,$json)
        ON CONFLICT(id) DO UPDATE SET account=excluded.account,json=excluded.json
        """, binding.SyncFolderId, binding.AccountKey, "", binding);

    public void SaveFile(string folder, SyncFileState state) => Write("""
        INSERT INTO sync_state(folder,path,json) VALUES($folder,$path,$json)
        ON CONFLICT(folder,path) DO UPDATE SET json=excluded.json
        """, "", folder, state.RelativePath, state);

    public void SaveIssue(SyncIssue issue) => Write("""
        INSERT INTO sync_issues(id,folder,path,json) VALUES($id,$folder,$path,$json)
        ON CONFLICT(folder,path) DO UPDATE SET id=excluded.id,json=excluded.json
        """, issue.Id, issue.SyncFolderId, issue.RelativePath, issue);

    public void DeleteIssue(string folder, string path) => Write(
        "DELETE FROM sync_issues WHERE folder=$folder AND path=$path", "", folder, path, null);
    public void DeleteFile(string folder, string path) => Write(
        "DELETE FROM sync_state WHERE folder=$folder AND path=$path", "", folder, path, null);

    public void RemoveFolder(string id)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM sync_folders WHERE id=$id; DELETE FROM sync_state WHERE folder=$id; DELETE FROM sync_issues WHERE folder=$id;";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private List<T> Read<T>(string sql, string value)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$value", value);
            using var reader = command.ExecuteReader();
            var results = new List<T>();
            while (reader.Read()) results.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), SyncJson.Options)
                ?? throw new InvalidDataException("Estado de sincronização inválido; nenhum arquivo foi alterado."));
            return results;
        }
    }

    private void Write(string sql, string id, string folder, string path, object? value)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$folder", folder);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$json", value == null ? "" : JsonSerializer.Serialize(value, SyncJson.Options));
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = _database, DefaultTimeout = 30 }.ToString());
        connection.Open();
        return connection;
    }
}
