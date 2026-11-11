using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion2DpsMeter.Core.Protocol;

/// <summary>
/// The two-byte opcodes the parser keys on. Game patches move these (the June 2026 update
/// shifted the <c>xx 36</c> spawn/death family by +1), so every entry is a list: old and new
/// values can be accepted side by side, and an <c>opcodes.json</c> next to the executable
/// overrides the defaults without a new release.
/// </summary>
public sealed class Opcodes
{
    /// <summary>Direct damage records (<c>04 38</c>).</summary>
    public List<string> Damage { get; set; } = ["04 38"];
    /// <summary>Damage-over-time and heal ticks (<c>05 38</c>).</summary>
    public List<string> Dot { get; set; } = ["05 38"];
    /// <summary>NPC / summon spawn (<c>40 36</c> before June 2026, <c>41 36</c> after).</summary>
    public List<string> MobSpawn { get; set; } = ["40 36", "41 36"];
    /// <summary>Another player's spawn and identity (<c>44 36</c> / <c>45 36</c>).</summary>
    public List<string> PlayerSpawn { get; set; } = ["44 36", "45 36"];
    /// <summary>The local player's own identity record (<c>33 36</c>).</summary>
    public List<string> SelfRecord { get; set; } = ["33 36"];
    /// <summary>Entity death (<c>41 36</c> before June 2026, <c>42 36</c> after). Gated by flag 3.</summary>
    public List<string> Death { get; set; } = ["41 36", "42 36"];
    /// <summary>Teleport; with entity id 0 it is the local player entering a zone.</summary>
    public List<string> ZoneChange { get; set; } = ["23 36"];
    /// <summary>Summon ownership / kill record carrying the owner's name (<c>04 8D</c>).</summary>
    public List<string> SummonOwnership { get; set; } = ["04 8D"];
    /// <summary>HP / MP update (<c>1B 92</c>).</summary>
    public List<string> HpUpdate { get; set; } = ["1B 92"];
    /// <summary>Party roster broadcast (<c>02 97</c>).</summary>
    public List<string> PartyRoster { get; set; } = ["02 97"];
    /// <summary>A record the server sends only about you and your party (<c>06 38</c>).</summary>
    public List<string> PartyScope { get; set; } = ["06 38"];
    /// <summary>
    /// Record terminator bytes (<c>?? 00 36</c>) that mark game traffic. Used to find the game
    /// connection before a port is locked, and by the nickname scans.
    /// </summary>
    public List<string> Signatures { get; set; } = ["0E 00 36", "06 00 36"];

