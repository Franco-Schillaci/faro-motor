using System.Buffers.Binary;
using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.Progress;

namespace Aion2DpsMeter.Core.Protocol;

/// <summary>
/// Decodes AION 2 game packets from one server-to-client TCP stream and feeds what it finds
/// (damage, heals, spawns, deaths, names, party roster) into a <see cref="CombatStore"/>.
/// Ported from A2Tools (GPL-3.0), <c>capture/stream_processor.rs</c>.
/// </summary>
public sealed class PacketParser
{
    private const int MaxEntityId = 9_999_999;

    private readonly CombatStore _store;
    private readonly GameData _data;
    private readonly Opcodes _op;
    private readonly BoundedSet _seenEmbedded = new(16_384);
    private (int ActorId, int SkillRaw)? _pendingCompactSkill;
    // Fork (Faro): character progress (tickets, energy, weekly counters), one session per connection.
    private readonly ProgressTracker? _progress;
    private readonly ProgressSession _progressSession = new();
    // Fork (Faro): who killed you in PvP (null = off).
    private readonly Pvp.PvpTracker? _pvp;

    /// <summary>Timestamp (ms) stamped on every event; the capture time of the current segment.</summary>
    public long CurrentTimestampMs { get; set; }

    /// <summary>Fork (Faro): keeps the last seconds of packets for «captura de momentos» (null = off).</summary>
    public static MomentRecorder? Recorder { get; set; }

    public PacketParser(CombatStore store, GameData data, Opcodes? opcodes = null, ProgressTracker? progress = null, Pvp.PvpTracker? pvp = null)
    {
        _store = store;
        _data = data;
        _op = opcodes ?? Opcodes.Default;
        _progress = progress;
        _pvp = pvp;
    }

    /// <summary>Parses every complete packet in <paramref name="buffer"/>; returns the bytes consumed.</summary>
    public int ConsumeStream(ReadOnlySpan<byte> buffer)
    {
        var framing = Framing.Walk(buffer);
        foreach (var frame in framing.Frames)
        {
            if (frame.Kind == FrameKind.Bundle)
            {
                UnwrapBundle(frame.Payload(buffer));
            }
            else
            {
                ParsePacket(frame.Bytes(buffer));
                ScanEmbeddedBundlesForIdentity(frame.Bytes(buffer));
            }
        }

        if (buffer.Length >= 4)
        {
            ScanEmbeddedOwnership(buffer);
            ScanEntityHp(buffer);
        }
        if (buffer.Length >= 6)
            ScanEmbeddedSpawns(buffer);
        ScanMaskedIdentity(buffer);
        ScanPartyRoster(buffer);

        return framing.Consumed;
    }

    // ───── bundles ─────

    private void UnwrapBundle(ReadOnlySpan<byte> payload)
    {
        var decompressed = Framing.DecompressBundle(payload);
        if (decompressed is null)
            return;
        ReadOnlySpan<byte> data = decompressed;

        _pendingCompactSkill = null;
        foreach (var frame in Framing.WalkInner(data).Frames)
        {
            if (frame.Kind == FrameKind.Bundle)
            {
                UnwrapBundle(frame.Payload(data));
                continue;
            }
            var inner = frame.Bytes(data);
            if (ExtractPendingCompactSkill(inner) is { } ctx)
                _pendingCompactSkill = ctx;
            ParsePacket(inner);
        }

        ScanEmbeddedOwnership(data);
        ScanEntityHp(data);
        ScanEmbeddedSpawns(data);
        ScanMaskedIdentity(data);
        ScanPartyRoster(data);
        _pendingCompactSkill = null;
    }

    /// <summary>
    /// The self record and party roster also arrive in bundles carried inside a larger packet,
    /// where the framing never opens them. Only identity is read from those.
    /// </summary>
    private void ScanEmbeddedBundlesForIdentity(ReadOnlySpan<byte> packet)
    {
        int i = 1;
        while (i + 8 < packet.Length)
        {
            if (packet[i] != 0xFF || packet[i + 1] != 0xFF)
            {
                i++;
                continue;
            }
            bool found = false;
            for (int n = 3; n >= 1 && !found; n--)
            {
                int at = i - n;
                if (at < 0)
                    continue;
                var len = VarInt.Read(packet, at);
                if (len.Length != n || Framing.FrameSize(len.Value, len.Length) is not int size)
                    continue;
                int end = at + size;
                if (end > packet.Length)
                    continue;
                var data = Framing.DecompressBundle(packet[i..end]);
                if (data is null)
                    continue;
                if (_progress is not null || Recorder is not null)
                {
                    foreach (var inner in Framing.WalkInner(data).Frames)
                    {
                        if (inner.Kind != FrameKind.Packet)
                            continue;
                        Recorder?.Add(CurrentTimestampMs, inner.Bytes(data));
                        if (_progress is not null)
                            ParseProgress(inner.Bytes(data));
                    }
                }
                ScanMaskedIdentity(data);
                ScanPartyRoster(data);
                i = end;
                found = true;
            }
            if (!found)
                i++;
        }
    }

    private void ParsePacket(ReadOnlySpan<byte> packet)
    {
        Recorder?.Add(CurrentTimestampMs, packet);
        if (packet.Length < 3)
            return;

        ParseProgress(packet);
        bool damage = ParseDamage(packet, allowEmbeddedScan: true, requireTrusted: false);
        bool ownership = ParseSummonOwnership(packet);
        bool summon = ParseSpawnPacket(packet);
        bool name = ParseActorNameBinding(packet) || ParseNicknamePatterns(packet);
        bool hp = ParseHpUpdate(packet);
        ParsePartyScope(packet);
        ParseSelfStatus(packet);
        ParseLeave(packet);
        ParseDeath(packet);
        ParseZoneChange(packet);

        if (!damage && !name && !summon && !ownership && !hp)
            ParseDot(packet);
    }

    private static int BodyOffset(ReadOnlySpan<byte> packet)
    {
        var len = VarInt.Read(packet, 0);
        return len.Length;
    }

    /// <summary>Fork (Faro): a frame about the character's progress (see <see cref="ProgressDecoder"/>).</summary>
    private void ParseProgress(ReadOnlySpan<byte> packet)
    {
        if (_progress is null)
            return;
        int o = BodyOffset(packet);
        if (o <= 0 || o + 3 > packet.Length)
            return;
        var body = packet[o..];
        var ev = ProgressDecoder.Parse(body, _op);
        if (ProgressDecoder.IsCounterFrame(body, _op))
            _progress.NoteCounterBytes(body, ev is not null);
        if (ProgressDecoder.IsCurrencyFrame(body, _op))
            _progress.NoteCurrencyBytes(body, ev is not null);
        if (ev is null)
            return;
        // Fork (Faro): the meter started mid-session (no world entry on this connection yet): live progress goes to
        // the name the combat side already knows instead of waiting for the next world entry. Frames that only come
        // at a world entry are left to the normal identity flow.
        if (_progressSession.Character is null && !_progressSession.WorldEntrySeen
            && ev is not (IdentityEvent or CountersEvent or CombatPowerEvent or BoardsEvent)
            && _store.KnownLocalName is { } me)
            _progress.BindByName(_progressSession, me, CurrentTimestampMs);
        _progress.Apply(_progressSession, CurrentTimestampMs, ev);
    }

