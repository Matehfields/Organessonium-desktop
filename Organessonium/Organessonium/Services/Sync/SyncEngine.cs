using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using Organessonium.Models;

namespace Organessonium.Services.Sync;

// No Window, Dispatcher, WebView2, DOM or JS dependencies. The host owns scheduling.
public sealed class SyncEngine : IAsyncDisposable
{
    private readonly SyncStateStore _state;
    private readonly ICloudSyncRepository _cloud;
    private readonly SyncScanner _scanner = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<string, SyncFolderStatus> _statuses = new();
    private readonly ConcurrentDictionary<string, byte> _folderPauses = new();
    private readonly ConcurrentDictionary<string, string> _failures = new();
    private readonly CancellationTokenSource _lifetime = new();
    private volatile bool _paused;
    public event Action? Changed;
    public event Action<SyncTransferProgress>? Progress;
    public event Action<string>? Notification;
    public IReadOnlyList<SyncFolderStatus> Statuses => _statuses.Values.ToList();
    public bool Paused => _paused;

    public SyncEngine(SyncStateStore state, ICloudSyncRepository cloud)
    {
        _state = state;
        _cloud = cloud;
    }

    public void Pause(bool paused, string? folderId = null)
    {
        if (folderId == null) _paused = paused;
        else if (paused) _folderPauses[folderId] = 1;
        else _folderPauses.TryRemove(folderId, out _);
        if (paused)
            foreach (var operation in _running.Where(p => folderId == null || p.Key == folderId))
                operation.Value.Cancel();
        Changed?.Invoke();
    }

