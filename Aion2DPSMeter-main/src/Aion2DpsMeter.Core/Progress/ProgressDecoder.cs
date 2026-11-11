// Fork for Faro. The frame layouts read here (and the way ProgressTracker attributes them to a
// character) are ported from AcompanION (https://github.com/Swellshinider/AcompanION,
// src-tauri/src/events.rs, src-tauri/src/state.rs and docs/protocol.md), under this license:
//
//   MIT License
//
//   Copyright (c) 2026 Eduardo Leal
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy
//   of this software and associated documentation files (the "Software"), to deal
//   in the Software without restriction, including without limitation the rights
//   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//   copies of the Software, and to permit persons to whom the Software is
//   furnished to do so, subject to the following conditions:
//
//   The above copyright notice and this permission notice shall be included in all
//   copies or substantial portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//   SOFTWARE.

using System.Buffers.Binary;
using Aion2DpsMeter.Core.Protocol;

namespace Aion2DpsMeter.Core.Progress;

/// <summary>A ticket, energy or weekly counter as the server sends it: what is left, or milliseconds left for a time counter.</summary>
/// <param name="Extra">The record's second number (mask bit 3), when it has one. AcompanION calls it "charged" and does
/// not use it; Faro is checking whether it is the extra shown in light blue in the game, as in "2(+2)/14".</param>
public readonly record struct CounterRecord(uint Id, ulong Value, bool Ms, uint? Extra = null);

/// <summary>One quest board: <c>Slot</c> 1 is the day's window, 2 the week's; <c>Done</c> counts its finished entries.</summary>
public readonly record struct QuestBoard(byte Slot, uint Group, uint Done);

public abstract record ProgressEvent;
/// <summary>The character you play (<c>33 36</c>): sent after the world-entry data it describes.</summary>
public sealed record IdentityEvent(string Name, ushort Server, uint ClassCode, int Level, int? ItemLevel) : ProgressEvent;
public sealed record CombatPowerEvent(uint Value) : ProgressEvent;
public sealed record ItemLevelEvent(uint Value) : ProgressEvent;
public sealed record BoardsEvent(IReadOnlyList<QuestBoard> Boards) : ProgressEvent;
/// <summary>One board entry changed; with <c>Done</c> it is one more Supply Emergency delivery on the board of <c>Group</c>.</summary>
public sealed record BoardEntryEvent(uint Group, bool Done) : ProgressEvent;
public sealed record KinahTodayEvent(ulong Value) : ProgressEvent;
/// <summary>Fork (Faro): Abyss Points you hold (<c>AB FF</c>) and the most you can hold (2,500,000).</summary>
public sealed record AbyssPointsEvent(ulong Value, ulong Limit) : ProgressEvent;
/// <summary>Fork (Faro): the kinah you hold (the count of the kinah item in an inventory update <c>1B 56</c>).</summary>
public sealed record KinahHeldEvent(ulong Value) : ProgressEvent;
public sealed record DutyMissionsEvent(uint Done) : ProgressEvent;
/// <summary>Daeva Pass weekly counters: <c>(counter id, points used)</c>.</summary>
public sealed record PassUsedEvent(IReadOnlyList<(uint Id, uint Used)> Entries) : ProgressEvent;
public sealed record CountersEvent(IReadOnlyList<CounterRecord> Records) : ProgressEvent;
public sealed record CounterEvent(CounterRecord Record) : ProgressEvent;
/// <summary>
/// The account's pet collection (<c>00 90</c>, at world entry): the pet out, each pet's level (0 = not unlocked yet)
/// and its souls toward the next level. The same for every character of the account.
/// </summary>
public sealed record PetsEvent(uint Active, IReadOnlyList<(uint Id, uint Level)> Levels, IReadOnlyList<(uint Id, uint Souls)> Souls) : ProgressEvent;
/// <summary>Souls gained (<c>0D 90</c>): how many, per pet (a delta, not the new total).</summary>
public sealed record PetSoulsEvent(IReadOnlyList<(uint Id, uint Amount)> Gains) : ProgressEvent;
/// <summary>A pet reached a level (<c>14 90</c>); its souls start again from 0.</summary>
public sealed record PetLevelEvent(uint Id, uint Level) : ProgressEvent;

