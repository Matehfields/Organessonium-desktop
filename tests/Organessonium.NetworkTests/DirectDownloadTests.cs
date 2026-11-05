using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private sealed class DirectDownloadFixture : IDisposable
    {
        internal readonly DirectoryInfo Root = Directory.CreateTempSubdirectory("organessonium-direct-download-");
        internal DirectoryInfo Destination => Root.CreateSubdirectory("destination");
        internal readonly byte[][] Plain;
        internal readonly byte[][] Wire;
        internal readonly EncryptionService Crypto = CreateEncryptionService(out _);
        internal readonly AppEntry Entry;
        internal readonly TransferManager Transfers;
        internal readonly ConcurrentDictionary<int, int> Reads = new();
        internal Func<int, CancellationToken, Task>? BeforeRead;
        internal Func<int, CancellationToken, Task>? BeforeMessage;
        internal Func<int, Stream>? Body;
        internal string? Batch;
        internal string? PreviewKind;
        internal int LargestRead;
        private readonly VideoHttpHandler _handler;
        private readonly DiscordApiClient _api;

        internal DirectDownloadFixture(bool encrypted = true, bool references = true, bool hashes = false, int[]? lengths = null)
        {
            lengths ??= hashes ? [1024, 1024, 1024, 1024, 317] : [4097, 31, 8000, 1025, 239];
            Plain = lengths.Select((length, index) => Enumerable.Repeat((byte)(index + 13), length).ToArray()).ToArray();
            Entry = AppEntry.File("content.bin", "thread", TransferState.Now(), lengths.Sum(length => (long)length), "/");
            Entry.FileId = "direct-download"; Entry.VersionId = "v1";
            if (encrypted) Entry.Encryption = Crypto.CreateFileEncryption(EncryptionModes.Standard);
            Entry.ChunkCount = lengths.Length;
            if (references) Entry.ChunkReferences = lengths.Select((_, i) => new ChunkReference { Index = i, MessageId = (100 + i).ToString(), AttachmentId = "a" + i }).ToList();
            else Entry.ChunkMessageIds = lengths.Select((_, i) => (100 + i).ToString()).ToList();
            if (hashes) Entry.Checksums = new PayloadChecksums
            {
                ChunkSizeBytes = lengths[0], Chunks = Plain.Select(bytes => Convert.ToHexStringLower(SHA256.HashData(bytes))).ToList(),
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(Plain.SelectMany(bytes => bytes).ToArray()))
            };
            var context = Entry.Encryption == null ? null : new EncryptedPayloadContext(Entry.FileId, Entry.Encryption, EncryptedPayloadRoles.Original, 0, Plain.Length);
            Wire = Plain.Select((bytes, i) => context == null ? bytes : Crypto.EncryptBytes(bytes, context.WithChunkIndex(i))).ToArray();
            Transfers = new TransferManager(new TransferStateRepository(Root.CreateSubdirectory("states")));
            _handler = new VideoHttpHandler(async (request, token) =>
            {
                if (request.RequestUri!.Host == "discord.com")
                {
                    var last = request.RequestUri.AbsolutePath.Split('/').Last();
                    var around = System.Text.RegularExpressions.Regex.Match(request.RequestUri.Query, @"[?&]around=(\d+)");
                    var anchor = int.TryParse(last, out var id) ? id - 100 : around.Success ? int.Parse(around.Groups[1].Value) - 100 : Wire.Length - 1;
                    if (BeforeMessage != null) await BeforeMessage(anchor, token);
                    if (int.TryParse(last, out var message)) return message - 100 < 0 || message - 100 >= Wire.Length
                        ? new HttpResponseMessage(HttpStatusCode.NotFound) : VideoJsonResponse(JsonSerializer.Serialize(Message(message - 100)));
                    return VideoJsonResponse(JsonSerializer.Serialize(Enumerable.Range(0, Wire.Length).Select(Message)));
                }
                var index = int.Parse(request.RequestUri.Segments.Last());
                Reads.AddOrUpdate(index, 1, (_, count) => count + 1);
                if (BeforeRead != null) await BeforeRead(index, token);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(Body?.Invoke(index) ?? new BoundedDownloadStream(Wire[index], size => LargestRead = Math.Max(LargestRead, size)))
                };
            });
            _api = new DiscordApiClient("direct-fixture-" + Guid.NewGuid(), new HttpClient(_handler, false));
        }

        private object Message(int index) => new { id = (100 + index).ToString(), content = Batch == null ? "" :
            (PreviewKind == null ? $"batch:{Batch} Parte {index + 1}/{Wire.Length}" :
                $"ORG_PREVIEW|{PreviewKind}|batch:{Batch}|Parte {index + 1}/{Wire.Length}"),
            attachments = new[] { new { id = "a" + index, filename = "chunk-" + index + ".bin", size = Wire[index].Length, url = "https://cdn.invalid/" + index } } };
        internal Task<FileInfo> Run(TransferState? state = null, CancellationToken token = default, Action<DownloadProgress>? progress = null) => state == null
            ? Downloader.DownloadAndAssembleAsync(Entry, Destination, _ => { }, token, progress)
            : Downloader.DownloadAndAssembleAsync(Entry, Destination, state, Transfers, _ => { }, token, progress);
        internal ForumDownloader Downloader => new(_api, new AppSettings { DownloadThreads = 5 }, Crypto, () => new HttpClient(_handler, false));
        internal byte[] Expected => Plain.SelectMany(bytes => bytes).ToArray();
        public void Dispose() { _api.Dispose(); _handler.Dispose(); try { Root.Delete(true); } catch { } }
    }

    private sealed class BoundedDownloadStream(byte[] bytes, Action<int>? requested = null, Action? interrupted = null) : MemoryStream(bytes, false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            requested?.Invoke(buffer.Length);
            if (interrupted != null && Position >= Length / 2) { interrupted(); throw new OperationCanceledException(token); }
            return base.ReadAsync(buffer[..Math.Min(1024, buffer.Length)], token);
        }
    }

    private static async Task<TestOutcome> DirectDownloadOutOfOrderAsync()
    {
        foreach (var encrypted in new[] { false, true })
        {
            using var h = new DirectDownloadFixture(encrypted);
            var created = new ConcurrentQueue<string>();
            var renamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watcher = new FileSystemWatcher(h.Destination.FullName) { IncludeSubdirectories = true, EnableRaisingEvents = true };
            watcher.Created += (_, item) => created.Enqueue(item.FullPath);
            watcher.Renamed += (_, item) => { if (item.OldName!.EndsWith(".part") && item.Name == "content.bin") renamed.TrySetResult(); };
            var releases = h.Plain.Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
            h.BeforeRead = (index, token) => releases[index].Task.WaitAsync(token);
            var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
            var progress = new ConcurrentQueue<DownloadProgress>();
            var running = h.Run(state, progress: progress.Enqueue);
            await WaitForVideoConditionAsync(() => h.Reads.Count == 5);
            foreach (var index in new[] { 3, 1, 4, 0, 2 })
            {
                releases[index].TrySetResult();
                await WaitForVideoConditionAsync(() => state.Chunks[index].Downloaded);
            }
            var output = await running;
            await renamed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            RequireSync((await File.ReadAllBytesAsync(output.FullName)).SequenceEqual(h.Expected), "a escrita fora de ordem alterou o conteúdo");
            RequireSync(h.Destination.GetFiles().Select(file => file.Name).SequenceEqual(new[] { "content.bin" }) &&
                h.Destination.GetDirectories().Length == 0 && h.LargestRead <= 1024 * 1024, "houve chunk intermediário, montagem ou buffer legado integral");
            RequireSync(created.All(path => path.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || path == output.FullName), "houve arquivo intermediário além do parcial");
            RequireSync(progress.All(item => item.Percent <= 100) && progress.Last().Percent == 100 &&
                progress.Any(item => item.Message == "Verificando integridade do arquivo...") && !progress.Any(item => item.Message.Contains("Montando")),
                "o progresso ainda mostra montagem ou não conclui em 100%");
        }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadCheckpointSchedulingAsync()
    {
        using var h = new DirectDownloadFixture(lengths: Enumerable.Repeat(128 * 1024, 20).ToArray());
        var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
        var locks = (ConcurrentDictionary<string, SemaphoreSlim>)typeof(TransferStateRepository)
            .GetField("_locks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(h.Transfers.Repository)!;
        var checkpoint = locks[state.TransferId];
        var held = 0;
        // Delay the real checkpoint write after the initial checkpoint, not the CDN or payload writes.
        h.BeforeRead = async (_, token) =>
        {
            if (Interlocked.CompareExchange(ref held, 1, 0) == 0) await checkpoint.WaitAsync(token);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = h.Run(state, timeout.Token);
        try
        {
            await WaitForVideoConditionAsync(() => h.Reads.Count == h.Plain.Length);
            RequireSync(!running.IsCompleted, "o download concluiu antes de persistir o checkpoint");
        }
        finally
        {
            if (held != 0) checkpoint.Release();
            await running;
        }
        var file = await running;
        var saved = await h.Transfers.Repository.LoadActiveByIdAsync(state.TransferId);
        RequireSync((await File.ReadAllBytesAsync(file.FullName)).SequenceEqual(h.Expected) &&
            saved is { DownloadReadyToFinalize: true } && saved.Chunks.All(chunk => chunk.Downloaded && chunk.Sha256?.Length == 64),
            "a sobreposição de rede/checkpoint perdeu integridade ou a confirmação durável");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadIncrementalDiscoveryAsync()
    {
        foreach (var mode in new[] { "manifest", "variable-legacy", "plain" })
        {
            var lengths = Enumerable.Range(0, 120).Select(index => mode == "manifest" ? (index == 119 ? 317 : 1024) : 1024 + index % 19 * 13).ToArray();
            using var h = new DirectDownloadFixture(encrypted: mode != "plain", references: mode == "manifest",
                hashes: mode == "manifest", lengths: lengths);
            var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.BeforeMessage = (index, token) => index < 10 ? Task.CompletedTask : tail.Task.WaitAsync(token);
            var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var running = h.Run(state, cancel.Token);
            try
            {
                await WaitForVideoConditionAsync(() => state.Chunks.Count == 120 && state.Chunks.Take(10).All(chunk => chunk.Downloaded));
                RequireSync(h.Reads.Count == 10 && new FileInfo(state.PartialFilePath!).Length > 0 && !running.IsCompleted,
                    "o download esperou a lista completa, adivinhou offsets ou concluiu sem as partes restantes: " + mode);
                if (mode == "variable-legacy")
                {
                    cancel.Cancel();
                    try { await running; return TestOutcome.Fail("a descoberta ignorou o cancelamento"); } catch (OperationCanceledException) { }
                    state = (await h.Transfers.Repository.LoadActiveByIdAsync(state.TransferId))!;
                    RequireSync(state.Chunks.Take(10).All(chunk => chunk.Downloaded) && state.Chunks.Skip(10).All(chunk => !chunk.Downloaded),
                        "a descoberta incompleta perdeu o checkpoint ou confirmou regiões desconhecidas");
                }
            }
            finally { tail.TrySetResult(); }
            h.BeforeMessage = null;
            var file = mode == "variable-legacy" ? await h.Run(state) : await running;
            RequireSync(h.Reads.Count == 120 && h.Reads.All(pair => pair.Value == 1) &&
                (await File.ReadAllBytesAsync(file.FullName)).SequenceEqual(h.Expected),
                "a descoberta incremental perdeu bytes, refez partes válidas ou quebrou o contexto criptográfico: " + mode);
        }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadLegacyLayoutsAsync()
    {
        foreach (var route in new[] { "thread", "batch", "preview" })
        {
            using var h = new DirectDownloadFixture(encrypted: false, references: false);
            FileInfo? output;
            if (route == "thread")
            {
                h.Entry.ChunkMessageIds.Clear(); h.Entry.ChunkCount = 0;
                output = await h.Run();
            }
            else if (route == "batch")
            {
                h.Batch = h.Entry.BatchId = "legacy-batch";
                h.Entry.ChunkMessageIds = ["9999"]; h.Entry.ChunkCount = 1;
                output = await h.Run();
            }
            else
            {
                h.Batch = "preview-batch"; h.PreviewKind = "image";
                output = await h.Downloader.DownloadPreviewAndAssembleAsync("thread", "image", h.Destination);
            }
            RequireSync(output != null && (await File.ReadAllBytesAsync(output.FullName)).SequenceEqual(h.Expected) &&
                !h.Destination.GetDirectories().Any() && !h.Destination.GetFiles("*.part").Any(), "o fluxo legado falhou: " + route);
        }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadResumeCorruptionAsync()
    {
        foreach (var remoteHashes in new[] { false, true })
        foreach (var damage in new[] { "corrupt", "truncate", "missing", "stale" })
        {
            using var h = new DirectDownloadFixture(hashes: remoteHashes);
            using var interrupted = new CancellationTokenSource();
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.BeforeRead = (index, token) => index is 0 or 3 ? Task.CompletedTask : hold.Task.WaitAsync(token);
            var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
            var running = h.Run(state, interrupted.Token);
            await WaitForVideoConditionAsync(() => state.Chunks.Count == 5 && state.Chunks[0].Downloaded && state.Chunks[3].Downloaded);
            interrupted.Cancel();
            try { await running; return TestOutcome.Fail("não interrompeu o download"); } catch (OperationCanceledException) { }
            state = (await new TransferStateRepository(h.Transfers.Repository.RootDirectory).LoadActiveByIdAsync(state.TransferId))!;
            RequireSync(state.DownloadedChunks.SequenceEqual(new[] { 0, 3 }) && state.Chunks[0].Sha256?.Length == 64 && state.Chunks.All(chunk => chunk.TempChunkPath == null), "checkpoint não conserva regiões/hashes duráveis");
            if (damage == "missing") File.Delete(state.PartialFilePath!);
            else if (damage == "stale") { state.Chunks[0].Downloaded = false; await h.Transfers.SaveAsync(state); }
            else using (var file = new FileStream(state.PartialFilePath!, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                if (damage == "truncate") file.SetLength(state.Chunks[3].Offset!.Value + state.Chunks[3].LogicalSizeBytes / 2);
                else { file.Position = state.Chunks[0].Offset!.Value; file.WriteByte(255); }
                file.Flush(true);
            }
            var before = h.Reads.ToDictionary(pair => pair.Key, pair => pair.Value);
            h.BeforeRead = null;
            var output = await h.Run(state);
            RequireSync((await File.ReadAllBytesAsync(output.FullName)).SequenceEqual(h.Expected) &&
                (damage is "truncate" or "missing" ? h.Reads[3] > before[3] : h.Reads[3] == before[3]) &&
                (damage == "truncate" ? h.Reads[0] == before[0] : h.Reads[0] > before[0]),
                "a retomada não isolou regiões inválidas: " + damage);
        }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadInterruptedWriteAsync()
    {
        using var h = new DirectDownloadFixture(encrypted: false, references: false);
        using var interrupted = new CancellationTokenSource();
        var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
        h.BeforeRead = async (index, token) =>
        {
            if (index != 2) return;
            await WaitForVideoConditionAsync(() => state.Chunks.Count == 5 && state.Chunks[0].Downloaded);
        };
        h.Body = index => new BoundedDownloadStream(h.Wire[index], interrupted: index == 2 ? () => interrupted.Cancel() : null);
        try { await h.Run(state, interrupted.Token); return TestOutcome.Fail("a escrita interrompida foi aceita"); } catch (OperationCanceledException) { }
        state = (await h.Transfers.Repository.LoadActiveByIdAsync(state.TransferId))!;
        RequireSync(state.Chunks[0].Downloaded && !state.Chunks[2].Downloaded && File.Exists(state.PartialFilePath), "a falha de escrita perdeu a região válida ou marcou a incompleta");
        var before = h.Reads[0]; h.BeforeRead = null; h.Body = null;
        var output = await h.Run(state);
        RequireSync(h.Reads[0] == before && (await File.ReadAllBytesAsync(output.FullName)).SequenceEqual(h.Expected), "a região incompleta não foi sobrescrita isoladamente");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadLegacyMigrationAsync()
    {
        foreach (var hashes in new[] { true, false })
        {
            using var h = new DirectDownloadFixture(hashes: hashes);
            var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
            var old = h.Destination.CreateSubdirectory(".organessonium_parts_" + state.TransferId[..12]);
            state.TempPath = old.FullName;
            var legacy = Path.Combine(old.FullName, "part_000000_chunk-0.bin");
            await File.WriteAllBytesAsync(legacy, h.Plain[0]);
            await h.Transfers.MarkDownloadChunkAsync(state, 0, legacy, h.Plain[0].Length);
            var unrelated = Path.Combine(h.Destination.FullName, "unrelated.bin");
            await File.WriteAllTextAsync(unrelated, "preserve");
            var output = await h.Run(state);
            RequireSync((await File.ReadAllBytesAsync(output.FullName)).SequenceEqual(h.Expected) && !File.Exists(legacy) &&
                await File.ReadAllTextAsync(unrelated) == "preserve" && h.Reads.ContainsKey(0) != hashes,
                "migração reutilizou plaintext sem hash ou apagou arquivo independente");
        }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadAuthenticationAndFinalizationAsync()
    {
        foreach (var badKey in new[] { false, true })
        {
            using var h = new DirectDownloadFixture(lengths: [4096]);
            if (badKey) h.Entry.Encryption = h.Crypto.CreateFileEncryption(EncryptionModes.Standard);
            else h.Wire[0][^1] ^= 1;
            var target = Path.Combine(h.Destination.FullName, h.Entry.Name);
            await File.WriteAllTextAsync(target, "preserve");
            var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
            try { await h.Run(state); return TestOutcome.Fail("payload não autenticado foi aceito"); }
            catch (EncryptedPayloadIntegrityException) { }
            RequireSync(state.DownloadedChunks.Count == 0 && new FileInfo(state.PartialFilePath!).Length == 0 &&
                await File.ReadAllTextAsync(target) == "preserve", "dados não autenticados foram gravados ou substituíram o destino");
        }
        using (var h = new DirectDownloadFixture(hashes: true))
        {
            var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
            var target = Path.Combine(h.Destination.FullName, h.Entry.Name);
            await File.WriteAllTextAsync(target, "preserve");
            using (var locked = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { await h.Run(state); return TestOutcome.Fail("a renomeação ignorou o destino bloqueado"); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                RequireSync(File.Exists(state.PartialFilePath) && state.DownloadReadyToFinalize, "a falha de renomeação descartou o arquivo validado");
            }
            var before = h.Reads.Values.Sum();
            await h.Run(state);
            RequireSync(h.Reads.Values.Sum() == before, "a finalização repetiu downloads já validados");
            // Simulate process termination after rename but before CompleteAsync moves the checkpoint.
            state = (await new TransferStateRepository(h.Transfers.Repository.RootDirectory).LoadActiveByIdAsync(state.TransferId))!;
            await h.Run(state);
            RequireSync(h.Reads.Values.Sum() == before && (await File.ReadAllBytesAsync(target)).SequenceEqual(h.Expected), "a retomada não reconheceu a renomeação concluída");
        }
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadCancellationAndIdentityAsync()
    {
        using var h = new DirectDownloadFixture();
        var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
        RequireSync((await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination)).TransferId == state.TransferId, "criou checkpoint duplicado");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.BeforeRead = (_, token) => hold.Task.WaitAsync(token);
        using var cancelled = new CancellationTokenSource();
        var running = h.Run(state, cancelled.Token);
        await WaitForVideoConditionAsync(() => h.Reads.Count > 0);
        try { await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination); return TestOutcome.Fail("um segundo escritor foi admitido"); } catch (DownloadAlreadyActiveException) { }
        cancelled.Cancel(); try { await running; } catch (OperationCanceledException) { }
        await h.Transfers.CancelAsync(state, false, false);
        RequireSync((await h.Transfers.RecoverableAsync()).Any(saved => saved.TransferId == state.TransferId) && File.Exists(state.PartialFilePath), "cancelamento sem limpeza perdeu a retomada");
        h.Entry.VersionId = "v2";
        RequireSync((await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination)).TransferId != state.TransferId, "reutilizou checkpoint de outra versão");
        await h.Transfers.CancelAsync(state, false, true);
        RequireSync(!File.Exists(state.PartialFilePath), "cancelamento definitivo deixou o arquivo parcial");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadMemoryAdmissionAsync()
    {
        AppMemoryBudget.Initialize();
        using var h = new DirectDownloadFixture(lengths: [EncryptionService.MaximumPlaintextPayloadBytes]);
        var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
        GC.Collect();
        var reservedBytes = AppMemoryBudget.AdmissionBytes - AppMemoryBudget.CommittedBytes() -
            2L * DiscordLimits.AttachmentBytes - 8L * 1024 * 1024;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var reservation = await RamUploadBudget.ReserveExternalBytesAsync(reservedBytes, 0, timeout.Token);
        using (var cancelled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
        {
            var running = h.Run(state, cancelled.Token);
            await Task.Delay(250);
            RequireSync(h.Reads.Count == 0 && !running.IsCompleted, "o downloader ignorou a reserva global de RAM");
            cancelled.Cancel(); try { await running; } catch (OperationCanceledException) { }
        }
        reservation.Dispose();
        var file = await h.Run(state, timeout.Token);
        RequireSync(file.Length == h.Entry.SizeBytes && AppMemoryBudget.PeakCommittedBytes < AppMemoryBudget.LimitBytes,
            "o download ultrapassou o orçamento ou perdeu bytes após a espera");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadReusesPayloadBuffersAsync()
    {
        const int mib = 1024 * 1024;
        using var h = new DirectDownloadFixture(lengths: Enumerable.Repeat(mib, 32).ToArray());
        h.Body = index => new MemoryStream(h.Wire[index], false);
        var state = await h.Transfers.CreateDownloadAsync(h.Entry, h.Destination);
        RamUploadBudget.Pool.TrimFreeBuffers();
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var file = await h.Run(state);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        RequireSync(allocated < 80L * mib, "o download continua criando ciphertext e plaintext novos a cada chunk");
        await using var input = file.OpenRead();
        var buffer = new byte[mib];
        foreach (var expected in h.Plain)
        {
            await input.ReadExactlyAsync(buffer);
            RequireSync(buffer.SequenceEqual(expected), "a reutilização dos buffers alterou o conteúdo autenticado");
        }
        RequireSync(input.Position == input.Length, "o download reutilizado contém bytes extras");
        Console.WriteLine($"DOWNLOAD_ALLOCATIONS payloadMiB=32 allocatedMiB={allocated / mib}");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> DirectDownloadSparseRegionsAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium-sparse-download-");
        try
        {
            const int mib = 1024 * 1024;
            var layout = new DownloadFileLayout(new[] { (0, 512L * mib), (1, 8L * mib) }, false, null);
            var partial = Path.Combine(root.FullName, "content.part");
            var payload = Enumerable.Repeat((byte)173, 8 * mib).ToArray();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (var file = new PartialDownloadFile(partial, Path.Combine(root.FullName, "content.bin"), layout, false))
            {
                await using (var region = file.OpenRegion(1)) await region.WriteAsync(payload);
                file.Flush();
                var allocated = PartialDownloadFile.AllocatedBytes(partial);
                RequireSync(file.Length == 520L * mib, "a escrita fora de ordem alterou o tamanho do parcial");
                if (new DriveInfo(Path.GetPathRoot(root.FullName)!).DriveFormat is "NTFS" or "ReFS")
                    RequireSync(allocated <= 9L * mib, "o download gravou regiões vazias além do payload recebido");
                await using var input = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var probe = new byte[4096];
                input.Position = 256L * mib;
                await input.ReadExactlyAsync(probe);
                RequireSync(probe.All(value => value == 0), "uma região pendente expôs dados diferentes de zero");
                RequireSync(await file.MatchesAsync(layout.Regions[1], Convert.ToHexStringLower(SHA256.HashData(payload)), default),
                    "a alocação esparsa alterou o payload recebido");
                Console.WriteLine($"SPARSE_DOWNLOAD payloadMiB=8 logicalMiB=520 allocatedBytes={allocated} elapsedMs={watch.ElapsedMilliseconds}");
            }
        }
        finally { root.Delete(true); }
        return TestOutcome.Pass();
    }

    private static Task<TestOutcome> DirectDownloadLargeOffsetsAsync()
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var layout = new DownloadFileLayout(new[] { (0, 50_000_000_000L), (1, 51_000_000_000L), (2, 49_000_000_000L) }, false, null);
        RequireSync(layout.Length == 150_000_000_000L && layout.Regions[2].Offset == 101_000_000_000L &&
            GC.GetAllocatedBytesForCurrentThread() - before < 1024 * 1024, "o layout de 150 GB usa int ou aloca memória proporcional ao arquivo");
        try { _ = new DownloadFileLayout(new[] { (0, long.MaxValue), (1, 1L) }, false, null); return Task.FromResult(TestOutcome.Fail("overflow aceito")); } catch (OverflowException) { }
        return Task.FromResult(TestOutcome.Pass());
    }
}