    public async Task SynchronizeAsync(SyncFolderBinding folder, CancellationToken cancellationToken = default,
        IReadOnlySet<string>? changedPaths = null)
    {
        if (_paused || folder.Paused || _folderPauses.ContainsKey(folder.SyncFolderId)) { Publish(folder, "paused", 0, "Sincronização pausada"); return; }
        var gate = _gates.GetOrAdd(folder.SyncFolderId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken)) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _running[folder.SyncFolderId] = operation;
        var token = operation.Token;
        var completed = 0;
        try
        {
            if (_paused || folder.Paused || _folderPauses.ContainsKey(folder.SyncFolderId)) { Publish(folder, "paused", 0, "Sincronização pausada"); return; }
            Publish(folder, "scanning", 0, "Verificando alterações");
            var baseline = _state.Files(folder.SyncFolderId);
            var local = await _scanner.ScanAsync(folder.LocalPath, baseline, token, changedPaths);
            if (!local.Complete) { Publish(folder, "unavailable", 0, "Pasta indisponível ou acesso incompleto. Nenhuma exclusão foi propagada."); return; }
            RememberObserved(folder, baseline, local);
            var pending = local.Entries.Count(p => !baseline.TryGetValue(p.Key, out var b) || b.BaseHash != p.Value.Hash);
            Publish(folder, "syncing", pending, "Sincronizando...");
            var remote = await _cloud.ReadAsync(folder, token);
            var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // A file disappearing and another appearing with the same hash can reuse the uploaded object.
            foreach (var added in local.Entries.Values.Where(f => f.Kind == "file" &&
                         (!baseline.TryGetValue(f.RelativePath, out var state) || state.BaseRevisionId.Length == 0)))
            {
                var matches = baseline.Values.Where(b => b.Kind == "file" && b.BaseRevisionId.Length > 0 && !b.BaseDeleted &&
                    b.BaseHash == added.Hash && !local.Entries.ContainsKey(b.RelativePath) && !local.BusyPaths.Contains(b.RelativePath)).ToList();
                if (matches.Count != 1) continue;
                var old = matches[0];
                var heads = HeadsFor(remote, old.RelativePath, old.FileId);
                if (heads.Count != 1 || heads[0].RevisionId != old.BaseRevisionId || heads[0].IsDeleted) continue;
                var moved = NewRevision(folder, added, old.FileId, heads);
                moved.Payload = heads[0].Payload;
                await _cloud.CommitAsync(folder, moved, heads.Select(h => h.RevisionId).ToList(), token);
                Accept(folder, added, moved);
                _state.DeleteFile(folder.SyncFolderId, old.RelativePath);
                _state.DeleteIssue(folder.SyncFolderId, old.RelativePath);
                baseline.Remove(old.RelativePath);
                suppressed.Add(added.RelativePath);
                suppressed.Add(old.RelativePath);
                completed++;
            }

            // Rename/move received from another PC is a remote update, not a missing-file deletion.
            foreach (var old in baseline.Values.Where(b => b.BaseRevisionId.Length > 0 && !b.BaseDeleted).ToList())
            {
                var heads = HeadsFor(remote, old.RelativePath, old.FileId);
                if (heads.Count != 1 || heads[0].IsDeleted || heads[0].RelativePath.Equals(old.RelativePath, StringComparison.OrdinalIgnoreCase)) continue;
                var head = heads[0];
                local.Entries.TryGetValue(old.RelativePath, out var previousLocal);
                local.Entries.TryGetValue(head.RelativePath, out var targetLocal);
                var issue = FindIssue(folder, old.RelativePath, heads, previousLocal);
                if (issue?.Resolution is "local" or "cloud" or "both")
                {
                    await ResolveAsync(folder, old.RelativePath, previousLocal, old, heads, issue, token);
                    completed++;
                }
                else if (old.IgnoredRevisionId == head.RevisionId) { }
                else if (previousLocal?.Hash != old.BaseHash || targetLocal != null)
                    SetIssue(folder, old.RelativePath, previousLocal, heads, "conflict", "O arquivo foi movido na nuvem e também alterado neste computador.", issue);
                else if (!folder.AutomaticRemoteUpdates && issue?.Resolution is not ("cloud" or "both"))
                    SetIssue(folder, old.RelativePath, previousLocal, heads, "remoteRename", "O arquivo foi renomeado ou movido na nuvem.", issue);
                else
                {
                    await ApplyRemoteAsync(folder, old.RelativePath, previousLocal, head, issue?.Resolution == "both", token);
                    _state.DeleteFile(folder.SyncFolderId, old.RelativePath);
                    completed++;
                }
                suppressed.Add(old.RelativePath);
                suppressed.Add(head.RelativePath);
            }

            var paths = local.Entries.Keys.Concat(baseline.Keys).Concat(remote.Heads.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => !local.Entries.ContainsKey(p) || (remote.Heads.TryGetValue(p, out var h) && h.All(r => r.IsDeleted)) ? 1 : 0)
                .ThenBy(p => !local.Entries.ContainsKey(p) || (remote.Heads.TryGetValue(p, out var h) && h.All(r => r.IsDeleted)) ? -p.Count(c => c == '/') : p.Count(c => c == '/'))
                .ToList();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                if (suppressed.Contains(path) || local.BusyPaths.Contains(path)) continue;
                local.Entries.TryGetValue(path, out var file);
                baseline.TryGetValue(path, out var known);
                var heads = HeadsFor(remote, path, known?.FileId);
                var action = SyncPlanner.Decide(file, known, heads, folder);
                var issue = FindIssue(folder, path, heads, file);
                if (issue?.Resolution is "local" or "cloud" or "both")
                {
                    if (await ResolveAsync(folder, path, file, known, heads, issue, token)) { completed++; continue; }
                }
                switch (action)
                {
                    case SyncAction.None: break;
                    case SyncAction.Accept:
                        if (_cloud is CloudSyncRepository committed && heads[0].Kind == "file" && !heads[0].IsDeleted)
                            await committed.CompleteUploadAsync(folder, heads[0].FileId, heads[0].Hash, token, p => Progress?.Invoke(p));
                        Accept(folder, file, heads[0]);
                        break;
                    case SyncAction.Upload:
                        await UploadAsync(folder, file!, known?.FileId, heads, token);
                        completed++;
                        break;
                    case SyncAction.Download:
                    case SyncAction.RemoteDeletion:
                        await ApplyRemoteAsync(folder, path, file, heads[0], false, token);
                        completed++;
                        break;
                    case SyncAction.LocalDeletion:
                        await TombstoneAsync(folder, path, known!, heads, token);
                        completed++;
                        break;
                    case SyncAction.Conflict:
                        if (folder.ConflictPolicy == "both" && file != null && heads.Count > 0)
                        {
                            var both = new SyncIssue { Resolution = "both" };
                            await ResolveAsync(folder, path, file, known, heads, both, token);
                            completed++;
                        }
                        else SetIssue(folder, path, file, heads, "conflict",
                            file == null ? "A exclusão local precisa de confirmação ou conflita com uma alteração remota." :
                            "Este arquivo foi modificado neste computador e em outro dispositivo.", issue);
                        break;
                    case SyncAction.RemoteUpdate:
                        SetIssue(folder, path, file, heads, "remoteUpdate", heads[0].IsDeleted ?
                            "O arquivo foi excluído na nuvem. Confirmar a remoção local?" :
                            "Existe uma versão mais recente deste arquivo na nuvem.", issue);
                        break;
                    case SyncAction.Unavailable:
                        SetIssue(folder, path, file, [], "unavailable", "A revisão remota conhecida não foi encontrada. Nenhuma cópia foi apagada.", issue);
                        break;
                }
            }
            var count = _state.Issues(folder.SyncFolderId).Count;
            if (_failures.TryRemove(folder.SyncFolderId, out _)) AppLogger.SyncEvent("Sync.Reconnected", "reconciliation recovered");
            if (completed > 0) AppLogger.SyncEvent("Sync.Completed", "items=" + completed);
            Publish(folder, count > 0 ? "attention" : local.BusyPaths.Count > 0 ? "pending" : "synced",
                local.BusyPaths.Count, count > 0 ? $"{count} decisão(ões) pendente(s)" : local.BusyPaths.Count > 0 ?
                "Aguardando arquivos terminarem de ser gravados" : "Sincronizado");
            if (count > 0) Notification?.Invoke($"A pasta {folder.Name} precisa de uma decisão de sincronização.");
            else if (completed > 0) Notification?.Invoke($"Organessônium terminou de sincronizar {completed} item(ns) em {folder.Name}.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Publish(folder, "paused", 0, "Sincronização interrompida com segurança");
        }
        catch (SyncConcurrencyException)
        {
            Publish(folder, "pending", 1, "Outra revisão chegou durante a operação. A reconciliação será repetida.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Net.Http.HttpRequestException or
            InvalidOperationException or DiscordApiException or System.Text.Json.JsonException or CryptographicException)
        {
            var failure = ex is DiscordApiException apiError ? "Discord HTTP " + (int)apiError.StatusCode : ex.GetType().Name;
            if (!_failures.TryGetValue(folder.SyncFolderId, out var previous) || previous != failure)
                AppLogger.SyncEvent("Sync.Deferred", failure + "; previous copies preserved", true);
            _failures[folder.SyncFolderId] = failure;
            Publish(folder, "offline", 1, "Não foi possível sincronizar. Os arquivos foram preservados; haverá nova tentativa.");
        }
        finally
        {
            _running.TryRemove(folder.SyncFolderId, out _);
            gate.Release();
        }
    }

