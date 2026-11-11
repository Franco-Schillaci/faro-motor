using Aion2DpsMeter.Core.Data;

namespace Aion2DpsMeter.Core.Pvp;

/// <summary>An equipped item seen on a player: slot (1 weapon … 11 earring), item id and the level next to it (enchant).</summary>
public sealed record GearPiece(int Slot, int Item, int Enchant);

/// <summary>A player who hit you before you died (or who killed you).</summary>
public sealed record PvpAttacker(
    int Id,
    string? Name,
    JobClass Job,
    ushort Server,
    string? Legion,
    long Damage,
    int Hits,
    IReadOnlyList<(string Skill, long Damage)> Skills,
    IReadOnlyList<GearPiece> Gear,
    bool FinalBlow);

/// <summary>You died to a player: who killed you and everyone who hit you in the seconds before.</summary>
public sealed record PvpDeath(long At, int Victim, PvpAttacker Killer, IReadOnlyList<PvpAttacker> Attackers, long TotalDamage);

/// <summary>
/// Fork (Faro): who killed you in PvP. Reads only what the server sends the client: player spawns (name, class and
/// the gear other players can see), the damage records you receive, and the kill record that names the killer, its
/// server and legion. Your entity id changes on some teleports, so the <c>4A 36</c> record (only ever about you) says
/// which one is you now. Thread-safe; <see cref="Died"/> is raised outside the lock.
/// </summary>
public sealed class PvpTracker
{
    /// <summary>Hits taken in this window before a death count for it.</summary>
    public const int WindowMs = 20_000;
    /// <summary>The same death seen again (the record repeats across packets) within this time is ignored.</summary>
    private const int RepeatMs = 15_000;
    private const int MaxHits = 6_000;
    private const int MaxPlayers = 4_000;
    private const int MaxAttackers = 10;
    private const int MaxSkillsPerAttacker = 3;

    private sealed record Seen(int Id, string Name, uint ClassCode, IReadOnlyList<GearPiece> Gear);
    private readonly record struct Hit(long At, int Target, int Actor, int Skill, long Damage);

