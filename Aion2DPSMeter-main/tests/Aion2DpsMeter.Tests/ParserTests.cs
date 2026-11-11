using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.Protocol;
using static Aion2DpsMeter.Tests.PacketBuilder;

namespace Aion2DpsMeter.Tests;

public class ParserTests
{
    internal const int Me = 14957;
    internal const int Mob = 50001;
    internal const int Skill = 11020000; // a Gladiator class skill

    internal static (CombatStore Store, PacketParser Parser, List<EncounterRecord> Ended) Setup(GameData? data = null)
    {
        data ??= new GameData();
        var store = new CombatStore(data);
        var ended = new List<EncounterRecord>();
        store.EncounterEnded += ended.Add;
        return (store, new PacketParser(store, data), ended);
    }

    [Fact]
    public void Self_record_names_you_with_server_class_and_level()
    {
        // From a live capture (2026-10-04): Naicha, entity 14957, server 1304, Cleric, level 28.
        var (store, parser, _) = Setup();
        parser.ConsumeStream(Hex("3336ed745e91c12837064e616963686118051e000000011c0000007f0100007f0100001c000000d002040000000000"));
        var me = store.GetLocalProfile();
        Assert.NotNull(me);
        Assert.Equal("Naicha", me.Name);
        Assert.Equal(14957, me.EntityId);
        Assert.Equal(JobClass.Cleric, me.Job);
        Assert.Equal(28, me.Level);
        Assert.Equal((ushort)1304, me.ServerId);
    }

    [Fact]
    public void Kill_record_names_its_owner_on_any_server()
    {
        var (store, parser, _) = Setup();
        store.AppendDamage(new DamageEvent { ActorId = 1454, TargetId = 9999, SkillCode = 15010000, Damage = 10, TimestampMs = 1000 });
        byte[] record = [0x04, 0x8d, 0xec, 0xde, 0x02, 0x72, 0x28, 0xe9, 0x00, 0xae, 0x0b, 0x19, 0x05, 0x05,
            .. "ApexZ"u8, 0x06, .. "Ventus"u8, 0x01, 0x00, 0x00, 0x00];
        parser.ConsumeStream(record);
        Assert.Equal(1454, store.FindIdByNickname("ApexZ"));
    }

    [Fact]
    public void Damage_record_is_decoded()
    {
        var (store, parser, _) = Setup();
        parser.CurrentTimestampMs = 10_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 12345, crit: true));
        var r = store.GetDisplayRecord();
        Assert.NotNull(r);
        var p = Assert.Single(r.Players);
        Assert.Equal(Me, p.ActorId);
        Assert.Equal(12345, p.Damage);
        Assert.Equal(1, p.Crits);
        Assert.Equal(JobClass.Gladiator, p.Job);
    }

    [Fact]
    public void Damage_inside_a_bundle_is_decoded()
    {
        var (store, parser, _) = Setup();
        parser.CurrentTimestampMs = 10_000;
        byte[] inner = [.. Damage(Me, Mob, Skill, 500), .. Damage(Me, Mob, Skill, 700)];
        parser.ConsumeStream(Bundle(inner));
        Assert.Equal(1200, store.GetDisplayRecord()!.TotalDamage);
    }

    [Fact]
    public void Player_spawn_names_the_damage_dealer()
    {
        var (store, parser, _) = Setup();
        parser.CurrentTimestampMs = 10_000;
        parser.ConsumeStream(PlayerSpawn(Me, "Naicha"));
        parser.ConsumeStream(Damage(Me, Mob, Skill, 100));
        Assert.Equal("Naicha", store.GetDisplayRecord()!.Players[0].Name);
    }

    [Fact]
    public void Heal_ticks_count_for_the_healer()
    {
        var (store, parser, _) = Setup();
        parser.CurrentTimestampMs = 10_000;
        parser.ConsumeStream(Damage(Me, Mob, 17010000, 100));
        parser.ConsumeStream(Heal(Me, Me, 17020000, 4000));
        parser.ConsumeStream(Heal(Me, Me, 17030000, 1000, hot: true));
        var p = store.GetDisplayRecord()!.Players.Single();
        Assert.Equal(5000, p.Heal);
        Assert.Equal(2, p.HealSkills.Count);
    }

    [Fact]
    public void Mob_spawn_registers_npc_and_boss()
    {
        var data = new GameData();
        data.Npcs[3351057] = new NpcInfo("Test Boss", IsBoss: true, IsDummy: false, DungeonId: 0);
        var (store, parser, _) = Setup(data);
        parser.ConsumeStream(MobSpawn(Mob, 3351057, 1_000_000));
        Assert.True(store.IsBossEntity(Mob));
        parser.CurrentTimestampMs = 10_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 100));
        var r = store.GetDisplayRecord()!;
        Assert.Equal("Test Boss", r.Name);
        Assert.True(r.IsBoss);
        Assert.Equal(1_000_000, r.BossMaxHp);
    }
}