/// <summary>
/// Reads the server frames that carry a character's progress: identity, combat power, gear score,
/// tickets and energy, Supply Emergency, Duty Missions, kinah gained today, the Daeva Pass and the pet collection.
/// Only server-to-client frames, which the game does not encrypt; it reads ids and numbers, nothing else.
/// Every layout is checked strictly: a frame that does not fit exactly is ignored.
/// </summary>
public static class ProgressDecoder
{
    /// <summary>Largest counter snapshot accepted (real ones are ~500 bytes).</summary>
    private const int MaxSnapshot = 8 << 10;
    /// <summary>Counter ids stay below this; a larger "id" means the records are misaligned.</summary>
    private const uint MaxCounterId = 100_000_000;
    private const int MaxPass = 8;
    /// <summary>Pets in a collection (209 exist today) and the largest pet id, level and soul count accepted.</summary>
    private const int MaxPets = 2048;
    private const uint MaxPetId = 1_000_000;
    private const uint MaxPetLevel = 20;
    private const uint MaxPetSouls = 100_000;

    /// <summary>Decodes one frame body (opcode first). Null for other frames or one that does not parse.</summary>
    public static ProgressEvent? Parse(ReadOnlySpan<byte> body, Opcodes op)
    {
        if (body.Length < 3)
            return null;
        try
        {
            if (Opcodes.At(body, 0, op.SelfRecordB)) return Identity(body);
            if (Opcodes.At(body, 0, op.CombatPowerB)) return CombatPower(body);
            if (Opcodes.At(body, 0, op.ItemLevelB)) return ItemLevel(body);
            if (Opcodes.At(body, 0, op.QuestBoardsB)) return Boards(body);
            if (Opcodes.At(body, 0, op.QuestEntryB)) return BoardEntry(body);
            if (Opcodes.At(body, 0, op.KinahTodayB)) return Kinah(body);
            if (Opcodes.At(body, 0, op.DutyMissionsB)) return Duty(body);
            if (Opcodes.At(body, 0, op.PassUsedB)) return Pass(body);
            if (Opcodes.At(body, 0, op.CountersSnapshotB)) return Counters(body);
            if (Opcodes.At(body, 0, op.CounterUpdateB)) return Counter(body);
            if (Opcodes.At(body, 0, op.PetsSnapshotB)) return Pets(body);
            if (Opcodes.At(body, 0, op.PetSoulsB)) return PetSouls(body);
            if (Opcodes.At(body, 0, op.PetLevelB)) return PetLevel(body);
            if (Opcodes.At(body, 0, op.AbyssPointsB)) return Abyss(body);
            if (Opcodes.At(body, 0, op.InventoryItemB)) return InventoryItem(body);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A frame shorter than its own fields say; the cursor checks bounds, this is a last guard.
        }
        return null;
    }

    /// <summary>A counter snapshot or update (<c>0B 61</c> / <c>0C 61</c>), whether it parses or not.</summary>
    public static bool IsCounterFrame(ReadOnlySpan<byte> body, Opcodes op) =>
        body.Length >= 3 && (Opcodes.At(body, 0, op.CountersSnapshotB) || Opcodes.At(body, 0, op.CounterUpdateB));

    /// <summary>Fork (Faro): a currency frame (<c>A7 FF</c>…<c>AC FF</c>) whose bytes are logged, whether it parses or not.</summary>
    public static bool IsCurrencyFrame(ReadOnlySpan<byte> body, Opcodes op) =>
        body.Length >= 3 && Opcodes.At(body, 0, op.CurrencyProbeB);

    /// <summary>Fork (Faro): the kinah item's id; its count in an inventory update is the kinah you hold.</summary>
    public const uint KinahItemId = 930_100_031;

