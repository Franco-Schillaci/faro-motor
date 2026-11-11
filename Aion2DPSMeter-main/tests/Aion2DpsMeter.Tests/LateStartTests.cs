using static Aion2DpsMeter.Tests.PacketBuilder;
using static Aion2DpsMeter.Tests.ParserTests;

namespace Aion2DpsMeter.Tests;

/// <summary>
/// Fork (Faro): the meter started mid-dungeon (28/10/2026, Faro opened before the last boss). Before it knew you were
/// a player, a heal or a hit on you read as damage and made you a target of the fight; the record leaves targets out of
/// the players, so your 3.2M damage on the boss was missing. An entity the meter learns is a player stops being a target.
/// </summary>
public class LateStartTests
{
    private const int Healer = 8922;
    private const int HealerSkill = 17010000;   // a Cleric class skill

    [Fact]
    public void You_hit_before_the_meter_knew_you_still_get_your_damage_once_you_attack()
    {
        var (store, parser, _) = Setup();
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Healer, Me, HealerSkill, 1_193));   // read as damage to you
        parser.CurrentTimestampMs = 2_000;
        parser.ConsumeStream(Damage(Healer, Mob, HealerSkill, 40_000));
        parser.ConsumeStream(Damage(Me, Mob, Skill, 60_000));
        var r = store.GetDisplayRecord();
        Assert.NotNull(r);
        Assert.Contains(r!.Players, p => p.ActorId == Me && p.Damage == 60_000);
        Assert.DoesNotContain(r.Targets, t => t.EntityId == Me);
    }

    [Fact]
    public void You_hit_before_the_meter_knew_you_still_get_your_damage_once_it_identifies_you()
    {
        var (store, parser, _) = Setup();
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Healer, Me, HealerSkill, 1_193));
        store.SetLocalIdentity(Me, "Nocturna");   // the self record (or the 06 38 vote) says who you are
        parser.CurrentTimestampMs = 2_000;
        parser.ConsumeStream(Damage(Healer, Mob, HealerSkill, 40_000));
        parser.ConsumeStream(Damage(Me, Mob, Skill, 60_000));
        var r = store.GetDisplayRecord();
        Assert.NotNull(r);
        var me = Assert.Single(r!.Players, p => p.ActorId == Me);
        Assert.Equal(60_000, me.Damage);
        Assert.True(me.IsLocal);
        Assert.DoesNotContain(r.Targets, t => t.EntityId == Me);
    }

    [Fact]
    public void Dying_after_having_been_a_target_is_not_reported_as_a_monster_kill()
    {
        var (store, parser, _) = Setup();
        var kills = new List<(int, int)>();
        store.MobKilled += (id, code) => kills.Add((id, code));
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Healer, Me, HealerSkill, 1_193));
        parser.ConsumeStream(Damage(Me, Mob, Skill, 60_000));
        parser.ConsumeStream(Death(Me));
        Assert.Empty(kills);
    }
}
