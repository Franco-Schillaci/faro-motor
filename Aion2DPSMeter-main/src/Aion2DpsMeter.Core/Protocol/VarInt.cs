namespace Aion2DpsMeter.Core.Protocol;

/// <summary>Result of decoding a LEB128-style varint: the value and how many bytes it took.</summary>
public readonly record struct VarIntResult(int Value, int Length)
{
    public static readonly VarIntResult Invalid = new(-1, -1);
    public bool IsValid => Length > 0;
}

/// <summary>
/// Little-endian base-128 varints, the integer encoding used throughout the game protocol.
/// </summary>
public static class VarInt
{
    public static VarIntResult Read(ReadOnlySpan<byte> bytes, int offset)
    {
        int value = 0;
        int shift = 0;
        int count = 0;
        while (true)
        {
            if (offset < 0 || offset + count >= bytes.Length)
                return VarIntResult.Invalid;

            uint b = bytes[offset + count];
            count++;
            value |= (int)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return new VarIntResult(value, count);

            shift += 7;
            if (shift >= 32)
                return VarIntResult.Invalid;
        }
    }

    /// <summary>Reads a varint at <paramref name="offset"/> and advances it. Fails on negative values.</summary>
    public static bool TryRead(ReadOnlySpan<byte> bytes, ref int offset, out int value)
    {
        var r = Read(bytes, offset);
        value = 0;
        if (r.Length <= 0)
            return false;
        offset += r.Length;
        if (r.Value < 0)
            return false;
        value = r.Value;
        return true;
    }

    public static bool CanRead(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset >= bytes.Length)
            return false;
        for (int i = offset, n = 0; i < bytes.Length && n < 5; i++, n++)
        {
            if ((bytes[i] & 0x80) == 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The varint that ends just before <paramref name="end"/>, starting no earlier than
    /// <paramref name="minStart"/> and at most three bytes back, whose value is in range.
    /// </summary>
    /// <remarks>
    /// The last byte of a multi-byte varint is also a valid one-byte varint on its own
    /// (13978 is <c>9A 6D</c>, and <c>6D</c> alone is 109), so a candidate that is not
    /// preceded by a continuation byte wins; the shortest valid one is only the fallback.
    /// </remarks>
    public static int? EndingAt(ReadOnlySpan<byte> data, int end, int minStart, int min, int max)
    {
        int? fallback = null;
        for (int len = 1; len <= 3; len++)
        {
            int start = end - len;
            if (start < 0)
                break;
            if (start < minStart || !CanRead(data, start))
                continue;
            var v = Read(data, start);
            if (v.Length != len || v.Value < min || v.Value > max)
                continue;
            bool continued = start > 0 && (data[start - 1] & 0x80) != 0;
            if (!continued)
                return v.Value;
            fallback ??= v.Value;
        }
        return fallback;
    }

    public static byte[] Encode(uint value)
    {
        var output = new List<byte>(5);
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value == 0)
            {
                output.Add(b);
                return output.ToArray();
            }
            output.Add((byte)(b | 0x80));
        }
    }
}
