using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using static Aion2DpsMeter.Tests.PacketBuilder;
using static Aion2DpsMeter.Tests.ParserTests;

namespace Aion2DpsMeter.Tests;

/// <summary>
/// Fork (Faro): which dungeon a fight is labeled with. The party roster's dungeon stays after you leave it,
/// so a boss that knows its own dungeon wins over it.
/// </summary>
public class DungeonTests
{
    private const int Boss = 60001;
    private const int OldDungeon = 600072;   // Vakron Sky Island, from the last party roster
    private const int TrialDungeon = 910013; // Nightmare Altar (Hard)
    private const int AponosCode = 2701807;
    private const int VakronCode = 2300582;  // a boss with no dungeon in its data

    private static GameData Data(bool trialNamed = true)
    {
        var data = new GameData();
        data.Dungeons[OldDungeon] = "Vakron Sky Island";
        if (trialNamed)
            data.Dungeons[TrialDungeon] = "Nightmare Altar";
        data.Npcs[AponosCode] = new NpcInfo("Divided Aponos", true, false, TrialDungeon, "Hard", "Ascension Trial");
        data.Npcs[VakronCode] = new NpcInfo("Vakron", true, false, 0);
        return data;
    }

    private static EncounterRecord KillBoss(GameData data, int bossCode)
    {
        var (store, parser, ended) = Setup(data);
        store.SetCurrentDungeon(OldDungeon);
        parser.ConsumeStream(MobSpawn(Boss, bossCode, 1_000_000));
        parser.CurrentTimestampMs = 1_000;
        parser.ConsumeStream(Damage(Me, Boss, Skill, 500_000));
        parser.CurrentTimestampMs = 20_000;
        parser.ConsumeStream(Damage(Me, Boss, Skill, 500_000));
        parser.ConsumeStream(Death(Boss));
        store.Tick(20_000 + store.Options.BossKillGraceMs);
        return Assert.Single(ended);
    }

    [Fact]
    public void Boss_with_its_own_dungeon_replaces_the_old_roster_dungeon()
    {
        var r = KillBoss(Data(), AponosCode);
        Assert.Equal(TrialDungeon, r.DungeonId);
        Assert.Equal("Nightmare Altar", r.DungeonName);
        Assert.Equal("Hard", r.Tier);
    }

    [Fact]
    public void Unnamed_boss_dungeon_falls_back_to_its_category()
    {
        var r = KillBoss(Data(trialNamed: false), AponosCode);
        Assert.Equal("Ascension Trial", r.DungeonName);
    }

    [Fact]
    public void Boss_without_dungeon_data_keeps_the_roster_dungeon()
    {
        var r = KillBoss(Data(), VakronCode);
        Assert.Equal(OldDungeon, r.DungeonId);
        Assert.Equal("Vakron Sky Island", r.DungeonName);
    }

    [Fact]
    public void Extra_dungeon_names_only_fill_gaps()
    {
        string dir = Path.Combine(Path.GetTempPath(), "faro-dungeons-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "dungeons.en.json"), """{"600072":{"name":"Vakron Sky Island"}}""");
            File.WriteAllText(Path.Combine(dir, "dungeons.extra.en.json"),
                """{"600072":{"name":"Other name"},"910013":{"name":"Nightmare Altar"}}""");
            var data = GameData.Load(dir);
            Assert.Equal("Vakron Sky Island", data.DungeonName(600072));
            Assert.Equal("Nightmare Altar", data.DungeonName(910013));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
