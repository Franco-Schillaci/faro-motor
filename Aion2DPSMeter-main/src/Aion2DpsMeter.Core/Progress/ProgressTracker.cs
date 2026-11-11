// Fork for Faro. Attribution rules ported from AcompanION's state.rs (MIT, Copyright (c) 2026
// Eduardo Leal; the full notice is in ProgressDecoder.cs).

namespace Aion2DpsMeter.Core.Progress;

/// <summary>What one connection is doing: the character it plays and the events that came before its identity.</summary>
public sealed class ProgressSession
{
    internal (ushort Server, string Name)? Character;
    internal readonly List<(long At, ProgressEvent Event)> Pending = new();
    /// <summary>Fork (Faro): a world entry (counter snapshot or identity) was seen on this connection.</summary>
    internal bool WorldEntrySeen;
}

/// <summary>A copy of one character's progress, safe to hand to another thread.</summary>
/// <param name="Counters">Every counter the server sent: what is left (or ms left), when it was read, and the
/// record's second number if it had one (see <see cref="CounterRecord.Extra"/>).</param>
/// <param name="Gains">What was done or gained, by key: <c>supply_daily</c>, <c>supply_weekly</c>, <c>duty_daily</c>,
/// <c>kinah_today</c> and <c>pass_&lt;id&gt;</c> (Daeva Pass points used).</param>
public sealed record ProgressSnapshot(
    string Name,
    ushort Server,
    uint ClassCode,
    int Level,
    int? ItemLevel,
    uint? CombatPower,
    long SeenMs,
    IReadOnlyList<(uint Id, ulong Value, bool Ms, long At, uint? Extra)> Counters,
    IReadOnlyDictionary<string, (ulong Value, long At)> Gains);

/// <summary>The account's pet collection: the pet out (as of the last world entry) and each pet's level and souls.</summary>
/// <param name="Pets">Every pet the server listed: level (0 = not unlocked yet) and souls toward the next level.</param>
/// <param name="At">Capture time (ms) of the last change.</param>
public sealed record PetsSnapshot(uint Active, IReadOnlyList<(uint Id, uint Level, uint Souls)> Pets, long At);

/// <summary>
/// Fork (Faro): souls gained or a level reached while the collection is not known yet (the meter started with the game
/// already in the world). Faro adds them to the collection it saved.
/// </summary>
/// <param name="Souls">Souls gained per pet (a delta).</param>
/// <param name="Level">The pet that reached a level and that level, or null.</param>
public sealed record PetsDelta(IReadOnlyList<(uint Id, uint Amount)> Souls, (uint Id, uint Level)? Level, long At);

/// <summary>
/// Keeps each character's tickets, energy, weekly counters and daily gains as the server reports them.
/// The world-entry data (combat power, counters, boards, kinah) arrives <i>before</i> the identity frame it
/// belongs to, so it is held per connection and filed under the identity that follows. A counter snapshot
/// also unbinds the connection: a character (maybe another one) is entering the world.
/// </summary>
public sealed class ProgressTracker
{
    public const string SupplyDaily = "supply_daily";
    public const string SupplyWeekly = "supply_weekly";
    public const string DutyDaily = "duty_daily";
    public const string KinahToday = "kinah_today";
    /// <summary>Fork (Faro): Abyss Points (<c>A9 FF</c>) and the limit that comes with them.</summary>
    public const string AbyssPoints = "abyss_points";
    public const string AbyssLimit = "abyss_limit";
    /// <summary>Fork (Faro): the kinah you hold (inventory), the one that goes up with each monster's loot.</summary>
    public const string KinahHeld = "kinah_held";
    private const int PendingMax = 256;

    private sealed class Character
    {
        public required string Name;
        public required ushort Server;
        public uint ClassCode;
        public int Level;
        public int? ItemLevel;
        public uint? CombatPower;
        public long SeenMs;
        public readonly SortedDictionary<uint, (ulong Value, bool Ms, long At, uint? Extra)> Counters = new();
        public readonly SortedDictionary<string, (ulong Value, long At, uint? Group)> Gains = new(StringComparer.Ordinal);
    }

    private readonly object _lock = new();
    private readonly Dictionary<(ushort, string), Character> _chars = new();
    private (ushort, string)? _current;

    /// <summary>Raised (on the capture thread) after a character's progress changed.</summary>
    public event Action<ProgressSnapshot>? Changed;
    /// <summary>One line per notable event, for the log.</summary>
    public event Action<string>? Trace;

