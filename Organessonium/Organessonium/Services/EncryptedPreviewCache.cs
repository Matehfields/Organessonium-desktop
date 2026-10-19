using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Organessonium.Services;

/// <summary>Local-only, seekable cache. Keys are protected for the current Windows user.</summary>
internal static class EncryptedPreviewCache
{
    private static ReadOnlySpan<byte> Magic => "ORGPRV01"u8;
    private const int BlockSize = 64 * 1024;

    public static bool IsEncrypted(FileInfo file)
    {
        using var input = file.OpenRead();
        Span<byte> magic = stackalloc byte[8];
        return input.Read(magic) == magic.Length && magic.SequenceEqual(Magic);
    }

    public static Stream OpenRead(FileInfo file) => new Reader(file);
    public static Stream OpenRead(FileInfo file, long start, long count, Action<Stream>? onClosed = null) => new Reader(file, start, count, onClosed);
    public static long PlaintextLength(FileInfo file)
    {
        using var input = OpenRead(file);
        return input.Length;
    }

    public static void Store(FileInfo target, byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        StoreAsync(target, input).GetAwaiter().GetResult();
    }

    public static async Task StoreAsync(FileInfo target, Stream input, CancellationToken cancellation = default)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var wrapped = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        var header = new byte[28 + wrapped.Length];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), input.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), wrapped.Length);
        wrapped.CopyTo(header, 20);
        RandomNumberGenerator.Fill(header.AsSpan(20 + wrapped.Length, 8));
        var temporary = target.FullName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var plain = new byte[BlockSize];
        var cipher = new byte[BlockSize + 16];
        try
        {
            target.Directory!.Create();
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, BlockSize, true))
            using (var aes = new AesGcm(key, 16))
            {
                await output.WriteAsync(header, cancellation).ConfigureAwait(false);
                var remaining = input.Length;
                uint index = 0;
                do
                {
                    var count = (int)Math.Min(BlockSize, remaining);
                    await input.ReadExactlyAsync(plain.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    aes.Encrypt(Nonce(header, checked(index++)), plain.AsSpan(0, count), cipher.AsSpan(0, count), cipher.AsSpan(count, 16), header);
                    await output.WriteAsync(cipher.AsMemory(0, count + 16), cancellation).ConfigureAwait(false);
                    remaining -= count;
                } while (remaining > 0);
                await output.FlushAsync(cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, target.FullName, overwrite: true);
            target.Refresh();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(cipher);
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static byte[] Nonce(byte[] header, uint index)
    {
        var nonce = new byte[12];
        header.AsSpan(header.Length - 8).CopyTo(nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce.AsSpan(8), index);
        return nonce;
    }

    private sealed class Reader : Stream
    {
        private readonly FileStream _input;
        private readonly byte[] _header;
        private readonly AesGcm _aes;
        private readonly byte[] _plain = new byte[BlockSize];
        private readonly byte[] _cipher = new byte[BlockSize + 16];
        private long _block = -1;
        private long _position;
        private readonly long _length;
        private readonly long _viewStart;
        private readonly long _viewLength;
        private bool _disposed;
        private readonly Action<Stream>? _onClosed;

        public Reader(FileInfo file, long start = 0, long? count = null, Action<Stream>? onClosed = null)
        {
            _onClosed = onClosed;
            _input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, BlockSize);
            byte[]? key = null;
            try
            {
                var prefix = new byte[20];
                _input.ReadExactly(prefix);
                var wrappedLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(16));
                _length = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(8));
                if (!prefix.AsSpan(0, 8).SequenceEqual(Magic) || wrappedLength is < 32 or > 4096 || _length < 0)
                    throw new InvalidDataException("Cache de prévia inválido.");
                _header = new byte[28 + wrappedLength];
                prefix.CopyTo(_header, 0);
                _input.ReadExactly(_header.AsSpan(20));
                var blocks = Math.Max(1, (_length + BlockSize - 1) / BlockSize);
                if (blocks > uint.MaxValue || _input.Length != checked(_header.Length + _length + blocks * 16))
                    throw new InvalidDataException("Cache de prévia incompleto.");
                _viewStart = start;
                _viewLength = count ?? _length;
                if (start < 0 || _viewLength < 0 || start > _length - _viewLength)
                    throw new ArgumentOutOfRangeException(nameof(start));
                key = ProtectedData.Unprotect(_header.AsSpan(20, wrappedLength).ToArray(), null, DataProtectionScope.CurrentUser);
                _aes = new AesGcm(key, 16);
                if (_length == 0) LoadBlock(0);
            }
            catch { _input.Dispose(); throw; }
            finally { if (key != null) CryptographicOperations.ZeroMemory(key); }
        }

        private void LoadBlock(long block)
        {
            _block = -1;
            CryptographicOperations.ZeroMemory(_plain);
            var count = (int)Math.Min(BlockSize, _length - block * BlockSize);
            _input.Position = _header.Length + block * (BlockSize + 16L);
            _input.ReadExactly(_cipher.AsSpan(0, count + 16));
            _aes.Decrypt(Nonce(_header, checked((uint)block)), _cipher.AsSpan(0, count), _cipher.AsSpan(count, 16), _plain.AsSpan(0, count), _header);
            _block = block;
        }

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var total = 0;
            while (buffer.Length > 0 && _position < _viewLength)
            {
                var block = (_viewStart + _position) / BlockSize;
                if (_block != block) LoadBlock(block);
                var offset = (int)((_viewStart + _position) % BlockSize);
                var count = (int)Math.Min(Math.Min(buffer.Length, BlockSize - offset), _viewLength - _position);
                _plain.AsSpan(offset, count).CopyTo(buffer);
                buffer = buffer[count..];
                _position += count;
                total += count;
            }
            return total;
        }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_position + offset), SeekOrigin.End => checked(_viewLength + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
            return Position;
        }
        public override long Position { get => _position; set { if (value < 0) throw new ArgumentOutOfRangeException(nameof(value)); _position = value; } }
        public override long Length => _viewLength;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _input.Dispose();
                _aes.Dispose();
                CryptographicOperations.ZeroMemory(_plain);
                CryptographicOperations.ZeroMemory(_cipher);
                _onClosed?.Invoke(this);
            }
            base.Dispose(disposing);
        }
    }
}
