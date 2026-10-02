using System.IO;

namespace Organessonium.Services;

/// <summary>A response-sized view; never assembles the virtual video into one buffer.</summary>
public sealed class VirtualFileStream : Stream
{
    private IVirtualFile? _file;
    private readonly long _start;
    private readonly long _length;
    private readonly CancellationTokenSource _closed = new();
    private long _position;

    public VirtualFileStream(IVirtualFile file, long start, long length)
    {
        if (start < 0 || length < 0 || start > file.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        _file = file;
        _start = start;
        _length = length;
    }

    public override bool CanRead => _file != null;
    public override bool CanSeek => _file != null;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        // COM IStream pulls synchronously on WebView's reader thread; network awaits run off that thread.
        return Task.Run(async () => await ReadAsync(buffer.AsMemory(offset, count), _closed.Token).ConfigureAwait(false))
            .GetAwaiter().GetResult();
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var file = _file ?? throw new ObjectDisposedException(nameof(VirtualFileStream));
        var take = (int)Math.Min(Math.Min(buffer.Length, 64 * 1024), Length - _position);
        if (take <= 0) return 0;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token, cancellationToken);
        var read = await file.ReadAsync(_start + _position, buffer[..take], linked.Token).ConfigureAwait(false);
        _position += read;
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_position + offset), SeekOrigin.End => checked(Length + offset), _ => -1 };
        if (target < 0 || target > Length) throw new IOException("Posição inválida no stream de vídeo.");
        return _position = target;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && _file != null) { _closed.Cancel(); _file = null; }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
