using System.Windows.Threading;
using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;

namespace Aion2DpsMeter.App.Infrastructure;

/// <summary>
/// Feeds a made-up party fighting a boss into the meter (<c>--demo</c>), for trying the panel and
/// the history without the game running. Nothing is captured in this mode.
/// </summary>
public sealed class DemoFeeder
{
    private const int BossCode = 2300001; // Deep Sea Pydeon
    private readonly int _bossMaxHp;

    private sealed record Member(int Id, string Name, JobClass Job, int[] Skills, int BaseHit, int? HealSkill);

    private static readonly Member[] Party =
    [
        new(10001, "Naicha", JobClass.Gladiator, [11010000, 11020000, 11030000, 11040000, 11050000], 21_000, null),
        new(10002, "ApexZ", JobClass.Sorcerer, [15010000, 15020000, 15030000], 26_000, null),
        new(10003, "Mirelle", JobClass.Ranger, [14010000, 14020000, 14030000], 19_000, null),
        new(10004, "Thorgal", JobClass.Chanter, [18010000, 18020000], 12_000, 18020000),
        new(10005, "Lunette", JobClass.Cleric, [17010000, 17020000, 17030000], 7_000, 17020000),
    ];

    private readonly CombatStore _store;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new(7);
    private int _bossId = 60_000;
    private long _hp;
    private long _restartAt;

    public DemoFeeder(CombatStore store, int bossHp = 40_000_000)
    {
        _store = store;
        _bossMaxHp = bossHp;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Step();
    }

    public void Start()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var m in Party)
            _store.AppendNicknameAuthoritative(m.Id, m.Name);
        _store.SetLocalIdentity(Party[0].Id, Party[0].Name);
        _store.SetPartyRoster(Party.Select((m, i) => new PartyMember(m.Name, i + 1, 45, 2100, 98_000, 1304, m.Job)).ToList(), complete: true);
        _store.SetCurrentDungeon(610011);
        SpawnBoss(now);
        _timer.Start();
    }

    private void SpawnBoss(long now)
    {
        _bossId++;
        _hp = _bossMaxHp;
        _store.AppendMob(_bossId, BossCode);
        _store.SetMobMaxHp(_bossId, _bossMaxHp);
        _restartAt = 0;
    }

    private void Step()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_hp <= 0)
        {
            if (_restartAt == 0)
                _restartAt = now + 10_000;
            else if (now >= _restartAt)
                SpawnBoss(now);
            return;
        }

        foreach (var m in Party)
        {
            if (_rng.NextDouble() > 0.45)
                continue;
            bool crit = _rng.NextDouble() < 0.35;
            int hit = (int)(m.BaseHit * (0.6 + _rng.NextDouble() * 0.8) * (crit ? 1.8 : 1));
            var flags = crit ? HitFlags.Critical : HitFlags.None;
            if (_rng.NextDouble() < 0.2) flags |= HitFlags.Back;
            if (_rng.NextDouble() < 0.05) flags |= HitFlags.Perfect;
            _store.AppendDamage(new DamageEvent
            {
                TimestampMs = now,
                ActorId = m.Id,
                TargetId = _bossId,
                SkillCode = m.Skills[_rng.Next(m.Skills.Length)],
                Damage = hit,
                Flags = flags,
                DamageType = crit ? 3 : 1,
            });
            _hp -= hit;

            if (m.HealSkill is int heal && _rng.NextDouble() < 0.3)
                _store.AppendHeal(m.Id, heal, (long)(m.BaseHit * 1.5 * (0.5 + _rng.NextDouble())), _rng.NextDouble() < 0.4, now);
        }

        _store.SetMobCurrentHp(_bossId, (int)Math.Max(_hp, 0));
        if (_hp <= 0)
            _store.MarkDead(_bossId, now);
    }
}
