using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.Sync;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    // Real repositories, encrypted upload/download, serials, SQLite and engine; only HTTP is simulated.
    private static async Task<TestOutcome> SyncDiscordPublicationAndRecoveryAsync()
    {
        var temporary = Directory.CreateTempSubdirectory("organessonium-sync-discord-");
        try
        {
            using var server = new SyncDiscordFixture();
            var settings = new AppSettings { BotToken = "sync-fixture-" + Guid.NewGuid().ToString("N"),
                NamesChannelId = "names", FoldersForumId = "folders", AppsForumId = "files", SerialChannelId = "serial",
                SearchWorkers = 1, UploadThreads = 2, DownloadThreads = 2, PreviewEnabled = false };
            var storeA = new SettingsStore(Directory.CreateDirectory(Path.Combine(temporary.FullName, "A", "Data")));
            var storeB = new SettingsStore(Directory.CreateDirectory(Path.Combine(temporary.FullName, "B", "Data")));
            storeA.Save(settings); storeB.Save(settings);
            var stateA = new SyncStateStore(storeA, Path.Combine(temporary.FullName, "A", "identity.txt"));
            var stateB = new SyncStateStore(storeB, Path.Combine(temporary.FullName, "B", "identity.txt"));
            using var apiA = new DiscordApiClient(settings.BotToken, new HttpClient(server, false));
            using var apiB = new DiscordApiClient(settings.BotToken, new HttpClient(server, false));
            using var cloudA = new CloudSyncRepository(storeA, settings, apiA, () => new HttpClient(server, false));
            using var cloudB = new CloudSyncRepository(storeB, settings, apiB, () => new HttpClient(server, false));
            var a = new SyncFolderBinding { Name = "Projetos", LocalPath = Directory.CreateDirectory(Path.Combine(temporary.FullName, "A", "Local")).FullName };
            await cloudA.CreateOrLinkAsync(a, stateA, false, CancellationToken.None);
            var b = new SyncFolderBinding { Name = "Projetos", SyncFolderId = a.SyncFolderId, CloudFolderId = a.CloudFolderId,
                CloudPath = a.CloudPath, LocalPath = Directory.CreateDirectory(Path.Combine(temporary.FullName, "B", "Local")).FullName };
            await cloudB.CreateOrLinkAsync(b, stateB, true, CancellationToken.None);
            stateA.SaveFolder(a); stateB.SaveFolder(b);
            var deviceRegistry = new AppRegistry(apiB, storeB, settings);
            await deviceRegistry.RefreshMappingsAsync();
            var devices = await deviceRegistry.LoadSynchronizedDevicesAsync(forceRefresh: true);
            RequireSync(devices.Count(d => d.DeviceType == "pc") == 2 && devices.Any(d => d.DeviceType == "phone"),
                "dois PCs com o mesmo nome foram combinados ou a galeria existente foi removida");
            var sources = await cloudB.DiscoverAsync(CancellationToken.None);
            RequireSync(sources.Count == 2 && sources.Single(s => s.FolderId != a.CloudFolderId).Sync?.CanonicalFolderId == a.CloudFolderId,
                "segundo vínculo criou uma cópia independente da pasta lógica");
            var path = SyncPaths.Resolve(a.LocalPath, "src/grande.dat");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = RandomNumberGenerator.GetBytes(64 * 1024);
                var remaining = EncryptionService.MaximumPlaintextPayloadBytes + 65536;
                while (remaining > 0) { var count = (int)Math.Min(buffer.Length, remaining); await file.WriteAsync(buffer.AsMemory(0, count)); remaining -= count; }
            }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-10));
            await using var engineA = new SyncEngine(stateA, cloudA);
            await using var engineB = new SyncEngine(stateB, cloudB);
            var progress = new List<SyncTransferProgress>(); engineA.Progress += p => progress.Add(p);
            server.RejectNextFileProjection = true;
            await engineA.SynchronizeAsync(a);
            RequireSync(engineA.Statuses.Single().State == "offline", "falha do índice não foi conservadora");
            var sent = server.PayloadPosts;
            await engineA.SynchronizeAsync(a);
            var remote = await cloudA.ReadAsync(a, CancellationToken.None);
            var revision = remote.Heads["src/grande.dat"].Single();
            RequireSync(revision.Payload is { Encryption: not null, Chunks.Count: 2 } && server.PayloadPosts == sent,
                "recuperação do card reenviou conteúdo ou perdeu chunks criptografados");
            await engineB.SynchronizeAsync(b);
            RequireSync(await CloudSyncRepository.HashAsync(SyncPaths.Resolve(b.LocalPath, "src/grande.dat"), default) == revision.Hash,
                "download real não reconstruiu o conteúdo criptografado");
            var folderId = AppRegistry.SyncDirectoryId(a.SyncFolderId, "src");
            await deviceRegistry.RefreshMappingsAsync();
            var cards = await deviceRegistry.RefreshFolderAsync(folderId, a.CloudPath + "/src");
            RequireSync(cards.Count == 1 && cards[0].Sync?.RevisionId == revision.RevisionId && cards[0].ChunkMessageIds.Count == 2,
                "revisão confirmada não apareceu no índice dos cards");
            var serial = await deviceRegistry.LoadCachedFolderAsync(folderId, a.CloudPath + "/src");
            RequireSync(serial.Single().FileId == revision.FileId && serial.Single().Sync?.Hash == revision.Hash,
                "serial/cache perdeu a identidade ou extensão de sincronização");
            cards[0].Favorite = true;
            await deviceRegistry.UpdateEntryAsync(cards[0]);
            await using (var changed = new FileStream(path, FileMode.Open, FileAccess.Write))
                await changed.WriteAsync("local-change"u8.ToArray());
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-10));
            await engineA.SynchronizeAsync(a);
            var updatedCards = await deviceRegistry.RefreshFolderAsync(folderId, a.CloudPath + "/src");
            RequireSync(updatedCards.Single().Favorite && updatedCards.Single().Sync?.RevisionId != revision.RevisionId,
                "a nova revisão apagou o favorito do card existente");
            b.AutomaticRemoteUpdates = true;
            await engineB.SynchronizeAsync(b);
            revision = (await cloudA.ReadAsync(a, CancellationToken.None)).Heads["src/grande.dat"].Single();
            a.DeletionPolicy = "propagate"; b.DeletionPolicy = "propagate";
            File.Delete(path);
            await engineA.SynchronizeAsync(a); await engineB.SynchronizeAsync(b);
            RequireSync(!File.Exists(SyncPaths.Resolve(b.LocalPath, "src/grande.dat")) &&
                (await deviceRegistry.RefreshFolderAsync(folderId, a.CloudPath + "/src")).Count == 0 &&
                !server.DeletedChannels.Contains(revision.Payload!.ThreadId), "tombstone não atualizou cards ou apagou a última cópia de recuperação");
            return TestOutcome.Pass();
        }
        finally { SqliteConnection.ClearAllPools(); temporary.Delete(true); }
    }

    private sealed class SyncDiscordFixture : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Dictionary<string, DiscordMessageDto>> _channels = new();
        private readonly Dictionary<string, byte[]> _attachments = new();
        private readonly Dictionary<string, string> _parents = new();
        private ulong _next = 930000000000000000;
        public int PayloadPosts { get; private set; }
        public bool RejectNextFileProjection;
        public List<string> DeletedChannels { get; } = [];
        public SyncDiscordFixture()
        {
            _channels["names"] = []; _channels["serial"] = []; _channels["root-thread"] = [];
            var metadata = new MetadataService();
            Add("names", metadata.SerializeFolderIndex(new("root", "root-thread", "")));
            Add("root-thread", metadata.SerializeFolderIndexHeader("root", "root", "root"));
            Add("names", JsonSerializer.Serialize(new FolderMetadata { FolderId = "synchronized", Name = "synchronized", ParentFolderId = "root" }, JsonOptions.Web));
            Add("names", JsonSerializer.Serialize(new FolderMetadata { FolderId = "phone-id", Name = "PHONE", ParentFolderId = "synchronized", DeviceType = "phone" }, JsonOptions.Web));
        }
        private DiscordMessageDto Add(string channel, string content)
        {
            var message = new DiscordMessageDto { Id = (++_next).ToString(), Content = content };
            _channels[channel][message.Id] = message; return message;
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions.Web), Encoding.UTF8, "application/json") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/attachments/", StringComparison.Ordinal))
            { lock (_gate) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(_attachments[path.Split('/')[^1]]) }; }
            if (path.Contains("/guilds/", StringComparison.Ordinal)) return Json(new { threads = Array.Empty<object>() });
            var offset = path.IndexOf("/channels/", StringComparison.Ordinal);
            if (offset < 0) return new(HttpStatusCode.NotFound);
            var parts = path[(offset + 10)..].Split('/'); var channel = parts[0];
            if (request.Method == HttpMethod.Get)
            {
                lock (_gate)
                {
                    if (parts.Length == 1) return Json(new { id = channel, type = channel is "folders" or "files" ? 15 : 11, guild_id = "fixture" });
                    if (parts.Length > 2 && parts[1] == "threads") return Json(new { threads = Array.Empty<object>(), has_more = false });
                    if (parts.Length == 3 && _channels.GetValueOrDefault(channel)?.TryGetValue(parts[2], out var m) == true) return Json(m);
                    if (parts.Length == 2 && parts[1] == "messages")
                    {
                        var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
                        var messages = _channels.GetValueOrDefault(channel)?.Values.AsEnumerable() ?? [];
                        if (query.TryGetValue("before", out var before)) messages = messages.Where(m => ulong.Parse(m.Id) < ulong.Parse(before));
                        return Json(messages.OrderByDescending(m => ulong.Parse(m.Id)).Take(int.Parse(query.GetValueOrDefault("limit", "100"))).ToArray());
                    }
                    return new(HttpStatusCode.NotFound);
                }
            }
            if (request.Method == HttpMethod.Delete)
            {
                lock (_gate)
                {
                    if (parts.Length == 3) _channels.GetValueOrDefault(channel)?.Remove(parts[2]);
                    else { DeletedChannels.Add(channel); _channels.Remove(channel); }
                    return new(HttpStatusCode.NoContent);
                }
            }
            if (request.Method == HttpMethod.Patch && parts.Length == 1) return Json(new { id = channel });
            JsonDocument document; byte[]? bytes = null; string? filename = null;
            if (request.Content is MultipartFormDataContent multipart)
            {
                document = JsonDocument.Parse(await multipart.First(p => p.Headers.ContentDisposition?.Name?.Trim('"') == "payload_json").ReadAsStringAsync(token));
                var file = multipart.First(p => p.Headers.ContentDisposition?.FileName != null);
                filename = file.Headers.ContentDisposition?.FileNameStar ?? file.Headers.ContentDisposition!.FileName!.Trim('"');
                bytes = await file.ReadAsByteArrayAsync(token);
            }
            else document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            using (document)
            {
                var forum = parts.Length == 2 && parts[1] == "threads";
                var payload = forum ? document.RootElement.GetProperty("message") : document.RootElement;
                var content = payload.TryGetProperty("content", out var value) ? value.GetString() ?? "" : "";
                lock (_gate)
                {
                    if (RejectNextFileProjection && content.Contains("\"entryType\":\"file\"", StringComparison.Ordinal))
                    { RejectNextFileProjection = false; return new(HttpStatusCode.Forbidden); }
                    if (forum)
                    {
                        var thread = (++_next).ToString(); _channels[thread] = []; _parents[thread] = channel;
                        var message = Add(thread, content); Attach(message, filename, bytes);
                        if (channel == "files") PayloadPosts++;
                        return Json(new { id = thread, message });
                    }
                    var result = request.Method == HttpMethod.Post ? Add(channel, content) : new DiscordMessageDto { Id = parts[2], Content = content };
                    Attach(result, filename, bytes); _channels[channel][result.Id] = result;
                    return Json(result);
                }
            }
        }
        private void Attach(DiscordMessageDto message, string? filename, byte[]? bytes)
        {
            if (bytes == null) return;
            _attachments[message.Id] = bytes;
            message.Attachments = [new DiscordAttachmentDto { Id = message.Id, FileName = filename!, Size = bytes.Length,
                Url = "https://sync.fixture/attachments/" + message.Id }];
        }
    }
}
