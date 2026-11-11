using System.Buffers.Binary;
using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;

namespace Aion2DpsMeter.Core.Protocol;

public sealed record PartyRoster(List<PartyMember> Members, bool Complete, int DungeonId);

/// <summary>
/// Decodes the <c>02 97</c> party roster the server broadcasts on any party change:
/// <code>
/// party_key u32, party_name str, party_size u8, dungeon_id u32, u8 u8, leader_dbid u64, u8 u8 u8,
/// member_count varint, then per member:
///   presence_mask u8, slot u8, dbid u64 (high u16 = world id), nickname str, class u32,
///   level u32, gear_score u32, … server u16, u16, u8, combat_power u64, …
/// </code>
/// Ported from A2Tools (GPL-3.0).
/// </summary>
public static class PartyRosterParser
{
    public static PartyRoster? Parse(ReadOnlySpan<byte> data, int at)
    {
        int o = at + 4; // party_key
        if (o >= data.Length)
            return null;
        int nameLen = data[o++];
        if (nameLen < 1 || nameLen > 40 || o + nameLen > data.Length)
            return null;
        if (Names.DecodeUtf8(data.Slice(o, nameLen)) is null)
            return null;
        o += nameLen;

        if (o >= data.Length)
            return null;
        int partySize = data[o++];
        if (partySize < 1 || partySize > 12)
            return null;
        if (o + 4 > data.Length)
            return null;
        int dungeonId = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(o, 4));
        o += 4 + 2 + 8 + 3;

        var count = VarInt.Read(data, o);
        if (count.Length <= 0 || count.Value < 1 || count.Value > 12)
            return null;
        o += count.Length;

        var members = new List<PartyMember>();
        bool complete = false;
        for (int index = 0; index < count.Value; index++)
        {
            if (o + 20 > data.Length)
                break;
            byte slot = data[o + 1];
            o += 2;
            ulong dbid = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(o, 8));
            o += 8;
            ushort serverId = (ushort)(dbid >> 48);
            int nickLen = data[o++];
            // An empty name is a vacant slot; the ones after it are vacant too.
            if (nickLen == 0)
            {
                complete = true;
                break;
            }
            if (nickLen > 40 || o + nickLen > data.Length)
                break;
            var nickname = Names.DecodeUtf8(data.Slice(o, nickLen));
            if (nickname is null)
                break;
            o += nickLen;
            if (o + 12 > data.Length)
                break;
            var job = JobClasses.FromRosterClass(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(o, 4)));
            o += 4;
            int level = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(o, 4));
            o += 4;
            if (level < 1 || level > 200)
                break;
            int gearScore = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(o, 4));
            o += 4;
            if (gearScore < 0 || gearScore > 1_000_000)
                break;

            // The tail is not fixed width: anchor on the world id repeated as a u16. Fork (Faro): it can be another
            // server than the one in the id (28/10/2026: id 2404, tail 2405, and the rest of the list was lost); then
            // the first server-like number there.
            if ((FindU16(data, o, o + 10, serverId) ?? FindServerLike(data, o, o + 10)) is not int anchor)
                break;
            o = anchor + 2 + 2 + 1;
            if (o + 8 > data.Length)
                break;
            ulong combatPower = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(o, 8));
            o += 8;
            if (combatPower > 100_000_000)
                break;

            members.Add(new PartyMember(nickname, slot, level, gearScore, (long)combatPower, serverId, job));

            if (index + 1 == count.Value)
            {
                complete = true;
                break;
            }
            if (FindNextMember(data, o, (byte)(slot + 1)) is not int next)
            {
                // Fork (Faro): mientras el grupo se arma, los lugares libres vienen como «00 <slot> + 8 ceros + nombre
                // vacío». Si el que sigue está libre, la lista está completa (y así se borran los del grupo anterior).
                if (NextIsVacant(data, o, (byte)(slot + 1)))
                    complete = true;
                break;
            }
            o = next;
        }

        return members.Count == 0 ? null : new PartyRoster(members, complete, dungeonId);
    }

    private static int? FindU16(ReadOnlySpan<byte> data, int from, int to, ushort wanted)
    {
        int end = Math.Min(to, data.Length - 2);
        for (int i = from; i <= end; i++)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i, 2)) == wanted)
                return i;
        }
        return null;
    }

    /// <summary>Fork (Faro): the first u16 in the range of server numbers (1000-2999).</summary>
    private static int? FindServerLike(ReadOnlySpan<byte> data, int from, int to)
    {
        int end = Math.Min(to, data.Length - 2);
        for (int i = from; i <= end; i++)
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i, 2));
            if (v >= 1000 && v <= 2999)
                return i;
        }
        return null;
    }

    /// <summary>Fork (Faro): a vacant slot (presence 0, the expected slot, a zero dbid and an empty name) follows.</summary>
    private static bool NextIsVacant(ReadOnlySpan<byte> data, int from, byte expectedSlot)
    {
        int end = Math.Min(from + 32, data.Length - 11);
        for (int i = from; i <= end; i++)
        {
            if (data[i] != 0 || data[i + 1] != expectedSlot || data[i + 10] != 0)
                continue;
            bool zeros = true;
            for (int k = 2; k < 10 && zeros; k++)
                zeros = data[i + k] == 0;
            if (zeros)
                return true;
        }
        return false;
    }

    private static int? FindNextMember(ReadOnlySpan<byte> data, int from, byte expectedSlot)
    {
        int end = Math.Min(from + 32, data.Length - 12);
        for (int i = from; i <= end; i++)
        {
            if (data[i + 1] != expectedSlot)
                continue;
            ushort server = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i + 8, 2));
            if (server == 0 || server > 9_999)
                continue;
            int nameLen = data[i + 10];
            if (nameLen == 0 || nameLen > 40 || i + 11 + nameLen > data.Length)
                continue;
            if (Names.DecodeUtf8(data.Slice(i + 11, nameLen)) is null)
                continue;
            return i;
        }
        return null;
    }
}
