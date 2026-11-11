using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Protocol;
using static Aion2DpsMeter.Tests.PacketBuilder;
using static Aion2DpsMeter.Tests.ParserTests;

namespace Aion2DpsMeter.Tests;

/// <summary>
/// Fork (Faro): ground effects nobody tied to their caster. Fire Temple, 01/11/2026: the party's Sorcerer (GRANITO) and
/// Cleric (LeoBrisa) left four «#id» rows (Bittercold Wind ×2, Fire Wall, Divine Aura) while other casts of the same
/// skills were tied fine. Such an entity hits with one skill only, its caster's.
/// </summary>
public class OrphanEffectTests
{
    private const int Sorc = 6812, Cleric = 88, Ranger = 13288;
    private const int FireWall = 15390000, FireWallTick = 15390010, Blaze = 15050000, Bittercold = 15280000;
    private const int DivineAura = 17150000, Judgment = 17040000, Deadshot = 14010000;

    /// <summary>A <c>41 36</c> skill-effect spawn (kind 0x1C) with nothing that names its caster.</summary>
    private static byte[] EffectSpawn(int entity) => Frame([0x41, 0x36, .. VarInt.Encode((uint)entity), 0x1C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

    private static (CombatStore Store, PacketParser Parser) Party()
    {
        var (store, parser, _) = Setup();
        parser.ConsumeStream(PlayerSpawn(Sorc, "GRANITO"));
        parser.ConsumeStream(PlayerSpawn(Cleric, "LeoBrisa"));
        parser.ConsumeStream(PlayerSpawn(Ranger, "KoruPPLx"));
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Sorc, Mob, Blaze, 2_000));
        parser.ConsumeStream(Damage(Sorc, Mob, FireWall, 1_000));
        parser.ConsumeStream(Damage(Cleric, Mob, Judgment, 500));
        parser.ConsumeStream(Damage(Ranger, Mob, Deadshot, 800));
        return (store, parser);
    }

    [Fact]
    public void Untied_ground_effects_go_to_their_caster()
    {
        var (store, parser) = Party();
        // A Divine Aura tied the normal way (its spawn named the caster).
        parser.ConsumeStream(Damage(30001, Mob, DivineAura, 100));
        store.LinkSummon(30001, Cleric);
        // The four orphans: same skill as their caster (Fire Wall also with its tick variant).
        parser.ConsumeStream(Damage(19559, Mob, FireWall, 300));
        parser.ConsumeStream(Damage(19559, Mob, FireWallTick, 50));
        parser.ConsumeStream(Damage(32566, Mob, DivineAura, 61));
        // Bittercold Wind: nobody used it directly, but they spawned as effects and there is one Sorcerer.
        parser.ConsumeStream(EffectSpawn(59878));
        parser.ConsumeStream(EffectSpawn(33532));
        parser.ConsumeStream(Damage(59878, Mob, Bittercold, 60));
        parser.ConsumeStream(Damage(33532, Mob, Bittercold, 31));

        var r = store.GetDisplayRecord()!;
        Assert.Equal(["GRANITO", "KoruPPLx", "LeoBrisa"], r.Players.Select(p => p.Name).Order());
        var sorc = r.Players.Single(p => p.Name == "GRANITO");
        Assert.Equal(2_000 + 1_000 + 300 + 50 + 60 + 31, sorc.Damage);
        Assert.Contains(sorc.Skills, s => s.Code == Bittercold && s.IsSummon && s.Damage == 91);
        Assert.Contains(sorc.Skills, s => s.Code == FireWall && s.IsSummon && s.Damage == 300);
        Assert.Contains(sorc.Skills, s => s.Code == FireWall && !s.IsSummon && s.Damage == 1_000);
        Assert.Equal(500 + 100 + 61, r.Players.Single(p => p.Name == "LeoBrisa").Damage);
        Assert.Equal(r.TotalDamage, r.Players.Sum(p => p.Damage));
    }

    [Fact]
    public void A_stranger_using_several_skills_stays_apart()
    {
        var (store, parser) = Party();
        parser.ConsumeStream(Damage(777, Mob, FireWall, 400));
        parser.ConsumeStream(Damage(777, Mob, Blaze, 400));

        var r = store.GetDisplayRecord()!;
        Assert.Contains(r.Players, p => p.ActorId == 777 && p.Damage == 800);
        Assert.Equal(3_000, r.Players.Single(p => p.Name == "GRANITO").Damage);
    }

    [Fact]
    public void Two_casters_of_the_same_skill_is_ambiguous()
    {
        var (store, parser) = Party();
        parser.ConsumeStream(PlayerSpawn(4000, "OtraSorc"));
        parser.ConsumeStream(Damage(4000, Mob, FireWall, 900));
        parser.ConsumeStream(Damage(19559, Mob, FireWall, 300));

        var r = store.GetDisplayRecord()!;
        Assert.Contains(r.Players, p => p.ActorId == 19559 && p.Damage == 300);
        Assert.Equal(3_000, r.Players.Single(p => p.Name == "GRANITO").Damage);
    }

    [Fact]
    public void Class_alone_is_not_enough_without_an_effect_spawn()
    {
        // Nobody used Bittercold Wind and the entity did not spawn as an effect: it could be a stranger Sorcerer.
        var (store, parser) = Party();
        parser.ConsumeStream(Damage(59878, Mob, Bittercold, 60));

        var r = store.GetDisplayRecord()!;
        Assert.Contains(r.Players, p => p.ActorId == 59878 && p.Damage == 60);
        Assert.Equal(3_000, r.Players.Single(p => p.Name == "GRANITO").Damage);
    }
}
