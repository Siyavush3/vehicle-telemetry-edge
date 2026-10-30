namespace Wim.Protocol;

/// <summary>
/// Incremental, allocation-light frame decoder for a byte stream.
/// Feed it whatever chunks the serial port returns; it yields complete frames,
/// keeps partial frames for the next call and resynchronises on garbage.
/// </summary>
/// <remarks>
/// Unlike the v1 algorithm, frame boundaries come from the length byte, not from
/// searching for the next 0xFF, so 0xFF inside the payload is handled correctly,
/// and no index is read before the buffer is long enough.
/// </remarks>
public sealed class FrameDecoder
{
    private byte[] _buffer;
    private int _start;   // index of the first unread byte
    private int _count;   // number of unread bytes

    public FrameDecoder(int initialCapacity = 512) => _buffer = new byte[initialCapacity];

    /// <summary>Bytes received but not yet part of a complete frame.</summary>
    public int PendingBytes => _count;

    /// <summary>Total bytes thrown away while resynchronising (useful as a line-quality metric).</summary>
    public long DiscardedBytes { get; private set; }

    /// <summary>Appends received bytes and returns every frame that became complete.</summary>
    public IReadOnlyList<Frame> Push(ReadOnlySpan<byte> data)
    {
        Append(data);
        var frames = new List<Frame>();
        while (TryReadFrame(out var frame))
            frames.Add(frame);
        Compact();
        return frames;
    }

    /// <summary>
    /// Drops the current start byte and searches for the next one. Call this when a partial
    /// frame has been pending for too long (e.g. a corrupted length byte made it look huge).
    /// </summary>
    public void SkipCurrentFrame()
    {
        if (_count == 0) return;
        Discard(1);
        AlignToStartByte();
    }

    public void Reset()
    {
        _start = 0;
        _count = 0;
    }

    private bool TryReadFrame(out Frame frame)
    {
        frame = null!;
        while (true)
        {
            AlignToStartByte();
            if (_count < Frame.HeaderLength)
                return false;

            int length = _buffer[_start + 3];
            if (length < Frame.HeaderLength)
            {
                // Impossible length: this 0xFF was not a real start byte.
                Discard(1);
                continue;
            }
            if (_count < length)
                return false; // wait for the rest of the frame

            byte address = _buffer[_start + 1];
            byte command = _buffer[_start + 2];
            var payload = _buffer.AsSpan(_start + Frame.HeaderLength, length - Frame.HeaderLength).ToArray();
            _start += length;
            _count -= length;
            frame = new Frame(address, command, payload);
            return true;
        }
    }

    private void AlignToStartByte()
    {
        int index = Array.IndexOf(_buffer, Frame.StartByte, _start, _count);
        int skip = index < 0 ? _count : index - _start;
        if (skip > 0) Discard(skip);
    }

    private void Discard(int n)
    {
        _start += n;
        _count -= n;
        DiscardedBytes += n;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        if (_start + _count + data.Length > _buffer.Length)
        {
            Compact();
            if (_count + data.Length > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _count + data.Length));
        }
        data.CopyTo(_buffer.AsSpan(_start + _count));
        _count += data.Length;
    }

    private void Compact()
    {
        if (_start == 0) return;
        if (_count > 0)
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _count);
        _start = 0;
    }
}
