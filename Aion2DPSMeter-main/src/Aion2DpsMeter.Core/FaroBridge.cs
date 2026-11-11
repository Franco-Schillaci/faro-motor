using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Aion2DpsMeter.Core.Combat;

namespace Aion2DpsMeter.Core;

/// <summary>
/// Faro bridge (added in this fork): sends the fight the meter is showing to the Faro overlay as
/// small JSON datagrams on the local machine only (127.0.0.1), and listens for a "reset" command
/// from Faro. It never sends anything outside this PC and never touches the game.
///
/// Messages to Faro (UDP 127.0.0.1:<see cref="DefaultFaroPort"/>):
///   { "v":1, "type":"live", "state":"Locked", "status":"…", "update":null, "party":[…], "fight": {…} | null }   ~4 times a second
///   { "v":1, "type":"end",  "fight": {…} }                          when a fight is saved
///   { "v":1, "type":"progress", "name":…, "counters":[…], "gains":{…} }   when your character's tickets/energy/dailies change
///   { "v":1, "type":"kill", "npc":2600023, "at":… }                 when a monster you or your party fought dies
///   { "v":1, "type":"petsdelta", "souls":[[id,n]…], "level":[id,lv] }  pet souls before the collection is known
///   { "v":1, "type":"death", "at":…, "total":…, "killer":{…}, "attackers":[{…}] }  you died to a player (see SendDeath)
///   { "v":1, "type":"nearby", "players":[{ "name", "server", "legion", "job", "cp", "weapon" }…] }  players in view, on change and every 5 s
/// Commands from Faro (UDP 127.0.0.1:<see cref="DefaultCommandPort"/>): "reset", "quit".
/// </summary>
public sealed class FaroBridge : IDisposable
{
    public const int DefaultFaroPort = 47321;
    public const int DefaultCommandPort = 47322;
    private const int MaxPlayers = 24;
    private const int MaxSkills = 12;
    private const int MaxSkillsEnded = 24;
    private const int MaxHealSkills = 8;
    private const int MaxDatagram = 60_000;

    // Los campos vacíos (null) no se mandan: así entra más detalle en cada datagrama.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly UdpClient _out = new();
    private readonly IPEndPoint _faro;
    private readonly UdpClient? _in;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sendLock = new();

    /// <summary>Raised (on a background thread) when Faro asks to reset the meter.</summary>
    public event Action? ResetRequested;
    /// <summary>Raised (on a background thread) when Faro closes and asks the meter to close too.</summary>
    public event Action? QuitRequested;
    /// <summary>Fork (Faro): raised (on a background thread) when Faro asks to save the last seconds of packets.</summary>
    public event Action? MarkRequested;

    public FaroBridge(int faroPort = DefaultFaroPort, int commandPort = DefaultCommandPort)
    {
        _faro = new IPEndPoint(IPAddress.Loopback, faroPort);
        try
        {
            _in = new UdpClient(new IPEndPoint(IPAddress.Loopback, commandPort));
            _ = ListenAsync(_in);
        }
        catch (SocketException)
        {
            _in = null; // port busy: live data still works, only the reset button from Faro does not
        }
    }

    /// <param name="state">Capture state: Stopped, WaitingForGame, Searching, Locked, Error or Demo.</param>
    /// <param name="status">Human-readable status line (English).</param>
    /// <param name="update">Tag of a newer release of the original meter, if there is one.</param>
    /// <param name="partyRoster">The party roster the server sent (class, level, gear score, combat power).</param>
    public void SendLive(EncounterRecord? record, string state, string status, string? update, IReadOnlyList<PartyMember>? partyRoster)
    {
        var party = partyRoster?.Select(m => new
        {
            name = m.Name,
            job = m.Job.ToString(),
            level = m.Level,
            gearScore = m.GearScore,
            combatPower = m.CombatPower,
            server = (int)m.ServerId,
        }).ToArray();
        if (record is null)
        {
            Send(new { v = 1, type = "live", state, status, update, party, fight = (object?)null });
            return;
        }
        // A big raid with every skill may not fit in one datagram: then send it without the skill breakdown.
        if (!Send(new { v = 1, type = "live", state, status, update, party, fight = Map(record, MaxSkills, detail: false) }))
            Send(new { v = 1, type = "live", state, status, update, party, fight = Map(record, 0, detail: false) });
    }

