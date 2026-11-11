using static Aion2DpsMeter.Tests.PacketBuilder;
using static Aion2DpsMeter.Tests.ParserTests;

namespace Aion2DpsMeter.Tests;

/// <summary>
/// Fork (Faro): each monster you kill is reported with its npc code, so Faro can tell you when the pet its souls
/// go to is already maxed.
/// </summary>
public class KillTests
{
    private const int Slime = 70001;
    private const int Other = 70002;
    private const int SlimeCode = 2600023;   // Abyss Sulfur Slime

    [Fact]
    public void A_mob_you_fought_reports_its_code_once_when_it_dies()
    {
        var (store, parser, _) = Setup();
        var kills = new List<(int Id, int Code)>();
        store.MobKilled += (id, code) => kills.Add((id, code));
        parser.ConsumeStream(MobSpawn(Slime, SlimeCode, 50_000));
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Slime, Skill, 60_000));
        parser.ConsumeStream(Death(Slime));
        parser.ConsumeStream(Death(Slime));   // the same death again
        Assert.Equal([(Slime, SlimeCode)], kills);
    }

    [Fact]
    public void A_mob_that_spawned_before_the_meter_started_is_reported_without_code()
    {
        var (store, parser, _) = Setup();
        var kills = new List<(int Id, int Code)>();
        store.MobKilled += (id, code) => kills.Add((id, code));
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Slime, Skill, 60_000));   // no spawn seen
        parser.ConsumeStream(Death(Slime));
        Assert.Equal([(Slime, 0)], kills);
    }

    [Fact]
    public void A_mob_nobody_in_your_party_hit_is_not_reported()
    {
        var (store, parser, _) = Setup();
        var kills = new List<(int Id, int Code)>();
        store.MobKilled += (id, code) => kills.Add((id, code));
        parser.ConsumeStream(MobSpawn(Other, SlimeCode, 50_000));
        parser.ConsumeStream(Death(Other));
        Assert.Empty(kills);
    }

    [Fact]
    public void A_respawned_mob_with_the_same_id_is_reported_again()
    {
        var (store, parser, _) = Setup();
        var kills = new List<(int Id, int Code)>();
        store.MobKilled += (id, code) => kills.Add((id, code));
        for (int i = 0; i < 2; i++)
        {
            parser.ConsumeStream(MobSpawn(Slime, SlimeCode, 50_000));
            parser.CurrentTimestampMs = 1_000 + i * 60_000;
            parser.ConsumeStream(Damage(Me, Slime, Skill, 60_000));
            parser.ConsumeStream(Death(Slime));
        }
        Assert.Equal(2, kills.Count);
    }
}
