using Aion2DpsMeter.Core.Data;

namespace Aion2DpsMeter.Core.Combat;

public sealed record PartyMember(
    string Name,
    int Slot,
    int Level,
    int GearScore,
    long CombatPower,
    ushort ServerId,
    JobClass Job);

/// <summary>
/// A finished (or in-progress) fight, flattened for display and storage. This is what the
/// meter shows live and what the history keeps, so both read the same numbers.
/// </summary>
public sealed class EncounterRecord
{
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public long DurationMs { get; set; }
    /// <summary>Boss name, or the most-damaged target with "+N" for the rest.</summary>
    public string Name { get; set; } = "";
    /// <summary>NPC type id of the main target, 0 if unknown.</summary>
    public int NpcCode { get; set; }
    public bool IsBoss { get; set; }
    public bool IsTrainingDummy { get; set; }
    /// <summary>The main target died (all bosses for a boss fight).</summary>
    public bool Killed { get; set; }
    public bool InProgress { get; set; }
    public int DungeonId { get; set; }
    public string DungeonName { get; set; } = "";
    /// <summary>Fork (Faro): difficulty of the boss's dungeon ("Conquest [Normal]"…), empty if unknown.</summary>
    public string Tier { get; set; } = "";
    public long TotalDamage { get; set; }
    public long TotalHeal { get; set; }
    public long BossMaxHp { get; set; }
    public long BossCurrentHp { get; set; }
    public List<PlayerRecord> Players { get; set; } = new();
    public List<TargetRecord> Targets { get; set; } = new();

    public double DurationSeconds => Math.Max(DurationMs, 1000) / 1000.0;
    public double TotalDps => TotalDamage / DurationSeconds;
}

public sealed class PlayerRecord
{
    public int ActorId { get; set; }
    public string Name { get; set; } = "";
    public JobClass Job { get; set; }
    public bool IsLocal { get; set; }
    public long Damage { get; set; }
    public double Dps { get; set; }
    /// <summary>0-1 share of the encounter's damage.</summary>
    public double DamageShare { get; set; }
    public long Heal { get; set; }
    public double Hps { get; set; }
    public double HealShare { get; set; }
    public int Hits { get; set; }
    public int Crits { get; set; }
    public double CritRate => Hits == 0 ? 0 : (double)Crits / Hits;
    public long MaxHit { get; set; }
    public List<SkillRecord> Skills { get; set; } = new();
    public List<HealSkillRecord> HealSkills { get; set; } = new();
}

public sealed class SkillRecord
{
    public int Code { get; set; }
    public string Name { get; set; } = "";
    public bool IsDot { get; set; }
    /// <summary>Dealt by the player's summon / spirit rather than the player.</summary>
    public bool IsSummon { get; set; }
    public long Damage { get; set; }
    public int Hits { get; set; }
    public int Crits { get; set; }
    public int Back { get; set; }
    public int Perfect { get; set; }
    public int Double { get; set; }
    public int Parry { get; set; }
    public long Min { get; set; }
    public long Max { get; set; }
    public double Share { get; set; }
    public double CritRate => Hits == 0 ? 0 : (double)Crits / Hits;
    public double Average => Hits == 0 ? 0 : (double)Damage / Hits;
}

public sealed class HealSkillRecord
{
    public int Code { get; set; }
    public string Name { get; set; } = "";
    public bool IsHot { get; set; }
    public long Amount { get; set; }
    public int Ticks { get; set; }
}

public sealed class TargetRecord
{
    public int EntityId { get; set; }
    public int NpcCode { get; set; }
    public string Name { get; set; } = "";
    public bool IsBoss { get; set; }
    public bool Killed { get; set; }
    public long DamageTaken { get; set; }
    public long MaxHp { get; set; }
}
