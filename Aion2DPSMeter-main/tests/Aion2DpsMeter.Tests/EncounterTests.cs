using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.History;
using static Aion2DpsMeter.Tests.PacketBuilder;
using static Aion2DpsMeter.Tests.ParserTests;

namespace Aion2DpsMeter.Tests;

public class EncounterTests
{
    private const int BossCode = 3351057;
    private const int TrashCode = 2000002;
    private const int Boss = 60001;

    private static GameData Data()
    {
        var data = new GameData();
        data.Npcs[BossCode] = new NpcInfo("Test Boss", true, false, 0);
        data.Npcs[TrashCode] = new NpcInfo("Draconute Ranger", false, false, 0);
        return data;
    }

    [Fact]
    public void Idle_gap_ends_the_fight()
    {
        var (store, parser, ended) = Setup();
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 100));
        parser.CurrentTimestampMs = 5_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 100));

        store.Tick(5_000 + store.Options.IdleTimeoutMs + 1);

        var r = Assert.Single(ended);
        Assert.Equal(200, r.TotalDamage);
        Assert.Equal(4_000, r.DurationMs);
        Assert.Equal(50, r.Players[0].Dps);
    }

    [Fact]
    public void Pulling_a_boss_splits_off_the_trash_fight()
    {
        var (store, parser, ended) = Setup(Data());
        parser.ConsumeStream(MobSpawn(Mob, TrashCode, 5000));
        parser.ConsumeStream(MobSpawn(Boss, BossCode, 1_000_000));

        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 300));
        parser.CurrentTimestampMs = 3_000;
        parser.ConsumeStream(Damage(Me, Boss, Skill, 9000));

        var trash = Assert.Single(ended);
        Assert.Equal("Draconute Ranger", trash.Name);
        Assert.False(trash.IsBoss);

        var live = store.GetDisplayRecord()!;
        Assert.True(live.InProgress);
        Assert.Equal("Test Boss", live.Name);
        Assert.Equal(9000, live.TotalDamage);
    }

    [Fact]
    public void Boss_death_ends_the_fight_after_the_grace()
    {
        var (store, parser, ended) = Setup(Data());
        parser.ConsumeStream(MobSpawn(Boss, BossCode, 1_000_000));
        for (long t = 1_000; t <= 61_000; t += 10_000)
        {
            parser.CurrentTimestampMs = t;
            parser.ConsumeStream(Damage(Me, Boss, Skill, 100_000));
        }
        parser.ConsumeStream(Death(Boss));

        store.Tick(61_000 + store.Options.BossKillGraceMs - 1);
        Assert.Empty(ended);
        store.Tick(61_000 + store.Options.BossKillGraceMs);

        var r = Assert.Single(ended);
        Assert.True(r.Killed);
        Assert.True(r.IsBoss);
        Assert.Equal(60_000, r.DurationMs);
    }

    [Fact]
    public void Boss_fight_counts_only_boss_damage()
    {
        var (store, parser, _) = Setup(Data());
        parser.ConsumeStream(MobSpawn(Boss, BossCode, 1_000_000));
        parser.ConsumeStream(MobSpawn(Mob, TrashCode, 5000));
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Boss, Skill, 1000));
        parser.ConsumeStream(Damage(Me, Mob, Skill, 400));
        Assert.Equal(1000, store.GetDisplayRecord()!.TotalDamage);
    }

    [Fact]
    public void Cleared_trash_pack_ends_unless_a_new_target_is_hit_when_split_is_off()
    {
        var (store, parser, ended) = Setup(Data());
        // Tests the trash-clear grace alone; with SplitOnTargetChange on (the default),
        // hitting Mob + 1 below would start a new fight instead (see the next test).
        store.Options.SplitOnTargetChange = false;
        parser.ConsumeStream(MobSpawn(Mob, TrashCode, 5000));
        parser.ConsumeStream(MobSpawn(Mob + 1, TrashCode, 5000));
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 5000));
        parser.ConsumeStream(Death(Mob));
        // The next mob of the pack is hit within the grace: same fight.
        parser.CurrentTimestampMs = 3_000;
        parser.ConsumeStream(Damage(Me, Mob + 1, Skill, 5000));
        store.Tick(1_000 + store.Options.TrashClearGraceMs + 10);
        Assert.Empty(ended);

        parser.ConsumeStream(Death(Mob + 1));
        store.Tick(3_000 + store.Options.TrashClearGraceMs);
        var r = Assert.Single(ended);
        Assert.Equal(10_000, r.TotalDamage);
        Assert.True(r.Killed);
    }

    [Fact]
    public void Hitting_a_new_target_after_everything_died_starts_a_new_fight()
    {
        var (store, parser, ended) = Setup(Data());
        Assert.True(store.Options.SplitOnTargetChange); // the default
        parser.ConsumeStream(MobSpawn(Mob, TrashCode, 5000));
        parser.ConsumeStream(MobSpawn(Mob + 1, TrashCode, 5000));
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 5000));
        parser.ConsumeStream(Death(Mob));

        // Within the trash-clear grace, but everything we fought is dead: the old fight ends now.
        parser.CurrentTimestampMs = 3_000;
        parser.ConsumeStream(Damage(Me, Mob + 1, Skill, 5000));
        var first = Assert.Single(ended);
        Assert.Equal(5000, first.TotalDamage);
        Assert.True(first.Killed);

        var live = store.GetDisplayRecord()!;
        Assert.True(live.InProgress);
        Assert.Equal(5000, live.TotalDamage);

        parser.ConsumeStream(Death(Mob + 1));
        store.Tick(3_000 + store.Options.TrashClearGraceMs);
        Assert.Equal(2, ended.Count);
        Assert.Equal(5000, ended[1].TotalDamage);
        Assert.True(ended[1].Killed);
    }

    [Fact]
    public void Party_damage_is_split_per_player_and_summons_go_to_their_owner()
    {
        var (store, parser, _) = Setup();
        const int Ally = 20002, Spirit = 70007;
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(PlayerSpawn(Me, "Naicha"));
        parser.ConsumeStream(PlayerSpawn(Ally, "ApexZ"));
        parser.ConsumeStream(Damage(Me, Mob, Skill, 600));
        parser.ConsumeStream(Damage(Ally, Mob, 15010000, 300));
        store.LinkSummon(Spirit, Ally);
        parser.ConsumeStream(Damage(Spirit, Mob, 16010000, 100));

        var r = store.GetDisplayRecord()!;
        Assert.Equal(2, r.Players.Count);
        Assert.Equal("Naicha", r.Players[0].Name);
        Assert.Equal(0.6, r.Players[0].DamageShare, 3);
        var ally = r.Players[1];
        Assert.Equal(400, ally.Damage);
        Assert.Contains(ally.Skills, s => s.IsSummon);
    }

    [Fact]
    public void Zone_change_after_a_lull_ends_the_fight_but_not_mid_combat()
    {
        var (store, parser, ended) = Setup();
        parser.CurrentTimestampMs = 1_000_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 100));
        store.NoteZoneChange(1_000_500); // a knockback teleport mid-fight
        Assert.Empty(ended);
        store.NoteZoneChange(1_010_000);
        Assert.Single(ended);
    }

    [Fact]
    public void Reset_ends_and_clears_the_meter()
    {
        var (store, parser, ended) = Setup();
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Mob, Skill, 100));
        store.Reset();
        Assert.Single(ended);
        Assert.Null(store.GetDisplayRecord());
    }

    [Fact]
    public void History_round_trips_a_fight()
    {
        var (store, parser, ended) = Setup(Data());
        parser.ConsumeStream(MobSpawn(Boss, BossCode, 1_000_000));
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(PlayerSpawn(Me, "Naicha"));
        parser.ConsumeStream(Damage(Me, Boss, Skill, 1000));
        parser.CurrentTimestampMs = 11_000;
        parser.ConsumeStream(Damage(Me, Boss, Skill, 1000));
        store.Reset();

        string db = Path.Combine(Path.GetTempPath(), $"a2meter-test-{Guid.NewGuid():N}.db");
        try
        {
            var repo = new HistoryRepository(db);
            repo.Save(ended[0]);

            var list = repo.List(new HistoryQuery { NameContains = "boss" });
            var row = Assert.Single(list);
            Assert.Equal("Test Boss", row.Name);
            Assert.True(row.IsBoss);

            var loaded = repo.Get(row.Id)!;
            Assert.Equal(2000, loaded.TotalDamage);
            Assert.Equal("Naicha", loaded.Players[0].Name);
            Assert.Equal(JobClass.Gladiator, loaded.Players[0].Job);

            var monster = Assert.Single(repo.Monsters());
            Assert.Equal(1, monster.Fights);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(db);
        }
    }
}