    /// <summary>
    /// A saved fight, with the full breakdown Faro keeps in its history (skills with crits / back attacks,
    /// healing, the boss code and its dungeon). If it does not fit in one datagram, with fewer skills.
    /// </summary>
    public void SendEnded(EncounterRecord record)
    {
        foreach (int skills in new[] { MaxSkillsEnded, MaxSkills, 4, 0 })
        {
            if (Send(new { v = 1, type = "end", fight = Map(record, skills, detail: true) }))
                return;
        }
    }

    /// <summary>
    /// The character you are playing and its progress as the server reports it (raw ids and numbers; Faro
    /// knows which ticket each counter is). Every value carries the capture time it was read at (ms).
    /// </summary>
    public void SendProgress(Progress.ProgressSnapshot p)
    {
        var job = Data.JobClasses.FromRosterClass(p.ClassCode);
        Send(new
        {
            v = 1,
            type = "progress",
            name = p.Name,
            server = p.Server,
            job = job == Data.JobClass.Unknown ? null : job.ToString(),
            classCode = p.ClassCode,
            level = p.Level,
            itemLevel = p.ItemLevel,
            combatPower = p.CombatPower,
            seen = p.SeenMs,
            counters = p.Counters.Select(c => new { id = c.Id, value = c.Value, ms = c.Ms, at = c.At, extra = c.Extra }).ToArray(),
            gains = p.Gains.ToDictionary(g => g.Key, g => new { value = g.Value.Value, at = g.Value.At }),
        });
    }

    /// <summary>
    /// The account's pet collection: each pet's level (0 = not unlocked yet) and souls toward the next level, the pet
    /// out at the last world entry, and the capture time (ms) of the last change. Faro knows the pets' names.
    /// </summary>
    public void SendPets(Progress.PetsSnapshot p) =>
        Send(new
        {
            v = 1,
            type = "pets",
            active = p.Active,
            at = p.At,
            list = p.Pets.Select(x => new[] { x.Id, x.Level, x.Souls }).ToArray(),
        });

    /// <summary>
    /// Fork (Faro): a monster you or your party fought died (its npc code and the time, ms). Faro looks up which pet
    /// its souls go to and tells you when that pet is already maxed.
    /// </summary>
    public void SendKill(int npcCode, long at) =>
        Send(new { v = 1, type = "kill", npc = npcCode, at });

    /// <summary>
    /// Fork (Faro): souls gained (and a level reached) before the meter knows the collection — it started with the game
    /// already in the world. Faro adds them to the collection it saved: <c>souls</c> = [[pet, amount]…], <c>level</c> =
    /// [pet, level] or absent.
    /// </summary>
    public void SendPetsDelta(Progress.PetsDelta d) =>
        Send(new
        {
            v = 1,
            type = "petsdelta",
            at = d.At,
            souls = d.Souls.Select(x => new[] { x.Id, x.Amount }).ToArray(),
            level = d.Level is { } l ? new[] { l.Id, l.Level } : null,
        });

    /// <summary>
    /// Fork (Faro): you died to a player. <c>killer</c> and each of <c>attackers</c> (who hit you in the last seconds,
    /// most damage first): name, server, legion, class, damage, hits, top skills [[name, damage]…] and the gear the
    /// server showed [[slot, item, level]…]; <c>final</c> marks the killing blow.
    /// </summary>
    public void SendDeath(Pvp.PvpDeath d)
    {
        static object Map(Pvp.PvpAttacker a) => new
        {
            name = a.Name,
            server = a.Server == 0 ? (int?)null : a.Server,
            legion = a.Legion,
            job = a.Job == Data.JobClass.Unknown ? null : a.Job.ToString(),
            damage = a.Damage,
            hits = a.Hits,
            skills = a.Skills.Select(s => new object[] { s.Skill, s.Damage }).ToArray(),
            gear = a.Gear.Select(g => new[] { g.Slot, g.Item, g.Enchant }).ToArray(),
            final = a.FinalBlow ? true : (bool?)null,
        };
        Send(new
        {
            v = 1,
            type = "death",
            at = d.At,
            total = d.TotalDamage,
            killer = Map(d.Killer),
            attackers = d.Attackers.Select(Map).ToArray(),
        });
    }

