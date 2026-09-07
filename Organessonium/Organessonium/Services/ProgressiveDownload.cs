using System.IO;
using System.Net.Http;

namespace Organessonium.Services;

public sealed class ProgressiveDownload : IDisposable
{
    private const int MaxAttempts = 4;
private readonly IReadOnlyList<ProgressiveDownloadPart> _parts;
    private readonly FileInfo _file;
    private readonly HttpClient _http = NetworkConfig.Current.CreateClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Action<DownloadProgress>? _onProgress;
    private int _nextPartIndex;
    private long _availableBytes;
    private Exception? _failed;

    public ProgressiveDownload(
        IReadOnlyList<ProgressiveDownloadPart> parts,
        FileInfo file,
        long expectedLength,
        Action<DownloadProgress>? onProgress = null)
    {
        _parts = parts.OrderBy(part => part.Index).ToList();
        _file = file;
        _onProgress = onProgress;
        Length = expectedLength > 0 ? expectedLength : _parts.Sum(part => Math.Max(0, part.Size));
        _file.Directory?.Create();
        using var stream = new FileStream(_file.FullName, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    }

    public FileInfo File => _file;
    public long Length { get; }
    public bool IsComplete => Length <= 0 || Interlocked.Read(ref _availableBytes) >= Length;

    public async Task EnsureAvailableAsync(long endInclusive, CancellationToken cancellationToken = default)
    {
        if (Length <= 0)
        {
            return;
        }

        endInclusive = Math.Clamp(endInclusive, 0, Length - 1);
        if (Interlocked.Read(ref _availableBytes) > endInclusive)
        {
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        await _gate.WaitAsync(linkedCts.Token);
        try
        {
            if (_failed != null)
            {
                throw new IOException("Falha anterior ao baixar o vídeo progressivo.", _failed);
            }

            while (Interlocked.Read(ref _availableBytes) <= endInclusive && _nextPartIndex < _parts.Count)
            {
                await DownloadPartWithRetryAsync(_parts[_nextPartIndex], linkedCts.Token);
            }

            if (Interlocked.Read(ref _availableBytes) <= endInclusive)
            {
                throw new EndOfStreamException("O vídeo terminou antes do byte solicitado.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try { _disposeCts.Cancel(); } catch { }
        _disposeCts.Dispose();
        _gate.Dispose();
        _http.Dispose();
    }

    private async Task DownloadPartWithRetryAsync(ProgressiveDownloadPart part, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var before = Interlocked.Read(ref _availableBytes);
            var written = 0L;
            try
            {
                _file.Directory?.Create();
                await using var output = new FileStream(
                    _file.FullName,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    128 * 1024,
                    true);
                output.SetLength(before);
                output.Seek(before, SeekOrigin.Begin);

                using var response = await _http.GetAsync(part.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    var current = before + written;
                    Interlocked.Exchange(ref _availableBytes, current);
                    ReportProgress(part, current);
                }

                if (part.Size > 0 && written != part.Size)
                {
                    throw new IOException($"Chunk {part.Index + 1} incompleto. Esperado {part.Size} bytes, recebido {written} bytes.");
                }

                Interlocked.Exchange(ref _availableBytes, before + written);
                _nextPartIndex++;
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < MaxAttempts)
            {
                last = ex;
                Interlocked.Exchange(ref _availableBytes, before);
                TryTruncate(before);
                AppLogger.Error("ProgressiveVideo.ChunkRetry", ex, $"{part.Index}:{part.Name}:attempt {attempt}");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                break;
            }
        }

        _failed = last ?? new IOException("Falha desconhecida ao baixar chunk de vídeo.");
        throw new IOException($"Não foi possível baixar a parte {_nextPartIndex + 1}/{_parts.Count} do vídeo.", _failed);
    }

    private void ReportProgress(ProgressiveDownloadPart part, long downloaded)
    {
        if (_onProgress == null)
        {
            return;
        }

        var percent = Length <= 0 ? 0 : Math.Clamp((int)Math.Round(downloaded * 100.0 / Length), 0, 100);
        _onProgress(new DownloadProgress(percent, part.Name, downloaded, Length));
    }

    private void TryTruncate(long length)
    {
        try
        {
            using var stream = new FileStream(_file.FullName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(Math.Max(0, length));
        }
        catch (Exception ex)
        {
            AppLogger.Error("ProgressiveVideo.Truncate", ex, _file.FullName);
        }
    }
}

public sealed record ProgressiveDownloadPart(int Index, string Url, string Name, long Size);
