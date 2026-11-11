namespace Aion2DpsMeter.Core.Combat;

[Flags]
public enum HitFlags
{
    None = 0,
    Critical = 1 << 0,
    Back = 1 << 1,
    Front = 1 << 2,
    Parry = 1 << 3,
    Perfect = 1 << 4,
    Double = 1 << 5,
    Smite = 1 << 6,
    PowerShard = 1 << 7,
}

/// <summary>One damage record decoded from a <c>04 38</c> or <c>05 38</c> packet.</summary>
public sealed class DamageEvent
{
    public long TimestampMs { get; set; }
    public int ActorId { get; set; }
    public int TargetId { get; set; }
    public int SkillCode { get; set; }
    /// <summary>The main hit, without the multi-hit follow-ups.</summary>
    public int Damage { get; set; }
    public int MultiHitCount { get; set; }
    public int MultiHitDamage { get; set; }
    /// <summary>Life steal attached to the hit.</summary>
    public int HealAmount { get; set; }
    public bool IsDot { get; set; }
    public int DamageType { get; set; }
    public HitFlags Flags { get; set; }

    public long TotalDamage => (long)Damage + MultiHitDamage;
    public bool IsCrit => (Flags & HitFlags.Critical) != 0;

    public override string ToString() =>
        $"{ActorId} -> {TargetId} skill {SkillCode} dmg {TotalDamage}{(IsDot ? " (dot)" : "")} [{Flags}]";
}
