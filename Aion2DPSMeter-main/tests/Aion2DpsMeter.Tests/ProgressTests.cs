using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.Progress;
using Aion2DpsMeter.Core.Protocol;
using static Aion2DpsMeter.Tests.PacketBuilder;

namespace Aion2DpsMeter.Tests;

/// <summary>Fork (Faro): character progress frames, in the layouts AcompanION documents.</summary>
public class ProgressTests
{
    private static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
    private static byte[] U32(uint v) => BitConverter.GetBytes(v);
    private static byte[] U64(ulong v) => BitConverter.GetBytes(v);

    private static byte[] Identity(string name, ushort server = 2001, uint cls = 18, uint level = 45, uint gear = 2454)
    {
        var n = System.Text.Encoding.UTF8.GetBytes(name);
        return [0x33, 0x36, .. VarInt.Encode(1234), 0, 0, 0, 0, 0x07, (byte)n.Length, .. n,
            .. U16(server), .. U32(cls), 0x01, .. U32(level), .. U32(gear), .. U32(gear), .. U32(level), 0, 0];
    }

    private static byte[] CombatPower(uint cp, uint other = 204_769) => [0x56, 0x36, .. U64(cp), .. U64(other)];
    private static byte[] Gear(uint v) => [0x1D, 0x56, .. U32(v), .. U32(v)];
    private static byte[] Kinah(ulong v, bool live = false) => [live ? (byte)0xA6 : (byte)0xA5, 0xFF, .. U64(v), .. U64(1_761_000_000_000)];
    private static byte[] Duty(uint a, uint done) => [0x1C, 0x8D, .. U32(a), .. U32(done), 9, 9, 9];

    /// <summary><c>[mask][u32 id][fields]</c>: bit 0 a u64 (ms), bit 2 a varint left, bit 3 a varint.</summary>
    private static byte[] Rec(byte mask, uint id, ulong? ms = null, uint? left = null, uint? charged = null) =>
        [mask, .. U32(id),
            .. (ms is ulong m ? U64(m) : []),
            .. (left is uint l ? VarInt.Encode(l) : []),
            .. (charged is uint c ? VarInt.Encode(c) : [])];

    private static byte[] Counters(params byte[][] records) => [0x0B, 0x61, (byte)records.Length, .. records.SelectMany(r => r)];
    private static byte[] CounterUpdate(byte[] record) => [0x0C, 0x61, 0x00, .. record, 0x02];

    /// <summary>Boards with their entries' done flags, packed in one LSB-first pool shared by the whole frame.</summary>
    private static byte[] Boards(params (uint Group, byte Slot, bool[] Done)[] boards)
    {
        var o = new List<byte> { 0x56, 0xE2, (byte)boards.Length };
        int flagAt = -1, used = 8;
        foreach (var (group, slot, done) in boards)
        {
            o.AddRange(U32(group));
            o.Add(slot);
            o.AddRange(U64(1));
            o.AddRange(U64(2));
            o.Add((byte)done.Length);
            foreach (bool d in done)
            {
                o.AddRange(U32(group));
                o.AddRange(U32(7));
                o.AddRange(U32(0));
                if (used == 8)
                {
                    flagAt = o.Count;
                    o.Add(0);
                    used = 0;
                }
                if (d)
                    o[flagAt] |= (byte)(1 << used);
                used++;
            }
        }
        return o.ToArray();
    }

    private static byte[] BoardEntry(uint group, bool done) =>
        [0x3E, 0xE2, 0, 0, .. U32(group), .. U32(7), .. U32(1), done ? (byte)1 : (byte)0, .. new byte[18]];

    private static byte[] PassEntry(uint id, uint used, bool tag) =>
        [.. U32(id), .. U32(used), .. U32(used), .. U64(5), .. U64(6), 1, .. U32(100150), 0, 1, .. U32(9), .. U32(9),
            .. (tag ? new byte[] { 0x02 } : []), .. U32(1)];

