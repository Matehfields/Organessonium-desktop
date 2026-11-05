using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class DownloadAlreadyActiveException() : IOException("Já existe um download ativo para esse destino.");

/// <summary>One handle, disjoint checked regions, no chunk files or assembly copy.</summary>
internal sealed class PartialDownloadFile : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> Active = new(StringComparer.OrdinalIgnoreCase);
    private SafeFileHandle? _handle;
    private readonly string _destination;
    internal string Path { get; }
    internal DownloadFileLayout Layout { get; }
    internal static bool IsActive(string destination) => Active.ContainsKey(System.IO.Path.GetFullPath(destination));

    internal static string Destination(DirectoryInfo directory, string name) =>
        System.IO.Path.Combine(directory.FullName, PathRules.CleanFileName(name));

    internal static string PartialPath(string destination, string transferId)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(transferId)))[..32];
        var name = System.IO.Path.GetFileName(destination);
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!, name[..Math.Min(120, name.Length)] + "." + id + ".part");
    }

    internal static bool OwnsPartial(TransferState state) =>
        !string.IsNullOrWhiteSpace(state.FinalFilePath) && !string.IsNullOrWhiteSpace(state.PartialFilePath) &&
        string.Equals(System.IO.Path.GetFullPath(state.FinalFilePath), Destination(new DirectoryInfo(state.DestinationPath), state.FileName), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(System.IO.Path.GetFullPath(state.PartialFilePath), PartialPath(state.FinalFilePath, state.TransferId), StringComparison.OrdinalIgnoreCase);

    internal PartialDownloadFile(string path, string destination, DownloadFileLayout layout, bool resume)
    {
        Path = path; _destination = System.IO.Path.GetFullPath(destination); Layout = layout;
        if (!Active.TryAdd(_destination, 0)) throw new DownloadAlreadyActiveException();
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("O arquivo parcial não pode ser um link.");
            _handle = File.OpenHandle(path, resume ? FileMode.OpenOrCreate : FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
            TryEnableSparseFile();
        }
        catch { Active.TryRemove(_destination, out _); throw; }
    }

    internal Stream OpenRegion(int index) => new RegionStream(this, Layout.Regions[index]);
    internal long Length => RandomAccess.GetLength(_handle!);
    internal void Flush() => RandomAccess.FlushToDisk(_handle!);
    internal void CloseWriter() { _handle?.Dispose(); _handle = null; }

    private void TryEnableSparseFile()
    {
        // Out-of-order writes must not physically zero the regions still awaiting the network.
        // The low event bit keeps this control request out of the .NET I/O completion port.
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        var overlapped = new NativeOverlapped
        {
            EventHandle = new IntPtr(completed.SafeWaitHandle.DangerousGetHandle().ToInt64() | 1)
        };
        if (!DeviceIoControl(_handle!, 0x000900C4 /* FSCTL_SET_SPARSE */, IntPtr.Zero, 0,
                IntPtr.Zero, 0, out _, ref overlapped) && Marshal.GetLastWin32Error() == 997 /* ERROR_IO_PENDING */)
            _ = GetOverlappedResult(_handle!, ref overlapped, out _, true);
        // Filesystems without sparse support retain the previous allocation behavior.
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, ref NativeOverlapped overlapped,
        out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    internal async Task<bool> MatchesAsync(DownloadRegion region, string? digest, CancellationToken token)
    {
        if (!IsDigest(digest) || Length < checked(region.Offset + region.Length)) return false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[(int)Math.Min(1024 * 1024, Math.Max(1, region.Length))];
        try
        {
            for (long read = 0; read < region.Length;)
            {
                var count = await RandomAccess.ReadAsync(_handle!, buffer.AsMemory(0, (int)Math.Min(buffer.Length, region.Length - read)), region.Offset + read, token);
                if (count == 0) return false;
                hash.AppendData(buffer, 0, count); read += count;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset()) == digest;
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal async Task<(string Digest, List<int> Invalid)> VerifyAsync(IReadOnlyList<TransferChunkState> chunks,
        PayloadChecksums? checksums, Action<long>? progress, CancellationToken token)
    {
        using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var chunk = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var invalid = new List<int>();
        var buffer = new byte[1024 * 1024];
        long verified = 0;
        try
        {
            foreach (var region in Layout.Regions)
            {
                long read = 0;
                while (read < region.Length)
                {
                    var count = await RandomAccess.ReadAsync(_handle!, buffer.AsMemory(0, (int)Math.Min(buffer.Length, region.Length - read)), region.Offset + read, token);
                    if (count == 0) break;
                    whole.AppendData(buffer, 0, count); chunk.AppendData(buffer, 0, count);
                    read += count; verified += count; progress?.Invoke(verified);
                }
                var digest = Convert.ToHexStringLower(chunk.GetHashAndReset());
                if (read != region.Length || digest != (checksums?.Chunks[region.Index] ?? chunks[region.Index].Sha256)) invalid.Add(region.Index);
            }
            return (Convert.ToHexStringLower(whole.GetHashAndReset()), invalid);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal static bool IsDigest(string? digest) => digest is { Length: 64 } && digest.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static long AllocatedBytes(string path)
    {
        if (!File.Exists(path)) return 0;
        var low = GetCompressedFileSizeW(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastWin32Error() is var error && error != 0) throw new Win32Exception(error);
        return checked((long)(((ulong)high << 32) | low));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string fileName, out uint high);

    public void Dispose() { CloseWriter(); Active.TryRemove(_destination, out _); }

    private sealed class RegionStream(PartialDownloadFile file, DownloadRegion region) : Stream
    {
        private long _position;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => region.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length > region.Length - _position) throw new InvalidDataException("O chunk excede a região de destino.");
            await RandomAccess.WriteAsync(file._handle!, buffer, checked(region.Offset + _position), cancellationToken);
            _position += buffer.Length;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token) => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
