using Aion2DpsMeter.Core.Capture;
using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.History;
using Aion2DpsMeter.Core.Progress;
using Aion2DpsMeter.Core.Protocol;

namespace Aion2DpsMeter.Core;

public sealed class HistoryOptions
{
    /// <summary>Boss fights are always saved; other fights only when at least this long.</summary>
    public int MinTrashFightSeconds { get; set; } = 15;
    public bool SaveTrash { get; set; } = true;
    public bool SaveTrainingDummy { get; set; } = true;
}

/// <summary>
/// Wires capture, parsing, combat tracking and history together. One background thread reads
/// captured segments and drives the parser; the UI polls <see cref="Store"/> for the live fight.
/// </summary>
public sealed class MeterEngine : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private LiveCapture? _live;
    private Thread? _worker;
    private PacketRecorder? _recorder;
    private volatile bool _disposing;

    public GameData Data { get; }
    public Opcodes Opcodes { get; }
    public CombatStore Store { get; }
    /// <summary>Fork (Faro): tickets, energy, weekly counters and daily gains of the characters seen.</summary>
    public ProgressTracker Progress { get; } = new();
    /// <summary>Fork (Faro): who killed you in PvP, with their class and gear.</summary>
    public Pvp.PvpTracker Pvp { get; } = new();
    public CaptureDispatcher Dispatcher { get; }
    public HistoryRepository History { get; }
    public HistoryOptions HistoryOptions { get; }

    /// <summary>Raised after a finished fight was written to the history.</summary>
    public event Action<EncounterRecord>? EncounterSaved;
    public event Action<Exception>? Error;

    public MeterEngine(GameData data, Opcodes opcodes, HistoryRepository history, CombatOptions combat, HistoryOptions historyOptions)
    {
        Data = data;
        Opcodes = opcodes;
        History = history;
        HistoryOptions = historyOptions;
        Store = new CombatStore(data, combat);
        Pvp.SkillName = Store.SkillDisplayName;
        Pvp.NameOf = Store.NicknameOf;
        Dispatcher = new CaptureDispatcher(Store, data, opcodes, Progress, Pvp);
        Store.EncounterEnded += OnEncounterEnded;
    }

    public bool IsRecording => _recorder is not null;
    public string? RecordingFile => _recorder?.CurrentFile;

    /// <summary>Starts recording the game stream to <paramref name="directory"/> as .pcap files.</summary>
    public void StartRecording(string directory)
    {
        if (_recorder is not null)
            return;
        var recorder = new PacketRecorder(directory);
        Dispatcher.GamePacket += recorder.Write;
        _recorder = recorder;
    }

    public void StopRecording()
    {
        if (_recorder is not { } r)
            return;
        Dispatcher.GamePacket -= r.Write;
        _recorder = null;
        r.Dispose();
    }

    /// <summary>Starts live capture. Returns false (with a reason) when Npcap is missing or no adapter opens.</summary>
    public bool StartLive(out string error)
    {
        if (!LiveCapture.IsNpcapAvailable(out error))
        {
            error = "Npcap is not installed (install it with \"WinPcap API-compatible Mode\" enabled). " + error;
            Dispatcher.SetStatus(new CaptureStatus(CaptureState.Error, "Npcap not installed - click here to download it"));
            return false;
        }

        _live = new LiveCapture();
        int opened = _live.Start();
        if (opened == 0)
        {
            error = "No network adapter could be opened for capture. Try running the meter as administrator.";
            Dispatcher.SetStatus(new CaptureStatus(CaptureState.Error, "No adapter could be opened"));
            return false;
        }

        Dispatcher.RequireGameProcess = true;
        Dispatcher.SetStatus(new CaptureStatus(CaptureState.Searching, $"Capturing on {opened} adapter(s)"));
        var reader = _live.Reader;
        _worker = new Thread(() => Run(reader)) { IsBackground = true, Name = "Aion2 capture" };
        _worker.Start();
        error = "";
        return true;
    }

    private void Run(System.Threading.Channels.ChannelReader<TcpPayload> reader)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!reader.WaitToReadAsync(_cts.Token).AsTask().GetAwaiter().GetResult())
                    break;
                while (reader.TryRead(out var payload))
                {
                    try
                    {
                        Dispatcher.Process(payload);
                    }
                    catch (Exception ex)
                    {
                        Error?.Invoke(ex);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Replays a .pcap recording through the parser as fast as possible. Returns the packets read.</summary>
    public int Replay(string pcapPath)
    {
        // A dispatcher of its own: the live one runs on the capture thread and is paused meanwhile.
        var replay = new CaptureDispatcher(Store, Data, Opcodes, Progress, Pvp) { RequireGameProcess = false };
        Dispatcher.Suspended = true;
        try
        {
            int count = 0;
            long last = 0;
            foreach (var payload in PcapFileReader.Read(pcapPath))
            {
                replay.Process(payload);
                last = payload.TimestampMs;
                count++;
            }
            // Let the last fight end as if time had passed.
            Store.Tick(last + Store.Options.IdleTimeoutMs + 1);
            return count;
        }
        finally
        {
            Dispatcher.Suspended = false;
        }
    }

    /// <summary>Drives fight timeouts by the wall clock; call it a few times a second while capturing live.</summary>
    public void Tick() => Store.Tick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private void OnEncounterEnded(EncounterRecord r)
    {
        if (!ShouldSave(r))
            return;
        void Save()
        {
            try
            {
                History.Save(r);
                EncounterSaved?.Invoke(r);
            }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
            }
        }
        // While closing, save before the process exits.
        if (_disposing)
            Save();
        else
            ThreadPool.QueueUserWorkItem(_ => Save());
    }

    private bool ShouldSave(EncounterRecord r)
    {
        if (r.TotalDamage <= 0 || r.Players.Count == 0)
            return false;
        if (r.IsTrainingDummy)
            return HistoryOptions.SaveTrainingDummy && r.DurationMs >= HistoryOptions.MinTrashFightSeconds * 1000L;
        if (r.IsBoss)
            return true;
        return HistoryOptions.SaveTrash && r.DurationMs >= HistoryOptions.MinTrashFightSeconds * 1000L;
    }

    public void Dispose()
    {
        _disposing = true;
        _cts.Cancel();
        _live?.Dispose();
        _worker?.Join(2000);
        StopRecording();
        // Save the fight in progress when the meter closes.
        Store.Reset();
    }
}