    private readonly object _lock = new();
    private readonly Dictionary<int, Seen> _byId = new();
    private readonly Dictionary<string, Seen> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ushort Server, string? Legion)> _origin = new(StringComparer.Ordinal);
    private readonly Queue<Hit> _hits = new();
    private readonly Dictionary<int, long> _deaths = new();
    private int? _self;

    /// <summary>Fork (Faro): the players in view right now, for «Enemigos cerca».</summary>
    public NearbyTracker Nearby { get; } = new();

    /// <summary>Skill code → name (empty when unknown).</summary>
    public Func<int, string>? SkillName { get; set; }

    /// <summary>The name the meter knows for an entity, for players whose spawn came before the meter started.</summary>
    public Func<int, string?>? NameOf { get; set; }

    /// <summary>Raised when you die to a player.</summary>
    public event Action<PvpDeath>? Died;

    /// <summary>Diagnostics for the log.</summary>
    public event Action<string>? Trace;

    /// <summary>Your entity id as the <c>4A 36</c> record states it, or null before the first one.</summary>
    public int? SelfId
    {
        get { lock (_lock) return _self; }
    }

    public void NoteSelf(int id)
    {
        if (id < 1)
            return;
        bool changed;
        lock (_lock)
        {
            changed = _self != id;
            _self = id;
        }
        if (changed)
            Trace?.Invoke($"pvp: you are entity {id}");
    }

    /// <summary>
    /// A player spawned: their name, class code, the gear the server shows and, when the spawn carries them, their
    /// server and legion (so attackers who did not land the kill also get theirs).
    /// </summary>
    public void NoteSpawn(int id, string name, uint classCode, IReadOnlyList<GearPiece> gear, ushort server = 0, string? legion = null)
    {
        lock (_lock)
        {
            if (_byId.Count >= MaxPlayers)
            {
                _byId.Clear();
                _byName.Clear();
            }
            if (server != 0)
            {
                if (_origin.Count >= MaxPlayers)
                    _origin.Clear();
                _origin[name] = (server, string.IsNullOrEmpty(legion) ? null : legion);
            }
            // The same player also turns up inside other records whose layout carries no class or gear here:
            // keep what the full spawn said.
            if (_byName.TryGetValue(name, out var old))
            {
                if (gear.Count == 0)
                    gear = old.Gear;
                if (classCode == 0)
                    classCode = old.ClassCode;
            }
            var seen = new Seen(id, name, classCode, gear);
            _byId[id] = seen;
            _byName[name] = seen;
        }
    }

    /// <summary>
    /// A damage record: kept for a while when it is a player hitting someone (a spawn named them, or the meter knows
    /// them as a player: <paramref name="knownPlayer"/>).
    /// </summary>
    public void NoteHit(int target, int actor, int skill, long damage, long at, bool knownPlayer = false)
    {
        if (damage <= 0 || target == actor)
            return;
        lock (_lock)
        {
            if (!knownPlayer && !_byId.ContainsKey(actor))
                return;
            _hits.Enqueue(new Hit(at, target, actor, skill, damage));
            while (_hits.Count > MaxHits || (_hits.Count > 0 && at - _hits.Peek().At > WindowMs * 2))
                _hits.Dequeue();
        }
    }

    /// <summary>
    /// A kill record: <paramref name="killerName"/> (entity <paramref name="killer"/>, its server and legion) killed
    /// <paramref name="victim"/>. When the victim is you, <see cref="Died"/> reports it. <paramref name="localFallback"/>
    /// is who the meter thinks you are, used only before the first <c>4A 36</c> record.
    /// </summary>
    public void NoteKill(int victim, int killer, ushort server, string killerName, string? legion, long at, int? localFallback)
    {
        PvpDeath? death = null;
        lock (_lock)
        {
            if (_origin.Count >= MaxPlayers)
                _origin.Clear();
            _origin[killerName] = (server, string.IsNullOrEmpty(legion) ? null : legion);

            int? me = _self ?? localFallback;
            if (me != victim)
                return;
            if (_deaths.TryGetValue(victim, out long last) && Math.Abs(at - last) < RepeatMs)
                return;
            _deaths[victim] = at;
            death = BuildDeath(victim, killer, killerName, at);
        }
        Trace?.Invoke($"pvp: killed by '{death.Killer.Name}' (server {death.Killer.Server}, legion '{death.Killer.Legion}'), "
            + $"{death.Attackers.Count} attacker(s), {death.TotalDamage} damage taken");
        Died?.Invoke(death);
    }

    private PvpDeath BuildDeath(int victim, int killer, string killerName, long at)
    {
        var byActor = new Dictionary<int, (long Damage, int Hits, Dictionary<string, long> Skills)>();
        var jobBySkill = new Dictionary<int, JobClass>();
        foreach (var h in _hits)
        {
            if (h.Target != victim || h.At < at - WindowMs || h.At > at + 1_000)
                continue;
            if (!jobBySkill.ContainsKey(h.Actor) && JobClasses.FromSkill(h.Skill) is var job && job != JobClass.Unknown)
                jobBySkill[h.Actor] = job;
            if (!byActor.TryGetValue(h.Actor, out var a))
                a = (0, 0, new Dictionary<string, long>());
            string skill = SkillName?.Invoke(h.Skill) is { Length: > 0 } n ? n : $"#{h.Skill}";
            a.Skills[skill] = a.Skills.GetValueOrDefault(skill) + h.Damage;
            byActor[h.Actor] = (a.Damage + h.Damage, a.Hits + 1, a.Skills);
        }

        // The kill record names the killer; its id there can differ from the one its hits carry.
        int killerActor = byActor.ContainsKey(killer) ? killer
            : byActor.Keys.FirstOrDefault(id => _byId.TryGetValue(id, out var s) && s.Name == killerName);

        PvpAttacker Make(int id, (long Damage, int Hits, Dictionary<string, long> Skills) a, bool final)
        {
            _byId.TryGetValue(id, out var seen);
            string? name = final ? killerName : seen?.Name ?? NameOf?.Invoke(id);
            if (name is not null && (seen is null || seen.Name != name))
                _byName.TryGetValue(name, out seen);
            var origin = name is not null && _origin.TryGetValue(name, out var o) ? o : default;
            // No spawn seen (it came before the meter started): the class its skills belong to.
            var job = seen is null ? JobClass.Unknown : JobClasses.FromRosterClass(seen.ClassCode);
            if (job == JobClass.Unknown)
                job = jobBySkill.GetValueOrDefault(id);
            return new PvpAttacker(
                id,
                name,
                job,
                origin.Server,
                origin.Legion,
                a.Damage,
                a.Hits,
                a.Skills.OrderByDescending(kv => kv.Value).Take(MaxSkillsPerAttacker).Select(kv => (kv.Key, kv.Value)).ToList(),
                seen?.Gear ?? [],
                final);
        }

        var attackers = byActor
            .OrderByDescending(kv => kv.Value.Damage)
            .Take(MaxAttackers)
            .Select(kv => Make(kv.Key, kv.Value, kv.Key == killerActor && killerActor != 0))
            .ToList();
        int killerId = killerActor != 0 ? killerActor : killer;
        var killerEntry = attackers.FirstOrDefault(a => a.FinalBlow)
            ?? Make(killerId, byActor.TryGetValue(killerId, out var k) ? k : (0L, 0, new Dictionary<string, long>()), true);
        if (!attackers.Contains(killerEntry))
            attackers.Add(killerEntry);
        long total = byActor.Values.Sum(a => a.Damage);
        return new PvpDeath(at, victim, killerEntry, attackers, total);
    }

    /// <summary>
    /// Equipped items in a player spawn: <c>&lt;u32 item&gt; &lt;u8 enchant&gt; &lt;u8 slot&gt; &lt;8 zero bytes&gt;</c>,
    /// records of varying length, slots 1-11. The item id's first digit must match the slot (1 weapon, 2 armour, 3
    /// accessory), which keeps look-alike bytes out. The first item per slot wins.
    /// </summary>
    public static List<GearPiece> ReadGear(ReadOnlySpan<byte> data, int start, int end)
    {
        var gear = new List<GearPiece>();
        var slots = new HashSet<int>();
        end = Math.Min(end, data.Length);
        for (int i = Math.Max(0, start); i + 14 <= end && slots.Count < 11; i++)
        {
            uint item = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4));
            int enchant = data[i + 4], slot = data[i + 5];
            if (item < 100_000_000 || item > 399_999_999 || enchant > 30 || slot < 1 || slot > 11)
                continue;
            int kind = (int)(item / 100_000_000);
            int expected = slot == 1 ? 1 : slot <= 8 ? 2 : 3;
            if (kind != expected || slots.Contains(slot))
                continue;
            if (data.Slice(i + 6, 8).IndexOfAnyExcept((byte)0) >= 0)
                continue;
            slots.Add(slot);
            gear.Add(new GearPiece(slot, (int)item, enchant));
            i += 13;
        }
        gear.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        return gear;
    }
}
