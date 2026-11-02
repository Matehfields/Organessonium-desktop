using System.Net;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    internal async Task RunJanitorAsync(CancellationToken token)
    {
        var threads = await _discord.GetForumThreadsAsync(_settings.AppsForumId, token);
        await SharedStorage.AuditOrphansAsync(threads.Select(thread => thread.Id).ToHashSet(StringComparer.Ordinal), token);
        var archived = 0;
        foreach (var forum in new[] { _settings.FoldersForumId, _settings.AppsForumId }.Distinct())
            foreach (var thread in await _discord.GetActiveThreadsAsync(forum, token))
            {
                if (_uploadIndexThreads.ContainsKey(thread.Id) || SharedStorage.IsForeignPool(thread.Id)) continue;
                try { if (await _discord.ArchiveIdleThreadAsync(thread.Id, token)) archived++; }
                catch (DiscordApiException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { }
            }
        await CleanupSupersededRemoteSerialsAsync(token);
        Diagnostics.DevelopmentDiagnostics.Current?.Record("janitor.archived", "maintenance", new { count = archived });
    }
}