    private static ProgressEvent? Decode(byte[] body) => ProgressDecoder.Parse(body, Opcodes.Default);

    [Fact]
    public void Each_frame_decodes_to_its_event()
    {
        Assert.Equal(new IdentityEvent("Nocturna", 2001, 18, 45, 2454), Decode(Identity("Nocturna")));
        Assert.Equal(new CombatPowerEvent(81_817), Decode(CombatPower(81_817)));
        Assert.Equal(new ItemLevelEvent(2_460), Decode(Gear(2_460)));
        Assert.Equal(new KinahTodayEvent(640_000), Decode(Kinah(640_000)));
        Assert.Equal(new KinahTodayEvent(700_000), Decode(Kinah(700_000, live: true)));
        Assert.Equal(new DutyMissionsEvent(2), Decode(Duty(0, 2)));
        Assert.Null(Decode(Duty(1, 5))); // the world-entry form: not a done count
        Assert.Equal(new CounterEvent(new CounterRecord(4, 1, false, 12)), Decode(CounterUpdate(Rec(0x0C, 4, left: 1, charged: 12))));
        Assert.Equal(new CounterEvent(new CounterRecord(4, 1, false)), Decode(CounterUpdate(Rec(0x04, 4, left: 1))));
        Assert.Equal(new BoardEntryEvent(11, true), Decode(BoardEntry(11, true)));

        var pass = Assert.IsType<PassUsedEvent>(Decode([0x24, 0xE3, 2, .. PassEntry(10044, 1200, tag: true), .. PassEntry(10045, 300, tag: false)]));
        Assert.Equal(new[] { (10044u, 1200u), (10045u, 300u) }, pass.Entries);
        var single = Assert.IsType<PassUsedEvent>(Decode([0x24, 0xE3, 1, .. PassEntry(10045, 301, tag: true)]));
        Assert.Equal(new[] { (10045u, 301u) }, single.Entries);
    }

    [Fact]
    public void A_counter_snapshot_reads_each_record_by_its_mask()
    {
        var ev = Assert.IsType<CountersEvent>(Decode(Counters(
            Rec(0x04, 1, left: 21),
            Rec(0x03, 3, ms: 25_200_000).Concat(U64(3_600_000)).ToArray(),   // bit 1 adds the refill period
            Rec(0x0C, 4, left: 3, charged: 12),
            Rec(0x00, 8),
            Rec(0x08, 80_000_001, charged: 2))));
        Assert.Equal(
            new CounterRecord[] { new(1, 21, false), new(3, 25_200_000, true), new(4, 3, false, 12), new(8, 0, false), new(80_000_001, 0, false, 2) },
            ev.Records);
    }

    [Fact]
    public void Frames_that_do_not_fit_exactly_are_ignored()
    {
        Assert.Null(Decode([.. Counters(Rec(0x04, 1, left: 21)), 0x00]));                 // a byte too many
        Assert.Null(Decode(Counters(Rec(0x04, 4, left: 1), Rec(0x04, 1, left: 2))));      // ids must ascend
        Assert.Null(Decode([.. Gear(2_460), 0x00]));
        Assert.Null(Decode([.. Kinah(5), 0x00]));
        Assert.Null(Decode([.. BoardEntry(11, true), 0x00]));
        Assert.Null(Decode([.. Boards((11, 1, [true])), 0x00]));
        Assert.Null(Decode(Identity("$Ab12345")));                                         // tutorial placeholder
        Assert.Null(Decode(Identity("Nocturna", server: 7)));
        var bytes = Identity("Nocturna");
        for (int cut = 3; cut < bytes.Length - 8; cut++)
            Decode(bytes[..cut]); // never throws
    }

