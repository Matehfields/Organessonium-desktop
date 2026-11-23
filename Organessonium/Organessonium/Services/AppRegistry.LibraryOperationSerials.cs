using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class AppRegistry
{
    private const string LibraryOperationMarker = "library-operation-v1:";

    private sealed record LibraryOperationDocument(int SchemaVersion, string EntryType, LibraryMutation Operation);

    internal static bool CanResumeLibraryOperation(LibraryMutation job) =>
        job.Kind is LibraryMutationKind.Move or LibraryMutationKind.Trash or LibraryMutationKind.Restore or LibraryMutationKind.Rename;

    private static string LibraryOperationNonce(string operationId, string purpose) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(operationId + ":" + purpose)))[..24];

    internal static string? LibraryOperationClaim(LibraryMutation job, AppEntry entry) =>
        job.RemoteMessageId.Length == 0 ? null : "library-" + LibraryOperationNonce(job.OperationId, entry.MetadataMessageId);

    private static bool SameLibraryTimestamp(string left, string right) =>
        left == right || (DateTimeOffset.TryParse(left, out var first) && DateTimeOffset.TryParse(right, out var second) &&
            first.ToUnixTimeSeconds() == second.ToUnixTimeSeconds());

    internal async Task<List<LibraryMutation>> LoadLibraryOperationSerialsAsync(CancellationToken token)
    {
        if (string.IsNullOrEmpty(_settings.SerialChannelId)) return [];
        var messages = await _discord.GetMessagesAsync(_settings.SerialChannelId, null, token);
        var pending = new List<LibraryMutation>();
        foreach (var message in messages.Reverse().Where(message => message.Content.StartsWith(LibraryOperationMarker, StringComparison.Ordinal)))
        {
            try { pending.Add(await ReadLibraryOperationAsync(message, token)); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (IOException error) { AppLogger.Error("Registry.ReadLibraryOperation", error); }
            catch (JsonException error) { AppLogger.Error("Registry.ReadLibraryOperation", error); }
        }
        return pending;
    }

    internal async Task<bool> EnsureLibraryOperationSerialAsync(LibraryMutation job, Action checkpoint, CancellationToken token)
    {
        if (string.IsNullOrEmpty(_settings.SerialChannelId) || !CanResumeLibraryOperation(job)) return true;
        if (job.RemoteMessageId.Length > 0)
        {
            try { await VerifyLibraryOperationAsync(job, token); return true; }
            catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { return false; }
        }

        var marker = LibraryOperationMarker + job.OperationId;
        DiscordMessageDto? current = null;
        if (job.RemoteAttempted)
            current = (await _discord.GetMessagesAsync(_settings.SerialChannelId, null, token))
                .FirstOrDefault(message => message.Content == marker);
        if (current == null)
        {
            if (job.Kind != LibraryMutationKind.Rename)
                job.Relocations ??= await PlanLibraryRelocationsAsync(job, token);
            var record = new LibraryOperationDocument(1, "libraryOperation", job);
            var encoded = _metadata.EncodeJson(JsonSerializer.Serialize(record, JsonOptions.Web));
            // Keep the existing local queue usable for selections bigger than a Discord metadata attachment.
            if (Encoding.UTF8.GetByteCount(encoded) > DiscordLimits.AttachmentBytes) return true;
            job.RemoteAttempted = true;
            checkpoint();
            current = await _discord.WriteEncodedDocumentAsync(_settings.SerialChannelId, encoded, null, token,
                marker: marker, nonce: LibraryOperationNonce(job.OperationId, "operation"));
        }
        var saved = await ReadLibraryOperationAsync(current, token);
        if (saved.OperationId != job.OperationId || saved.Kind != job.Kind)
            throw new InvalidDataException("O serial identifica outra operação.");
        job.Relocations ??= saved.Relocations;
        job.RemoteMessageId = saved.RemoteMessageId;
        checkpoint();
        return true;
    }

    private async Task<LibraryMutation> ReadLibraryOperationAsync(DiscordMessageDto message, CancellationToken token)
    {
        if (message.Author is not { Bot: true } author || author.Id != await _discord.GetAuthenticatedUserIdAsync(token))
            throw new InvalidDataException("O serial não foi publicado pelo bot configurado.");
        using var json = await _metadata.ReadJsonAsync(message, token);
        var record = json?.RootElement.Deserialize<LibraryOperationDocument>(JsonOptions.Web);
        if (record is not { SchemaVersion: 1, EntryType: "libraryOperation", Operation: { } job } ||
            !Guid.TryParseExact(job.OperationId, "N", out _) || message.Content != LibraryOperationMarker + job.OperationId ||
            !CanResumeLibraryOperation(job) || job.Changes is not { Count: > 0 } ||
            job.Changes.Any(change => change.Source == null || change.Destination == null) ||
            (job.Kind == LibraryMutationKind.Rename ? job.Changes.Count != 1 : job.Relocations is not { Count: > 0 }))
            throw new InvalidDataException("Serial de operação inválido ou incompatível.");
        job.RemoteMessageId = message.Id;
        return job;
    }

    private async Task VerifyLibraryOperationAsync(LibraryMutation job, CancellationToken token)
    {
        var message = await _discord.GetMessageAsync(_settings.SerialChannelId, job.RemoteMessageId, token);
        var saved = await ReadLibraryOperationAsync(message, token);
        if (saved.OperationId != job.OperationId || saved.Kind != job.Kind)
            throw new InvalidDataException("O serial identifica outra operação.");
    }

    internal async Task CompleteLibraryOperationSerialAsync(LibraryMutation job, CancellationToken token, bool rejected = false)
    {
        if (job.RemoteMessageId.Length == 0) return;
        try
        {
            await VerifyLibraryOperationAsync(job, token);
            if (rejected)
            {
                var possibleClaims = job.Relocations != null ?
                    job.Relocations.Where(move => move.FilePayload is { } payload &&
                        SharedStorageManager.Payloads(payload).Any(version => version.Storage?.Shared == true))
                        .Select(move => move.Source) :
                    job.Changes.Select(change => change.Source).Where(source => source.Type == "file");
                await SharedStorage.CancelPendingLibraryClaimsAsync(
                    possibleClaims.Select(source => (LibraryOperationClaim(job, source)!, source.FileId)), token);
            }
            await _discord.DeleteMessageAsync(_settings.SerialChannelId, job.RemoteMessageId, token);
        }
        catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { }
    }

    internal async Task<List<AppEntry>> ApplyLibraryRenameAsync(LibraryMutation job, CancellationToken token)
    {
        var change = job.Changes.Single();
        var (live, _) = await ReadTrashEntryAsync(change.Source, token);
        var name = change.Destination!.Name;
        if (live.Name != name || live.LibraryOperationId != job.OperationId)
        {
            if (live.Name != change.Source.Name ||
                (change.Source.ModifiedAt.Length > 0 && !SameLibraryTimestamp(live.ModifiedAt, change.Source.ModifiedAt)))
                throw new InvalidOperationException("O item foi alterado no servidor antes de ser renomeado.");
            live.Name = name;
            live.ModifiedAt = job.Timestamp;
        }
        live.LibraryOperationId = job.OperationId;
        // Retry the index/thread update even if the metadata PATCH was confirmed before a disconnect.
        await UpdateEntryAsync(live, token, pendingOperation: LibraryOperationClaim(job, live));
        return [live];
    }
}
