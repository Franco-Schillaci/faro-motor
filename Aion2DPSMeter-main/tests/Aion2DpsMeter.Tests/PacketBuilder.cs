using Aion2DpsMeter.Core.Protocol;

namespace Aion2DpsMeter.Tests;

/// <summary>Builds game packets in the wire format the parser reads.</summary>
internal static class PacketBuilder
{
    public static byte[] Hex(string hex)
    {
        hex = hex.Replace(" ", "");
        return Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
    }

    /// <summary><c>&lt;varint len&gt; &lt;payload&gt;</c>, where len counts the payload plus 4.</summary>
    public static byte[] Frame(byte[] payload) => [.. VarInt.Encode(Framing.LengthValue(payload.Length)), .. payload];

    public static byte[] Bundle(byte[] inner)
    {
        var compressed = new byte[K4os.Compression.LZ4.LZ4Codec.MaximumOutputSize(inner.Length)];
        int n = K4os.Compression.LZ4.LZ4Codec.Encode(inner, compressed);
        return Frame([0xFF, 0xFF, .. BitConverter.GetBytes((uint)inner.Length), .. compressed.AsSpan(0, n)]);
    }

    public static byte[] Body(byte op, int len) =>
        [op, 0x36, .. Enumerable.Range(0, len - 2).Select(i => (byte)((i % 251) | 1))];

    /// <summary>A single <c>04 38</c> direct damage record in the post-June-2026 layout.</summary>
    public static byte[] Damage(int actor, int target, int skill, int damage, bool crit = false, int powerScalar = 10000)
    {
        var body = new List<byte> { 0x04, 0x38 };
        body.AddRange(VarInt.Encode((uint)target));
        body.Add(0x04);                         // switch value, & 0x0F = 4
        body.Add(0x00);                         // unused flag
        body.AddRange(VarInt.Encode((uint)actor));
        body.AddRange(BitConverter.GetBytes((uint)skill));
        body.Add(0x00);                         // uid byte
        body.Add(crit ? (byte)3 : (byte)1);     // damage type
        body.AddRange(new byte[8]);             // fixed block for switch & 0x0F = 4
        body.Add(0x00);                         // zero pad
        body.AddRange(VarInt.Encode((uint)powerScalar));
        body.AddRange(VarInt.Encode((uint)damage));
        body.Add(0x00);                         // hit count
        return Frame(body.ToArray());
    }

    /// <summary>A <c>41 36</c> NPC spawn carrying its NPC code and HP.</summary>
    public static byte[] MobSpawn(int entity, int npcCode, int maxHp)
    {
        var body = new List<byte> { 0x41, 0x36 };
        body.AddRange(VarInt.Encode((uint)entity));
        body.AddRange([0x0C, 0x00, 0x00, 0x00]); // mask, kind 0x0C = NPC
        body.AddRange([(byte)npcCode, (byte)(npcCode >> 8), (byte)(npcCode >> 16)]);
        body.AddRange([0x00, 0x40, 0x02]);
        body.Add(0x01);
        body.AddRange(VarInt.Encode((uint)maxHp));
        body.AddRange(VarInt.Encode((uint)maxHp));
        return Frame(body.ToArray());
    }

    /// <summary><c>42 36 &lt;entity&gt; 00 03</c>: a combat death.</summary>
    public static byte[] Death(int entity) => Frame([0x42, 0x36, .. VarInt.Encode((uint)entity), 0x00, 0x03]);

    /// <summary>A <c>45 36</c> player spawn naming an entity.</summary>
    public static byte[] PlayerSpawn(int entity, string name)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        return Frame([0x45, 0x36, .. VarInt.Encode((uint)entity), 0x00, 0x00, 0x00, 0x00, 0x07, (byte)nameBytes.Length, .. nameBytes, 0x00]);
    }

    /// <summary>A <c>05 38</c> heal tick.</summary>
    public static byte[] Heal(int healer, int target, int skill, int amount, bool hot = false)
    {
        var body = new List<byte> { 0x05, 0x38 };
        body.AddRange(VarInt.Encode((uint)target));
        body.Add(hot ? (byte)0x0B : (byte)0x01);
        body.AddRange(VarInt.Encode((uint)healer));
        body.Add(0x00);
        body.AddRange(BitConverter.GetBytes((uint)(skill * 100)));
        body.AddRange(VarInt.Encode((uint)amount));
        return Frame(body.ToArray());
    }
}