    /// <summary>
    /// Also log the bytes of every counter frame (<c>0B 61</c> / <c>0C 61</c>), parsed or not. Off by default; it was
    /// used to check what the counters' second number means (the extra shown in light blue in the game).
    /// </summary>
    public bool LogCounterBytes { get; set; }

    /// <summary>Logs one counter frame's bytes when <see cref="LogCounterBytes"/> is on.</summary>
    public void NoteCounterBytes(ReadOnlySpan<byte> body, bool parsed)
    {
        if (LogCounterBytes)
            Trace?.Invoke($"progress: bytes{(parsed ? "" : " (did not parse)")} {Convert.ToHexString(body)}");
    }

    /// <summary>
    /// Fork (Faro): log the bytes of currency frames (<c>A7 FF</c>…<c>AA FF</c>) to read the Abyss Points against the
    /// game; at most <see cref="CurrencyBytesMax"/> per session so a long farm does not fill the log.
    /// </summary>
    public bool LogCurrencyBytes { get; set; } = true;
    public const int CurrencyBytesMax = 200;
    private int _currencyBytes;
    public void NoteCurrencyBytes(ReadOnlySpan<byte> body, bool parsed)
    {
        if (LogCurrencyBytes && Interlocked.Increment(ref _currencyBytes) <= CurrencyBytesMax)
            Trace?.Invoke($"progress: currency bytes{(parsed ? "" : " (did not parse)")} {Convert.ToHexString(body[..Math.Min(body.Length, 64)])}");
    }

    /// <summary>The character last seen entering the world, if any.</summary>
    public ProgressSnapshot? Current()
    {
        lock (_lock)
            return _current is { } k && _chars.TryGetValue(k, out var c) ? Snap(c) : null;
    }

    // The pet collection is the account's (every character gets the same list), so it is kept once, not per character.
    // ponytail: two accounts played on one PC would share it; the last world entry wins.
    private readonly SortedDictionary<uint, (uint Level, uint Souls)> _pets = new();
    private uint _petOut;
    private long _petsAt;
    private bool _havePets;

    /// <summary>Raised (on the capture thread) after the pet collection changed.</summary>
    public event Action<PetsSnapshot>? PetsChanged;
    /// <summary>Fork (Faro): raised for soul gains and level-ups that arrive before the collection is known.</summary>
    public event Action<PetsDelta>? PetsGainedBeforeCollection;

    /// <summary>The pet collection, once a world entry sent it.</summary>
    public PetsSnapshot? CurrentPets()
    {
        lock (_lock)
            return _havePets ? SnapPets() : null;
    }

    public void Apply(ProgressSession conn, long at, ProgressEvent ev)
    {
        if (ev is PetsEvent or PetSoulsEvent or PetLevelEvent)
        {
            ApplyPets(at, ev);
            return;
        }
        ProgressSnapshot? changed = null;
        string? trace = null;
        lock (_lock)
        {
            switch (ev)
            {
                case IdentityEvent id:
                {
                    conn.WorldEntrySeen = true;
                    var key = (id.Server, id.Name);
                    conn.Character = key;
                    if (!_chars.TryGetValue(key, out var c))
                        _chars[key] = c = new Character { Name = id.Name, Server = id.Server };
                    c.ClassCode = id.ClassCode;
                    c.Level = id.Level;
                    c.SeenMs = Math.Max(c.SeenMs, at);
                    int held = conn.Pending.Count;
                    foreach (var (pAt, pEv) in conn.Pending)
                        Record(c, pAt, pEv);
                    conn.Pending.Clear();
                    // The identity's own gear score is the latest: it trails the frames it was held with.
                    if (id.ItemLevel is int gs)
                        c.ItemLevel = gs;
                    bool swap = _current != key;
                    _current = key;
                    changed = Snap(c);
                    if (swap || held > 0)
                        trace = $"progress: {id.Name} [{id.Server}] class {id.ClassCode}, level {id.Level}, gear {id.ItemLevel}; {held} held event(s) applied";
                    break;
                }
                case ItemLevelEvent when conn.Character is null:
                    break; // the next identity carries the same value
                case CombatPowerEvent or CountersEvent:
                    if (ev is CountersEvent counters)
                    {
                        conn.Character = null;
                        conn.WorldEntrySeen = true;
                        trace = "progress: counters " + string.Join(' ', counters.Records.Select(r => $"{r.Id}={r.Value}{(r.Ms ? "ms" : "")}{Extra(r)}"));
                    }
                    Hold(conn, at, ev);
                    break;
                default:
                    if (conn.Character is { } k && _chars.TryGetValue(k, out var bound))
                    {
                        Record(bound, at, ev);
                        changed = Snap(bound);
                    }
                    else
                    {
                        Hold(conn, at, ev);
                    }
                    trace = Describe(ev, conn.Character?.Name);
                    break;
            }
        }
        if (trace is not null)
            Trace?.Invoke(trace);
        if (changed is not null)
            Changed?.Invoke(changed);
    }