    public void Resolve(SyncFolderBinding folder, string issueId, string choice, bool remember)
    {
        if (choice is not ("local" or "cloud" or "both" or "later")) throw new ArgumentException("Decisão inválida.");
        var issue = _state.Issues(folder.SyncFolderId).SingleOrDefault(i => i.Id == issueId)
            ?? throw new InvalidOperationException("A decisão não está mais pendente.");
        if (issue.Kind == "unavailable") throw new InvalidOperationException("Atualize o estado remoto antes de resolver este item.");
        if (choice == "later") return;
        issue.Resolution = choice;
        issue.Remember = remember;
        _state.SaveIssue(issue);
        Changed?.Invoke();
    }

    public async Task WaitForIdleAsync(string folderId)
    {
        if (!_gates.TryGetValue(folderId, out var gate)) return;
        await gate.WaitAsync();
        gate.Release();
    }

    private async Task<bool> ResolveAsync(SyncFolderBinding folder, string path, SyncLocalFile? local,
        SyncFileState? known, IReadOnlyList<SyncRevision> heads, SyncIssue issue, CancellationToken token)
    {
        if (heads.Count == 0) return false;
        if (issue.Resolution == "local")
        {
            if (issue.Kind is "remoteUpdate" or "remoteRename" && known != null)
            {
                known.IgnoredRevisionId = heads[0].RevisionId;
                _state.SaveFile(folder.SyncFolderId, known);
                _state.DeleteIssue(folder.SyncFolderId, path);
                return true;
            }
            if (local == null && known != null) await TombstoneAsync(folder, path, known, heads, token);
            else if (local != null) await UploadAsync(folder, local, known?.FileId ?? heads[0].FileId, heads, token);
            return true;
        }
        // Keep both also preserves all remote conflict heads before publishing a resolution.
        if (issue.Resolution == "both")
        {
            if (local != null && local.Kind == "file") await PreserveLocalAsync(folder, path, local.Hash, token);
            foreach (var alternate in heads.Skip(1).Where(h => !h.IsDeleted && h.Kind == "file"))
            {
                var copy = ConflictName(folder, alternate.RelativePath, alternate.DeviceName);
                await DownloadToAsync(folder, alternate, copy, null, token);
            }
        }
        var head = heads[0];
        if (heads.Count > 1)
        {
            var resolution = NewRevision(folder,
                head.IsDeleted ? null : new SyncLocalFile(path, head.Kind, head.SizeBytes,
                    DateTimeOffset.Parse(head.ModifiedUtc).UtcTicks, head.Hash), head.FileId, heads);
            resolution.RelativePath = path;
            resolution.Payload = head.Payload;
            resolution.State = head.State;
            resolution.DeletedUtc = head.DeletedUtc;
            head = await _cloud.CommitAsync(folder, resolution, heads.Select(h => h.RevisionId).ToList(), token);
        }
        await ApplyRemoteAsync(folder, path, local, head, false, token);
        if (issue.Remember)
        {
            if (issue.Kind == "remoteUpdate" && !head.IsDeleted) folder.AutomaticRemoteUpdates = true;
            if (issue.Kind == "conflict" && issue.Resolution == "both") folder.ConflictPolicy = "both";
            _state.SaveFolder(folder);
        }
        return true;
    }

