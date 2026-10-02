using System.IO;

namespace Organessonium.Services;

/// <summary>Available response bytes, released as soon as WebView2 finishes its COM read.</summary>
internal sealed class AvailableMemoryStream(byte[] buffer, Action<Stream> released) : Stream
{
    private byte[]? _buffer = buffer;
    private readonly int _length = buffer.Length;
    private int _position;
    public long BufferedBytes => _buffer?.LongLength ?? 0;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] destination, int offset, int count) => Read(destination.AsSpan(offset, count));
    public override int Read(Span<byte> destination)
    {
        var count = Math.Min(destination.Length, _length - _position);
        if (count == 0) return 0;
        var source = _buffer ?? throw new ObjectDisposedException(nameof(AvailableMemoryStream));
        source.AsSpan(_position, count).CopyTo(destination);
        _position += count;
        if (_position == _length) Release();
        return count;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(destination.Span));
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _position + offset, SeekOrigin.End => _length + offset, _ => -1 };
        if (target < 0 || target > _length) throw new IOException("Posição inválida na resposta de mídia.");
        _position = checked((int)target);
        return target;
    }
    private void Release()
    {
        if (Interlocked.Exchange(ref _buffer, null) != null) released(this);
    }
    protected override void Dispose(bool disposing) { if (disposing) Release(); base.Dispose(disposing); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
