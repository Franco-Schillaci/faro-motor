using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using static Aion2DpsMeter.Tests.PacketBuilder;
using static Aion2DpsMeter.Tests.ParserTests;

namespace Aion2DpsMeter.Tests;

public class IdentityTests
{
    private const int Ally = 20002;

    private static byte[] Scope(int entity) => Frame([0x06, 0x38, .. Aion2DpsMeter.Core.Protocol.VarInt.Encode((uint)entity), 0x01, 0x02]);

    private static void Fight(CombatStore store, Aion2DpsMeter.Core.Protocol.PacketParser parser)
    {
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 500));
        parser.ConsumeStream(Damage(Ally, Mob, 15010000, 400));
    }

    [Fact]
    public void The_one_player_the_party_scope_records_are_about_is_you()
    {
        var (store, parser, _) = Setup();
        Fight(store, parser);
        for (int i = 0; i < 25; i++)
            parser.ConsumeStream(Scope(Me));
        parser.ConsumeStream(Scope(Ally)); // the odd record about someone else does not confuse it

        var r = store.GetDisplayRecord()!;
        Assert.True(r.Players.Single(p => p.ActorId == Me).IsLocal);
        Assert.False(r.Players.Single(p => p.ActorId == Ally).IsLocal);
    }

    [Fact]
    public void A_few_scope_records_are_not_enough()
    {
        var (store, parser, _) = Setup();
        Fight(store, parser);
        for (int i = 0; i < 5; i++)
            parser.ConsumeStream(Scope(Me));
        Assert.DoesNotContain(store.GetDisplayRecord()!.Players, p => p.IsLocal);
    }

    [Fact]
    public void The_self_record_outranks_a_guess()
    {
        var (store, parser, _) = Setup();
        Fight(store, parser);
        store.SetLocalIdentity(Ally, "ApexZ");
        for (int i = 0; i < 25; i++)
            parser.ConsumeStream(Scope(Me));
        Assert.True(store.GetDisplayRecord()!.Players.Single(p => p.ActorId == Ally).IsLocal);
    }

    [Fact]
    public void A_remembered_name_shows_until_the_game_names_you()
    {
        var store = new CombatStore(new GameData()) { RememberedLocalName = "Lolka" };
        var parser = new Aion2DpsMeter.Core.Protocol.PacketParser(store, new GameData());
        Fight(store, parser);
        for (int i = 0; i < 25; i++)
            parser.ConsumeStream(Scope(Me));
        Assert.Equal("Lolka", store.GetDisplayRecord()!.Players.Single(p => p.IsLocal).Name);

        // Playing another character of the account: the game's name replaces the remembered one.
        store.AppendNickname(Me, "Ab");
        Assert.Equal("Ab", store.GetDisplayRecord()!.Players.Single(p => p.IsLocal).Name);
    }

    [Fact]
    public void Loot_records_of_your_kills_identify_you()
    {
        var (store, parser, _) = Setup();
        Fight(store, parser);
        store.NotePartyScope(Me);
        store.NoteLootOwner(Mob, Me, "Lolka");
        var me = store.GetLocalProfile();
        Assert.Equal(Me, me!.EntityId);
    }
}
