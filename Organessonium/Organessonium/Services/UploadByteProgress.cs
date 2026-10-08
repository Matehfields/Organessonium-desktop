using System.Diagnostics;

namespace Organessonium.Services;

internal sealed class UploadByteProgress(long size, long chunkSize, bool encrypted, Action<long, long> publish)
{
    private readonly object _sync = new();
    private readonly Dictionary<int, (long Wire, long Logical)> _parts = [];
    private long _logicalBytes, _wireBytes, _publishedLogical, _publishedWire, _lastPublished;
    internal long LogicalBytes { get { lock (_sync) return _logicalBytes; } }

    internal void Report(int index, long wire)
    {
        lock (_sync)
        {
            var previous = _parts.GetValueOrDefault(index);
            var logicalSize = Math.Min(chunkSize, Math.Max(0, size - index * chunkSize));
            var wireSize = logicalSize + (encrypted ? EncryptionService.EnvelopeOverheadBytes : 0);
            var logical = wireSize == 0 ? 0 : Math.Min(logicalSize, Math.Max(0, wire) * logicalSize / wireSize);
            logical = Math.Max(previous.Logical, logical);
            _logicalBytes += logical - previous.Logical;
            _wireBytes += wire >= previous.Wire ? wire - previous.Wire : Math.Max(0, wire);
            _parts[index] = (Math.Max(0, wire), logical);
            Publish(false);
        }
    }

    internal void Confirm(int index)
    {
        lock (_sync)
        {
            var previous = _parts.GetValueOrDefault(index);
            var logical = Math.Min(chunkSize, Math.Max(0, size - index * chunkSize));
            _logicalBytes += logical - previous.Logical;
            _parts[index] = (previous.Wire, logical);
            Publish(true);
        }
    }

    private void Publish(bool force)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && _lastPublished != 0 && Stopwatch.GetElapsedTime(_lastPublished, now).TotalMilliseconds < 250) return;
        var logical = _logicalBytes - _publishedLogical;
        var wire = _wireBytes - _publishedWire;
        if (logical == 0 && wire == 0) return;
        _publishedLogical = _logicalBytes;
        _publishedWire = _wireBytes;
        _lastPublished = now;
        publish(logical, wire);
    }
}
