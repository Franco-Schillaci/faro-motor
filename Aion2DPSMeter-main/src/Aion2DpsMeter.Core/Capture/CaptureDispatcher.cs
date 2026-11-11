using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.Protocol;

namespace Aion2DpsMeter.Core.Capture;

public enum CaptureState
{
    Stopped,
    /// <summary>No AION2 process is running; packets are ignored.</summary>
    WaitingForGame,
    /// <summary>The game runs; looking for the connection that carries combat.</summary>
    Searching,
    /// <summary>Locked onto the game connection.</summary>
    Locked,
    Error,
}

public sealed record CaptureStatus(CaptureState State, string Message, ushort? Port = null, string? Device = null);

/// <summary>
/// Finds the game connection among everything captured and routes its server-to-client stream
/// through the parser. Before a port is locked, only packets carrying the game's record
/// terminator are considered; a flow locks once it sustains that signature at the game's rate.
/// Ported in spirit from A2Tools (GPL-3.0), <c>combat/capture_dispatcher.rs</c>.
/// </summary>
public sealed class CaptureDispatcher
{
    /// <summary>Signature-bearing packets a flow must produce within the window to lock.</summary>
    private const int SignatureLockThreshold = 12;
    private const long SignatureWindowMs = 3_000;
    /// <summary>A loopback relay (ping reducer) carries the stream the client really reads; give it time to show up.</summary>
    private const long LoopbackGraceMs = 2_500;
    private const long StaleConnectionMs = 120_000;
    private const long GameCheckStoppedMs = 10_000;
    private const long GameCheckRunningMs = 60_000;

    private readonly CombatStore _store;
    private readonly GameData _data;
    private readonly Opcodes _opcodes;
    private readonly Progress.ProgressTracker? _progress; // fork (Faro)
    private readonly Pvp.PvpTracker? _pvp; // fork (Faro)
    private readonly Dictionary<string, TcpStream> _streams = new();
    private readonly Dictionary<string, (int Count, long LastMs)> _signatureHits = new();

    private ushort? _lockedPort;
    private string? _lockedDevice;
    private long _firstCandidateMs;
    private long _lastParsedMs;
    /// <summary>When the game process was last looked for; null = never (checked on the first packet).</summary>
    private long? _lastGameCheckMs;
    private bool _gameRunning;
    private CaptureStatus _status = new(CaptureState.Stopped, "Stopped");

    /// <summary>Only process traffic while an AION2 process runs. Off for replays.</summary>
    public bool RequireGameProcess { get; set; } = true;

    /// <summary>While set, captured packets are dropped (the meter is paused).</summary>
    public bool Suspended { get => _suspended; set => _suspended = value; }
    private volatile bool _suspended;

    public CaptureStatus Status => _status;
    public event Action<CaptureStatus>? StatusChanged;
    /// <summary>Raised for every packet of the locked game stream (server to client), e.g. for recording.</summary>
    public event Action<TcpPayload>? GamePacket;

    public CaptureDispatcher(CombatStore store, GameData data, Opcodes opcodes, Progress.ProgressTracker? progress = null, Pvp.PvpTracker? pvp = null)
    {
        _store = store;
        _data = data;
        _opcodes = opcodes;
        _progress = progress;
        _pvp = pvp;
    }

    /// <summary>Processes one captured segment. Must be called from a single thread.</summary>
    public void Process(TcpPayload p)
    {
        if (Suspended)
            return;

        long now = p.TimestampMs;
        if (RequireGameProcess)
        {
            long interval = _gameRunning ? GameCheckRunningMs : GameCheckStoppedMs;
            if (_lastGameCheckMs is not long last || now - last >= interval || now < last)
            {
                _lastGameCheckMs = now;
                bool running = IsGameRunning();
                if (!running && _gameRunning)
                    ResetLock();
                _gameRunning = running;
                if (!running)
                    SetStatus(new CaptureStatus(CaptureState.WaitingForGame, "Waiting for AION2 to start"));
                else if (_lockedPort is null)
                    SetStatus(new CaptureStatus(CaptureState.Searching, "Looking for the game connection"));
            }
            if (!_gameRunning)
                return;
        }
        else if (_status.State is CaptureState.Stopped or CaptureState.WaitingForGame)
        {
            SetStatus(new CaptureStatus(CaptureState.Searching, "Looking for the game connection"));
        }

        if (_lockedPort is not null && _lastParsedMs > 0 && now - _lastParsedMs > StaleConnectionMs)
            ResetLock();

        if (_lockedPort is ushort port)
        {
            if (!string.Equals(p.Device, _lockedDevice, StringComparison.OrdinalIgnoreCase))
                return;
            // Only the server-to-client direction carries what we parse.
            if (p.SrcPort != port)
            {
                if (p.DstPort == port)
                    _lastParsedMs = now; // the connection is alive
                return;
            }
        }
        else
        {
            if (LooksLikeTls(p.Data) || !Opcodes.ContainsAny(p.Data, _opcodes.SignaturesB))
                return;
        }

        string key = p.Device + "|" + p.FlowKey;
        if (!_streams.TryGetValue(key, out var stream))
            _streams[key] = stream = new TcpStream(new PacketParser(_store, _data, _opcodes, _progress, _pvp));

        if (_lockedPort is null)
        {
            if (_firstCandidateMs == 0)
                _firstCandidateMs = now;
            var hit = _signatureHits.GetValueOrDefault(key);
            if (now - hit.LastMs > SignatureWindowMs)
                hit.Count = 0;
            _signatureHits[key] = (hit.Count + 1, now);
        }
        else
        {
            GamePacket?.Invoke(p);
        }

        bool parsed = stream.Add(p.Sequence, p.Data, now);
        if (parsed)
            _lastParsedMs = now;

        if (_lockedPort is null && _signatureHits.GetValueOrDefault(key).Count >= SignatureLockThreshold)
        {
            bool graceOver = p.IsLoopbackDevice || now - _firstCandidateMs >= LoopbackGraceMs;
            if (graceOver)
                Lock(p, key);
        }

        _store.Tick(now);
    }

    private void Lock(TcpPayload p, string key)
    {
        _lockedPort = p.SrcPort;
        _lockedDevice = p.Device;
        _lastParsedMs = p.TimestampMs;
        foreach (var other in _streams.Keys.Where(k => k != key).ToList())
            _streams.Remove(other);
        _signatureHits.Clear();
        SetStatus(new CaptureStatus(CaptureState.Locked, $"Connected to game (port {p.SrcPort})", p.SrcPort, p.Device));
    }

    public void ResetLock()
    {
        _lockedPort = null;
        _lockedDevice = null;
        _firstCandidateMs = 0;
        _lastParsedMs = 0;
        _streams.Clear();
        _signatureHits.Clear();
        SetStatus(new CaptureStatus(CaptureState.Searching, "Looking for the game connection"));
    }

    public void SetStatus(CaptureStatus status)
    {
        if (status == _status)
            return;
        _status = status;
        StatusChanged?.Invoke(status);
    }

    private static bool LooksLikeTls(ReadOnlySpan<byte> d) =>
        d.Length >= 3 && d[0] >= 0x14 && d[0] <= 0x17 && d[1] == 0x03 && d[2] <= 0x04;

    public static bool IsGameRunning()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("AION2");
        bool any = processes.Length > 0;
        foreach (var p in processes)
            p.Dispose();
        return any;
    }
}
