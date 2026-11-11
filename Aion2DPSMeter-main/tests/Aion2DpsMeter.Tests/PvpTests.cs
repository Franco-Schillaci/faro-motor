using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.Protocol;
using Aion2DpsMeter.Core.Pvp;
using static Aion2DpsMeter.Tests.PacketBuilder;

namespace Aion2DpsMeter.Tests;

/// <summary>
/// Fork (Faro): when a player kills you, the meter reports who it was (server, legion, class, the gear the server
/// shows) and who else hit you in the seconds before. Layouts from captures of 28/10/2026 (two deaths in the Abyss).
/// </summary>
public class PvpTests
{
    private const int Me = 15580;
    private const int OldMe = 15724;      // who the self record said you were before a teleport
    private const int Killer = 14896;
    private const int Helper = 15069;
    private const int Stranger = 15757;
    private const int ChanterSkill = 18010000;
    private const int ClericSkill = 17010000;

    // Kaelthorn's gear in the capture: weapon +18, armour 2-8, necklace and two earrings.
    private static readonly GearPiece[] KillerGear =
    [
        new(1, 110830051, 18), new(2, 210330052, 10), new(3, 210430053, 10), new(4, 210130053, 11),
        new(5, 210230052, 10), new(6, 210530053, 11), new(7, 210630053, 10), new(8, 210730053, 11),
        new(9, 310130051, 12), new(10, 310230053, 12), new(11, 310230052, 12),
    ];

    private static (CombatStore Store, PacketParser Parser, PvpTracker Pvp, List<PvpDeath> Deaths) Setup()
    {
        var data = new GameData();
        var store = new CombatStore(data);
        var pvp = new PvpTracker { SkillName = store.SkillDisplayName, NameOf = store.NicknameOf };
        var deaths = new List<PvpDeath>();
        pvp.Died += deaths.Add;
        return (store, new PacketParser(store, data, null, null, pvp), pvp, deaths);
    }

    /// <summary>
    /// A <c>45 36</c> spawn as the game sends it: name, class code, then a block of other data and the gear records
    /// (<c>&lt;u32 item&gt;&lt;u8 level&gt;&lt;u8 slot&gt;&lt;8 zeros&gt;</c> followed by more bytes). <paramref name="lookAlike"/>
    /// puts an accessory id on an armour slot first, like a record seen in a capture.
    /// </summary>
    private static byte[] Spawn(int entity, string name, uint classCode, IEnumerable<GearPiece> gear, bool lookAlike = false)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var body = new List<byte> { 0x45, 0x36 };
        body.AddRange(VarInt.Encode((uint)entity));
        body.AddRange([0x00, 0x00, 0x00, 0x00, 0x07, (byte)nameBytes.Length]);
        body.AddRange(nameBytes);
        body.AddRange(BitConverter.GetBytes(classCode));
        body.AddRange(Enumerable.Range(0, 120).Select(i => (byte)(0x80 | (i % 50))));
        if (lookAlike)
            AddRecord(body, new GearPiece(4, 316_736_000, 18));
        foreach (var g in gear)
            AddRecord(body, g);
        body.AddRange([0x00, 0x00, 0x0B, 0x00]);
        return Frame(body.ToArray());