    // ───── Character progress (fork for Faro; layouts from AcompanION, MIT: see Progress/ProgressDecoder.cs) ─────
    /// <summary>Combat power, sent at world entry (<c>56 36</c>).</summary>
    public List<string> CombatPower { get; set; } = ["56 36"];
    /// <summary>Gear score (item level), live as equipment changes (<c>1D 56</c>).</summary>
    public List<string> ItemLevel { get; set; } = ["1D 56"];
    /// <summary>Quest boards at world entry: Supply Emergency done today / this week (<c>56 E2</c>).</summary>
    public List<string> QuestBoards { get; set; } = ["56 E2"];
    /// <summary>One quest board entry changed: a Supply Emergency delivery (<c>3E E2</c>).</summary>
    public List<string> QuestEntry { get; set; } = ["3E E2"];
    /// <summary>Kinah gained today, snapshot and live (<c>A5 FF</c>, <c>A6 FF</c>).</summary>
    public List<string> KinahToday { get; set; } = ["A5 FF", "A6 FF"];
    /// <summary>Duty Missions done today (<c>1C 8D</c>).</summary>
    public List<string> DutyMissions { get; set; } = ["1C 8D"];
    /// <summary>Daeva Pass weekly points (<c>24 E3</c>).</summary>
    public List<string> PassUsed { get; set; } = ["24 E3"];
    /// <summary>Tickets, energy and weekly counters, snapshot at world entry (<c>0B 61</c>).</summary>
    public List<string> CountersSnapshot { get; set; } = ["0B 61"];
    /// <summary>One of those counters changed (<c>0C 61</c>).</summary>
    public List<string> CounterUpdate { get; set; } = ["0C 61"];
    /// <summary>The account's pet collection, at world entry (<c>00 90</c>).</summary>
    public List<string> PetsSnapshot { get; set; } = ["00 90"];
    /// <summary>Pet souls gained, a delta (<c>0D 90</c>).</summary>
    public List<string> PetSouls { get; set; } = ["0D 90"];
    /// <summary>A pet reached a new level (<c>14 90</c>).</summary>
    public List<string> PetLevel { get; set; } = ["14 90"];
    /// <summary>
    /// Fork (Faro): Abyss Points you hold (<c>AB FF &lt;u64 value&gt;&lt;u64 limit&gt;</c>). AcompanION saw the layout as
    /// <c>A9 FF</c> on an older build; on 28/10/2026 a capture showed <c>AB FF</c> going 38,998 → 39,014 as the game
    /// said «Abyss Points x16», with a limit of 2,500,000.
    /// </summary>
    public List<string> AbyssPoints { get; set; } = ["AB FF"];
    /// <summary>
    /// Fork (Faro): an inventory item's count changed (<c>1B 56</c>). Kinah is an item (930100031): its count is the
    /// kinah you hold. Found on 28/10/2026: it went up by what the game showed as «Kinah xN» after each kill.
    /// </summary>
    public List<string> InventoryItem { get; set; } = ["1B 56"];
    /// <summary>Fork (Faro): currency frames whose bytes go to the log, to read kinah and Abyss Points against the game.</summary>
    public List<string> CurrencyProbe { get; set; } = ["A7 FF", "A8 FF", "A9 FF", "AA FF", "AB FF", "AC FF"];
    /// <summary>
    /// Fork (Faro): a record only ever about you (<c>4A 36 &lt;entity&gt;</c>). Your entity id changes on some teleports
    /// without a new self record; in every capture of 28/10/2026 this named only the local player (3518 in the morning,
    /// 15580 in the Abyss), so PvP uses it to know which entity is you.
    /// </summary>
    public List<string> SelfStatus { get; set; } = ["4A 36"];
    /// <summary>
    /// Fork (Faro): an entity left your view (<c>47 36 &lt;entity&gt; 01 00</c>). In the captures of 28-30/10/2026 it was
    /// the last record about 34 players who walked away; the 20 who came back got a new spawn first.
    /// </summary>
    public List<string> Leave { get; set; } = ["47 36"];

    [JsonIgnore] internal byte[][] DamageB { get; private set; } = [];
    [JsonIgnore] internal byte[][] DotB { get; private set; } = [];
    [JsonIgnore] internal byte[][] MobSpawnB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PlayerSpawnB { get; private set; } = [];
    [JsonIgnore] internal byte[][] SelfRecordB { get; private set; } = [];
    [JsonIgnore] internal byte[][] DeathB { get; private set; } = [];
    [JsonIgnore] internal byte[][] ZoneChangeB { get; private set; } = [];
    [JsonIgnore] internal byte[][] SummonOwnershipB { get; private set; } = [];
    [JsonIgnore] internal byte[][] HpUpdateB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PartyRosterB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PartyScopeB { get; private set; } = [];
    [JsonIgnore] public byte[][] SignaturesB { get; private set; } = [];
    [JsonIgnore] internal byte[][] CombatPowerB { get; private set; } = [];
    [JsonIgnore] internal byte[][] ItemLevelB { get; private set; } = [];
    [JsonIgnore] internal byte[][] QuestBoardsB { get; private set; } = [];
    [JsonIgnore] internal byte[][] QuestEntryB { get; private set; } = [];
    [JsonIgnore] internal byte[][] KinahTodayB { get; private set; } = [];
    [JsonIgnore] internal byte[][] DutyMissionsB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PassUsedB { get; private set; } = [];
    [JsonIgnore] internal byte[][] CountersSnapshotB { get; private set; } = [];
    [JsonIgnore] internal byte[][] CounterUpdateB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PetsSnapshotB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PetSoulsB { get; private set; } = [];
    [JsonIgnore] internal byte[][] PetLevelB { get; private set; } = [];
    [JsonIgnore] internal byte[][] AbyssPointsB { get; private set; } = [];
    [JsonIgnore] internal byte[][] InventoryItemB { get; private set; } = [];
    [JsonIgnore] internal byte[][] CurrencyProbeB { get; private set; } = [];
    [JsonIgnore] internal byte[][] SelfStatusB { get; private set; } = [];
    [JsonIgnore] internal byte[][] LeaveB { get; private set; } = [];