    private async Task UploadAsync(SyncFolderBinding folder, SyncLocalFile local, string? fileId,
        IReadOnlyList<SyncRevision> heads, CancellationToken token)
    {
        fileId = string.IsNullOrWhiteSpace(fileId) ? (local.Kind == "directory" ?
            AppRegistry.SyncDirectoryId(folder.SyncFolderId, local.RelativePath) : Guid.NewGuid().ToString("N")) : fileId;
        var revision = NewRevision(folder, local, fileId, heads);
        string? transferId = null;
        if (local.Kind == "file")
            revision.Payload = await _cloud.UploadAsync(folder, local, fileId, token, p => { transferId = p.Id; Progress?.Invoke(p); });
        await _cloud.CommitAsync(folder, revision, heads.Select(h => h.RevisionId).ToList(), token);
        if (_cloud is CloudSyncRepository repository)
            await repository.CompleteUploadAsync(folder, fileId, local.Hash, token, p => Progress?.Invoke(p));
        Accept(folder, local, revision);
        if (_cloud is not CloudSyncRepository && transferId != null) Progress?.Invoke(new(transferId, local.RelativePath, "upload", 100, "completed"));
    }

    private async Task TombstoneAsync(SyncFolderBinding folder, string path, SyncFileState known,
        IReadOnlyList<SyncRevision> heads, CancellationToken token)
    {
        var revision = NewRevision(folder, null, known.FileId, heads);
        revision.RelativePath = path;
        revision.Kind = known.Kind;
        revision.Payload = heads.FirstOrDefault()?.Payload;
        await _cloud.CommitAsync(folder, revision, heads.Select(h => h.RevisionId).ToList(), token);
        Accept(folder, null, revision);
    }

    private SyncRevision NewRevision(SyncFolderBinding folder, SyncLocalFile? local, string fileId, IReadOnlyList<SyncRevision> heads)
        => new()
        {
            SyncFolderId = folder.SyncFolderId, FileId = fileId, RelativePath = local?.RelativePath ?? "",
            Kind = local?.Kind ?? heads.FirstOrDefault()?.Kind ?? "file", SizeBytes = local?.Size ?? 0,
            Hash = local?.Hash ?? "", ModifiedUtc = local is { WriteTicks: > 0 } ? new DateTime(local.WriteTicks, DateTimeKind.Utc).ToString("O") : DateTime.UtcNow.ToString("O"),
            Revision = heads.Count == 0 ? 1 : heads.Max(h => h.Revision) + 1,
            Parents = heads.Select(h => h.RevisionId).ToList(), DeviceId = _state.DeviceId, DeviceName = _state.DeviceName,
            State = local == null ? "deleted" : "active", DeletedUtc = local == null ? DateTime.UtcNow.ToString("O") : null
        };