    [Fact]
    public void Combat_power_is_the_first_number_of_its_frame()
    {
        // Real frames from a world entry (12/11/2026): the first number grows while the gear loads and ends at the
        // 175.0K the game showed; the second stayed at 204,769, which Faro used to show as your CP.
        static byte[] Real(string hex) => Convert.FromHexString(hex);
        Assert.Equal(new CombatPowerEvent(131_793), Decode(Real("5636" + "D10202" + "0000000000" + "E11F03" + "0000000000")));
        Assert.Equal(new CombatPowerEvent(144_404), Decode(Real("5636" + "143402" + "0000000000" + "E11F03" + "0000000000")));
        Assert.Equal(new CombatPowerEvent(174_579), Decode(Real("5636" + "F3A902" + "0000000000" + "E11F03" + "0000000000")));
        // 28/10/2026: both numbers ended equal.
        Assert.Equal(new CombatPowerEvent(96_404), Decode(Real("5636" + "947801" + "0000000000" + "947801" + "0000000000")));
        Assert.Null(Decode(CombatPower(0)));
    }

    [Fact]
    public void World_entry_data_waits_for_the_identity_that_trails_it()
    {
        var tracker = new ProgressTracker();
        var conn = new ProgressSession();
        var sent = new List<ProgressSnapshot>();
        tracker.Changed += sent.Add;
        void Feed(long at, byte[] body) => tracker.Apply(conn, at, Decode(body)!);

        Feed(1, CombatPower(81_817));
        Feed(2, Counters(Rec(0x04, 1, left: 21), Rec(0x04, 4, left: 6)));
        Feed(3, Boards((11, 1, [true, true, false]), (22, 2, Enumerable.Range(0, 10).Select(i => i < 5).ToArray()), (33, 3, [true])));
        Feed(4, Kinah(250_000));
        Feed(5, Gear(1)); // unbound: dropped, the identity carries the gear score
        Assert.Empty(sent);

        Feed(6, Identity("Nocturna"));
        var a = Assert.Single(sent);
        Assert.Equal("Nocturna", a.Name);
        Assert.Equal(81_817u, a.CombatPower);
        Assert.Equal(2454, a.ItemLevel);
        Assert.Contains((4u, 6ul, false, 2L, (uint?)null), a.Counters);
        Assert.Equal((2ul, 3L), a.Gains[ProgressTracker.SupplyDaily]);
        Assert.Equal((5ul, 3L), a.Gains[ProgressTracker.SupplyWeekly]);
        Assert.Equal((250_000ul, 4L), a.Gains[ProgressTracker.KinahToday]);

        // Live, once the character is known: applied right away.
        Feed(10, CounterUpdate(Rec(0x0C, 4, left: 5, charged: 12)));
        Feed(11, BoardEntry(11, true));
        Feed(12, Duty(0, 3));
        var live = sent[^1];
        Assert.Contains((4u, 5ul, false, 10L, (uint?)12), live.Counters);
        Assert.Equal((3ul, 11L), live.Gains[ProgressTracker.SupplyDaily]);
        Assert.Equal((5ul, 3L), live.Gains[ProgressTracker.SupplyWeekly]);   // the delivery was on the day's board
        Assert.Equal((3ul, 12L), live.Gains[ProgressTracker.DutyDaily]);
        Assert.Equal("Nocturna", tracker.Current()!.Name);
    }

    [Fact]
    public void A_character_swap_files_the_new_bundle_under_the_new_character()
    {
        var tracker = new ProgressTracker();
        var conn = new ProgressSession();
        var sent = new List<ProgressSnapshot>();
        tracker.Changed += sent.Add;
        void Feed(long at, byte[] body) => tracker.Apply(conn, at, Decode(body)!);

        Feed(1, Counters(Rec(0x04, 4, left: 6)));
        Feed(2, Identity("Nocturna"));
        // Swap: the second character's snapshot comes before its identity and must not touch the first.
        Feed(10, CombatPower(50_000));
        Feed(11, Counters(Rec(0x04, 4, left: 14)));
        Feed(12, Kinah(9));
        Assert.Single(sent);
        Feed(13, Identity("Lunaverde", cls: 30, level: 40, gear: 1800));

        var b = sent[^1];
        Assert.Equal("Lunaverde", b.Name);
        Assert.Equal(30u, b.ClassCode);
        Assert.Contains((4u, 14ul, false, 11L, (uint?)null), b.Counters);
        Assert.Equal(50_000u, b.CombatPower);
        Assert.Equal("Lunaverde", tracker.Current()!.Name);
        Assert.Contains((4u, 6ul, false, 1L, (uint?)null), sent[0].Counters);
        Assert.False(sent[0].Gains.ContainsKey(ProgressTracker.KinahToday));
    }