    /// <summary>First bytes of every spawn opcode (mob and player), used by the embedded scans.</summary>
    [JsonIgnore] internal HashSet<byte> SpawnLeadBytes { get; private set; } = [];

    public static Opcodes Default { get; } = new Opcodes().Compile();

    public Opcodes Compile()
    {
        DamageB = Parse(Damage);
        DotB = Parse(Dot);
        MobSpawnB = Parse(MobSpawn);
        PlayerSpawnB = Parse(PlayerSpawn);
        SelfRecordB = Parse(SelfRecord);
        DeathB = Parse(Death);
        ZoneChangeB = Parse(ZoneChange);
        SummonOwnershipB = Parse(SummonOwnership);
        HpUpdateB = Parse(HpUpdate);
        PartyRosterB = Parse(PartyRoster);
        PartyScopeB = Parse(PartyScope);
        SignaturesB = Parse(Signatures);
        CombatPowerB = Parse(CombatPower);
        ItemLevelB = Parse(ItemLevel);
        QuestBoardsB = Parse(QuestBoards);
        QuestEntryB = Parse(QuestEntry);
        KinahTodayB = Parse(KinahToday);
        DutyMissionsB = Parse(DutyMissions);
        PassUsedB = Parse(PassUsed);
        CountersSnapshotB = Parse(CountersSnapshot);
        CounterUpdateB = Parse(CounterUpdate);
        PetsSnapshotB = Parse(PetsSnapshot);
        PetSoulsB = Parse(PetSouls);
        PetLevelB = Parse(PetLevel);
        AbyssPointsB = Parse(AbyssPoints);
        InventoryItemB = Parse(InventoryItem);
        CurrencyProbeB = Parse(CurrencyProbe);
        SelfStatusB = Parse(SelfStatus);
        LeaveB = Parse(Leave);
        SpawnLeadBytes = MobSpawnB.Concat(PlayerSpawnB).Select(b => b[0]).ToHashSet();
        return this;
    }

    public static Opcodes Load(string path)
    {
        if (!File.Exists(path))
            return Default;
        var loaded = JsonSerializer.Deserialize<Opcodes>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        return (loaded ?? new Opcodes()).Compile();
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Whether any of <paramref name="codes"/> sits at <paramref name="at"/>.</summary>
    internal static bool At(ReadOnlySpan<byte> data, int at, byte[][] codes)
    {
        foreach (var code in codes)
        {
            if (at >= 0 && at + code.Length <= data.Length && data.Slice(at, code.Length).SequenceEqual(code))
                return true;
        }
        return false;
    }

    public static bool ContainsAny(ReadOnlySpan<byte> data, byte[][] needles)
    {
        foreach (var n in needles)
        {
            if (data.IndexOf(n) >= 0)
                return true;
        }
        return false;
    }

    private static byte[][] Parse(List<string> codes) =>
        codes.Select(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => byte.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray())
            .Where(b => b.Length > 0)
            .ToArray();
}