    private async Task ApplyRemoteAsync(SyncFolderBinding folder, string oldPath, SyncLocalFile? local,
        SyncRevision head, bool keepBoth, CancellationToken token)
    {
        if (keepBoth && local?.Kind == "file") await PreserveLocalAsync(folder, oldPath, local.Hash, token);
        if (head.IsDeleted)
        {
            var target = SyncPaths.Resolve(folder.LocalPath, oldPath);
            if (local?.Kind == "file") await PreserveLocalAsync(folder, oldPath, local.Hash, token, recovery: true);
            if (File.Exists(target))
            {
                await AssertUnchangedAsync(target, local, token);
                File.Delete(target);
            }
            else if (Directory.Exists(target)) Directory.Delete(target, false); // Never delete a nonempty directory recursively.
            Accept(folder, null, head);
            return;
        }
        var path = head.RelativePath;
        if (head.Kind == "directory")
        {
            Directory.CreateDirectory(SyncPaths.Resolve(folder.LocalPath, path));
            Accept(folder, new SyncLocalFile(path, "directory", 0, 0, "directory"), head);
            return;
        }
        var expected = path.Equals(oldPath, StringComparison.OrdinalIgnoreCase) ? local : null;
        await DownloadToAsync(folder, head, path, expected, token);
        if (!path.Equals(oldPath, StringComparison.OrdinalIgnoreCase) && local != null)
        {
            var old = SyncPaths.Resolve(folder.LocalPath, oldPath);
            await AssertUnchangedAsync(old, local, token);
            File.Delete(old);
            _state.DeleteFile(folder.SyncFolderId, oldPath);
        }
        var file = new FileInfo(SyncPaths.Resolve(folder.LocalPath, path));
        Accept(folder, new SyncLocalFile(path, "file", file.Length, file.LastWriteTimeUtc.Ticks, head.Hash), head);
        _state.DeleteIssue(folder.SyncFolderId, oldPath);
    }

