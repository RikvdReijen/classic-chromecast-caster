namespace RikRealization.ClassicCast.Protocol.Media;

/// <summary>
/// Turns an arbitrarily chunked H.264 Annex-B byte stream into whole access units.
///
/// A pipe gives no frame boundaries, so a picture is only known to be complete once the
/// next one starts. That costs a frame of latency, which <see cref="FlushPending"/> exists
/// to reclaim: when the encoder has gone quiet, whatever is buffered is a finished frame.
/// </summary>
public sealed class H264StreamSplitter
{
    private byte[] _buffer = new byte[1 << 16];
    private int _length;

    /// <summary>Bytes held back waiting for the next access unit to begin.</summary>
    public int PendingBytes => _length;

    /// <summary>Appends a chunk and returns whichever access units are now complete.</summary>
    public IReadOnlyList<H264AccessUnit> Push(ReadOnlySpan<byte> chunk)
    {
        Append(chunk);

        var boundaries = H264AnnexBStream.FindBoundaries(_buffer.AsSpan(0, _length));

        // The final group may still be arriving, so it is never emitted here.
        if (boundaries.Count < 2) return Array.Empty<H264AccessUnit>();

        var units = new List<H264AccessUnit>(boundaries.Count - 1);
        for (int i = 0; i < boundaries.Count - 1; i++)
        {
            if (!boundaries[i].HasVideoSlice) continue;

            int start = boundaries[i].Offset;
            int end = boundaries[i + 1].Offset;
            units.Add(new H264AccessUnit(
                _buffer.AsMemory(start, end - start).ToArray(), boundaries[i].IsKeyFrame));
        }

        Consume(boundaries[^1].Offset);
        return units;
    }

    /// <summary>
    /// Emits the buffered access unit if it holds a complete picture. Call this when the
    /// encoder has produced nothing for a short while: the frame is finished, and waiting
    /// for the next one before sending it would add a frame of pure latency.
    /// </summary>
    public H264AccessUnit? FlushPending()
    {
        if (_length == 0) return null;

        var boundaries = H264AnnexBStream.FindBoundaries(_buffer.AsSpan(0, _length));
        if (boundaries.Count != 1 || !boundaries[0].HasVideoSlice) return null;

        var unit = new H264AccessUnit(
            _buffer.AsMemory(boundaries[0].Offset, _length - boundaries[0].Offset).ToArray(),
            boundaries[0].IsKeyFrame);

        _length = 0;
        return unit;
    }

    private void Append(ReadOnlySpan<byte> chunk)
    {
        if (_length + chunk.Length > _buffer.Length)
        {
            int capacity = _buffer.Length;
            while (capacity < _length + chunk.Length) capacity *= 2;
            Array.Resize(ref _buffer, capacity);
        }

        chunk.CopyTo(_buffer.AsSpan(_length));
        _length += chunk.Length;
    }

    private void Consume(int count)
    {
        if (count <= 0) return;
        Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
        _length -= count;
    }
}
