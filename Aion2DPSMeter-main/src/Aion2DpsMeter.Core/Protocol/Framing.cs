using K4os.Compression.LZ4;

namespace Aion2DpsMeter.Core.Protocol;

public enum FrameKind
{
    /// <summary>A plain packet.</summary>
    Packet,
    /// <summary>An <c>FF FF</c> LZ4 bundle holding further packets.</summary>
    Bundle,
}

/// <summary>A frame inside a buffer. <see cref="PayloadStart"/> is relative to <see cref="Start"/>.</summary>
public readonly record struct Frame(FrameKind Kind, int Start, int End, int PayloadStart)
{
    public int Length => End - Start;
    public ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> buffer) => buffer[Start..End];
    public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> buffer) => buffer[(Start + PayloadStart)..End];
}

public sealed class FramingResult
{
    public List<Frame> Frames { get; } = new();
    /// <summary>Bytes consumed. Anything past this is an incomplete trailing packet.</summary>
    public int Consumed { get; set; }
}

/// <summary>
/// Where one game packet ends and the next begins. Wire shape:
/// <code>
/// 00 ...                               padding, skipped
/// 17 03 03 &lt;u16 len&gt; ...           a TLS record from another connection, skipped
/// &lt;varint len&gt; &lt;payload&gt;          a packet; len counts the payload plus 4
/// &lt;varint len&gt; FF FF &lt;u32 size&gt; &lt;lz4&gt;   a compressed bundle of further packets
/// </code>
/// Ported from A2Tools (GPL-3.0), <c>capture/framing.rs</c>.
/// </summary>
public static class Framing
{
    /// <summary>The largest packet the parser will believe; past this it resynchronises.</summary>
    public const int MaxPacketBytes = 65535;
    /// <summary>A length above this that runs past the buffer is corruption, not a TCP fragment.</summary>
    public const int MaxFragmentWaitBytes = 16384;
    /// <summary>Refuse to allocate for a bundle claiming to decompress to more than this.</summary>
    public const int MaxDecompressedBytes = 1_000_000;

    /// <summary>
    /// Walks a TCP stream buffer into frames, stopping at the first incomplete one.
    /// A capture that starts mid-stream is normal, so invalid lengths resync a byte at a time.
    /// </summary>
    public static FramingResult Walk(ReadOnlySpan<byte> buffer)
    {
        var result = new FramingResult();
        int offset = 0;

        while (offset < buffer.Length)
        {
            if (buffer[offset] == 0x00)
            {
                offset++;
                continue;
            }

            int? tls = TlsRecordSize(buffer[offset..]);
            if (tls is int tlsSize)
            {
                if (offset + tlsSize > buffer.Length)
                    break; // the rest of the record is still to come
                offset += tlsSize;
                continue;
            }

            var len = VarInt.Read(buffer, offset);
            if (len.Length <= 0 || len.Value <= 0)
            {
                if (offset + 5 > buffer.Length)
                    break;
                offset++;
                continue;
            }

            int? size = FrameSize(len.Value, len.Length);
            if (size is not int total || total > MaxPacketBytes)
            {
                offset++;
                continue;
            }

            if (offset + total > buffer.Length)
            {
                if (total > MaxFragmentWaitBytes)
                {
                    offset++;
                    continue;
                }
                break; // a legitimate fragment: wait for more bytes
            }

            int payloadStart = len.Length;
            bool isBundle = payloadStart + 1 < total
                && buffer[offset + payloadStart] == 0xFF
                && buffer[offset + payloadStart + 1] == 0xFF;

            result.Frames.Add(new Frame(isBundle ? FrameKind.Bundle : FrameKind.Packet, offset, offset + total, payloadStart));
            offset += total;
        }

        result.Consumed = offset;
        return result;
    }

    /// <summary>
    /// Walks the decompressed contents of a bundle. The game framed this buffer itself,
    /// so an unparseable length means the assumption is wrong and the walk stops.
    /// </summary>
    public static FramingResult WalkInner(ReadOnlySpan<byte> buffer)
    {
        var result = new FramingResult();
        int offset = 0;

        while (offset < buffer.Length)
        {
            if (buffer[offset] == 0x00)
            {
                offset++;
                continue;
            }

            var len = VarInt.Read(buffer, offset);
            if (len.Length <= 0 || len.Value <= 0)
                break;

            int? size = FrameSize(len.Value, len.Length);
            if (size is not int total)
            {
                offset++;
                continue;
            }

            int end = offset + total;
            if (end > buffer.Length)
                break;

            int payloadStart = len.Length;
            bool nested = total > payloadStart + 1
                && buffer[offset + payloadStart] == 0xFF
                && buffer[offset + payloadStart + 1] == 0xFF;

            result.Frames.Add(new Frame(nested ? FrameKind.Bundle : FrameKind.Packet, offset, end, payloadStart));
            offset += total;
        }

        result.Consumed = offset;
        return result;
    }

    /// <summary>Bytes a frame occupies, given its length varint's value and width.</summary>
    public static int? FrameSize(int value, int varintBytes)
    {
        long size = (long)value - 4 + varintBytes;
        return size >= varintBytes && size > 0 ? (int)size : null;
    }

    /// <summary>The length varint value for a frame whose body is <paramref name="bodyLength"/> bytes.</summary>
    public static uint LengthValue(int bodyLength) => (uint)bodyLength + 4;

    /// <summary>Decompresses a bundle payload starting at its <c>FF FF</c>: <c>FF FF, u32 LE size, lz4 block</c>.</summary>
    public static byte[]? DecompressBundle(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 7)
            return null;
        int size = (int)BitConverter.ToUInt32(payload.Slice(2, 4));
        if (size <= 0 || size > MaxDecompressedBytes)
            return null;
        var output = new byte[size];
        int decoded;
        try
        {
            decoded = LZ4Codec.Decode(payload[6..], output);
        }
        catch
        {
            return null;
        }
        return decoded == size ? output : null;
    }

    /// <summary>
    /// Size of a TLS record at the start of <paramref name="b"/>, header included:
    /// content type 20-23, version 3.1-3.4, then a big-endian length of at most 2^14 + 256.
    /// </summary>
    public static int? TlsRecordSize(ReadOnlySpan<byte> b)
    {
        if (b.Length < 5 || b[0] < 0x14 || b[0] > 0x17 || b[1] != 0x03 || b[2] < 0x01 || b[2] > 0x04)
            return null;
        int len = (b[3] << 8) | b[4];
        return len > 0 && len <= 16_384 + 256 ? 5 + len : null;
    }
}
