using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Organessonium.Services;

public sealed record VideoPlaybackPosition(string FileId, double PositionSeconds, double DurationSeconds,
    DateTimeOffset LastPlayedUtc, bool Completed)
{
    public double ResumeAt(double duration = 0)
    {
        if (Completed || !double.IsFinite(PositionSeconds) || PositionSeconds < 0) return 0;
        if (duration > 0 && DurationSeconds > 0 && Math.Abs(duration - DurationSeconds) > Math.Max(5, DurationSeconds * 0.01)) return 0;
        return duration > 0 ? Math.Min(PositionSeconds, duration * 0.95) : PositionSeconds;
    }
}

/// <summary>Small local JSON records in the existing SQLite settings table, never in remote metadata.</summary>
public sealed class VideoPlaybackProgressStore(string databasePath)
{
    private const string Prefix = "videoProgress:";
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        return connection;
    }
    public VideoPlaybackPosition? Load(string fileId)
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM settings WHERE key = $key";
            command.Parameters.AddWithValue("$key", Prefix + fileId);
            return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<VideoPlaybackPosition>(json) : null;
        }
        catch (JsonException) { return null; }
        catch (SqliteException) { ReportError(); return null; }
    }
    public void Save(VideoPlaybackPosition position)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings(key, value, updated_at) VALUES($key, $value, CURRENT_TIMESTAMP)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = CURRENT_TIMESTAMP
            """;
        command.Parameters.AddWithValue("$key", Prefix + position.FileId);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(position));
        command.ExecuteNonQuery();
    }
    public void Delete(string fileId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", Prefix + fileId);
        command.ExecuteNonQuery();
    }
    public void Prune()
    {
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM settings WHERE key LIKE 'videoProgress:%' AND updated_at < datetime('now', '-180 days')";
            command.ExecuteNonQuery();
        }
        catch (SqliteException) { ReportError(); }
    }
    internal static void ReportError() => AppLogger.Error("Video.Progress", new IOException("Não foi possível acessar o progresso local de reprodução."));
}

public sealed class VideoPlaybackTracker(VideoPlaybackProgressStore store, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _sync = new();
    private readonly Dictionary<string, VideoPlaybackPosition> _pending = [];
    private readonly Dictionary<string, DateTimeOffset> _lastWrites = [];
    private bool _suspended;
    public VideoPlaybackPosition? Load(string fileId) => store.Load(fileId);

    public void Update(string fileId, double position, double duration, bool force)
    {
        if (string.IsNullOrWhiteSpace(fileId) || !double.IsFinite(position) || position < 0 || !double.IsFinite(duration) || duration < 0) return;
        lock (_sync)
        {
            if (_suspended) return;
            var now = _clock();
            var completed = duration > 0 && position >= duration * 0.95;
            _pending[fileId] = new VideoPlaybackPosition(fileId, completed ? 0 : position, duration, now, completed);
            if (!force && _lastWrites.TryGetValue(fileId, out var written) && now - written < TimeSpan.FromSeconds(10)) return;
            Persist(_pending[fileId]);
            _lastWrites[fileId] = now;
        }
    }
    public void Flush(string fileId)
    {
        lock (_sync)
        {
            if (_pending.Remove(fileId, out var position)) Persist(position);
            _lastWrites.Remove(fileId);
        }
    }
    public void FlushAll()
    {
        lock (_sync) foreach (var fileId in _pending.Keys.ToArray()) Flush(fileId);
    }
    public void Suspend()
    {
        lock (_sync) { _suspended = true; FlushAll(); }
    }
    public void Resume() { lock (_sync) _suspended = false; }
    private void Persist(VideoPlaybackPosition position)
    {
        try { store.Save(position); }
        catch (SqliteException) { VideoPlaybackProgressStore.ReportError(); }
    }
    public void Delete(string fileId)
    {
        lock (_sync)
        {
            _pending.Remove(fileId); _lastWrites.Remove(fileId);
            try { store.Delete(fileId); }
            catch (SqliteException) { VideoPlaybackProgressStore.ReportError(); }
        }
    }
}
