using System.Buffers;
using System.IO;

namespace Organessonium.Services;

internal sealed class UploadWireProgressStream(Stream source, Action<long> progress) : Stream
{
    public override bool CanRead => source.CanRead;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => source.Position; set => source.Position = value; }
    public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => source.ReadAsync(buffer, token);
    public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
    public override void Flush() => source.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(bufferSize, 64 * 1024));
        long sent = 0;
        progress(0);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, 64 * 1024)), token).ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                sent += read;
                progress(sent);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) source.Dispose();
        base.Dispose(disposing);
    }
}