    /// <summary>
    /// Fork (Faro): the players in view now (allies too; Faro picks the enemies by server): name, server, legion, class,
    /// combat power and the weapon's +N, null/absent when the spawn did not say. If the list does not fit in one
    /// datagram, the ones that arrived last go.
    /// </summary>
    public void SendNearby(IReadOnlyList<Pvp.NearbyPlayer> list)
    {
        static object Map(Pvp.NearbyPlayer p)
        {
            var job = Data.JobClasses.FromRosterClass(p.ClassCode);
            return new
            {
                name = p.Name,
                server = p.Server == 0 ? (int?)null : p.Server,
                legion = p.Legion,
                job = job == Data.JobClass.Unknown ? null : job.ToString(),
                cp = p.CombatPower == 0 ? (int?)null : p.CombatPower,
                weapon = p.Weapon == 0 ? (int?)null : p.Weapon,
            };
        }
        for (int n = list.Count; ; n = n * 3 / 4)
        {
            if (Send(new { v = 1, type = "nearby", players = list.Take(n).Select(Map).ToArray() }) || n == 0)
                return;
        }
    }

    /// <summary>Fork (Faro): the «captura de momentos» file just written (or null if there was nothing to save).</summary>
    public void SendMark(string? path, int count) =>
        Send(new { v = 1, type = "mark", path, count });

    private bool Send(object message)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaxDatagram)
            return false;
        try
        {
            lock (_sendLock)
                _out.Send(bytes, bytes.Length, _faro);
        }
        catch (SocketException)
        {
            // Faro not running: UDP just drops it.
        }
        return true;
    }

    /// <param name="skills">How many skills per player (0 = no breakdown).</param>
    /// <param name="detail">Saved fight: also crits / back attacks per skill, healing, boss code and difficulty.</param>
    private static object Map(EncounterRecord r, int skills, bool detail)
    {
        var players = r.Players
            .Where(p => p.Damage > 0 || (detail && p.Heal > 0))
            .OrderByDescending(p => p.Damage)
            .Take(MaxPlayers)
            .Select(p => new
            {
                id = p.ActorId,
                name = p.Name,
                job = p.Job.ToString(),
                local = p.IsLocal,
                damage = p.Damage,
                dps = Math.Round(p.Dps),
                share = Math.Round(p.DamageShare, 4),
                hits = p.Hits,
                crits = p.Crits,
                maxHit = p.MaxHit,
                heal = detail ? p.Heal : (long?)null,
                healShare = detail ? Math.Round(p.HealShare, 4) : (double?)null,
                skills = skills > 0
                    ? p.Skills.OrderByDescending(s => s.Damage).Take(skills).Select(s => new
                    {
                        code = detail ? s.Code : (int?)null,
                        name = s.Name,
                        damage = s.Damage,
                        hits = s.Hits,
                        crits = detail ? s.Crits : (int?)null,
                        back = detail ? s.Back : (int?)null,
                        max = s.Max,
                        share = Math.Round(s.Share, 4),
                        dot = s.IsDot,
                        summon = s.IsSummon,
                    }).ToArray()
                    : null,
                healSkills = detail && skills > 0 && p.Heal > 0
                    ? p.HealSkills.Take(MaxHealSkills).Select(h => new { code = h.Code, name = h.Name, amount = h.Amount, ticks = h.Ticks, hot = h.IsHot }).ToArray()
                    : null,
            })
            .ToArray();

        return new
        {
            id = r.Id,
            name = r.Name,
            startedAt = r.StartedAt.ToUnixTimeMilliseconds(),
            durationMs = r.DurationMs,
            isBoss = r.IsBoss,
            killed = r.Killed,
            inProgress = r.InProgress,
            dungeon = r.DungeonName,
            dungeonId = detail ? r.DungeonId : (int?)null,
            tier = detail ? r.Tier : null,
            npcCode = detail ? r.NpcCode : (int?)null,
            totalDamage = r.TotalDamage,
            totalDps = Math.Round(r.TotalDps),
            totalHeal = detail ? r.TotalHeal : (long?)null,
            bossHp = r.IsBoss && r.BossMaxHp > 0 ? Math.Round((double)r.BossCurrentHp / r.BossMaxHp, 4) : (double?)null,
            players,
        };
    }

    private async Task ListenAsync(UdpClient client)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(_cts.Token);
                string cmd = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (cmd.Equals("reset", StringComparison.OrdinalIgnoreCase))
                    ResetRequested?.Invoke();
                else if (cmd.Equals("quit", StringComparison.OrdinalIgnoreCase))
                    QuitRequested?.Invoke();
                else if (cmd.Equals("mark", StringComparison.OrdinalIgnoreCase))
                    MarkRequested?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _in?.Dispose();
        _out.Dispose();
    }
}
