using System.Buffers.Binary;

namespace Aion2DpsMeter.Core.Pvp;

/// <summary>
/// A player the server is showing you right now: name, class code, server, legion, combat power and the weapon's +N
/// (0 when unknown), and when its spawn arrived (capture time, ms).
/// </summary>
public sealed record NearbyPlayer(int Id, string Name, uint ClassCode, ushort Server, string? Legion, int CombatPower, int Weapon, long SeenAt);

/// <summary>
/// Fork (Faro): the players around you, for Faro's «Enemigos cerca». Reads only what the server sends the client: the
/// player spawn (<c>45 36</c>) when someone comes into view, <c>47 36 &lt;entity&gt;</c> when they leave it, and the
/// local zone change, which clears the list. Faro decides who is an enemy (the other faction's servers). Thread-safe.
/// </summary>
public sealed class NearbyTracker
{
    /// <summary>More than this many at once is a capital city: the oldest ones go.</summary>
    public const int MaxPlayers = 400;
    /// <summary>The list goes to Faro again after this long even if nothing changed (UDP may drop one).</summary>
    public const int ResendMs = 5_000;

    private readonly object _lock = new();
    private readonly Dictionary<int, NearbyPlayer> _byId = new();
    private readonly Dictionary<string, int> _idByName = new(StringComparer.Ordinal);
    private int _version;
    private int _sentVersion = -1;
    private long _sentAt;

    /// <summary>
    /// A player spawned. Values this record lacks (0 or null) keep what an earlier spawn of the same player said: the
    /// same player also turns up inside other records with less in them. A new entity id for the same name replaces
    /// the old one.
    /// </summary>
    public void NoteSpawn(NearbyPlayer p)
    {
        lock (_lock)
        {
            if (_idByName.TryGetValue(p.Name, out int oldId) && _byId.TryGetValue(oldId, out var old))
            {
                p = p with
                {
                    ClassCode = p.ClassCode != 0 ? p.ClassCode : old.ClassCode,
                    Server = p.Server != 0 ? p.Server : old.Server,
                    Legion = p.Legion ?? old.Legion,
                    CombatPower = p.CombatPower != 0 ? p.CombatPower : old.CombatPower,
                    Weapon = p.Weapon != 0 ? p.Weapon : old.Weapon,
                };
                if (oldId != p.Id)
                    _byId.Remove(oldId);
                if (old == p with { SeenAt = old.SeenAt } && oldId == p.Id)
                    return;
            }
            else if (_byId.TryGetValue(p.Id, out var other) && other.Name != p.Name)
            {
                _idByName.Remove(other.Name);
            }
            if (_byId.Count >= MaxPlayers && !_byId.ContainsKey(p.Id))
            {
                var oldest = _byId.Values.MinBy(x => x.SeenAt)!;
                _byId.Remove(oldest.Id);
                _idByName.Remove(oldest.Name);
            }
            _byId[p.Id] = p;
            _idByName[p.Name] = p.Id;
            _version++;
        }
    }

    /// <summary><c>47 36 &lt;entity&gt;</c>: that entity left your view.</summary>
    public void NoteGone(int id)
    {
        lock (_lock)
        {
            if (!_byId.Remove(id, out var p))
                return;
            if (_idByName.TryGetValue(p.Name, out int cur) && cur == id)
                _idByName.Remove(p.Name);
            _version++;
        }
    }

    /// <summary>You changed zone (teleport, dungeon, Abyss): nobody from before is around any more.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_byId.Count == 0)
                return;
            _byId.Clear();
            _idByName.Clear();
            _version++;
        }
    }

    public IReadOnlyList<NearbyPlayer> Snapshot()
    {
        lock (_lock)
            return _byId.Values.OrderBy(p => p.SeenAt).ToList();
    }

    /// <summary>
    /// The list to send to Faro when it changed since the last call that returned true, or when
    /// <see cref="ResendMs"/> went by; null otherwise. <paramref name="nowMs"/> is any steady clock in ms.
    /// </summary>
    public IReadOnlyList<NearbyPlayer>? TakeForSend(long nowMs)
    {
        lock (_lock)
        {
            if (_version == _sentVersion && nowMs - _sentAt < ResendMs)
                return null;
            _sentVersion = _version;
            _sentAt = nowMs;
            return _byId.Values.OrderBy(p => p.SeenAt).ToList();
        }
    }

    // ───── reading the spawn ─────

    /// <summary>Bytes that follow the server in a spawn without a legion (seen in every capture of 28-30/10/2026).</summary>
    private static ReadOnlySpan<byte> Anchor => [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x80, 0x75, 0xD5, 0x2A, 0xBB, 0x03];

    private static bool IsServer(int v) => v >= 1000 && v <= 2999;

    /// <summary>
    /// The player's server and legion from a spawn, after the name (<paramref name="nameEnd"/>). With a legion:
    /// <c>&lt;u16 server&gt; &lt;6 bytes&gt; &lt;u16 server&gt; &lt;len&gt;&lt;legion UTF-8&gt;</c>. Without one:
    /// <c>&lt;u16 server&gt; &lt;4 bytes&gt; &lt;3-byte varint&gt; FF×8 80 75 D5 2A BB 03</c>. (0, null) when not found.
    /// </summary>
    public static (ushort Server, string? Legion) ReadOrigin(ReadOnlySpan<byte> data, int nameEnd, int end)
    {
        end = Math.Min(end, data.Length);
        for (int i = Math.Max(0, nameEnd + 40); i + 11 <= end && i < nameEnd + 260; i++)
        {
            int s = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i, 2));
            if (!IsServer(s) || BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i + 8, 2)) != s)
                continue;
            int len = data[i + 10];
            if (len < 1 || len > 36 || i + 11 + len > end)
                continue;
            if (Utf8(data.Slice(i + 11, len)) is { } legion)
                return ((ushort)s, legion);
        }
        if (nameEnd >= 0 && nameEnd < end)
        {
            int f = data[nameEnd..end].IndexOf(Anchor);
            if (f >= 0)
            {
                f += nameEnd;
                // The varint before the anchor is 3 bytes long, 4 if the number behind it grows.
                foreach (int back in new[] { 9, 10 })
                {
                    int at = f - back;
                    if (at > nameEnd && IsServer(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2))))
                        return (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2)), null);
                }
            }
        }
        return (0, null);
    }

    /// <summary>
    /// The combat power near the end of a spawn (<paramref name="end"/> = where the record ends):
    /// <c>&lt;u32 500-9999&gt; &lt;u32 1-99&gt; [one byte 1-15] &lt;u32 combat power&gt;</c>. The same number the
    /// official character data shows (checked against a real player on 30/10/2026). 0 when not found.
    /// </summary>
    public static int ReadCombatPower(ReadOnlySpan<byte> data, int end)
    {
        end = Math.Min(end, data.Length);
        static bool Ok(uint v) => v >= 1_000 && v <= 5_000_000;
        for (int i = Math.Max(0, end - 48); i + 12 <= end; i++)
        {
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4, 4));
            if (a < 500 || a > 9_999 || b < 1 || b > 99)
                continue;
            int j = i + 8;
            if (j + 4 <= end && BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(j, 4)) is var cp && Ok(cp))
                return (int)cp;
            if (data[j] >= 1 && data[j] <= 15 && j + 5 <= end
                && BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(j + 1, 4)) is var cp2 && Ok(cp2))
                return (int)cp2;
        }
        return 0;
    }

    private static string? Utf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            string s = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            return s.Length == 0 || s.Any(char.IsControl) ? null : s;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }
}