    /// <summary>
    /// Fork (Faro): <c>1B 56 &lt;u8&gt;&lt;u8&gt;&lt;u8&gt; &lt;u32 slot&gt; &lt;u32&gt; &lt;u32 item&gt; &lt;u64 count&gt; …</c> (43 bytes seen).
    /// Only the kinah item is read; any other item gives null.
    /// </summary>
    private static ProgressEvent? InventoryItem(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.Skip(11) || !c.U32(out uint item) || item != KinahItemId || !c.U64(out ulong v) || v > 1_000_000_000_000)
            return null;
        return new KinahHeldEvent(v);
    }

    /// <summary>Fork (Faro): <c>AB FF &lt;u64 Abyss Points held&gt;&lt;u64 limit&gt;</c>, nothing after.</summary>
    private static ProgressEvent? Abyss(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U64(out ulong v) || !c.U64(out ulong limit) || !c.AtEnd || v > 1_000_000_000_000)
            return null;
        return new AbyssPointsEvent(v, limit);
    }

    /// <summary>
    /// <c>33 36 &lt;varint entity&gt; &lt;u32&gt; &lt;u8 mask, bit 0 = name&gt; &lt;u8 len&gt;&lt;name&gt; &lt;u16 server&gt;
    /// &lt;u32 class&gt; &lt;u8&gt; &lt;u32 level&gt; &lt;u32 gear score&gt; &lt;u32 gear score&gt; …</c>
    /// </summary>
    private static ProgressEvent? Identity(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.Varint(out uint entity) || entity == 0 || !c.Skip(4) || !c.U8(out byte mask) || (mask & 1) == 0)
            return null;
        if (!c.U8(out byte n) || !Names.IsNameFieldLength(n) || !c.Bytes(n, out var field))
            return null;
        var raw = Names.DecodeUtf8(field);
        if (raw is null || Names.IsPlaceholder(raw) || Names.Exact(field) is not { } name)
            return null;
        if (!c.U16(out ushort server) || !c.U32(out uint cls) || !c.Skip(1) || !c.U32(out uint level))
            return null;
        if (server < 1000 || server >= 3000 || level < 1 || level > 99)
            return null;
        int? gear = c.U32(out uint g) && g > 0 && g < 100_000 ? (int)g : null;
        return new IdentityEvent(name, server, cls, (int)level, gear);
    }

    /// <summary><c>56 36 &lt;8 bytes&gt; &lt;u32 combat power&gt; …</c></summary>
    private static ProgressEvent? CombatPower(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.Skip(8) || !c.U32(out uint cp) || cp == 0 || cp > 50_000_000)
            return null;
        return new CombatPowerEvent(cp);
    }

    /// <summary><c>1D 56 &lt;u32 gear score&gt; &lt;u32 the same&gt;</c>, nothing after.</summary>
    private static ProgressEvent? ItemLevel(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U32(out uint v) || !c.U32(out _) || !c.AtEnd || v == 0 || v >= 100_000)
            return null;
        return new ItemLevelEvent(v);
    }

    /// <summary>
    /// <c>56 E2 &lt;u8 boards&gt;</c>, per board <c>&lt;u32 group&gt;&lt;u8 slot&gt;&lt;u64 start&gt;&lt;u64 end&gt;&lt;u8 n&gt;</c> and n entries
    /// <c>&lt;u32 group&gt;&lt;u32 id&gt;&lt;u32 value&gt;</c>, each with one "done" bit. The bits of the whole frame
    /// share one pool, read LSB first: a byte is pulled from the stream whenever the pool is empty.
    /// </summary>
    private static ProgressEvent? Boards(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U8(out byte count))
            return null;
        byte pool = 0;
        int left = 0;
        var boards = new List<QuestBoard>(count);
        for (int i = 0; i < count; i++)
        {
            if (!c.U32(out uint group) || !c.U8(out byte slot) || !c.Skip(16) || !c.U8(out byte n))
                return null;
            uint done = 0;
            for (int e = 0; e < n; e++)
            {
                if (!c.Skip(12))
                    return null;
                if (left == 0)
                {
                    if (!c.U8(out pool))
                        return null;
                    left = 8;
                }
                done += (uint)(pool & 1);
                pool >>= 1;
                left--;
            }
            boards.Add(new QuestBoard(slot, group, done));
        }
        return c.AtEnd ? new BoardsEvent(boards) : null;
    }

    /// <summary><c>3E E2 00 00 &lt;u32 group&gt;&lt;u32 id&gt;&lt;u32 value&gt;&lt;u8 bits, bit 0 = done&gt;</c> then exactly 18 bytes.</summary>
    private static ProgressEvent? BoardEntry(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.Skip(2) || !c.U32(out uint group) || !c.Skip(8) || !c.U8(out byte bits) || c.Remaining != 18)
            return null;
        return new BoardEntryEvent(group, (bits & 1) != 0);
    }

    /// <summary><c>A5 FF</c> / <c>A6 FF &lt;u64 kinah gained today&gt;&lt;u64 server ms&gt;</c>, nothing after.</summary>
    private static ProgressEvent? Kinah(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U64(out ulong v) || !c.Skip(8) || !c.AtEnd || v > 100_000_000_000)
            return null;
        return new KinahTodayEvent(v);
    }

    /// <summary>
    /// <c>1C 8D &lt;u32 a&gt;&lt;u32 b&gt; …</c>: only frames with <c>a = 0</c> carry the missions done today in <c>b</c>
    /// (the world-entry form has <c>b = 5</c> even on a day with nothing done, so it is not read).
    /// </summary>
    private static ProgressEvent? Duty(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U32(out uint a) || a != 0 || !c.U32(out uint done) || done > 100)
            return null;
        return new DutyMissionsEvent(done);
    }

    /// <summary>
    /// <c>24 E3 &lt;u8 n&gt;</c>, n entries <c>&lt;u32 id&gt;&lt;u32&gt;&lt;u32 used&gt;&lt;u64&gt;&lt;i64&gt;</c>, three lists
    /// <c>[u8 k][k × 4 bytes]</c>, <c>[u8 k][k × 5]</c>, <c>[u8 k][k × 8]</c>, a tag byte (absent on the last entry of a
    /// frame with more than one) and <c>&lt;u32 = 1&gt;</c>.
    /// </summary>
    private static ProgressEvent? Pass(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U8(out byte n) || n < 1 || n > MaxPass)
            return null;
        var list = new List<(uint, uint)>(n);
        ReadOnlySpan<int> widths = [4, 5, 8];
        for (int i = 0; i < n; i++)
        {
            if (!c.U32(out uint id) || !c.Skip(4) || !c.U32(out uint used) || !c.Skip(16))
                return null;
            foreach (int width in widths)
            {
                if (!c.U8(out byte k) || !c.Skip(k * width))
                    return null;
            }
            if ((n == 1 || i + 1 < n) && !c.Skip(1))
                return null;
            if (!c.U32(out uint one) || one != 1)
                return null;
            list.Add((id, used));
        }
        return c.AtEnd ? new PassUsedEvent(list) : null;
    }

    /// <summary><c>0B 61 &lt;u8 count&gt;</c> then <c>count</c> records that tile the frame exactly.</summary>
    private static ProgressEvent? Counters(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U8(out byte count))
            return null;
        var records = Records(b[c.Position..], count);
        return records is null ? null : new CountersEvent(records);
    }

    /// <summary><c>0C 61 00</c>, one record (same layout as the snapshot's) and one trailer byte.</summary>
    private static ProgressEvent? Counter(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4)
            return null;
        var records = Records(b[3..^1], 1);
        return records is null ? null : new CounterEvent(records[0]);
    }

    /// <summary>
    /// <c>00 90 &lt;u32 pet out&gt; &lt;varint n&gt;</c>, n × <c>&lt;u32 id&gt;&lt;u32 id again&gt;&lt;u32 level&gt;</c>,
    /// <c>&lt;varint m&gt;</c>, m × <c>&lt;u32 id&gt;&lt;u32 souls&gt;</c>, then more lists (not read).
    /// </summary>
    private static ProgressEvent? Pets(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U32(out uint active) || active >= MaxPetId || !c.Varint(out uint n) || n > MaxPets || n * 12 > c.Remaining)
            return null;
        var levels = new List<(uint, uint)>((int)n);
        for (int i = 0; i < n; i++)
        {
            if (!c.U32(out uint id) || !c.U32(out uint again) || !c.U32(out uint level))
                return null;
            if (id == 0 || id >= MaxPetId || again != id || level > MaxPetLevel)
                return null;
            levels.Add((id, level));
        }
        if (!c.Varint(out uint m) || m > MaxPets || m * 8 > c.Remaining)
            return null;
        var souls = new List<(uint, uint)>((int)m);
        for (int i = 0; i < m; i++)
        {
            if (!c.U32(out uint id) || !c.U32(out uint s) || id == 0 || id >= MaxPetId || s > MaxPetSouls)
                return null;
            souls.Add((id, s));
        }
        return n + m == 0 ? null : new PetsEvent(active, levels, souls);
    }

    /// <summary><c>0D 90 &lt;u8 n&gt;</c>, n × <c>&lt;u32 pet id&gt;&lt;u32 souls gained&gt;</c>, nothing after.</summary>
    private static ProgressEvent? PetSouls(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U8(out byte n) || n == 0 || n * 8 != c.Remaining)
            return null;
        var gains = new List<(uint, uint)>(n);
        for (int i = 0; i < n; i++)
        {
            if (!c.U32(out uint id) || !c.U32(out uint amount) || id == 0 || id >= MaxPetId || amount == 0 || amount > MaxPetSouls)
                return null;
            gains.Add((id, amount));
        }
        return new PetSoulsEvent(gains);
    }

    /// <summary><c>14 90 00 00 &lt;u32 pet id&gt;&lt;u32 new level&gt;</c> then bytes not read (13 in the samples).</summary>
    private static ProgressEvent? PetLevel(ReadOnlySpan<byte> b)
    {
        var c = new Cur(b, 2);
        if (!c.U16(out ushort zero) || zero != 0 || !c.U32(out uint id) || !c.U32(out uint level))
            return null;
        if (id == 0 || id >= MaxPetId || level == 0 || level > MaxPetLevel)
            return null;
        return new PetLevelEvent(id, level);
    }

    /// <summary>
    /// Counter records: <c>&lt;u8 mask&gt;&lt;u32 id&gt;</c> then, only for the bits set and in this order: bit 0 a u64
    /// (milliseconds left), bit 1 a u64 (refill period, dropped), bit 2 a varint (what is left), bit 3 a varint
    /// (kept as <see cref="CounterRecord.Extra"/>). A record with neither bit 0 nor bit 2 has 0 left. Ids ascend;
    /// the records must tile the buffer.
    /// </summary>
    private static List<CounterRecord>? Records(ReadOnlySpan<byte> buf, int count)
    {
        if (count == 0 || buf.Length > MaxSnapshot || count > buf.Length / 5)
            return null;
        var c = new Cur(buf, 0);
        var list = new List<CounterRecord>(count);
        uint? prev = null;
        for (int i = 0; i < count; i++)
        {
            if (!c.U8(out byte mask) || !c.U32(out uint id))
                return null;
            if (mask > 0x0F || id >= MaxCounterId || (prev is uint p && p >= id))
                return null;
            prev = id;
            ulong? ms = null;
            uint? left = null;
            if ((mask & 1) != 0)
            {
                if (!c.U64(out ulong t)) return null;
                ms = t;
            }
            if ((mask & 2) != 0 && !c.Skip(8))
                return null;
            if ((mask & 4) != 0)
            {
                if (!c.Varint(out uint l)) return null;
                left = l;
            }
            uint? extra = null;
            if ((mask & 8) != 0)
            {
                if (!c.Varint(out uint x)) return null;
                extra = x;
            }
            list.Add(left is uint v ? new CounterRecord(id, v, false, extra)
                : ms is ulong t2 ? new CounterRecord(id, t2, true, extra)
                : new CounterRecord(id, 0, false, extra));
        }
        return c.AtEnd ? list : null;
    }

    /// <summary>Bounds-checked little-endian cursor.</summary>
    private ref struct Cur
    {
        private readonly ReadOnlySpan<byte> _b;
        private int _i;

        public Cur(ReadOnlySpan<byte> b, int start)
        {
            _b = b;
            _i = start;
        }

        public readonly int Position => _i;
        public readonly int Remaining => _b.Length - _i;
        public readonly bool AtEnd => _i == _b.Length;

        public bool Skip(int n)
        {
            if (n < 0 || n > _b.Length - _i)
                return false;
            _i += n;
            return true;
        }

        public bool Bytes(int n, out ReadOnlySpan<byte> v)
        {
            v = default;
            if (n < 0 || n > _b.Length - _i)
                return false;
            v = _b.Slice(_i, n);
            _i += n;
            return true;
        }

        public bool U8(out byte v)
        {
            v = 0;
            if (_i >= _b.Length)
                return false;
            v = _b[_i++];
            return true;
        }

        public bool U16(out ushort v)
        {
            v = 0;
            if (!Bytes(2, out var s)) return false;
            v = BinaryPrimitives.ReadUInt16LittleEndian(s);
            return true;
        }

        public bool U32(out uint v)
        {
            v = 0;
            if (!Bytes(4, out var s)) return false;
            v = BinaryPrimitives.ReadUInt32LittleEndian(s);
            return true;
        }

        public bool U64(out ulong v)
        {
            v = 0;
            if (!Bytes(8, out var s)) return false;
            v = BinaryPrimitives.ReadUInt64LittleEndian(s);
            return true;
        }

        /// <summary>LEB128 that fits a u32: at most 5 bytes, the 5th carrying only the top 4 bits.</summary>
        public bool Varint(out uint v)
        {
            v = 0;
            for (int n = 0; n < 5; n++)
            {
                if (!U8(out byte b))
                    return false;
                if (n == 4 && (b & 0x7F) > 0x0F)
                    return false;
                v |= (uint)(b & 0x7F) << (7 * n);
                if ((b & 0x80) == 0)
                    return true;
            }
            return false;
        }
    }
}