    private async Task DownloadToAsync(SyncFolderBinding folder, SyncRevision head, string path,
        SyncLocalFile? expected, CancellationToken token)
    {
        var target = SyncPaths.Resolve(folder.LocalPath, path);
        var temporary = Directory.CreateDirectory(Path.Combine(folder.LocalPath, ".organessonium-sync-" + Guid.NewGuid().ToString("N")));
        try
        {
            var downloaded = await _cloud.DownloadAsync(folder, head, temporary, token, p => Progress?.Invoke(p));
            if (downloaded.Length != head.SizeBytes || await CloudSyncRepository.HashAsync(downloaded.FullName, token) != head.Hash)
                throw new InvalidDataException("O arquivo remoto não passou na validação.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            SyncPaths.Resolve(folder.LocalPath, path);
            await AssertUnchangedAsync(target, expected, token);
            token.ThrowIfCancellationRequested();
            if (File.Exists(target))
            {
                // ReplaceFile preserves the exact replaced bytes, including a last-moment local edit.
                // An open read guard prevents ReplaceFile on Windows, so validate the captured backup.
                var recovery = SyncPaths.Resolve(folder.LocalPath, ".organessonium-recovery/last-version/" + path);
                Directory.CreateDirectory(Path.GetDirectoryName(recovery)!);
                File.Replace(downloaded.FullName, target, recovery, true);
                if (await CloudSyncRepository.HashAsync(recovery, CancellationToken.None) != expected?.Hash)
                {
                    var rejected = SyncPaths.Resolve(folder.LocalPath, ".organessonium-recovery/rejected-" + Guid.NewGuid().ToString("N") + "/" + path);
                    Directory.CreateDirectory(Path.GetDirectoryName(rejected)!);
                    File.Replace(recovery, target, rejected, true);
                    throw new SyncConcurrencyException();
                }
            }
            else File.Move(downloaded.FullName, target, false); // Do not overwrite a newly-created local file.
            File.SetLastWriteTimeUtc(target, DateTimeOffset.Parse(head.ModifiedUtc).UtcDateTime);
        }
        finally { if (temporary.Exists) temporary.Delete(true); }
    }

    private static async Task AssertUnchangedAsync(string target, SyncLocalFile? expected, CancellationToken token)
    {
        if (!File.Exists(target)) { if (expected != null) throw new SyncConcurrencyException(); return; }
        if (expected == null || await CloudSyncRepository.HashAsync(target, token) != expected.Hash) throw new SyncConcurrencyException();
    }

    private async Task PreserveLocalAsync(SyncFolderBinding folder, string path, string hash, CancellationToken token, bool recovery = false)
    {
        var source = SyncPaths.Resolve(folder.LocalPath, path);
        var destination = SyncPaths.Resolve(folder.LocalPath, recovery ?
            ".organessonium-recovery/" + ConflictName(folder, path, _state.DeviceName) : ConflictName(folder, path, _state.DeviceName));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            await input.CopyToAsync(output, token);
        if (await CloudSyncRepository.HashAsync(destination, token) != hash) throw new SyncConcurrencyException();
    }

    private static string ConflictName(SyncFolderBinding folder, string path, string device)
    {
        var directory = path.Contains('/') ? path[..(path.LastIndexOf('/') + 1)] : "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var label = PathRules.CleanFileName(device);
        var candidate = $"{directory}{name} (conflito de {label} {DateTime.Now:yyyy-MM-dd HHmmss}){extension}";
        var suffix = 1;
        while (File.Exists(SyncPaths.Resolve(folder.LocalPath, candidate)))
            candidate = $"{directory}{name} (conflito de {label} {DateTime.Now:yyyy-MM-dd HHmmss} {suffix++}){extension}";
        return candidate;
    }

    private void Accept(SyncFolderBinding folder, SyncLocalFile? local, SyncRevision head)
    {
        _state.SaveFile(folder.SyncFolderId, new SyncFileState
        {
            RelativePath = head.RelativePath, FileId = head.FileId, Kind = head.Kind,
            BaseRevisionId = head.RevisionId, BaseHash = head.Hash, BaseDeleted = head.IsDeleted,
            LocalExists = local != null, LocalSize = local?.Size ?? 0, LocalWriteTicks = local?.WriteTicks ?? 0, LocalHash = local?.Hash ?? ""
        });
        _state.DeleteIssue(folder.SyncFolderId, head.RelativePath);
        Changed?.Invoke();
    }

    private void RememberObserved(SyncFolderBinding folder, Dictionary<string, SyncFileState> baseline, SyncScan scan)
    {
        foreach (var file in scan.Entries.Values)
        {
            baseline.TryGetValue(file.RelativePath, out var known);
            if (known is { LocalExists: true } && known.LocalWriteTicks == file.WriteTicks && known.LocalSize == file.Size && known.LocalHash == file.Hash) continue;
            known ??= new SyncFileState { RelativePath = file.RelativePath, Kind = file.Kind,
                FileId = file.Kind == "directory" ? AppRegistry.SyncDirectoryId(folder.SyncFolderId, file.RelativePath) : Guid.NewGuid().ToString("N") };
            known.LocalExists = true;
            known.LocalSize = file.Size;
            known.LocalWriteTicks = file.WriteTicks;
            known.LocalHash = file.Hash;
            _state.SaveFile(folder.SyncFolderId, known);
            baseline[file.RelativePath] = known;
        }
    }

    private SyncIssue? FindIssue(SyncFolderBinding folder, string path, IReadOnlyList<SyncRevision> heads, SyncLocalFile? local)
    {
        var issue = _state.Issues(folder.SyncFolderId).FirstOrDefault(i => i.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (issue == null || issue.LocalHash != (local?.Hash ?? "") ||
            !issue.Remote.Select(r => r.RevisionId).Order(StringComparer.Ordinal).SequenceEqual(heads.Select(r => r.RevisionId).Order(StringComparer.Ordinal))) return null;
        return issue;
    }

    private void SetIssue(SyncFolderBinding folder, string path, SyncLocalFile? local, IReadOnlyList<SyncRevision> heads,
        string kind, string message, SyncIssue? existing)
    {
        if (existing != null) return;
        AppLogger.SyncEvent("Sync.Decision", "kind=" + kind, true);
        _state.SaveIssue(new SyncIssue
        {
            SyncFolderId = folder.SyncFolderId, RelativePath = path, Kind = kind, Message = message,
            LocalHash = local?.Hash ?? "", LocalModifiedUtc = local is { WriteTicks: > 0 } ? new DateTime(local.WriteTicks, DateTimeKind.Utc).ToString("O") : "",
            Remote = heads.ToList()
        });
        Changed?.Invoke();
    }

    private static IReadOnlyList<SyncRevision> HeadsFor(SyncCloudSnapshot snapshot, string path, string? fileId)
        => (snapshot.Heads.GetValueOrDefault(path) ?? [])
            .Concat(string.IsNullOrWhiteSpace(fileId) ? [] : snapshot.HeadsByFileId.GetValueOrDefault(fileId) ?? [])
            .DistinctBy(h => h.RevisionId).ToList();

    private void Publish(SyncFolderBinding folder, string state, int pending, string message)
    {
        _statuses[folder.SyncFolderId] = new(folder.SyncFolderId, folder.Name, folder.LocalPath, folder.CloudFolderId,
            state, pending, _state.Issues(folder.SyncFolderId).Count, _paused || folder.Paused, message);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        foreach (var operation in _running.Values) operation.Cancel();
        foreach (var gate in _gates.Values)
        {
            await gate.WaitAsync();
            gate.Release();
        }
        _lifetime.Dispose();
    }
}