    [Fact]
    public void The_parser_reads_progress_inside_bundles_and_plain_frames()
    {
        var tracker = new ProgressTracker();
        var store = new CombatStore(new GameData());
        var parser = new PacketParser(store, new GameData(), null, tracker) { CurrentTimestampMs = 5_000 };
        ProgressSnapshot? last = null;
        tracker.Changed += p => last = p;

        var bundle = Bundle([.. Frame(CombatPower(70_000)), .. Frame(Counters(Rec(0x04, 60_000_001, left: 840))),
            .. Frame(Boards((11, 1, [true]))), .. Frame(Identity("Nocturna"))]);
        parser.ConsumeStream(bundle);
        Assert.NotNull(last);
        Assert.Equal(70_000u, last!.CombatPower);
        Assert.Contains((60_000_001u, 840ul, false, 5_000L, (uint?)null), last.Counters);

        parser.CurrentTimestampMs = 6_000;
        parser.ConsumeStream(Frame(Kinah(1_000_000, live: true)));
        Assert.Equal((1_000_000ul, 6_000L), last.Gains[ProgressTracker.KinahToday]);
    }

    [Fact]
    public void The_log_shows_each_counters_second_number_and_the_counter_frames_bytes()
    {
        var tracker = new ProgressTracker { LogCounterBytes = true };
        var parser = new PacketParser(new CombatStore(new GameData()), new GameData(), null, tracker) { CurrentTimestampMs = 1 };
        var log = new List<string>();
        tracker.Trace += log.Add;

        byte[] snapshot = Counters(Rec(0x0C, 4, left: 2, charged: 2), Rec(0x04, 8, left: 7), Rec(0x0C, 60_000_001, left: 60, charged: 35));
        parser.ConsumeStream(Frame(snapshot));
        Assert.Contains("progress: bytes " + Convert.ToHexString(snapshot), log);
        Assert.Contains("progress: counters 4=2(+2) 8=7 60000001=60(+35)", log);

        parser.ConsumeStream(Frame(Identity("Nocturna")));
        log.Clear();
        parser.ConsumeStream(Frame(CounterUpdate(Rec(0x0C, 60_000_001, left: 60, charged: 50))));
        Assert.Contains("progress: counter 60000001 = 60(+50) (Nocturna)", log);
        Assert.Equal((uint?)50, tracker.Current()!.Counters.Single(c => c.Id == 60_000_001).Extra);

        // A counter frame that does not parse is still logged, so a new layout can be seen.
        log.Clear();
        byte[] odd = [0x0C, 0x61, 0x00, 0x1F, .. U32(4), 9, 9];
        parser.ConsumeStream(Frame(odd));
        Assert.Equal(["progress: bytes (did not parse) " + Convert.ToHexString(odd)], log);
    }

