using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.Sync;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> TwoComputerSyncLifecycleAsync()
    {
        using var fixture = new SyncFixture();
        var a = fixture.Binding("A");
        var b = fixture.Binding("B");
        var stateA = fixture.State("A");
        var stateB = fixture.State("B");
        RequireSync(stateA.DeviceId != stateB.DeviceId, "instalações diferentes compartilharam deviceId");
        RequireSync(fixture.State("A").DeviceId == stateA.DeviceId, "deviceId não persistiu");
        await using var engineA = new SyncEngine(stateA, fixture.Cloud);
        await using var engineB = new SyncEngine(stateB, fixture.Cloud);
        fixture.Write(a, "src/App.cs", "primeira versão");
        await engineA.SynchronizeAsync(a);
        await engineB.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "src/App.cs") == "primeira versão", "primeiro vínculo não preservou a árvore ou baixou o arquivo");
        RequireSync(stateB.Files(b.SyncFolderId).ContainsKey("src/App.cs"), "base do PC B não foi persistida");
        fixture.Write(a, "src/App.cs", "segunda versão");
        await engineA.SynchronizeAsync(a);
        await engineB.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "src/App.cs") == "primeira versão" && stateB.Issues(b.SyncFolderId).Count == 1,
            "mudança remota substituiu silenciosamente o arquivo");
        var issue = stateB.Issues(b.SyncFolderId).Single();
        engineB.Resolve(b, issue.Id, "cloud", true);
        await engineB.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "src/App.cs") == "segunda versão" && b.AutomaticRemoteUpdates,
            "a confirmação não substituiu o arquivo validado ou não persistiu a preferência");
        fixture.Write(a, "src/App.cs", "terceira versão");
        await engineA.SynchronizeAsync(a);
        await engineB.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "src/App.cs") == "terceira versão", "atualização automática da pasta não foi aplicada");
        fixture.Write(a, "src/App.cs", "alteração no A");
        fixture.Write(b, "src/App.cs", "alteração no B");
        await engineA.SynchronizeAsync(a);
        await engineB.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "src/App.cs") == "alteração no B", "conflito destruiu a versão local");
        issue = stateB.Issues(b.SyncFolderId).Single();
        RequireSync(issue.Kind == "conflict", "alterações concorrentes não foram reconhecidas");
        engineB.Resolve(b, issue.Id, "both", false);
        await engineB.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "src/App.cs") == "alteração no A" &&
            Directory.EnumerateFiles(Path.Combine(b.LocalPath, "src"), "*conflito*").Any(p => File.ReadAllText(p) == "alteração no B"),
            "manter ambas não preservou as duas versões");
        await engineB.SynchronizeAsync(b);
        await engineA.SynchronizeAsync(a);
        var uploadsBeforeRename = fixture.Cloud.Uploads;
        File.Move(SyncPaths.Resolve(a.LocalPath, "src/App.cs"), SyncPaths.Resolve(a.LocalPath, "src/Main.cs"));
        await engineA.SynchronizeAsync(a);
        RequireSync(fixture.Cloud.Uploads == uploadsBeforeRename, "renomeação reenviou o payload");
        await engineB.SynchronizeAsync(b);
        RequireSync(File.Exists(SyncPaths.Resolve(b.LocalPath, "src/Main.cs")) && !File.Exists(SyncPaths.Resolve(b.LocalPath, "src/App.cs")),
            "renomeação remota não atualizou o caminho local");
        a.DeletionPolicy = "propagate";
        b.DeletionPolicy = "propagate";
        File.Delete(SyncPaths.Resolve(a.LocalPath, "src/Main.cs"));
        fixture.Write(b, "src/Main.cs", "edição que não pode ser apagada");
        await engineA.SynchronizeAsync(a);
        await engineB.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "src/Main.cs") == "edição que não pode ser apagada", "tombstone apagou uma alteração concorrente");
        issue = stateB.Issues(b.SyncFolderId).Single(i => i.RelativePath == "src/Main.cs");
        engineB.Resolve(b, issue.Id, "cloud", false);
        await engineB.SynchronizeAsync(b);
        RequireSync(!File.Exists(SyncPaths.Resolve(b.LocalPath, "src/Main.cs")) &&
            Directory.EnumerateFiles(b.LocalPath, "*", SearchOption.AllDirectories).Any(p => p.Contains(".organessonium-recovery") && File.ReadAllText(p) == "edição que não pode ser apagada"),
            "exclusão confirmada não preservou a recuperação local");
        var persisted = fixture.State("B");
        RequireSync(persisted.Files(b.SyncFolderId).Values.Any(s => s.BaseDeleted) && persisted.Folders(b.AccountKey).Count == 1,
            "reinicialização perdeu a configuração ou tombstone");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> SyncFailuresPreserveFilesAsync()
    {
        using var fixture = new SyncFixture();
        var a = fixture.Binding("A"); var b = fixture.Binding("B");
        var sa = fixture.State("A"); var sb = fixture.State("B");
        await using var ea = new SyncEngine(sa, fixture.Cloud);
        await using var eb = new SyncEngine(sb, fixture.Cloud);
        fixture.Write(a, "documento.txt", "versão válida");
        await ea.SynchronizeAsync(a); await eb.SynchronizeAsync(b);
        fixture.Write(a, "documento.txt", "alteração offline");
        fixture.Cloud.Offline = true;
        await ea.SynchronizeAsync(a);
        RequireSync(sa.Files(a.SyncFolderId)["documento.txt"].LocalHash != sa.Files(a.SyncFolderId)["documento.txt"].BaseHash,
            "offline não manteve a alteração local pendente");
        fixture.Cloud.Offline = false;
        fixture.Cloud.FailUpload = true;
        await ea.SynchronizeAsync(a);
        await eb.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "documento.txt") == "versão válida", "falha no upload destruiu a última revisão válida");
        fixture.Cloud.FailUpload = false;
        await ea.SynchronizeAsync(a); await eb.SynchronizeAsync(b);
        var issue = sb.Issues(b.SyncFolderId).Single();
        eb.Resolve(b, issue.Id, "cloud", false);
        fixture.Cloud.CorruptDownload = true;
        await eb.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "documento.txt") == "versão válida", "download corrompido substituiu o arquivo definitivo");
        fixture.Cloud.CorruptDownload = false;
        fixture.Cloud.CancelDownload = true;
        await eb.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "documento.txt") == "versão válida", "download interrompido substituiu o arquivo anterior");
        fixture.Cloud.CancelDownload = false;
        await eb.SynchronizeAsync(b);
        RequireSync(fixture.Read(b, "documento.txt") == "alteração offline", "retry não concluiu o download validado");
        var count = fixture.Cloud.Uploads;
        using (var writer = new FileStream(SyncPaths.Resolve(a.LocalPath, "documento.txt"), FileMode.Open, FileAccess.Write, FileShare.None))
        {
            writer.WriteByte(7);
            await ea.SynchronizeAsync(a);
            RequireSync(fixture.Cloud.Uploads == count, "arquivo bloqueado/parcial foi enviado");
        }
        ea.Pause(true);
        fixture.Write(a, "pausado.txt", "aguardar");
        await ea.SynchronizeAsync(a);
        RequireSync(fixture.Cloud.Uploads == count, "pausa permitiu upload");
        ea.Pause(false);
        await ea.SynchronizeAsync(a);
        RequireSync(fixture.Cloud.Uploads > count, "retomada não reconciliou as alterações");
        RequireSync(!Directory.EnumerateDirectories(b.LocalPath, ".organessonium-sync-*").Any(), "temporário de download ficou abandonado");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> SyncScannerPathsAndPersistenceAsync()
    {
        using var fixture = new SyncFixture();
        var folder = fixture.Binding("A");
        foreach (var unsafePath in new[] { "../fora.txt", "a/../../fora.txt", "C:/fora", "//servidor/share", "a\\..\\b", "a:stream", "CON.txt", "a/./b", "a./b" })
        {
            var rejected = false;
            try { SyncPaths.Resolve(folder.LocalPath, unsafePath); } catch (InvalidDataException) { rejected = true; }
            RequireSync(rejected, "path traversal/nome inseguro foi aceito: " + unsafePath);
        }
        fixture.Write(folder, ".git/config", "git"); fixture.Write(folder, "bin/resultado.dll", "bin");
        fixture.Write(folder, "obj/objeto", "obj"); fixture.Write(folder, "node_modules/module/index.js", "js");
        fixture.Write(folder, "Thumbs.db", "ignorar"); fixture.Write(folder, "rascunho.tmp", "ignorar");
        for (var i = 0; i < 1500; i++) fixture.Write(folder, $"arquivos/{i}.txt", "teste");
        var scanner = new SyncScanner();
        var first = await scanner.ScanAsync(folder.LocalPath, new Dictionary<string, SyncFileState>());
        RequireSync(first.Complete && first.Entries.ContainsKey(".git/config") && first.Entries.ContainsKey("bin/resultado.dll") &&
            first.Entries.ContainsKey("obj/objeto") && first.Entries.ContainsKey("node_modules/module/index.js") &&
            !first.Entries.ContainsKey("Thumbs.db") && !first.Entries.ContainsKey("rascunho.tmp"), "exclusões afetaram pastas importantes");
        var cache = first.Entries.Values.ToDictionary(f => f.RelativePath, f => new SyncFileState
            { RelativePath = f.RelativePath, Kind = f.Kind, LocalExists = true, LocalHash = f.Hash, LocalSize = f.Size, LocalWriteTicks = f.WriteTicks });
        var next = await scanner.ScanAsync(folder.LocalPath, cache);
        RequireSync(next.Entries.Count == first.Entries.Count, "reconciliação perdeu itens numa pasta grande");
        fixture.Write(folder, "arquivos/0.txt", "outro");
        var changed = await scanner.ScanAsync(folder.LocalPath, cache, changedPaths: new HashSet<string> { "arquivos/0.txt" });
        RequireSync(changed.Entries["arquivos/0.txt"].Hash != first.Entries["arquivos/0.txt"].Hash && changed.Entries.Count == first.Entries.Count,
            "scan incremental perdeu a árvore ou ignorou o caminho alterado");
        fixture.Write(folder, "arquivos/1.txt", "evento perdido");
        var reconciled = await scanner.ScanAsync(folder.LocalPath, cache);
        RequireSync(reconciled.Entries["arquivos/1.txt"].Hash != first.Entries["arquivos/1.txt"].Hash,
            "reconciliação não encontrou uma alteração sem evento do watcher");
        var state = fixture.State("A");
        var identity = state.DeviceId;
        RequireSync(fixture.State("A").DeviceId == identity && fixture.State("A").Folders(folder.AccountKey).Single().SyncFolderId == folder.SyncFolderId,
            "IDs/vínculo não persistiram");
        var sibling = new SyncRevision { RelativePath = "arquivo.txt", FileId = "id", RevisionId = "a", Parents = ["base"] };
        var other = new SyncRevision { RelativePath = "arquivo.txt", FileId = "id", RevisionId = "b", Parents = ["base"] };
        RequireSync(CloudSyncRepository.Snapshot([sibling, other]).Heads["arquivo.txt"].Count == 2,
            "commits concorrentes foram resolvidos silenciosamente");
        RequireSync(StartupManager.CommandLine("C:\\Program Files\\Organessonium\\Organessonium.exe").EndsWith("\" --background"),
            "startup não usa modo background");
        return TestOutcome.Pass();
    }

    private static void RequireSync(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class SyncFixture : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("organessonium-sync-test-");
        public SyncTestCloud Cloud { get; } = new();
        private readonly Dictionary<string, SyncStateStore> _states = new();
        private readonly Dictionary<string, SettingsStore> _settings = new();
        public SyncStateStore State(string computer)
        {
            var store = new SettingsStore(Directory.CreateDirectory(Path.Combine(_root.FullName, computer, "Data")));
            _settings[computer] = store;
            return _states[computer] = new SyncStateStore(store, Path.Combine(_root.FullName, computer, "installation.txt"));
        }
        public SyncFolderBinding Binding(string computer)
        {
            var binding = new SyncFolderBinding { SyncFolderId = "shared-folder", AccountKey = "test-account", Name = "Projetos",
                LocalPath = Directory.CreateDirectory(Path.Combine(_root.FullName, computer, "Local")).FullName,
                CloudFolderId = "sync-shared-folder", CloudPath = "/@synchronized/PC/Projetos" };
            State(computer).SaveFolder(binding);
            return binding;
        }
        public void Write(SyncFolderBinding folder, string relative, string content)
        {
            var path = SyncPaths.Resolve(folder.LocalPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-10));
        }
        public string Read(SyncFolderBinding folder, string relative) => File.ReadAllText(SyncPaths.Resolve(folder.LocalPath, relative));
        public void Dispose() { SqliteConnection.ClearAllPools(); _root.Delete(true); }
    }

    private sealed class SyncTestCloud : ICloudSyncRepository
    {
        private readonly List<SyncRevision> _revisions = [];
        private readonly Dictionary<string, byte[]> _payloads = new();
        public int Uploads;
        public bool Offline, FailUpload, CorruptDownload, CancelDownload;
        public Task<SyncCloudSnapshot> ReadAsync(SyncFolderBinding folder, CancellationToken token)
        {
            if (Offline) throw new HttpRequestException("offline simulado");
            return Task.FromResult(CloudSyncRepository.Snapshot(_revisions.ToList()));
        }
        public async Task<FileMetadata> UploadAsync(SyncFolderBinding folder, SyncLocalFile local, string fileId,
            CancellationToken token, Action<SyncTransferProgress> progress)
        {
            if (FailUpload) throw new IOException("interrupção de upload simulada");
            var bytes = await File.ReadAllBytesAsync(SyncPaths.Resolve(folder.LocalPath, local.RelativePath), token);
            RequireSync(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() == local.Hash, "o scan entregou hash incorreto");
            var thread = "payload-" + Guid.NewGuid().ToString("N");
            _payloads[thread] = bytes; Uploads++;
            return new FileMetadata { FileId = fileId, ThreadId = thread, Name = Path.GetFileName(local.RelativePath),
                SizeBytes = bytes.Length, Chunks = ["chunk"], UploadedAt = DateTime.UtcNow.ToString("O") };
        }
        public Task<SyncRevision> CommitAsync(SyncFolderBinding folder, SyncRevision revision,
            IReadOnlyList<string> expectedHeads, CancellationToken token)
        {
            var current = CloudSyncRepository.Snapshot(_revisions).Heads.Values.SelectMany(h => h)
                .Where(h => h.FileId == revision.FileId || h.RelativePath.Equals(revision.RelativePath, StringComparison.OrdinalIgnoreCase));
            if (!current.Select(h => h.RevisionId).Order().SequenceEqual(expectedHeads.Order())) throw new SyncConcurrencyException();
            _revisions.Add(revision);
            return Task.FromResult(revision);
        }
        public async Task<FileInfo> DownloadAsync(SyncFolderBinding folder, SyncRevision revision, DirectoryInfo staging,
            CancellationToken token, Action<SyncTransferProgress> progress)
        {
            var path = Path.Combine(staging.FullName, "content");
            if (CancelDownload) { await File.WriteAllTextAsync(path, "parcial", token); throw new IOException("download interrompido"); }
            await File.WriteAllBytesAsync(path, CorruptDownload ? "corrompido"u8.ToArray() : _payloads[revision.Payload!.ThreadId], token);
            return new FileInfo(path);
        }
    }
}
