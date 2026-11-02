using System.Net;
using System.Text;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private static async Task<TestOutcome> SharedStorageMixedRolesAndReadProtectionAsync()
    {
        using var h = new SharedHarness();
        var states = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => h.PrepareAsync(index + ".bin", Encoding.UTF8.GetBytes("mixed-" + index))));
        var reservations = await h.Storage.ReserveManyAsync(states.Select(state => (state.FileId, state.TransferId)).ToArray(), default);
        using var memory = new UploadMemoryCoordinator(4);
        async Task<(TransferState State, string Role, ChunkReference Reference)> Send(TransferState state, string role)
        {
            var reservation = reservations[state.TransferId];
            var file = new FileInfo(state.SourcePath);
            var context = new EncryptedPayloadContext(state.FileId, state.Encryption!, role, 0, 1);
            using var part = await memory.PreparePartAsync(h.Encryption, file, file.Length, EncryptionService.MaximumPlaintextPayloadBytes,
                context, default, deferBuffers: true, isolateSendCancellation: true);
            var reference = await h.Storage.Batcher.SendAsync(reservation, context, part, null, default);
            return (state, role, reference);
        }
        var results = await Task.WhenAll(states.Select(state => Send(state, EncryptedPayloadRoles.Original))
            .Concat(states.Take(2).Select(state => Send(state, EncryptedPayloadRoles.Preview))));
        RequireSync(results.Select(result => result.Reference.MessageId).Distinct().Count() == 1 &&
            results.Select(result => result.Reference.AttachmentId).Distinct().Count() == 10, "originais e miniaturas não compartilharam o mesmo batch");
        foreach (var result in results)
        {
            var bytes = await h.Downloader.DownloadMessageAttachmentBytesAsync(reservations[result.State.TransferId].ThreadId,
                result.Reference.MessageId, encryption: new(result.State.FileId, result.State.Encryption!, result.Role, 0, 1), attachmentId: result.Reference.AttachmentId);
            var original = await File.ReadAllBytesAsync(result.State.SourcePath);
            RequireSync(bytes!.SequenceEqual(original), "o batch misto perdeu o contexto criptográfico de um attachment");
        }
        var held = (await h.UploadAsync([await h.PrepareAsync("held.bin", "held"u8.ToArray())])).Single();
        var checkpoint = await h.Transfers.CreateDownloadAsync(held, h.Directory.CreateSubdirectory("held-download"));
        var read = await h.Storage.BeginReadAsync("download:" + checkpoint.TransferId, held.FileId,
            SharedStorageManager.Payloads(MetadataService.CreateFileMetadata(held)), default);
        await read.LeaveRecoverableAsync();
        RequireSync(!await h.DeleteAsync(held), "a exclusão ignorou um download pausado");
        await h.Storage.ReconcileAsync();
        RequireSync(h.Server.Messages(held.ThreadId).Any(message => message.Id == held.ChunkReferences!.Single().MessageId), "reconciliação removeu bytes necessários ao checkpoint");
        var downloaded = await h.Downloader.DownloadAndAssembleAsync(held, new DirectoryInfo(checkpoint.DestinationPath), checkpoint, h.Transfers, _ => { });
        RequireSync(await File.ReadAllTextAsync(downloaded.FullName) == "held", "a retomada perdeu o conteúdo após a exclusão lógica");
        await h.Transfers.CompleteAsync(checkpoint, false);
        await h.Storage.ReconcileAsync();
        RequireSync(h.Server.Messages(held.ThreadId).All(message => message.Id != held.ChunkReferences!.Single().MessageId), "o término do download não permitiu concluir a limpeza");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> SharedStorageCreationAckAndMetadataFencesAsync()
    {
        using var h = new SharedHarness();
        await h.Storage.ReserveManyAsync(Enumerable.Range(0, DiscordLimits.MaxFilesPerSharedThread)
            .Select(_ => (Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"))).ToArray(), default);
        using var lostReply = new LostStorageCreationReplyHandler(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(lostReply, false));
        var storage = new SharedStorageManager(api, h.Store, h.Settings);
        var state = await h.PrepareAsync("lost.bin", "lost"u8.ToArray());
        try { await storage.ReserveAsync(state.FileId, state.TransferId, default); return TestOutcome.Fail("a resposta perdida não foi simulada"); }
        catch (DiscordApiException error) when (error.StatusCode == HttpStatusCode.ServiceUnavailable) { }
        var restarted = new SharedStorageManager(api, h.Store, h.Settings);
        var recovered = await restarted.ReserveAsync(state.FileId, state.TransferId, default);
        RequireSync(h.Server.FileThreads().Count == 2 && h.Server.FileThreads().Contains(recovered.ThreadId) &&
            (await h.CatalogAsync()).Single(row => row.ThreadId == recovered.ThreadId).Members.Single().FileId == state.FileId,
            "recuperar a criação com um catálogo cheio em cache duplicou a thread ou perdeu a vaga");
        await h.Storage.VerifyReservationAsync(recovered, default);
        var entry = (await h.UploadAsync([state])).Single();
        await using (var firstWriter = await h.Storage.AcquireMetadataMutationAsync(entry, default))
        {
            var peerStore = new SettingsStore(h.Directory.CreateSubdirectory("peer-data")); peerStore.Save(h.Settings);
            var peer = new AppRegistry(h.Api, peerStore, h.Settings); await peer.RefreshMappingsAsync();
            try { await peer.SharedStorage.EnsureMetadataAvailableAsync(entry.FileId, default); return TestOutcome.Fail("a preparação ignorou uma edição remota ativa"); }
            catch (IOException) { }
            entry.Name = "forbidden-rename.bin";
            try { await peer.UpdateEntryAsync(entry); return TestOutcome.Fail("dois escritores alteraram o mesmo metadata simultaneamente"); }
            catch (IOException) { }
            RequireSync((await h.Registry.RefreshFolderAsync("root", "/")).Single(file => file.Type == "file").Name == "lost.bin",
                "um conflito sobrescreveu o metadata remoto");
        }
        var emptyCheckpoint = new TransferState();
        RequireSync(emptyCheckpoint.PayloadLayoutVersion != TransferPayloadLayouts.SharedStorage && !emptyCheckpoint.SharedAllocationPending,
            "checkpoint antigo foi inferido como compartilhado");
        return TestOutcome.Pass();
    }

    private static async Task<TestOutcome> SharedStorageIndividualCancellationAsync()
    {
        using var h = new SharedHarness();
        using var blocked = new SharedAcknowledgementHandler(h.Server);
        using var api = new DiscordApiClient(h.Settings.BotToken, new HttpClient(blocked, false));
        var storage = new SharedStorageManager(api, h.Store, h.Settings);
        var states = await Task.WhenAll(new[] { h.PrepareAsync("cancel-a.bin", "a"u8.ToArray()), h.PrepareAsync("keep-b.bin", "b"u8.ToArray()) });
        var allocations = await storage.ReserveManyAsync(states.Select(state => (state.FileId, state.TransferId)).ToArray(), default);
        using var cancel = new CancellationTokenSource();
        using var memory = new UploadMemoryCoordinator(2);
        async Task<ChunkReference> Send(TransferState state, CancellationToken token)
        {
            var allocation = allocations[state.TransferId]; state.Storage = allocation.Layout; state.ThreadId = allocation.ThreadId;
            state.PayloadLayoutVersion = TransferPayloadLayouts.SharedStorage; await h.Transfers.SaveAsync(state);
            var source = new FileInfo(state.SourcePath);
            var context = new EncryptedPayloadContext(state.FileId, state.Encryption!, EncryptedPayloadRoles.Original, 0, 1);
            using var part = await memory.PreparePartAsync(h.Encryption, source, source.Length, EncryptionService.MaximumPlaintextPayloadBytes,
                context, token, deferBuffers: true, isolateSendCancellation: true);
            return await storage.Batcher.SendAsync(allocation, context, part, reference => h.Transfers.MarkUploadChunksAsync(state,
                new ForumUploadedBatch(allocation.ThreadId, state.TransferId[..10], [new ForumUploadedChunk(allocation.ThreadId,
                    state.TransferId[..10], 0, reference.MessageId, part.Length, part.LogicalSizeBytes, reference.AttachmentId)])), token);
        }
        var a = Send(states[0], cancel.Token); var b = Send(states[1], default);
        await blocked.Stored.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); blocked.Confirm.TrySetResult();
        try { await a; return TestOutcome.Fail("cancelamento individual não chegou ao participante"); }
        catch (OperationCanceledException) { }
        var kept = await b;
        RequireSync(states.All(state => state.UploadedChunks.Count == 1) && states[0].UploadedChunks[0].MessageId == kept.MessageId,
            "cancelar um participante perdeu a confirmação de outro ou do attachment já transmitido");
        await storage.CancelReservationAsync(states[0], default);
        RequireSync(h.Server.Messages(states[1].ThreadId).Single(message => message.Id == kept.MessageId).Attachments.Single().Id == kept.AttachmentId,
            "limpar o cancelado removeu o attachment do arquivo não cancelado");
        return TestOutcome.Pass();
    }

    private sealed class SharedAcknowledgementHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        internal TaskCompletionSource Stored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Confirm { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await _forward.SendAsync(request, token);
            if (request.Method == HttpMethod.Post && request.Content is MultipartFormDataContent &&
                !request.RequestUri!.AbsolutePath.Contains("/names/", StringComparison.Ordinal))
            { Stored.TrySetResult(); await Confirm.Task.WaitAsync(token); }
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }

    private sealed class LostStorageCreationReplyHandler(HttpMessageHandler inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _forward = new(inner, false);
        private int _lost;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await _forward.SendAsync(request, token);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/channels/files/threads", StringComparison.Ordinal) &&
                Interlocked.Exchange(ref _lost, 1) == 0)
            { response.Dispose(); throw new HttpRequestException("simulated creation acknowledgement loss"); }
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) _forward.Dispose(); base.Dispose(disposing); }
    }
}
