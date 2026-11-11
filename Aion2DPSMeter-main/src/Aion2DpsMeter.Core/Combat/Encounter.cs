namespace Aion2DpsMeter.Core.Combat;

/// <summary>Mutable aggregates of one fight. Owned by <see cref="CombatStore"/>, always used under its lock.</summary>
internal sealed class Encounter
{
    public Guid Id { get; } = Guid.NewGuid();
    public long StartMs { get; }
    public long LastMs { get; set; }
    /// <summary>Fork (Faro): last direct hit by you (not the party, not DoTs) on a target of this fight.</summary>
    public long LocalLastMs { get; set; }
    public DateTimeOffset StartedAt { get; }
    public bool HasBoss { get; set; }
    public int DungeonId { get; set; }

    /// <summary>When set, the fight ends at this time (its targets died; a short grace catches last ticks).</summary>
    public long? PendingEndMs { get; set; }
    public bool PendingIsBossKill { get; set; }

    public Dictionary<int, TargetAgg> Targets { get; } = new();
    /// <summary>Healing per raw healer id, then per (skill, is HoT).</summary>
    public Dictionary<int, Dictionary<(int Skill, bool Hot), HealAgg>> Heals { get; } = new();

    public Encounter(long startMs)
    {
        StartMs = startMs;
        LastMs = startMs;
        StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(startMs);
    }

    public bool IsEmpty => Targets.Count == 0;

    public void AddHeal(int healer, int skill, bool hot, long amount)
    {
        if (!Heals.TryGetValue(healer, out var perSkill))
            Heals[healer] = perSkill = new();
        if (!perSkill.TryGetValue((skill, hot), out var agg))
            perSkill[(skill, hot)] = agg = new HealAgg();
        agg.Amount += amount;
        agg.Ticks++;
    }

    /// <summary>Moves everything <paramref name="from"/> did onto <paramref name="to"/> (same character, or a summon's owner).</summary>
    public void MergeActor(int from, int to)
    {
        if (from == to)
            return;
        foreach (var target in Targets.Values)
        {
            if (!target.Actors.Remove(from, out var data))
                continue;
            if (target.Actors.TryGetValue(to, out var existing))
                existing.Absorb(data);
            else
                target.Actors[to] = data;
        }
        if (Heals.Remove(from, out var heals))
        {
            foreach (var (key, h) in heals)
            {
                if (!Heals.TryGetValue(to, out var mine))
                    Heals[to] = mine = new();
                if (!mine.TryGetValue(key, out var agg))
                    mine[key] = agg = new HealAgg();
                agg.Amount += h.Amount;
                agg.Ticks += h.Ticks;
            }
        }
    }
}

internal sealed class TargetAgg(int id, long firstMs)
{
    public int Id { get; } = id;
    public long FirstMs { get; set; } = firstMs;
    public long LastMs { get; set; } = firstMs;
    public long Total { get; set; }
    public bool Killed { get; set; }
    public Dictionary<int, ActorAgg> Actors { get; } = new();
}

internal sealed class ActorAgg
{
    public long Damage { get; set; }
    public long LifeSteal { get; set; }
    public Dictionary<(int Skill, bool Dot), SkillAgg> Skills { get; } = new();

    public void Add(DamageEvent e)
    {
        long total = e.TotalDamage;
        Damage += total;
        LifeSteal += e.HealAmount;
        var key = (e.SkillCode, e.IsDot);
        if (!Skills.TryGetValue(key, out var s))
            Skills[key] = s = new SkillAgg();
        s.Add(e);
    }

    public void Absorb(ActorAgg other)
    {
        Damage += other.Damage;
        LifeSteal += other.LifeSteal;
        foreach (var (key, s) in other.Skills)
        {
            if (Skills.TryGetValue(key, out var mine))
                mine.Absorb(s);
            else
                Skills[key] = s;
        }
    }
}

internal sealed class SkillAgg
{
    public int Hits;
    public long Damage;
    public long Min = long.MaxValue;
    public long Max;
    public int Crits, Back, Front, Parry, Perfect, Double, Smite, PowerShard, MultiHits;
    public long Heal;

    public void Add(DamageEvent e)
    {
        Hits++;
        Damage += e.TotalDamage;
        Min = Math.Min(Min, e.Damage);
        Max = Math.Max(Max, e.Damage);
        var f = e.Flags;
        if ((f & HitFlags.Critical) != 0) Crits++;
        if ((f & HitFlags.Back) != 0) Back++;
        if ((f & HitFlags.Front) != 0) Front++;
        if ((f & HitFlags.Parry) != 0) Parry++;
        if ((f & HitFlags.Perfect) != 0) Perfect++;
        if ((f & HitFlags.Double) != 0) Double++;
        if ((f & HitFlags.Smite) != 0) Smite++;
        if ((f & HitFlags.PowerShard) != 0) PowerShard++;
        if (e.MultiHitCount > 0) MultiHits++;
        Heal += e.HealAmount;
    }

    public void Absorb(SkillAgg o)
    {
        Hits += o.Hits;
        Damage += o.Damage;
        Min = Math.Min(Min, o.Min);
        Max = Math.Max(Max, o.Max);
        Crits += o.Crits;
        Back += o.Back;
        Front += o.Front;
        Parry += o.Parry;
        Perfect += o.Perfect;
        Double += o.Double;
        Smite += o.Smite;
        PowerShard += o.PowerShard;
        MultiHits += o.MultiHits;
        Heal += o.Heal;
    }
}

internal sealed class HealAgg
{
    public long Amount;
    public int Ticks;
}
