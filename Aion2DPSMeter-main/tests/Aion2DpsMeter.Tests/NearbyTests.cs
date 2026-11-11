using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.Protocol;
using Aion2DpsMeter.Core.Pvp;
using static Aion2DpsMeter.Tests.PacketBuilder;

namespace Aion2DpsMeter.Tests;

/// <summary>
/// Fork (Faro): «Enemigos cerca». Each player spawn carries the server, the legion and the combat power; <c>47 36</c>
/// says the player left your view. Byte layouts copied from the captures of 28-30/10/2026 (SolarFalcon, server 2404,
/// combat power 164,332, the same the official character data showed that day).
/// </summary>
public class NearbyTests
{
    private const uint RangerClass = 16;
    private static readonly byte[] Anchor = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x80, 0x75, 0xD5, 0x2A, 0xBB, 0x03];

    private static (PacketParser Parser, PvpTracker Pvp) Setup()
    {
        var data = new GameData();
        var store = new CombatStore(data);
        var pvp = new PvpTracker();
        return (new PacketParser(store, data, null, null, pvp), pvp);
    }

    /// <summary>
    /// A <c>45 36</c> spawn shaped like the real ones: name, class, a block of other data, vida, the origin block (with
    /// or without legion), the gear and, near the end, <c>&lt;u32&gt;&lt;u32&gt;[prefix]&lt;u32 combat power&gt;</c>.
    /// </summary>
    private static byte[] Spawn(int entity, string name, ushort server, string? legion, int cp, byte? cpPrefix = null, int weapon = 17, bool highBit = false)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var b = new List<byte> { 0x45, 0x36 };
        b.AddRange(VarInt.Encode((uint)entity));
        b.AddRange([0x07, 0x30, 0xA0, 0x03, 0x17, (byte)nameBytes.Length]);
        b.AddRange(nameBytes);
        b.AddRange(BitConverter.GetBytes(RangerClass));
        b.AddRange(Enumerable.Range(0, 50).Select(i => (byte)(0x80 | (i % 50))));
        b.AddRange([0xA0, 0x86, 0x01, 0x00, 0xA0, 0x86, 0x01, 0x00, 0xD0, 0xBF, 0x0B, 0x00, 0xD0, 0xBF, 0x0B, 0x00, 0x01, 0x00, 0x00, 0x00]);
        b.AddRange([0x02, 0xAC, 0x6E, 0x65, 0x02, 0xE8, 0xFB, 0x69, 0x02, 0xE5, 0x1A, 0x5F, 0x01]);
        var srv = BitConverter.GetBytes(server);
        if (legion is not null)
        {
            var l = System.Text.Encoding.UTF8.GetBytes(legion);
            b.AddRange([.. srv, 0x1C, 0x00, 0x00, 0x00, 0x00, 0x00, .. srv, (byte)l.Length, .. l, 0x02, 0x00, 0x01]);
            b.AddRange(new byte[14]);
            b.AddRange([0x12, 0x11, 0x10, 0x31, 0xC8, 0xC9, 0x01]);
        }
        else
        {
            // Sprongfaker's record: the fourth small byte has its high bit set (0xEF).
            b.AddRange([.. srv, 0x1C, 0x11, 0x10, highBit ? (byte)0xEF : (byte)0x49, 0xC3, 0xC9, 0x01]);
        }
        b.AddRange(Anchor);
        b.AddRange([0x00, 0x00, 0xE4, 0x0A, 0x01, 0x00]);
        // Weapon and a piece of armour.
        b.AddRange([.. BitConverter.GetBytes(110430051u), (byte)weapon, 0x01, .. new byte[8], 0x44, 0xB2, 0x98, 0x00]);
        b.AddRange([.. BitConverter.GetBytes(210330053u), 0x0B, 0x02, .. new byte[8], 0x44, 0xB2, 0x98, 0x00]);
        b.AddRange(Enumerable.Range(0, 40).Select(i => (byte)(0x90 | (i % 16))));
        // Tail: 20 zeros, a 4-byte value, <u32 1098> <u32 11> [prefix] <u32 cp>, then zeros.
        b.AddRange(new byte[20]);
        b.AddRange([0x21, 0xF4, 0xCB, 0x1F, .. BitConverter.GetBytes(1098u), .. BitConverter.GetBytes(11u)]);
        if (cpPrefix is { } p)
            b.Add(p);
        b.AddRange(BitConverter.GetBytes((uint)cp));
        b.AddRange(new byte[6]);
        return Frame(b.ToArray());
    }

    private static byte[] Leave(int entity) => Frame([0x47, 0x36, .. VarInt.Encode((uint)entity), 0x01, 0x00]);

    private static byte[] ZoneChange() => Frame([0x23, 0x36, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

    [Fact]
    public void A_spawn_with_a_legion_gives_server_legion_combat_power_class_and_weapon()
    {
        var (parser, pvp) = Setup();
        parser.ConsumeStream(Spawn(1380, "SolarFalcon", 2404, "ClanNorte", 164_332));

        var p = Assert.Single(pvp.Nearby.Snapshot());
        Assert.Equal("SolarFalcon", p.Name);
        Assert.Equal((ushort)2404, p.Server);
        Assert.Equal("ClanNorte", p.Legion);
        Assert.Equal(164_332, p.CombatPower);
        Assert.Equal(17, p.Weapon);
        Assert.Equal(JobClass.Ranger, JobClasses.FromRosterClass(p.ClassCode));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_a_legion_the_server_is_the_one_before_the_anchor(bool highBit)
    {
        var (parser, pvp) = Setup();
        parser.ConsumeStream(Spawn(2001, "Gatillo", 2404, null, 142_745, highBit: highBit));

        var p = Assert.Single(pvp.Nearby.Snapshot());
        Assert.Equal((ushort)2404, p.Server);
        Assert.Null(p.Legion);
        Assert.Equal(142_745, p.CombatPower);
    }

    [Theory]
    [InlineData((byte)0x01)]
    [InlineData((byte)0x03)]
    public void The_combat_power_can_come_after_a_small_prefix_byte(byte prefix)
    {
        var (parser, pvp) = Setup();
        parser.ConsumeStream(Spawn(2002, "Orlanda", 1406, "SUPRAMAX", 177_837, cpPrefix: prefix));

        Assert.Equal(177_837, Assert.Single(pvp.Nearby.Snapshot()).CombatPower);
    }

    [Fact]
    public void Leaving_the_view_removes_the_player_and_a_new_spawn_brings_them_back()
    {
        var (parser, pvp) = Setup();
        parser.ConsumeStream(Spawn(1380, "SolarFalcon", 2404, "ClanNorte", 164_332));
        parser.ConsumeStream(Spawn(1381, "Valentia", 2404, "Dragona", 206_080));
        parser.ConsumeStream(Leave(1380));

        Assert.Equal(["Valentia"], pvp.Nearby.Snapshot().Select(p => p.Name));

        parser.ConsumeStream(Spawn(1390, "SolarFalcon", 2404, "ClanNorte", 164_332));
        Assert.Equal(["SolarFalcon", "Valentia"], pvp.Nearby.Snapshot().Select(p => p.Name).Order());
    }

    [Fact]
    public void Changing_zone_clears_the_list()
    {
        var (parser, pvp) = Setup();
        parser.ConsumeStream(Spawn(1380, "SolarFalcon", 2404, "ClanNorte", 164_332));
        parser.ConsumeStream(ZoneChange());

        Assert.Empty(pvp.Nearby.Snapshot());
    }

    [Fact]
    public void A_later_record_with_less_in_it_keeps_what_the_earlier_spawn_said()
    {
        var t = new NearbyTracker();
        t.NoteSpawn(new NearbyPlayer(1380, "SolarFalcon", RangerClass, 2404, "ClanNorte", 164_332, 17, 1));
        t.NoteSpawn(new NearbyPlayer(1380, "SolarFalcon", 0, 0, null, 0, 0, 2));
        // Same player with a new entity id: replaces the old one.
        t.NoteSpawn(new NearbyPlayer(1500, "SolarFalcon", 0, 2404, null, 165_000, 0, 3));

        var p = Assert.Single(t.Snapshot());
        Assert.Equal(1500, p.Id);
        Assert.Equal("ClanNorte", p.Legion);
        Assert.Equal(165_000, p.CombatPower);
        Assert.Equal(17, p.Weapon);
        Assert.Equal(RangerClass, p.ClassCode);
    }

    [Fact]
    public void The_list_goes_to_Faro_when_it_changes_and_every_few_seconds()
    {
        var t = new NearbyTracker();
        Assert.NotNull(t.TakeForSend(1_000));                      // first time, even empty
        Assert.Null(t.TakeForSend(1_250));
        t.NoteSpawn(new NearbyPlayer(1, "A", 0, 2404, null, 100_000, 10, 1));
        Assert.Single(t.TakeForSend(1_500)!);
        t.NoteSpawn(new NearbyPlayer(1, "A", 0, 2404, null, 100_000, 10, 2));   // same again: no change
        Assert.Null(t.TakeForSend(1_750));
        Assert.NotNull(t.TakeForSend(1_500 + NearbyTracker.ResendMs));
        t.NoteGone(1);
        Assert.Empty(t.TakeForSend(1_500 + NearbyTracker.ResendMs + 250)!);
    }

    [Fact]
    public void An_attacker_who_did_not_land_the_kill_gets_server_and_legion_from_their_spawn()
    {
        const int me = 15580, killer = 14896, helper = 15069;
        var pvp = new PvpTracker();
        var deaths = new List<PvpDeath>();
        pvp.Died += deaths.Add;
        pvp.NoteSelf(me);
        pvp.NoteSpawn(helper, "Alianza", 29, [], 2404, "Fantas");
        pvp.NoteSpawn(killer, "SolarFalcon", RangerClass, [], 2404, "ClanNorte");
        pvp.NoteHit(me, helper, 17010000, 30_000, 1_000);
        pvp.NoteHit(me, killer, 14010000, 60_000, 1_200);
        pvp.NoteKill(me, killer, 2404, "SolarFalcon", "ClanNorte", 1_500, null);

        var death = Assert.Single(deaths);
        var a = Assert.Single(death.Attackers, x => x.Name == "Alianza");
        Assert.Equal((ushort)2404, a.Server);
        Assert.Equal("Fantas", a.Legion);
    }

    [Fact]
    public void Unreadable_records_give_nothing_instead_of_a_wrong_value()
    {
        byte[] junk = Enumerable.Range(0, 300).Select(i => (byte)(0x80 | (i % 64))).ToArray();
        Assert.Equal((ushort)0, NearbyTracker.ReadOrigin(junk, 10, junk.Length).Server);
        Assert.Equal(0, NearbyTracker.ReadCombatPower(junk, junk.Length));
        Assert.Equal(0, NearbyTracker.ReadCombatPower(junk, 5));
    }
}
