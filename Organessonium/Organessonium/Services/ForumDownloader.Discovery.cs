using System.IO;
using System.Threading.Channels;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed partial class ForumDownloader
{
    private sealed class PartDiscoveryException(Exception error) : IOException("Não foi possível localizar as partes do arquivo.", error);

    private sealed class PartDiscovery : IAsyncDisposable
    {
        private readonly Channel<IReadOnlyList<Part>> _batches = Channel.CreateBounded<IReadOnlyList<Part>>(
            new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        private readonly CancellationTokenSource _lifetime;
        private readonly Task _producer;
        internal IReadOnlyList<Part> References { get; }

        internal PartDiscovery(ForumDownloader owner, string threadId, IReadOnlyList<string> messageIds,
            string batchId, IReadOnlyList<ChunkReference>? references, CancellationToken token)
        {
            References = references == null
                ? messageIds.Select((id, index) => new Part(index, "", "", 0, id, batchId)).ToArray()
                : ChunkReference.Validate(references, messageIds).Select(reference => new Part(reference.Index,
                    "", "", 0, reference.MessageId, batchId, AttachmentId: reference.AttachmentId)).ToArray();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _producer = ProduceAsync(owner, threadId);
        }

        private async Task ProduceAsync(ForumDownloader owner, string threadId)
        {
            var token = _lifetime.Token;
            try
            {
                for (var start = 0; start < References.Count;)
                {
                    var window = References.Skip(start).Take(start == 0 ? 10 : 100).ToArray();
                    var messages = await owner._discord.ResolvePayloadMessagesAsync(threadId,
                        window.Select(reference => reference.MessageId), token);
                    foreach (var message in messages.Values) owner.DecodeReference(message);
                    var parts = new List<Part>(window.Length);
                    foreach (var reference in window)
                    {
                        if (!messages.TryGetValue(reference.MessageId, out var message))
                            throw new InvalidDataException("A mensagem referenciada pelo chunk não foi encontrada.");
                        var matches = string.IsNullOrEmpty(reference.AttachmentId)
                            ? message.Attachments.Take(1).ToArray()
                            : message.Attachments.Where(attachment => attachment.Id == reference.AttachmentId).ToArray();
                        if (matches.Length != 1 || matches[0].Size <= 0)
                            throw new InvalidDataException("O attachment referenciado pelo chunk não existe ou está inválido.");
                        var attachment = matches[0];
                        parts.Add(reference with { Url = attachment.Url, Name = attachment.FileName,
                            Size = attachment.Size, ProxyUrl = attachment.ProxyUrl });
                    }
                    await _batches.Writer.WriteAsync(parts, token);
                    start += window.Length;
                }
                _batches.Writer.TryComplete();
            }
            catch (OperationCanceledException error) { _batches.Writer.TryComplete(error); }
            catch (Exception error) { _batches.Writer.TryComplete(new PartDiscoveryException(error)); }
        }

        internal async Task<IReadOnlyList<Part>?> NextAsync(CancellationToken token)
        {
            while (await _batches.Reader.WaitToReadAsync(token))
                if (_batches.Reader.TryRead(out var parts)) return parts;
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            await _producer;
            _lifetime.Dispose();
        }
    }
}