    /// <summary>
    /// Fork (Faro): the meter started with the game already in the world, so this connection has seen no world entry
    /// and its progress (kinah, Abyss Points…) waits for an identity that only comes at the next one. The combat side
    /// already knows your name (loot and party records): the held events go to that character now. Never after a world
    /// entry on the connection: then the identity frame is on its way and it is the one that says who entered.
    /// </summary>
    public void BindByName(ProgressSession conn, string name, long at)
    {
        ProgressSnapshot? changed;
        int held;
        lock (_lock)
        {
            if (conn.Character is not null || conn.WorldEntrySeen || string.IsNullOrWhiteSpace(name))
                return;
            var key = _chars.Keys.FirstOrDefault(k => k.Item2 == name);
            if (key.Item2 is null)
            {
                key = ((ushort)0, name);
                _chars[key] = new Character { Name = name, Server = 0 };
            }
            var c = _chars[key];
            conn.Character = key;
            held = conn.Pending.Count;
            foreach (var (pAt, pEv) in conn.Pending)
                Record(c, pAt, pEv);
            conn.Pending.Clear();
            c.SeenMs = Math.Max(c.SeenMs, at);
            _current = key;
            changed = Snap(c);
        }
        Trace?.Invoke($"progress: {name} known from the combat records (no world entry seen yet); {held} held event(s) applied");
        Changed?.Invoke(changed);
    }

    /// <summary>
    /// The snapshot replaces the collection; a soul gain adds to a pet's souls and a level frame sets its level and
    /// starts its souls again (the gain that crosses a level is followed by that frame). Changes before the first
    /// snapshot are only logged: without the collection there is nothing to add them to.
    /// </summary>
    private void ApplyPets(long at, ProgressEvent ev)
    {
        PetsSnapshot? changed = null;
        PetsDelta? delta = null;
        string trace;
        lock (_lock)
        {
            switch (ev)
            {
                case PetsEvent p:
                    _pets.Clear();
                    foreach (var (id, level) in p.Levels)
                        _pets[id] = (level, 0);
                    foreach (var (id, souls) in p.Souls)
                        _pets[id] = (_pets.TryGetValue(id, out var had) ? had.Level : 0, souls);
                    (_petOut, _petsAt, _havePets) = (p.Active, at, true);
                    changed = SnapPets();
                    trace = $"progress: pets {_pets.Count} listed, {_pets.Values.Count(x => x.Level > 0)} unlocked, "
                        + $"{_pets.Values.Count(x => x.Level >= 3)} at level 3, out {p.Active}";
                    break;
                case PetSoulsEvent s:
                    trace = "progress: pet souls " + string.Join(' ', s.Gains.Select(g => $"{g.Id}+{g.Amount}"));
                    if (!_havePets)
                    {
                        trace += " (no collection yet: it comes at world entry; sent to Faro as a delta)";
                        delta = new PetsDelta(s.Gains, null, at);
                        break;
                    }
                    foreach (var (id, amount) in s.Gains)
                    {
                        _pets.TryGetValue(id, out var had);
                        _pets[id] = (had.Level, had.Souls + amount);
                    }
                    _petsAt = at;
                    changed = SnapPets();
                    trace += " -> " + string.Join(' ', s.Gains.Select(g => $"{g.Id} level {_pets[g.Id].Level}, {_pets[g.Id].Souls} souls"));
                    break;
                case PetLevelEvent l:
                    trace = $"progress: pet {l.Id} reached level {l.Level}";
                    if (!_havePets)
                    {
                        trace += " (no collection yet; sent to Faro as a delta)";
                        delta = new PetsDelta([], (l.Id, l.Level), at);
                        break;
                    }
                    _pets[l.Id] = (l.Level, 0);
                    _petsAt = at;
                    changed = SnapPets();
                    break;
                default:
                    return;
            }
        }
        Trace?.Invoke(trace);
        if (changed is not null)
            PetsChanged?.Invoke(changed);
        if (delta is not null)
            PetsGainedBeforeCollection?.Invoke(delta);
    }