    /// <summary><c>00 90</c>: pet out, n × [id][id][level], m × [id][souls], then a list we do not read.</summary>
    private static byte[] Pets(uint active, (uint Id, uint Level)[] levels, (uint Id, uint Souls)[] souls) =>
        [0x00, 0x90, .. U32(active), .. VarInt.Encode((uint)levels.Length), .. levels.SelectMany(l => U32(l.Id).Concat(U32(l.Id)).Concat(U32(l.Level))),
            .. VarInt.Encode((uint)souls.Length), .. souls.SelectMany(s => U32(s.Id).Concat(U32(s.Souls))), 0x01, 0x07, 0x00];
    private static byte[] PetSouls(params (uint Id, uint Amount)[] gains) =>
        [0x0D, 0x90, (byte)gains.Length, .. gains.SelectMany(g => U32(g.Id).Concat(U32(g.Amount)))];
    private static byte[] PetLevel(uint id, uint level) =>
        [0x14, 0x90, 0, 0, .. U32(id), .. U32(level), 0x04, 0x06, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void Pet_frames_decode_and_bad_ones_are_ignored()
    {
        var ev = Assert.IsType<PetsEvent>(Decode(Pets(1066, [(1001, 3), (1066, 1)], [(1066, 12), (1102, 3)])));
        Assert.Equal(1066u, ev.Active);
        Assert.Equal(new[] { (1001u, 3u), (1066u, 1u) }, ev.Levels);
        Assert.Equal(new[] { (1066u, 12u), (1102u, 3u) }, ev.Souls);
        Assert.Equal(new[] { (1066u, 1u) }, Assert.IsType<PetSoulsEvent>(Decode(PetSouls((1066, 1)))).Gains);
        Assert.Equal(new PetLevelEvent(1066, 2), Decode(PetLevel(1066, 2)));

        Assert.Null(Decode(Pets(1, [(1001, 3)], [])[..^5]));                               // cut inside a record
        var mismatch = Pets(1, [(1001, 3)], []);
        mismatch[11] = 0x09;                                                                 // the repeated id differs
        Assert.Null(Decode(mismatch));
        Assert.Null(Decode([0x00, 0x90, .. U32(1), 0x80, 0x80, 0x80, 0x80, 0x01, .. U32(1001), .. U32(1001), .. U32(3)])); // claims 2^28 records
        Assert.Null(Decode([.. PetSouls((1066, 1)), 0x00]));
        Assert.Null(Decode(PetSouls((1066, 0))));
        Assert.Null(Decode([0x14, 0x90, 1, 0, .. U32(1066), .. U32(2)]));
    }

    [Fact]
    public void The_pet_collection_is_the_accounts_and_follows_souls_and_levels()
    {
        var tracker = new ProgressTracker();
        var conn = new ProgressSession();
        var sent = new List<PetsSnapshot>();
        var log = new List<string>();
        tracker.PetsChanged += sent.Add;
        tracker.Trace += log.Add;
        void Feed(long at, byte[] body) => tracker.Apply(conn, at, Decode(body)!);

        Feed(1, PetSouls((1066, 1)));   // before the snapshot: only logged
        Assert.Empty(sent);
        Assert.Null(tracker.CurrentPets());

        Feed(2, Pets(1066, [(1001, 3), (1066, 1), (1102, 0)], [(1066, 23), (1102, 3)]));
        var p = Assert.Single(sent);
        Assert.Equal(new[] { (1001u, 3u, 0u), (1066u, 1u, 23u), (1102u, 0u, 3u) }, p.Pets);
        Assert.Equal((1066u, 2L), (p.Active, p.At));
        Assert.Contains("progress: pets 3 listed, 2 unlocked, 1 at level 3, out 1066", log);

        // No identity needed: the collection is the same for every character.
        Feed(3, PetSouls((1066, 1), (1200, 1)));
        Assert.Equal(new[] { (1001u, 3u, 0u), (1066u, 1u, 24u), (1102u, 0u, 3u), (1200u, 0u, 1u) }, sent[^1].Pets);
        Feed(4, PetSouls((1066, 1)));
        Feed(4, PetLevel(1066, 2));
        Assert.Contains((1066u, 2u, 0u), sent[^1].Pets);
        Assert.Equal(4L, tracker.CurrentPets()!.At);
        Assert.Contains("progress: pet 1066 reached level 2", log);

        // A character swap sends the collection again: it replaces the old one.
        Feed(10, Counters(Rec(0x04, 4, left: 6)));
        Feed(11, Pets(1102, [(1001, 3)], []));
        Assert.Equal(new[] { (1001u, 3u, 0u) }, tracker.CurrentPets()!.Pets);
    }

    private static byte[] Abyss(ulong v, ulong limit) => [0xAB, 0xFF, .. U64(v), .. U64(limit)];

    [Fact]
    public void Abyss_points_decode_and_are_kept_with_the_kinah()
    {
        Assert.Equal(new AbyssPointsEvent(1_234, 30_000), Decode(Abyss(1_234, 30_000)));
        Assert.Null(Decode([.. Abyss(1, 2), 0x00]));       // a byte too many: not this layout
        Assert.Null(Decode([0xAB, 0xFF, .. U64(1)]));       // too short
        // The real frame from the 28/10 capture: 38,998 held, limit 2,500,000.
        Assert.Equal(new AbyssPointsEvent(38_998, 2_500_000), Decode(Convert.FromHexString("ABFF5698000000000000A025260000000000")));
        Assert.Null(Decode([0xA9, 0xFF, .. U64(1), .. U64(2)]));   // the older build's opcode is not read

        var tracker = new ProgressTracker();
        var conn = new ProgressSession();
        var sent = new List<ProgressSnapshot>();
        var log = new List<string>();
        tracker.Changed += sent.Add;
        tracker.Trace += log.Add;
        void Feed(long at, byte[] body) => tracker.Apply(conn, at, Decode(body)!);
        Feed(1, Identity("Nocturna"));
        Feed(2, Kinah(1_000, live: true));
        Feed(3, Abyss(16, 30_000));
        var g = sent[^1].Gains;
        Assert.Equal((1_000ul, 2L), g[ProgressTracker.KinahToday]);
        Assert.Equal((16ul, 3L), g[ProgressTracker.AbyssPoints]);
        Assert.Equal((30_000ul, 3L), g[ProgressTracker.AbyssLimit]);
        Assert.Contains("progress: kinah today 1000 (Nocturna)", log);
        Assert.Contains("progress: abyss points 16 (limit 30000) (Nocturna)", log);
    }

    [Fact]
    public void The_kinah_item_in_an_inventory_update_is_the_kinah_held()
    {
        // Real frames from the 28/10 capture (after the length byte): the kinah item (930100031) with 4,480,151
        // and, right after, another item (930100003) that must be ignored.
        var kinah = Convert.FromHexString("1B5601010039E60000001A00003F337037975C4400000000000100000000000000000000000000000B0000");
        var other = Convert.FromHexString("1B56010100D1E60000000A000023337037763B0000000000000102000000000000000000000000000B0000");
        Assert.Equal(new KinahHeldEvent(4_480_151), Decode(kinah));
        Assert.Null(Decode(other));
        Assert.Null(Decode(kinah[..20]));   // cut short

        var tracker = new ProgressTracker();
        var conn = new ProgressSession();
        var sent = new List<ProgressSnapshot>();
        var log = new List<string>();
        tracker.Changed += sent.Add;
        tracker.Trace += log.Add;
        tracker.Apply(conn, 1, Decode(Identity("Nocturna"))!);
        tracker.Apply(conn, 2, Decode(kinah)!);
        Assert.Equal((4_480_151ul, 2L), sent[^1].Gains[ProgressTracker.KinahHeld]);
        Assert.Contains("progress: kinah held 4480151 (Nocturna)", log);
    }

    [Fact]
    public void Mid_session_start_binds_live_progress_to_the_name_the_combat_side_knows()
    {
        var tracker = new ProgressTracker();
        var conn = new ProgressSession();
        var sent = new List<ProgressSnapshot>();
        tracker.Changed += sent.Add;
        void Feed(long at, byte[] body) => tracker.Apply(conn, at, Decode(body)!);

        // The meter started with the game in the world: Abyss Points arrive with no identity yet.
        Feed(1, Abyss(41_466, 2_500_000));
        Assert.Empty(sent);
        tracker.BindByName(conn, "Nocturna", 2);
        Assert.Equal("Nocturna", sent[^1].Name);
        Assert.Equal((41_466ul, 1L), sent[^1].Gains[ProgressTracker.AbyssPoints]);
        // From then on, live changes go straight to it.
        Feed(3, Abyss(41_478, 2_500_000));
        Assert.Equal((41_478ul, 3L), sent[^1].Gains[ProgressTracker.AbyssPoints]);

        // After a world entry the name from combat is never used: the identity frame says who entered.
        var swap = new ProgressSession();
        int before = sent.Count;
        tracker.Apply(swap, 10, Decode(Counters(Rec(0x04, 4, left: 6)))!);
        tracker.Apply(swap, 11, Decode(Abyss(5, 2_500_000))!);
        tracker.BindByName(swap, "Nocturna", 12);
        Assert.Equal(before, sent.Count);
        tracker.Apply(swap, 13, Decode(Identity("Lunaverde"))!);
        Assert.Equal("Lunaverde", sent[^1].Name);
        Assert.Equal((5ul, 11L), sent[^1].Gains[ProgressTracker.AbyssPoints]);
    }

    [Fact]
    public void Currency_frames_are_logged_up_to_a_limit()
    {
        var tracker = new ProgressTracker();
        var log = new List<string>();
        tracker.Trace += log.Add;
        Assert.True(ProgressDecoder.IsCurrencyFrame(Abyss(1, 2), Opcodes.Default));
        Assert.True(ProgressDecoder.IsCurrencyFrame([0xA7, 0xFF, 1, 2, 3], Opcodes.Default));
        Assert.False(ProgressDecoder.IsCurrencyFrame(Kinah(1), Opcodes.Default));
        for (int i = 0; i < ProgressTracker.CurrencyBytesMax + 20; i++)
            tracker.NoteCurrencyBytes(Abyss((ulong)i, 2), true);
        Assert.Equal(ProgressTracker.CurrencyBytesMax, log.Count);
        Assert.StartsWith("progress: currency bytes ABFF", log[0]);
    }

    [Fact]
    public void Pet_souls_before_the_collection_go_to_Faro_as_deltas()
    {
        var tracker = new ProgressTracker();
        var conn = new ProgressSession();
        var deltas = new List<PetsDelta>();
        var sent = new List<PetsSnapshot>();
        tracker.PetsGainedBeforeCollection += deltas.Add;
        tracker.PetsChanged += sent.Add;
        void Feed(long at, byte[] body) => tracker.Apply(conn, at, Decode(body)!);

        // The meter started with the game already in the world: no collection yet.
        Feed(1, PetSouls((1209, 1), (1066, 2)));
        Feed(2, PetLevel(1209, 3));
        Assert.Empty(sent);
        Assert.Equal(2, deltas.Count);
        Assert.Equal(new[] { (1209u, 1u), (1066u, 2u) }, deltas[0].Souls);
        Assert.Null(deltas[0].Level);
        Assert.Equal(1L, deltas[0].At);
        Assert.Empty(deltas[1].Souls);
        Assert.Equal((1209u, 3u), deltas[1].Level);

        // Once the collection arrives, changes go as full snapshots again.
        Feed(3, Pets(1066, [(1066, 1)], [(1066, 5)]));
        Feed(4, PetSouls((1066, 1)));
        Assert.Equal(2, deltas.Count);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public void The_base_and_brawler_class_codes_are_known()
    {
        Assert.Equal(JobClass.Gladiator, JobClasses.FromRosterClass(2));
        Assert.Equal(JobClass.Ranger, JobClasses.FromRosterClass(4));
        Assert.Equal(JobClass.Assassin, JobClasses.FromRosterClass(18));
        Assert.Equal(JobClass.Fighter, JobClasses.FromRosterClass(46));
        Assert.Equal(JobClass.Unknown, JobClasses.FromRosterClass(40));
    }
}
