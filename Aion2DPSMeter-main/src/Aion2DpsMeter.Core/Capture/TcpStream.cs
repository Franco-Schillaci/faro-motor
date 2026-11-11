using Aion2DpsMeter.Core.Protocol;

namespace Aion2DpsMeter.Core.Capture;

/// <summary>
/// Reassembles one direction of a TCP connection and feeds it to a <see cref="PacketParser"/>.
/// Retransmitted bytes are dropped, out-of-order segments are held until the gap fills (or
/// given up on), and the parser's framing resynchronises after real loss.
/// </summary>
public sealed class TcpStream
{
    private const int MaxBufferBytes = 2 * 1024 * 1024;
    private const int MaxHeldSegments = 64;
    /// <summary>How long to wait for a missing segment before giving up on it (packet loss in the capture).</summary>
    private const int MaxHoldMs = 500;

    private readonly PacketParser _parser;
    private readonly SortedDictionary<uint, byte[]> _held = new(new SeqComparer());
    private long _heldSinceMs;
    private byte[] _buffer = new byte[64 * 1024];
    private int _length;
    private uint? _nextSeq;

    public TcpStream(PacketParser parser) => _parser = parser;

    public PacketParser Parser => _parser;
    public int BufferedBytes => _length;

    /// <summary>Adds a segment and parses what is complete. Returns true when any packet was consumed.</summary>
    public bool Add(uint seq, ReadOnlySpan<byte> data, long timestampMs)
    {
        _parser.CurrentTimestampMs = timestampMs;
        if (_nextSeq is not uint next)
        {
            Append(data);
            _nextSeq = seq + (uint)data.Length;
        }
        else
        {
            int diff = (int)(seq - next);
            if (diff > 0)
            {
                // A segment from the future: hold it until the gap fills.
                if (_held.Count == 0)
                    _heldSinceMs = timestampMs;
                _held[seq] = data.ToArray();
                if (_held.Count > MaxHeldSegments || timestampMs - _heldSinceMs > MaxHoldMs)
                    SkipGap();
            }
            else
            {
                int skip = -diff;
                if (skip < data.Length)
                {
                    Append(data[skip..]);
                    _nextSeq = next + (uint)(data.Length - skip);
                }
            }
            DrainHeld();
        }
        return Parse();
    }

    private void DrainHeld()
    {
        while (_held.Count > 0 && _nextSeq is uint next)
        {
            var first = _held.First();
            int diff = (int)(first.Key - next);
            if (diff > 0)
                return;
            _held.Remove(first.Key);
            int skip = -diff;
            if (skip < first.Value.Length)
            {
                Append(first.Value.AsSpan(skip));
                _nextSeq = next + (uint)(first.Value.Length - skip);
            }
        }
    }

    /// <summary>The missing bytes are not coming: continue from the earliest held segment.</summary>
    private void SkipGap()
    {
        var first = _held.First();
        _nextSeq = first.Key;
        DrainHeld();
    }

    private bool Parse()
    {
        bool any = false;
        while (_length > 0)
        {
            int consumed = _parser.ConsumeStream(_buffer.AsSpan(0, _length));
            if (consumed <= 0)
                break;
            Discard(consumed);
            any = true;
        }
        return any;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > MaxBufferBytes)
            _length = 0; // runaway buffer: start over and let the framing resync
        if (_length + data.Length > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    private void Discard(int count)
    {
        if (count >= _length)
        {
            _length = 0;
            return;
        }
        Buffer.BlockCopy(_buffer, count, _buffer, 0, _length - count);
        _length -= count;
    }

    /// <summary>Orders sequence numbers with 32-bit wraparound.</summary>
    private sealed class SeqComparer : IComparer<uint>
    {
        public int Compare(uint x, uint y) => ((int)(x - y)).CompareTo(0);
    }
}