        static void AddRecord(List<byte> b, GearPiece g)
        {
            b.AddRange(BitConverter.GetBytes((uint)g.Item));
            b.Add((byte)g.Enchant);
            b.Add((byte)g.Slot);
            b.AddRange(new byte[8]);
            b.AddRange([0x44, 0xB2, 0x98, 0x00, 0x00, 0x03, 0xBC, 0x69, 0x30, 0x01, 0x0E, 0xC5, 0x61, 0x89, 0x0C, 0x0A, 0x02, 0x11, 0x13]);
        }
    }

    /// <summary><c>4A 36 &lt;entity&gt; 00 …</c>: a record the server sends only about you.</summary>
    private static byte[] SelfStatus(int entity) =>
        Frame([0x4A, 0x36, .. VarInt.Encode((uint)entity), 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

    /// <summary><c>04 8D &lt;victim&gt; &lt;4 bytes&gt; &lt;killer&gt; &lt;u16 server&gt; &lt;len&gt;&lt;name&gt; &lt;len&gt;&lt;legion&gt; …</c></summary>
    private static byte[] KillRecord(int victim, int killer, ushort server, string name, string legion)
    {
        var n = System.Text.Encoding.UTF8.GetBytes(name);
        var l = System.Text.Encoding.UTF8.GetBytes(legion);
        return Frame([0x04, 0x8D, .. VarInt.Encode((uint)victim), 0x72, 0x28, 0xE9, 0x00, .. VarInt.Encode((uint)killer),
            .. BitConverter.GetBytes(server), (byte)n.Length, .. n, (byte)l.Length, .. l, 0x02, 0, 0, 0, 0, 0, 0, 0, 0x01]);
    }

    [Fact]
    public void Dying_to_a_player_reports_the_killer_with_server_legion_class_and_gear()
    {
        var (_, parser, _, deaths) = Setup();
        parser.CurrentTimestampMs = 100_000;
        parser.ConsumeStream(SelfStatus(Me));
        parser.ConsumeStream(Spawn(Killer, "Kaelthorn", 36, KillerGear));
        parser.ConsumeStream(Spawn(Helper, "Valentia", 29, KillerGear.Take(3)));
        for (int i = 0; i < 3; i++)
        {
            parser.CurrentTimestampMs = 101_000 + i * 1_000;
            parser.ConsumeStream(Damage(Helper, Me, ClericSkill, 20_000));
            parser.ConsumeStream(Damage(Killer, Me, ChanterSkill, 9_000));
        }
        parser.CurrentTimestampMs = 104_500;
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", "LOBO"));

        var d = Assert.Single(deaths);
        Assert.Equal(104_500, d.At);
        Assert.Equal("Kaelthorn", d.Killer.Name);
        Assert.Equal((ushort)2404, d.Killer.Server);
        Assert.Equal("LOBO", d.Killer.Legion);
        Assert.Equal(JobClass.Chanter, d.Killer.Job);
        Assert.True(d.Killer.FinalBlow);
        Assert.Equal(KillerGear, d.Killer.Gear);
        Assert.Equal(27_000, d.Killer.Damage);
        Assert.Equal(3, d.Killer.Hits);
        Assert.Equal(87_000, d.TotalDamage);

        // Most damage first: Valentia hit harder than the killer.
        Assert.Equal(["Valentia", "Kaelthorn"], d.Attackers.Select(a => a.Name));
        var helper = d.Attackers[0];
        Assert.Equal(JobClass.Cleric, helper.Job);
        Assert.False(helper.FinalBlow);
        Assert.Equal(3, helper.Gear.Count);
        Assert.Equal(60_000, helper.Skills.Single().Damage);
    }

    [Fact]
    public void A_later_record_without_class_or_gear_keeps_what_the_spawn_said()
    {
        var (_, parser, _, deaths) = Setup();
        parser.ConsumeStream(SelfStatus(Me));
        parser.ConsumeStream(Spawn(Killer, "Kaelthorn", 36, KillerGear));
        parser.ConsumeStream(Spawn(Killer, "Kaelthorn", 0x1234_5678, []));
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", "LOBO"));
        var d = Assert.Single(deaths);
        Assert.Equal(JobClass.Chanter, d.Killer.Job);
        Assert.Equal(KillerGear, d.Killer.Gear);
    }

    [Fact]
    public void A_look_alike_record_on_the_wrong_slot_is_not_gear()
    {
        var (_, parser, _, deaths) = Setup();
        parser.ConsumeStream(SelfStatus(Me));
        parser.ConsumeStream(Spawn(Killer, "Kaelthorn", 36, KillerGear, lookAlike: true));
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", ""));
        var d = Assert.Single(deaths);
        Assert.Equal(KillerGear, d.Killer.Gear);
        Assert.Null(d.Killer.Legion);
    }

    [Fact]
    public void Gear_is_not_taken_from_the_next_spawn_in_the_same_bundle()
    {
        var (_, parser, _, deaths) = Setup();
        parser.ConsumeStream(SelfStatus(Me));
        parser.ConsumeStream(Bundle([.. Spawn(Killer, "Kaelthorn", 36, []), .. Spawn(Helper, "Valentia", 29, KillerGear)]));
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", "LOBO"));
        Assert.Empty(Assert.Single(deaths).Killer.Gear);
    }

    [Fact]
    public void Someone_else_dying_is_not_your_death()
    {
        var (_, parser, _, deaths) = Setup();
        parser.ConsumeStream(SelfStatus(Me));
        parser.ConsumeStream(Spawn(Killer, "Kaelthorn", 36, KillerGear));
        parser.ConsumeStream(KillRecord(Stranger, Killer, 2404, "Kaelthorn", "LOBO"));
        Assert.Empty(deaths);
    }

    [Fact]
    public void The_same_death_seen_again_is_reported_once()
    {
        var (_, parser, _, deaths) = Setup();
        parser.ConsumeStream(SelfStatus(Me));
        parser.CurrentTimestampMs = 50_000;
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", "LOBO"));
        parser.CurrentTimestampMs = 50_100;
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", "LOBO"));
        Assert.Single(deaths);
        // Dying again later is a new death.
        parser.CurrentTimestampMs = 120_000;
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", "LOBO"));
        Assert.Equal(2, deaths.Count);
    }

    [Fact]
    public void After_a_teleport_the_4A36_record_says_who_you_are_not_the_old_self_record()
    {
        var (store, parser, _, deaths) = Setup();
        store.SetLocalIdentity(OldMe, "Nocturna");
        parser.ConsumeStream(SelfStatus(Me));
        parser.ConsumeStream(KillRecord(OldMe, Killer, 2404, "Kaelthorn", "LOBO"));
        Assert.Empty(deaths);
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", "LOBO"));
        Assert.Single(deaths);
    }

    [Fact]
    public void Before_any_4A36_record_the_meter_identity_is_used()
    {
        var (store, parser, _, deaths) = Setup();
        store.SetLocalIdentity(Me, "Nocturna");
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", "LOBO"));
        Assert.Single(deaths);
    }

    [Fact]
    public void Hits_from_before_the_window_do_not_count()
    {
        var (_, parser, _, deaths) = Setup();
        parser.ConsumeStream(SelfStatus(Me));
        parser.ConsumeStream(Spawn(Killer, "Kaelthorn", 36, KillerGear));
        parser.CurrentTimestampMs = 10_000;
        parser.ConsumeStream(Damage(Killer, Me, ChanterSkill, 50_000));
        parser.CurrentTimestampMs = 10_000 + PvpTracker.WindowMs + 5_000;
        parser.ConsumeStream(Damage(Killer, Me, ChanterSkill, 7_000));
        parser.ConsumeStream(KillRecord(Me, Killer, 2404, "Kaelthorn", "LOBO"));
        var d = Assert.Single(deaths);
        Assert.Equal(7_000, d.TotalDamage);
        Assert.Equal(1, d.Killer.Hits);
    }

    [Fact]
    public void The_killer_is_reported_even_when_its_hits_were_not_seen()
    {
        var (_, parser, _, deaths) = Setup();
        parser.ConsumeStream(SelfStatus(Me));
        parser.ConsumeStream(KillRecord(Me, Killer, 1406, "Brennor", "Pequenos"));
        var d = Assert.Single(deaths);
        Assert.Equal("Brennor", d.Killer.Name);
        Assert.Equal(0, d.Killer.Damage);
        Assert.Empty(d.Killer.Gear);
        Assert.Equal(JobClass.Unknown, d.Killer.Job);
        Assert.Contains(d.Killer, d.Attackers);
    }
}