    private PetsSnapshot SnapPets() =>
        new(_petOut, _pets.Select(kv => (kv.Key, kv.Value.Level, kv.Value.Souls)).ToList(), _petsAt);

    private static void Hold(ProgressSession conn, long at, ProgressEvent ev)
    {
        conn.Pending.Add((at, ev));
        if (conn.Pending.Count > PendingMax)
            conn.Pending.RemoveAt(0);
    }

    private static void Record(Character c, long at, ProgressEvent ev)
    {
        switch (ev)
        {
            case CombatPowerEvent cp:
                c.CombatPower = cp.Value;
                break;
            case ItemLevelEvent il:
                c.ItemLevel = (int)il.Value;
                break;
            case CountersEvent cs:
                foreach (var r in cs.Records)
                    c.Counters[r.Id] = (r.Value, r.Ms, at, r.Extra);
                break;
            case CounterEvent one:
                c.Counters[one.Record.Id] = (one.Record.Value, one.Record.Ms, at, one.Record.Extra);
                break;
            case BoardsEvent boards:
                foreach (var b in boards.Boards)
                {
                    string? key = b.Slot switch { 1 => SupplyDaily, 2 => SupplyWeekly, _ => null };
                    if (key is not null)
                        c.Gains[key] = (b.Done, at, b.Group);
                }
                break;
            case BoardEntryEvent { Done: true } entry:
                // A delivery counts on the board whose group it carries (the day's, in the one sample seen).
                foreach (var key in c.Gains.Where(g => g.Value.Group == entry.Group).Select(g => g.Key).ToList())
                {
                    var g = c.Gains[key];
                    c.Gains[key] = (g.Value + 1, at, g.Group);
                }
                break;
            case KinahTodayEvent k:
                c.Gains[KinahToday] = (k.Value, at, null);
                break;
            case AbyssPointsEvent ap:
                c.Gains[AbyssPoints] = (ap.Value, at, null);
                c.Gains[AbyssLimit] = (ap.Limit, at, null);
                break;
            case KinahHeldEvent kh:
                c.Gains[KinahHeld] = (kh.Value, at, null);
                break;
            case DutyMissionsEvent d:
                c.Gains[DutyDaily] = (d.Done, at, null);
                break;
            case PassUsedEvent p:
                foreach (var (id, used) in p.Entries)
                    c.Gains[$"pass_{id}"] = (used, at, null);
                break;
        }
        c.SeenMs = Math.Max(c.SeenMs, at);
    }

    private static ProgressSnapshot Snap(Character c) => new(
        c.Name, c.Server, c.ClassCode, c.Level, c.ItemLevel, c.CombatPower, c.SeenMs,
        c.Counters.Select(kv => (kv.Key, kv.Value.Value, kv.Value.Ms, kv.Value.At, kv.Value.Extra)).ToList(),
        c.Gains.ToDictionary(kv => kv.Key, kv => (kv.Value.Value, kv.Value.At), StringComparer.Ordinal));

    /// <summary>The record's second number for the log, as <c>(+N)</c> like the game shows it; empty without one.</summary>
    private static string Extra(CounterRecord r) => r.Extra is uint x ? $"(+{x})" : "";

    /// <summary>
    /// A log line for the events worth checking against the game; gear is too chatty. Kinah and Abyss Points are
    /// logged (fork for Faro) to see what each monster gives.
    /// </summary>
    private static string? Describe(ProgressEvent ev, string? who)
    {
        string to = who is null ? " (held until the character is known)" : $" ({who})";
        return ev switch
        {
            KinahTodayEvent k => $"progress: kinah today {k.Value}{to}",
            AbyssPointsEvent a => $"progress: abyss points {a.Value} (limit {a.Limit}){to}",
            KinahHeldEvent kh => $"progress: kinah held {kh.Value}{to}",
            CounterEvent c => $"progress: counter {c.Record.Id} = {c.Record.Value}{(c.Record.Ms ? " ms" : "")}{Extra(c.Record)}{to}",
            BoardsEvent b => "progress: boards " + string.Join(' ', b.Boards.Select(x => $"slot{x.Slot}={x.Done}")) + to,
            BoardEntryEvent { Done: true } e => $"progress: board entry done, group {e.Group}{to}",
            DutyMissionsEvent d => $"progress: duty missions done {d.Done}{to}",
            PassUsedEvent p => "progress: daeva pass " + string.Join(' ', p.Entries.Select(x => $"{x.Id}={x.Used}")) + to,
            _ => null,
        };
    }
}