    /// <summary><c>&lt;len&gt; 06 38 &lt;entity&gt; …</c>: sent only about you and your party; tells your loot from a stranger's.</summary>
    private void ParsePartyScope(ReadOnlySpan<byte> packet)
    {
        int o = BodyOffset(packet);
        if (o < 0 || o + 3 >= packet.Length || !Opcodes.At(packet, o, _op.PartyScopeB))
            return;
        var id = VarInt.Read(packet, o + 2);
        if (id.Length > 0)
            _store.NotePartyScope(id.Value);
    }

    /// <summary>Fork (Faro): <c>&lt;len&gt; 4A 36 &lt;entity&gt; …</c>, only ever about you: which entity you are now.</summary>
    private void ParseSelfStatus(ReadOnlySpan<byte> packet)
    {
        if (_pvp is null)
            return;
        int o = BodyOffset(packet);
        if (o < 0 || o + 3 > packet.Length || !Opcodes.At(packet, o, _op.SelfStatusB))
            return;
        var id = VarInt.Read(packet, o + 2);
        if (id.Length > 0 && id.Value >= 1 && id.Value <= MaxEntityId)
            _pvp.NoteSelf(id.Value);
    }

    /// <summary>Fork (Faro): <c>&lt;len&gt; 47 36 &lt;entity&gt; …</c>, that entity left your view («Enemigos cerca»).</summary>
    private void ParseLeave(ReadOnlySpan<byte> packet)
    {
        if (_pvp is null)
            return;
        int o = BodyOffset(packet);
        if (o < 0 || o + 3 > packet.Length || !Opcodes.At(packet, o, _op.LeaveB))
            return;
        var id = VarInt.Read(packet, o + 2);
        if (id.Length > 0 && id.Value >= 1 && id.Value <= MaxEntityId)
            _pvp.Nearby.NoteGone(id.Value);
    }

    /// <summary>Every damage record goes to the meter; fork (Faro): and to PvP, which keeps the hits players deal.</summary>
    private void AppendDamage(DamageEvent e)
    {
        _pvp?.NoteHit(e.TargetId, e.ActorId, e.SkillCode, e.TotalDamage, e.TimestampMs, _store.IsKnownPlayer(e.ActorId));
        _store.AppendDamage(e);
    }

    // ───── zone change / death / hp ─────

    /// <summary><c>&lt;len&gt; 23 36 00 ...</c>: the local player teleported into a zone.</summary>
    private void ParseZoneChange(ReadOnlySpan<byte> packet)
    {
        int o = BodyOffset(packet);
        if (o < 0 || o + 2 >= packet.Length)
            return;
        if (!Opcodes.At(packet, o, _op.ZoneChangeB) || packet[o + 2] != 0x00)
            return;
        _store.NoteZoneChange(CurrentTimestampMs);
        _pvp?.Nearby.Clear();   // fork (Faro): the players from before are not around any more
    }

    /// <summary><c>&lt;len&gt; 42 36 &lt;entity&gt; &lt;0&gt; &lt;flag&gt;</c>; flag 3 is a combat death.</summary>
    private void ParseDeath(ReadOnlySpan<byte> packet)
    {
        int o = BodyOffset(packet);
        if (o < 0 || o + 1 >= packet.Length || !Opcodes.At(packet, o, _op.DeathB))
            return;
        int pos = o + 2;
        var entity = VarInt.Read(packet, pos);
        if (entity.Length <= 0)
            return;
        pos += entity.Length;
        var skip = VarInt.Read(packet, pos);
        if (skip.Length <= 0)
            return;
        pos += skip.Length;
        var flag = VarInt.Read(packet, pos);
        if (flag.Length > 0 && flag.Value == 3)
            _store.MarkDead(entity.Value, CurrentTimestampMs);
    }

    private bool ParseHpUpdate(ReadOnlySpan<byte> packet)
    {
        int o = BodyOffset(packet);
        if (o < 0 || o + 1 >= packet.Length || !Opcodes.At(packet, o, _op.HpUpdateB))
            return false;
        int pos = o + 2;
        var actor = VarInt.Read(packet, pos);
        if (actor.Length <= 0 || actor.Value < 100 || actor.Value > MaxEntityId)
            return false;
        pos += actor.Length;
        var hp = VarInt.Read(packet, pos);
        if (hp.Length <= 0)
            return false;
        pos += hp.Length;
        var hpMax = VarInt.Read(packet, pos);
        if (hpMax.Length <= 0 || hpMax.Value <= 0 || hpMax.Value > 50_000_000)
            return false;
        _store.SetMobMaxHp(actor.Value, hpMax.Value);
        return true;
    }

    /// <summary>
    /// Live current-HP feed: <c>8D &lt;id&gt; 02 01 00 &lt;u32 LE hp&gt; 00 00 00 00</c> for NPCs.
    /// The trailing zero u32 is required, or a look-alike record inflates the HP.
    /// </summary>
    private void ScanEntityHp(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i + 1 < data.Length)
        {
            if (data[i] != 0x8D)
            {
                i++;
                continue;
            }
            var id = VarInt.Read(data, i + 1);
            if (id.Length <= 0 || id.Value < 100 || id.Value > MaxEntityId)
            {
                i++;
                continue;
            }
            int d = i + 1 + id.Length;
            if (d + 11 > data.Length)
            {
                i++;
                continue;
            }
            if (data[d] == 0x02 && data[d + 1] == 0x01 && data[d + 2] == 0x00
                && data[d + 7] == 0 && data[d + 8] == 0 && data[d + 9] == 0 && data[d + 10] == 0)
            {
                uint cur = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(d + 3, 4));
                if (cur <= 100_000_000)
                    _store.SetMobCurrentHp(id.Value, (int)cur);
                i = d + 11;
                continue;
            }
            i++;
        }
    }

    // ───── DoT / heal ticks (05 38) ─────

    private void ParseDot(ReadOnlySpan<byte> packet)
    {
        int o = BodyOffset(packet);
        if (o < 0 || packet.Length <= o + 1 || !Opcodes.At(packet, o, _op.DotB))
            return;
        o += 2;
        var target = VarInt.Read(packet, o);
        if (target.Length < 0)
            return;
        o += target.Length;
        if (o >= packet.Length)
            return;

        // 0x02/0x0A = damage; 0x01/0x09 = heal; 0x0B = HoT. Exact match, not a bitmask.
        int effect = packet[o++];
        bool isDamage = effect is 0x02 or 0x0A;
        bool isHeal = effect is 0x01 or 0x09 or 0x0B;
        if (!isDamage && !isHeal)
            return;

        var actor = VarInt.Read(packet, o);
        if (actor.Length < 0 || (isDamage && actor.Value == target.Value))
            return;
        o += actor.Length;

        var unknown = VarInt.Read(packet, o);
        if (unknown.Length < 0)
            return;
        o += unknown.Length;

        if (o + 4 > packet.Length)
            return;
        int skill = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(o, 4)) / 100;
        o += 4;
        if (!IsValidSkillCode(skill))
            return;

        var amount = VarInt.Read(packet, o);
        if (amount.Length < 0 || amount.Value <= 0 || amount.Value > 99_999_999)
            return;

        if (isHeal)
        {
            _store.AppendHeal(actor.Value, skill, amount.Value, effect == 0x0B, CurrentTimestampMs);
            return;
        }

        // Damage DoTs are gated by the curated list of DoT skills.
        if (!_data.DotSkillIds.Contains(skill))
            return;

        AppendDamage(new DamageEvent
        {
            TimestampMs = CurrentTimestampMs,
            IsDot = true,
            TargetId = target.Value,
            ActorId = actor.Value,
            SkillCode = skill,
            Damage = amount.Value,
        });
    }

    // ───── summon ownership (04 8D) ─────

    private bool ParseSummonOwnership(ReadOnlySpan<byte> packet)
    {
        int o = BodyOffset(packet);
        if (o < 0 || o + 1 >= packet.Length || !Opcodes.At(packet, o, _op.SummonOwnershipB))
            return false;
        int pos = o + 2;
        var summon = VarInt.Read(packet, pos);
        if (summon.Length <= 0 || summon.Value < 100)
            return false;
        pos += summon.Length;
        if (pos + 4 > packet.Length)
            return false;
        pos += 4;
        var owner = VarInt.Read(packet, pos);
        if (owner.Length <= 0 || owner.Value < 100 || owner.Value == summon.Value)
            return false;
        pos += owner.Length;

        if (_store.IsConfirmedSummon(summon.Value))
            _store.LinkSummon(summon.Value, owner.Value);

        var meta = VarInt.Read(packet, pos);
        if (meta.Length > 0)
        {
            pos += meta.Length;
            if (pos < packet.Length)
            {
                int nameLen = packet[pos];
                if (nameLen >= 1 && nameLen <= 36 && pos + 1 + nameLen <= packet.Length)
                    RegisterScannedNickname(packet, owner.Value, pos + 1, nameLen);
            }
        }
        return true;
    }

    /// <summary>
    /// Kill / ownership records embedded anywhere: <c>04 8D &lt;summon&gt; &lt;4 bytes&gt; &lt;owner varint&gt;
    /// &lt;server u16&gt; &lt;len&gt;&lt;name&gt;</c>. Server ids are in 1000-2999.
    /// </summary>
    private void ScanEmbeddedOwnership(ReadOnlySpan<byte> data)
    {
        foreach (var code in _op.SummonOwnershipB)
        {
            int search = 0;
            while (search + 1 < data.Length)
            {
                int rel = data[search..].IndexOf(code);
                if (rel < 0)
                    break;
                int idx = search + rel;
                search = idx + code.Length;
                if (search >= data.Length)
                    break;

                var summon = VarInt.Read(data, search);
                if (summon.Length <= 0 || summon.Value < 100 || summon.Value > MaxEntityId)
                    continue;
                int fixedStart = search + summon.Length;
                if (fixedStart + 4 > data.Length)
                    continue;
                int afterFixed = fixedStart + 4;
                // All zeros there is the despawn record, which has no owner.
                if (afterFixed >= data.Length || data[afterFixed] == 0)
                    continue;

                int scanEnd = Math.Min(data.Length - 2, afterFixed + 128);
                (int Owner, ushort Server, string Name, int End)? found = null;
                for (int s = afterFixed + 1; s < scanEnd; s++)
                {
                    ushort server = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(s, 2));
                    if (server < 1000 || server > 2999)
                        continue;
                    int? owner = VarInt.EndingAt(data, s, afterFixed, 100, 99_999);
                    if (owner is not int ownerId || ownerId == summon.Value)
                        continue;
                    int nameLenIdx = s + 2;
                    int nameLen = data[nameLenIdx];
                    int nameEnd = nameLenIdx + 1 + nameLen;
                    if (!Names.IsNameFieldLength(nameLen) || nameEnd > data.Length)
                        continue;
                    if (Names.Exact(data[(nameLenIdx + 1)..nameEnd]) is { } name)
                    {
                        found = (ownerId, server, name, nameEnd);
                        break;
                    }
                }
                if (found is not { } f)
                    continue;

                if (_store.IsConfirmedSummon(summon.Value))
                    _store.LinkSummon(summon.Value, f.Owner);
                _store.AppendNickname(f.Owner, f.Name);
                _store.NotePlayerServer(f.Name, f.Server);
                // For a mob that was fought, this record names who got the loot: usually you.
                if (!_store.IsConfirmedSummon(summon.Value) && _store.IsFoughtTarget(summon.Value))
                    _store.NoteLootOwner(summon.Value, f.Owner, f.Name);
                // Fork (Faro): for a player it names who killed them, then their legion (<c>&lt;len&gt;&lt;name&gt;</c>).
                _pvp?.NoteKill(summon.Value, f.Owner, f.Server, f.Name, ReadLegion(data, f.End), CurrentTimestampMs,
                    _store.GetLocalProfile()?.EntityId);
                search = f.End;
            }
        }
    }

    /// <summary>Fork (Faro): the legion after a kill record's name, <c>&lt;len&gt;&lt;UTF-8&gt;</c>; null when absent or unreadable.</summary>
    private static string? ReadLegion(ReadOnlySpan<byte> data, int at)
    {
        if (at >= data.Length)
            return null;
        int len = data[at];
        if (len < 1 || len > 36 || at + 1 + len > data.Length)
            return null;
        try
        {
            string s = new System.Text.UTF8Encoding(false, true).GetString(data.Slice(at + 1, len));
            return s.Any(char.IsControl) ? null : s;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    // ───── spawns ─────

    /// <summary>Spawn records sitting mid-packet or mid-bundle, where the packet-front parser never looks.</summary>
    private void ScanEmbeddedSpawns(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i + 5 < data.Length)
        {
            bool isPlayer = Opcodes.At(data, i, _op.PlayerSpawnB);
            bool isMob = !isPlayer && Opcodes.At(data, i, _op.MobSpawnB);
            if (!isPlayer && !isMob)
            {
                i++;
                continue;
            }
            if (i > 0 && data[i - 1] == 0x00)
            {
                i += 2;
                continue;
            }
            var target = VarInt.Read(data, i + 2);
            if (target.Length > 0 && target.Value >= 100 && target.Value <= MaxEntityId)
            {
                if (isPlayer)
                {
                    int end = FrameEndAt(data, i, out bool exact);
                    ParsePlayerSpawnName(data, i + 2, end, exact);
                }
                else
                {
                    int realId = NormalizeEntityId(target.Value);
                    if (!_store.IsMob(realId))
                        ParseMobSpawnAt(data, i + 2);
                }
            }
            i += 2 + Math.Max(target.Length, 0);
        }
    }

    private bool ParseSpawnPacket(ReadOnlySpan<byte> packet)
    {
        int o = BodyOffset(packet);
        if (o < 0 || o + 1 >= packet.Length)
            return false;
        if (Opcodes.At(packet, o, _op.PlayerSpawnB))
        {
            ParsePlayerSpawnName(packet, o + 2, packet.Length, exactEnd: true);
            return false;
        }
        if (!Opcodes.At(packet, o, _op.MobSpawnB))
            return false;
        return ParseMobSpawnAt(packet, o + 2);
    }

    private static int NormalizeEntityId(int id) => id > 1_000_000 ? (id & 0x3FFF) | 0x4000 : id;

    /// <summary>
    /// Fork (Faro): where the frame whose opcode sits at <paramref name="op"/> ends, from the length varint right before
    /// it (<paramref name="exact"/> = true); the end of <paramref name="data"/> when that cannot be read.
    /// </summary>
    private static int FrameEndAt(ReadOnlySpan<byte> data, int op, out bool exact)
    {
        exact = false;
        for (int n = 1; n <= 3; n++)
        {
            int at = op - n;
            if (at < 0)
                break;
            var len = VarInt.Read(data, at);
            if (len.Length != n || Framing.FrameSize(len.Value, n) is not int size)
                continue;
            int end = at + size;
            if (end > op + 2 && end <= data.Length)
            {
                exact = true;
                return end;
            }
        }
        return data.Length;
    }

    /// <summary>
    /// <c>45 36 &lt;id&gt; &lt;mask1 u32&gt; &lt;mask2 u8&gt; [mask2 &amp; 1] &lt;len&gt;&lt;name&gt; &lt;u32 class&gt; …</c>,
    /// then (fork, Faro) the gear others can see, read for PvP (see <see cref="Pvp.PvpTracker.ReadGear"/>), and their
    /// server, legion and combat power for «Enemigos cerca» (see <see cref="Pvp.NearbyTracker"/>). The combat power sits
    /// near the end of the record, so it is read only when <paramref name="exactEnd"/> says where that is.
    /// </summary>
    private void ParsePlayerSpawnName(ReadOnlySpan<byte> data, int offsetAfterOpcode, int end, bool exactEnd)
    {
        var actor = VarInt.Read(data, offsetAfterOpcode);
        if (actor.Length <= 0 || actor.Value < 1 || actor.Value > MaxEntityId)
            return;
        int mask2 = offsetAfterOpcode + actor.Length + 4;
        if (mask2 + 1 >= data.Length || (data[mask2] & 0x01) == 0)
            return;
        int nameLen = data[mask2 + 1];
        if (!Names.IsNameFieldLength(nameLen) || mask2 + 2 + nameLen > data.Length)
            return;
        if (Names.Exact(data.Slice(mask2 + 2, nameLen)) is not { } name)
            return;
        _store.NoteLowIdEntity(actor.Value);
        _store.NotePlayerSpawn(actor.Value);
        _store.AppendNicknameAuthoritative(actor.Value, name);

        if (_pvp is not null)
        {
            int nameEnd = mask2 + 2 + nameLen;
            uint classCode = nameEnd + 4 <= Math.Min(end, data.Length)
                ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(nameEnd, 4)) : 0;
            if (JobClasses.FromRosterClass(classCode) == JobClass.Unknown)
                classCode = 0;
            var gear = Pvp.PvpTracker.ReadGear(data, nameEnd + 4, end);
            var (server, legion) = Pvp.NearbyTracker.ReadOrigin(data, nameEnd, end);
            _pvp.NoteSpawn(actor.Value, name, classCode, gear, server, legion);
            int cp = exactEnd ? Pvp.NearbyTracker.ReadCombatPower(data, end) : 0;
            int weapon = gear.FirstOrDefault(g => g.Slot == 1)?.Enchant ?? 0;
            _pvp.Nearby.NoteSpawn(new Pvp.NearbyPlayer(actor.Value, name, classCode, server, legion, cp, weapon, CurrentTimestampMs));
        }
    }

    /// <summary>
    /// <c>41 36 &lt;id&gt; &lt;mask u32&gt; &lt;subtrees…&gt;</c>: NPCs, summons and skill-effect entities.
    /// The low byte of <c>mask</c> is the kind: 0x0C/0x0D NPC, 0x5F summon, 0x1C skill effect.
    /// </summary>
    private bool ParseMobSpawnAt(ReadOnlySpan<byte> packet, int offsetAfterOpcode)
    {
        int offset = offsetAfterOpcode;
        var target = VarInt.Read(packet, offset);
        if (target.Length < 0)
            return false;
        offset += target.Length;

        int realId = NormalizeEntityId(target.Value);
        _store.NoteSummonSpawn(realId);
        _store.NoteLowIdEntity(realId);

        if (offset + 2 >= packet.Length)
        {
            ExtractMobType(packet, offset, realId);
            return false;
        }

        uint mask = (uint)(packet[offset]
            | packet[offset + 1] << 8
            | (offset + 2 < packet.Length ? packet[offset + 2] : 0) << 16
            | (offset + 3 < packet.Length ? packet[offset + 3] : 0) << 24);
        byte kind = packet[offset];

        // The mask width changed from u16 to u32; try both positions for the inline name.
        (string Name, int Next)? ReadNameAt(ReadOnlySpan<byte> p, int sub)
        {
            int gate = offset + sub;
            if (gate >= p.Length || (p[gate] & 0x01) == 0)
                return null;
            int cursor = gate + 1;
            if (cursor >= p.Length)
                return null;
            int len = p[cursor];
            if (!Names.IsNameFieldLength(len) || cursor + 1 + len > p.Length)
                return null;
            var n = Names.Exact(p.Slice(cursor + 1, len));
            return n is null ? null : (n, cursor + 1 + len);
        }

        var named = ReadNameAt(packet, 4) ?? ReadNameAt(packet, 2);
        string? spawnName = named?.Name;
        int nameCursor = named?.Next ?? offset + 5;

        ExtractMobType(packet, offset, realId);

        // A 0x1C effect entity is parented to the skill's target, so only real summons use parent_key.
        if (kind == 0x5F && (mask & 0x0010) != 0 && FindSpawnParentKey(packet, nameCursor, realId) is int parent)
        {
            _store.NoteLowIdEntity(parent);
            _store.RegisterConfirmedSummon(realId, parent);
            return true;
        }

        // Summons and skill-effect entities are labelled with their caster's name.
        if (spawnName is not null && _store.FindIdByNickname(spawnName) is int ownerByName && ownerByName != realId)
        {
            _store.RegisterConfirmedSummon(realId, ownerByName);
            return true;
        }

        // Spirits: the caster recorded on the entity's own buff block.
        if (kind is 0x5F or 0x1F or 0x1D or 0x5D)
        {
            int owner = ExtractSummonOwnerFromSpawn(packet, offset);
            if (owner > 0 && owner != realId)
            {
                _store.RegisterConfirmedSummon(realId, owner);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Anchors on the owner block that follows a summon's parent_key:
    /// <c>&lt;parent u32&gt; &lt;legion u32&gt; &lt;u16 0&gt; &lt;u16 server&gt; &lt;len&gt;&lt;legion name&gt;</c>, preceded by 0x06.
    /// </summary>
    private static int? FindSpawnParentKey(ReadOnlySpan<byte> packet, int searchFrom, int selfId)
    {
        for (int i = Math.Max(searchFrom, 1); i + 13 <= packet.Length; i++)
        {
            if (packet[i - 1] == 0x06 && ParseSpawnOwnerBlock(packet, i, selfId) is int parent)
                return parent;
        }
        return null;
    }

    private static int? ParseSpawnOwnerBlock(ReadOnlySpan<byte> p, int at, int selfId)
    {
        if (at + 13 > p.Length)
            return null;
        uint parent = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(at, 4));
        if (parent == 0 || parent > MaxEntityId || (int)parent == selfId)
            return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(at + 8, 2)) != 0)
            return null;
        ushort server = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(at + 10, 2));
        if (server == 0 || server > 9_999)
            return null;
        int nameLen = p[at + 12];
        if (nameLen > 40 || at + 13 + nameLen > p.Length)
            return null;
        return Names.DecodeUtf8(p.Slice(at + 13, nameLen)) is null ? null : (int)parent;
    }

    private static readonly byte[] SpiritCasterAnchor = [0x80, 0x75, 0xD5, 0x2A, 0xBB, 0x03, 0x00, 0x00];

    private static int ExtractSummonOwnerFromSpawn(ReadOnlySpan<byte> packet, int start)
    {
        int max = Math.Min(packet.Length - SpiritCasterAnchor.Length, start + 120);
        for (int i = start; i <= max; i++)
        {
            if (!packet[i..].StartsWith(SpiritCasterAnchor))
                continue;
            var owner = VarInt.Read(packet, i + SpiritCasterAnchor.Length);
            if (owner.Length > 0 && owner.Value >= 1 && owner.Value <= MaxEntityId)
                return owner.Value;
        }
        return -1;
    }

    /// <summary>The NPC type id sits as a u24 just before <c>00 (40|00) 02</c>; max HP follows a <c>01</c>.</summary>
    private void ExtractMobType(ReadOnlySpan<byte> packet, int start, int realId)
    {
        int max = Math.Min(packet.Length - 2, start + 60);
        for (int s = start; s < max; s++)
        {
            if (packet[s] != 0x00 || (packet[s + 1] != 0x40 && packet[s + 1] != 0x00) || packet[s + 2] != 0x02)
                continue;
            if (s >= start + 3)
            {
                int code = packet[s - 3] | packet[s - 2] << 8 | packet[s - 1] << 16;
                _store.AppendMob(realId, code);

                int hpEnd = Math.Min(packet.Length - 2, s + 3 + 64);
                for (int h = s + 3; h < hpEnd; h++)
                {
                    if (packet[h] != 0x01)
                        continue;
                    var cur = VarInt.Read(packet, h + 1);
                    if (cur.Length <= 0 || cur.Value <= 0)
                        continue;
                    var maxHp = VarInt.Read(packet, h + 1 + cur.Length);
                    if (maxHp.Length > 0 && maxHp.Value >= cur.Value)
                    {
                        _store.SetMobMaxHp(realId, maxHp.Value);
                        break;
                    }
                }
            }
            break;
        }
    }

    // ───── identity ─────

    /// <summary>
    /// The self record (<c>33 36</c>) and other players' records (<c>45 36</c>) share one layout:
    /// <c>&lt;opcode&gt; &lt;id&gt; &lt;mask1 u32&gt; &lt;mask2 u8&gt; [mask2 &amp; 1] &lt;len&gt;&lt;name&gt;</c>.
    /// The self record continues with server u16, class u32, a byte and level u32.
    /// </summary>
    private void ScanMaskedIdentity(ReadOnlySpan<byte> data)
    {
        if (data.Length < 9)
            return;
        int i = 0;
        while (i + 8 < data.Length)
        {
            bool isSelf = Opcodes.At(data, i, _op.SelfRecordB);
            if (!isSelf && !Opcodes.At(data, i, _op.PlayerSpawnB))
            {
                i++;
                continue;
            }
            var id = VarInt.Read(data, i + 2);
            if (id.Length <= 0 || id.Value < 1 || id.Value > MaxEntityId)
            {
                i++;
                continue;
            }
            int mask2 = i + 2 + id.Length + 4;
            if (mask2 + 1 >= data.Length || (data[mask2] & 0x01) == 0)
            {
                i++;
                continue;
            }
            int nameLen = data[mask2 + 1];
            if (!Names.IsNameFieldLength(nameLen) || mask2 + 2 + nameLen > data.Length)
            {
                i++;
                continue;
            }
            var field = data.Slice(mask2 + 2, nameLen);
            var raw = Names.DecodeUtf8(field);
            if (raw is null)
            {
                i++;
                continue;
            }
            int after = mask2 + 2 + nameLen;

            if (isSelf && Names.IsPlaceholder(raw))
            {
                // A new character in the tutorial: still you, but with no name yet.
                _store.SetLocalIdentity(id.Value, null);
                i = after;
                continue;
            }
            if (Names.Exact(field) is not { } name)
            {
                i++;
                continue;
            }

            _store.NoteLowIdEntity(id.Value);
            _store.AppendNicknameAuthoritative(id.Value, name);
            if (isSelf)
            {
                _store.SetLocalIdentity(id.Value, name);
                if (after + 6 <= data.Length)
                {
                    ushort server = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(after, 2));
                    var job = JobClasses.FromRosterClass(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(after + 2, 4)));
                    if (server >= 1000 && server < 3000 && job != JobClass.Unknown)
                    {
                        _store.NotePlayerServer(name, server);
                        int? level = null;
                        if (after + 11 <= data.Length)
                        {
                            uint l = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(after + 7, 4));
                            if (l >= 1 && l <= 99)
                                level = (int)l;
                        }
                        _store.NoteSelfProfile(id.Value, name, job, level, server);
                    }
                }
            }
            i = after;
        }
    }

    /// <summary>Fallback naming: a <c>36 &lt;actor&gt;</c> anchor followed within 64 bytes by <c>07 &lt;len&gt;&lt;name&gt;</c>.</summary>
    private bool ParseActorNameBinding(ReadOnlySpan<byte> packet)
    {
        (int Actor, int End)? anchor = null;
        for (int i = 0; i < packet.Length; i++)
        {
            if (packet[i] == 0x36)
            {
                if (i > 0 && _op.SpawnLeadBytes.Contains(packet[i - 1]))
                    continue;
                if (i + 1 >= packet.Length)
                    continue;
                var actor = VarInt.Read(packet, i + 1);
                anchor = actor.Length > 0 && actor.Value >= 100 ? (actor.Value, i + 1 + actor.Length) : null;
                continue;
            }
            if (packet[i] == 0x07 && anchor is { } a && ReadNameAfter(packet, i) is { } n)
            {
                int distance = i - a.End;
                if (distance >= 0 && distance <= 64 && RegisterScannedNickname(packet, a.Actor, n.Start, n.Length))
                    return true;
            }
        }
        return false;
    }

    private static (int Start, int Length)? ReadNameAfter(ReadOnlySpan<byte> packet, int anchorIndex)
    {
        int lenIdx = anchorIndex + 1;
        if (lenIdx >= packet.Length)
            return null;
        int len = packet[lenIdx];
        if (len < 1 || len > 36 || lenIdx + 1 + len > packet.Length)
            return null;
        var s = Names.DecodeUtf8(packet.Slice(lenIdx + 1, len));
        return s is not null && Names.Sanitize(s) is { Length: > 0 } ? (lenIdx + 1, len) : null;
    }

    private bool RegisterScannedNickname(ReadOnlySpan<byte> packet, int actorId, int start, int length)
    {
        if (_store.HasNickname(actorId) || _store.IsSummon(actorId))
            return false;
        if (length <= 0 || length > 36 || start + length > packet.Length)
            return false;
        var s = Names.DecodeUtf8(packet.Slice(start, length));
        if (s is null || Names.Sanitize(s) is not { } name)
            return false;
        _store.AppendNickname(actorId, name);
        return true;
    }

    /// <summary>
    /// Further heuristic name sources: <c>E0/E2 07 &lt;len&gt;&lt;name&gt;</c> after an id,
    /// <c>0F 1D 37 &lt;id&gt; … 00 00 &lt;len&gt;&lt;name&gt;</c>, and <c>(04|00) 4C &lt;id&gt; … &lt;name&gt; ?? 00 36</c>.
    /// </summary>
    private bool ParseNicknamePatterns(ReadOnlySpan<byte> packet)
    {
        bool parsedAny = false;
        int s = 0;
        while (s + 2 < packet.Length)
        {
            // Pattern A: E2/E0 07 anchor.
            if ((packet[s] == 0xE2 || packet[s] == 0xE0) && packet[s + 1] == 0x07)
            {
                int lenIdx = s + 2;
                int len = packet[lenIdx];
                if (len >= 2 && len <= 36 && lenIdx + 1 + len <= packet.Length
                    && Names.ScanCandidate(packet.Slice(lenIdx + 1, len)) is { } name
                    && VarInt.EndingAt(packet, s, 0, 100, MaxEntityId) is int id)
                {
                    _store.AppendNickname(id, name);
                    parsedAny = true;
                    s = SkipGuildName(packet, lenIdx + 1 + len);
                }
            }

            // Pattern B: 0F 1D 37 block anchor.
            if (s + 2 < packet.Length && packet[s] == 0x0F && packet[s + 1] == 0x1D && packet[s + 2] == 0x37)
            {
                int idOffset = s + 3;
                if (VarInt.CanRead(packet, idOffset))
                {
                    var block = VarInt.Read(packet, idOffset);
                    if (block.Value >= 100 && block.Value <= MaxEntityId)
                    {
                        int scan = idOffset + block.Length;
                        int end = Math.Min(packet.Length, scan + 500);
                        while (scan + 3 < end)
                        {
                            if (IsTerminator(packet, scan))
                                break;
                            if (packet[scan] == 0x00 && packet[scan + 1] == 0x00)
                            {
                                int lenIdx = scan + 2;
                                int len = packet[lenIdx];
                                if (len >= 2 && len <= 36 && lenIdx + 1 + len <= packet.Length
                                    && Names.ScanCandidate(packet.Slice(lenIdx + 1, len)) is { } name)
                                {
                                    _store.AppendNickname(block.Value, name);
                                    parsedAny = true;
                                    break;
                                }
                            }
                            scan++;
                        }
                    }
                }
            }

            // Pattern D: (04|00) 4C <player id> … <len><name> ?? 00 36.
            if (s + 1 < packet.Length && (packet[s] == 0x04 || packet[s] == 0x00) && packet[s + 1] == 0x4C)
            {
                int idIdx = s + 2;
                if (VarInt.CanRead(packet, idIdx))
                {
                    var player = VarInt.Read(packet, idIdx);
                    if (player.Length > 0 && player.Value >= 100 && player.Value <= MaxEntityId)
                    {
                        int stop = Math.Min(packet.Length - 2, idIdx + 128);
                        for (int scan = idIdx + player.Length; scan < stop; scan++)
                        {
                            if (!IsTerminator(packet, scan))
                                continue;
                            for (int testLen = 2; testLen <= 36; testLen++)
                            {
                                int lenByte = scan - testLen - 1;
                                if (scan < testLen + 1 + idIdx || lenByte <= idIdx)
                                    continue;
                                if (packet[lenByte] != testLen)
                                    continue;
                                if (Names.ScanCandidate(packet.Slice(lenByte + 1, testLen)) is not { } name)
                                    continue;
                                var before = FindNameBefore(packet, lenByte, idIdx + player.Length);
                                _store.AppendNickname(player.Value, before ?? name);
                                parsedAny = true;
                                s = scan;
                                break;
                            }
                            break;
                        }
                    }
                }
            }
            s++;
        }
        return parsedAny;
    }

    private bool IsTerminator(ReadOnlySpan<byte> p, int at)
    {
        foreach (var sig in _op.SignaturesB)
        {
            if (at + sig.Length <= p.Length && p.Slice(at, sig.Length).SequenceEqual(sig))
                return true;
        }
        return false;
    }

    private static string? FindNameBefore(ReadOnlySpan<byte> packet, int beforeIdx, int minIdx)
    {
        for (int testLen = 2; testLen <= 36; testLen++)
        {
            for (int gap = 0; gap <= 1; gap++)
            {
                if (beforeIdx < gap + testLen + 1)
                    continue;
                int lenIdx = beforeIdx - gap - testLen - 1;
                if (lenIdx < minIdx || packet[lenIdx] != testLen)
                    continue;
                if (Names.ScanCandidate(packet.Slice(lenIdx + 1, testLen)) is { } name)
                    return name;
            }
        }
        return null;
    }

    private static int SkipGuildName(ReadOnlySpan<byte> packet, int start)
    {
        if (start >= packet.Length)
            return start;
        int o = start;
        if (packet[o] == 0x00)
        {
            o++;
            if (o >= packet.Length)
                return o;
        }
        int len = packet[o];
        if (len < 1 || len > 36 || o + 1 + len > packet.Length)
            return o;
        return Names.DecodeUtf8(packet.Slice(o + 1, len)) is null ? o : o + 1 + len;
    }

    // ───── party roster (02 97) ─────

    private void ScanPartyRoster(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
            return;
        int i = 0;
        while (i + 24 < data.Length)
        {
            if (!Opcodes.At(data, i, _op.PartyRosterB))
            {
                i++;
                continue;
            }
            var parsed = PartyRosterParser.Parse(data, i + 2);
            if (LogRosterBytes)
                LogRoster(data, i, parsed);
            if (parsed is { } roster)
            {
                _store.SetCurrentDungeon(roster.DungeonId);
                _store.SetPartyRoster(roster.Members, roster.Complete);
                i += 2;
            }
            else
            {
                i++;
            }
        }
    }

    // Fork (Faro): diagnóstico de la lista del grupo. Cuando una lista no se pudo leer entera, sus bytes quedan en el
    // log (una vez por contenido distinto, hasta 40 por sesión), más las 3 primeras que sí se leyeron, para comparar
    // y mejorar el lector. Solo mira lo que el servidor manda a esta PC (sin cifrar); no toca el juego.
    public bool LogRosterBytes { get; set; } = true;
    private readonly HashSet<int> _rosterLogged = new();
    private int _rosterOkLogged;

    private void LogRoster(ReadOnlySpan<byte> data, int at, PartyRoster? roster)
    {
        if (_rosterLogged.Count >= 40)
            return;
        bool complete = roster is { Complete: true };
        if (complete && _rosterOkLogged >= 3)
            return;
        // Solo si el encabezado parece de verdad una lista (nombre del grupo y tamaño razonables), no un «02 97» suelto.
        int o = at + 2 + 4;
        if (o >= data.Length)
            return;
        int nameLen = data[o];
        if (nameLen < 1 || nameLen > 40 || o + 1 + nameLen >= data.Length || Names.DecodeUtf8(data.Slice(o + 1, nameLen)) is null)
            return;
        int size = data[o + 1 + nameLen];
        if (size < 1 || size > 12)
            return;
        var slice = data.Slice(at, Math.Min(900, data.Length - at));
        var hash = new HashCode();
        hash.AddBytes(slice);
        if (!_rosterLogged.Add(hash.ToHashCode()))
            return;
        if (complete)
            _rosterOkLogged++;
        string what = roster is null ? "unreadable" : complete ? $"complete {roster.Members.Count}" : $"partial {roster.Members.Count}";
        _store.TraceDiag($"party roster bytes ({what}, size {size}): {Convert.ToHexString(slice)}");
    }

    // ───── damage (04 38) ─────

    private bool TryParseEmbeddedDamage(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6)
            return false;
        bool parsedAny = false;
        foreach (var code in _op.DamageB)
        {
            int s = 0;
            while (s + 1 < packet.Length)
            {
                int rel = packet[s..].IndexOf(code);
                if (rel < 0)
                    break;
                s += rel;

                string key = Convert.ToHexString(packet[s..Math.Min(s + 64, packet.Length)]);
                if (_seenEmbedded.Contains(key))
                {
                    s++;
                    continue;
                }

                // Give the record a fake two-byte length header so the regular parser can read it.
                var headless = new byte[packet.Length - s + 2];
                headless[0] = 0xFF;
                headless[1] = 0x01;
                packet[s..].CopyTo(headless.AsSpan(2));

                if (ParseDamage(headless, allowEmbeddedScan: false, requireTrusted: true))
                {
                    _seenEmbedded.Add(key);
                    parsedAny = true;
                    s += 2;
                }
                else
                {
                    s++;
                }
            }
        }
        return parsedAny;
    }

    private bool ParseDamage(ReadOnlySpan<byte> packet, bool allowEmbeddedScan, bool requireTrusted)
    {
        int offset = BodyOffset(packet);
        if (offset < 0 || offset + 1 >= packet.Length)
            return false;

        if (!Opcodes.At(packet, offset, _op.DamageB))
            return allowEmbeddedScan && TryParseEmbeddedDamage(packet);
        offset += 2;

        bool parsedAny = false;
        const int mask = 0x0F;

        while (offset < packet.Length)
        {
            // Chained hit marker.
            bool chained = false;
            if (offset + 1 < packet.Length && packet[offset] == 0x01 && packet[offset + 1] == 0x00)
            {
                offset += 2;
                chained = true;
            }
            if (parsedAny && !chained)
                break;

            if (!VarInt.TryRead(packet, ref offset, out int targetId) || !_store.IsPlausibleEntityId(targetId))
                break;
            if (!VarInt.TryRead(packet, ref offset, out int switchValue))
                break;
            int andResult = switchValue & mask;
            if (andResult < 4 || andResult > 7)
                break;
            if (!VarInt.TryRead(packet, ref offset, out _))
                break;
            if (!VarInt.TryRead(packet, ref offset, out int actorId) || !_store.IsPlausibleEntityId(actorId))
                break;

            if (offset + 4 > packet.Length)
                break;
            long exactSkill = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(offset, 4));
            offset += 4;

            // Theostone raw item ids.
            if (exactSkill >= 3_000_000 && exactSkill <= 3_099_999)
                exactSkill = exactSkill * 10 + 1;
            if (exactSkill < 1 || exactSkill > 299_999_999)
                break;
            // 7-digit NPC skills.
            if (exactSkill >= 1_000_000 && exactSkill <= 9_999_999)
                break;

            // One-byte uid field.
            if (offset < packet.Length)
                offset++;

            if (!VarInt.TryRead(packet, ref offset, out int damageTypeRaw))
                break;
            int damageType = (byte)damageTypeRaw;

            int tempV = andResult switch { 5 => 12, 6 => 10, 7 => 14, _ => 8 };

            // <damage_type> <modifications byte> 00 <direction byte>.
            var flags = HitFlags.None;
            if (andResult is 5 or 6 or 7 && offset < packet.Length)
            {
                int mods = packet[offset];
                if ((mods & 0x02) != 0) flags |= HitFlags.Parry;
                if ((mods & 0x04) != 0) flags |= HitFlags.Perfect;
                if ((mods & 0x08) != 0) flags |= HitFlags.Double;
                if ((mods & 0x20) != 0) flags |= HitFlags.Smite;
                if ((mods & 0x40) != 0) flags |= HitFlags.PowerShard;
                if (offset + 2 < packet.Length)
                {
                    if (packet[offset + 2] == 0x01) flags |= HitFlags.Back;
                    else if (packet[offset + 2] == 0x02) flags |= HitFlags.Front;
                }
            }
            if (damageType == 3)
                flags |= HitFlags.Critical;

            offset += tempV;
            if (offset >= packet.Length)
                break;

            if (!VarInt.TryRead(packet, ref offset, out int firstValue))
                break;
            int afterFirst = offset;
            if (!VarInt.TryRead(packet, ref offset, out int secondValue))
                break;

            // Post-2026-06 layout: a zero pad and the actor's power scalar precede the real value.
            if (firstValue == 0)
            {
                int afterSecond = offset;
                if (VarInt.TryRead(packet, ref offset, out int third))
                {
                    firstValue = secondValue;
                    afterFirst = afterSecond;
                    secondValue = third;
                }
            }

            bool firstIsDamage = FirstValueIsDamage(firstValue, secondValue, andResult, damageType);
            int finalDamage;
            if (firstIsDamage)
            {
                offset = afterFirst;
                finalDamage = firstValue;
            }
            else
            {
                finalDamage = secondValue;
            }

            // Multi-hit extra field.
            if ((switchValue & 0x30) == 0x30 && offset < packet.Length)
                VarInt.TryRead(packet, ref offset, out _);

            int hitCount = 0;
            int preHit = offset;
            if (offset < packet.Length && !IsMarkerAt(packet, offset))
            {
                if (VarInt.TryRead(packet, ref offset, out int peek))
                {
                    if (peek >= 0 && peek <= 25)
                    {
                        hitCount = peek;
                    }
                    else if (!IsMarkerAt(packet, offset) && VarInt.TryRead(packet, ref offset, out int actual))
                    {
                        if (actual >= 0 && actual <= 25)
                            hitCount = actual;
                        else
                            offset = preHit;
                    }
                }
            }

            if (finalDamage < 0 || finalDamage > 99_999_999)
                break;

            int multiHitCount = 0;
            int multiHitDamage = 0;
            int? firstMulti = null;
            bool allMatch = true;

            if (hitCount > 0 && offset < packet.Length)
            {
                int safeMax = Math.Min(hitCount, 25);
                int cap = Math.Max(finalDamage, 500_000);
                int read = 0;
                while (read < safeMax && offset < packet.Length)
                {
                    bool nextPacket = offset + 1 < packet.Length && Opcodes.At(packet, offset, _op.DamageB);
                    if (IsMarkerAt(packet, offset) || nextPacket)
                        break;
                    if (!VarInt.TryRead(packet, ref offset, out int hit))
                        break;
                    if (hit > cap || hit < 50)
                    {
                        multiHitDamage = 0;
                        firstMulti = null;
                        allMatch = true;
                        break;
                    }
                    if (firstMulti is null)
                        firstMulti = hit;
                    else if (firstMulti != hit)
                        allMatch = false;
                    multiHitDamage += hit;
                    read++;
                }
                multiHitCount = read;
            }

            if (switchValue == 54 && hitCount > multiHitCount && multiHitCount == 1 && firstMulti is int fm && allMatch)
            {
                multiHitCount = hitCount;
                multiHitDamage = fm * hitCount;
            }

            if (UseRepeatedHitDamage(switchValue, secondValue, multiHitCount, firstMulti, allMatch))
                finalDamage = firstMulti!.Value;

            if (multiHitCount > 0 && multiHitDamage > 0 && finalDamage > multiHitDamage)
                finalDamage -= multiHitDamage;

            var pending = _pendingCompactSkill;
            bool aggregatedCompact = pending is { } pc
                && exactSkill == 99_745_942
                && actorId == pc.ActorId
                && hitCount > 1
                && multiHitDamage > 0
                && secondValue > multiHitDamage;

            int resolvedSkill = aggregatedCompact ? pending!.Value.SkillRaw : NormalizeSkillId((int)exactSkill);
            if (aggregatedCompact)
            {
                finalDamage = secondValue - multiHitDamage;
                _pendingCompactSkill = null;
            }

            // Life-steal suffix: 03 00 <heal varint>.
            int heal = 0;
            if (offset + 1 < packet.Length && packet[offset] == 0x03 && packet[offset + 1] == 0x00)
            {
                offset += 2;
                if (VarInt.TryRead(packet, ref offset, out int h) && h > 0 && h < 10_000_000)
                    heal = h;
            }

            if (requireTrusted && !IsTrustedRecoveredShape(actorId, targetId, damageType, finalDamage, resolvedSkill))
                break;

            if (actorId != targetId)
            {
                AppendDamage(new DamageEvent
                {
                    TimestampMs = CurrentTimestampMs,
                    TargetId = targetId,
                    ActorId = actorId,
                    SkillCode = resolvedSkill,
                    DamageType = damageTypeRaw,
                    Flags = flags,
                    MultiHitCount = multiHitCount,
                    MultiHitDamage = multiHitDamage,
                    HealAmount = heal,
                    Damage = finalDamage,
                });
            }
            else if (finalDamage > 1 && _store.IsKnownPlayer(actorId))
            {
                // A self-cast record from a player is an instant self-heal.
                _store.AppendHeal(actorId, resolvedSkill, finalDamage, false, CurrentTimestampMs);
            }

            parsedAny = true;
        }
        return parsedAny;
    }

    /// <summary><c>01..07 00</c>: the marker that starts the next chained record.</summary>
    private static bool IsMarkerAt(ReadOnlySpan<byte> p, int o) =>
        o + 1 < p.Length && p[o + 1] == 0x00 && p[o] >= 1 && p[o] <= 7;

    private static bool FirstValueIsDamage(int first, int second, int andResult, int damageType)
    {
        if (first < 1_000 || first > 99_999_999)
            return false;
        if (second < 0 || second > 25)
            return false;
        if (first > 5_000_000)
            return false;
        return andResult == 6 && damageType == 3;
    }

    private static bool UseRepeatedHitDamage(int switchValue, int encoded, int multiHitCount, int? firstMulti, bool allMatch)
    {
        if (firstMulti is not int repeated || switchValue != 54 || multiHitCount <= 0 || !allMatch)
            return false;
        int main = encoded - multiHitCount * repeated;
        if (main > repeated)
            return false;
        return encoded / 10 == repeated;
    }

    private (int ActorId, int SkillRaw)? ExtractPendingCompactSkill(ReadOnlySpan<byte> packet)
    {
        var len = VarInt.Read(packet, 0);
        if (len.Length <= 0 || len.Length >= packet.Length)
            return null;
        var body = packet[len.Length..];

        // Marker 08 3B|3D 38 00 00.
        int marker = -1;
        for (int i = 0; i + 4 < body.Length; i++)
        {
            if (body[i] == 0x08 && (body[i + 1] == 0x3B || body[i + 1] == 0x3D)
                && body[i + 2] == 0x38 && body[i + 3] == 0x00 && body[i + 4] == 0x00)
            {
                marker = i;
                break;
            }
        }
        if (marker < 0)
            return null;

        int op = -1;
        for (int i = marker + 5; i < body.Length; i++)
        {
            if (body[i] == 0x38)
            {
                op = i;
                break;
            }
        }
        if (op < 0 || op + 2 >= body.Length)
            return null;

        var actor = VarInt.Read(body, op + 1);
        if (actor.Length <= 0 || actor.Value < 100)
            return null;
        int skillOffset = op + 1 + actor.Length + 1;
        if (skillOffset + 3 > body.Length)
            return null;

        var candidates = new List<int>(2);
        if (skillOffset + 4 <= body.Length)
            candidates.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(skillOffset, 4)));
        candidates.Add(body[skillOffset] | body[skillOffset + 1] << 8 | body[skillOffset + 2] << 16);

        foreach (int c in candidates)
        {
            if (IsKnownSkillCode(c))
                return (actor.Value, NormalizeSkillId(c));
        }
        return null;
    }

    // ───── skill helpers ─────

    private static bool IsValidSkillCode(int code) => code >= 1 && code <= 299_999_999;

    /// <summary>Collapses a skill variant (specialisation suffix) onto its base skill when they share a name.</summary>
    private int NormalizeSkillId(int raw)
    {
        if (raw >= 30_000_000 && raw <= 30_999_999)
            return raw;
        int baseCode = raw - raw % 10000;
        string baseName = _data.SkillName(baseCode);
        if (baseName.Length == 0)
            return raw;
        string rawName = _data.SkillName(raw);
        if (rawName.Length == 0)
            return baseCode;
        return rawName != baseName ? raw : baseCode;
    }

    private bool IsKnownSkillCode(int code)
    {
        if (!IsValidSkillCode(code))
            return false;
        int normalized = NormalizeSkillId(code);
        if (!IsValidSkillCode(normalized))
            return false;
        if (normalized >= 30_000_000 && normalized <= 30_999_999)
            return true;
        return _data.SkillName(normalized).Length > 0 || _data.SkillName(code).Length > 0;
    }

    private bool IsTrustedRecoveredShape(int actor, int target, int damageType, int damage, int skill) =>
        actor != target && damageType >= 1 && damageType <= 3 && damage > 0 && IsKnownSkillCode(skill);

    /// <summary>Small FIFO set used to avoid counting an embedded damage record twice.</summary>
    private sealed class BoundedSet(int capacity)
    {
        private readonly HashSet<string> _set = new();
        private readonly Queue<string> _order = new();

        public bool Contains(string key) => _set.Contains(key);

        public void Add(string key)
        {
            if (!_set.Add(key))
                return;
            _order.Enqueue(key);
            if (_order.Count > capacity)
                _set.Remove(_order.Dequeue());
        }
    }
}
