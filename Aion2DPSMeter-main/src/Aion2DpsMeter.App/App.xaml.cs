using System.IO;
using System.Windows;
using System.Windows.Threading;
using Aion2DpsMeter.App.Infrastructure;
using Aion2DpsMeter.App.ViewModels;
using Aion2DpsMeter.App.Views;
using Aion2DpsMeter.Core;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.History;
using Aion2DpsMeter.Core.Protocol;

namespace Aion2DpsMeter.App;

/// <summary>Application controller: owns the engine, the overlay, hotkeys and the tray icon.</summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = new();
    private MeterEngine? _engine;
    private MeterWindow? _meter;
    private TrayIcon? _tray;
    private HotkeyManager? _hotkeys;
    private HistoryWindow? _historyWindow;
    private DetailsWindow? _detailsWindow;
    private DispatcherTimer? _refresh;
    private bool _replaying;
    private FaroBridge? _faro; // fork: live data for the Faro overlay
    private bool _faroMode;    // fork: --faro, running as Faro's built-in engine (no UI of its own)
    private bool _demo;
    private bool _exiting;
    private DispatcherTimer? _parentWatch;

    private readonly MeterViewModel _vm = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Logger.Error("Unhandled exception", args.ExceptionObject as Exception);

        _faroMode = e.Args.Contains("--faro", StringComparer.OrdinalIgnoreCase);

        _singleInstance = new Mutex(true, "Aion2DpsMeter.SingleInstance", out bool first);
        if (!first)
        {
            if (_faroMode)
                Logger.Info("Another meter is already running; this Faro engine instance exits.");
            else
                MessageBox.Show("The AION 2 DPS Meter is already running (see the tray icon).", "AION 2 DPS Meter");
            Shutdown();
            return;
        }

        Logger.Info("Starting");
        _settings = AppSettings.Load();
        _vm.Mode = _settings.Mode;

        var data = GameData.Load(AppPaths.GameDataDir);
        if (data.Skills.Count == 0)
            Logger.Error($"Game data not found in {AppPaths.GameDataDir}");
        Opcodes opcodes;
        try
        {
            opcodes = Opcodes.Load(AppPaths.OpcodesFile);
        }
        catch (Exception ex)
        {
            Logger.Error("opcodes.json is invalid; using the defaults", ex);
            opcodes = Opcodes.Default;
        }

        // Demo fights go to a history of their own.
        bool demo = _demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        var historyDb = demo ? Path.Combine(AppPaths.DataDir, "history-demo.db") : AppPaths.HistoryDb;
        _engine = new MeterEngine(data, opcodes, new HistoryRepository(historyDb), _settings.Combat, _settings.HistorySaving);
        _engine.Error += ex => Logger.Error("Engine", ex);
        // Fork (Faro): write party rosters to the log, to diagnose members without combat power.
        _engine.Store.Trace += msg => { if (msg.StartsWith("party roster", StringComparison.Ordinal)) Logger.Info(msg); };
        _engine.Store.RememberedLocalName = _settings.LastCharacterName;
        _engine.Store.LocalIdentityChanged += (id, name, source) =>
        {
            Logger.Info($"You are entity {id}{(name is null ? "" : $" '{name}'")} (from {source})");
            if (source == "self record" && !string.IsNullOrEmpty(name) && !demo)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _settings.LastCharacterName = name;
                    _settings.Save();
                });
            }
        };
        _engine.EncounterSaved += r =>
        {
            Logger.Info($"Saved fight: {r.Name}, {Format.Duration(r.DurationMs)}, {Format.Number(r.TotalDamage)} damage");
            Dispatcher.BeginInvoke(() => _historyWindow?.Reload());
        };
        _engine.Dispatcher.StatusChanged += status =>
        {
            Logger.Info($"Capture: {status.State} {status.Message}");
            Dispatcher.BeginInvoke(() => _vm.SetStatus(status));
        };

        // Fork: send the live fight to Faro (local PC only) and let Faro's reset button reset the meter.
        _faro = new FaroBridge();
        _faro.ResetRequested += () => Dispatcher.BeginInvoke(ResetMeter);
        _engine.EncounterSaved += r => _faro?.SendEnded(r);
        // Fork: your character's tickets, energy and dailies (as the server reports them), to Faro and the log.
        _engine.Progress.Changed += p => _faro?.SendProgress(p);
        _engine.Progress.PetsChanged += p => _faro?.SendPets(p);
        _engine.Progress.PetsGainedBeforeCollection += d => _faro?.SendPetsDelta(d);
        // Fork: each monster you or your party kill, to the log and to Faro (pet maxed warning and loot counter).
        // Code 0 = it spawned before the meter started: Faro still counts the kill for the loot.
        _engine.Store.MobKilled += (id, code) =>
        {
            Logger.Info(code > 0 ? $"kill: npc {code} (entity {id})" : $"kill: entity {id} without npc code (it spawned before the meter started)");
            _faro?.SendKill(code, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        };
        _engine.Progress.Trace += msg => Logger.Info(msg);
        // Fork: you died to a player: who it was, who else hit you and their gear, to Faro's PvP tab.
        _engine.Pvp.Died += d => _faro?.SendDeath(d);
        _engine.Pvp.Trace += msg => Logger.Info(msg);
        // Fork: packets the capture missed on the game connection (e.g. an enemy stuck in «Enemigos cerca»).
        _engine.Dispatcher.Trace += msg => Logger.Info(msg);
        // Fork: «captura de momentos» — the last 30 s of game packets, saved to a file when Faro asks (Ctrl+Alt+K).
        if (!demo)
        {
            PacketParser.Recorder = new MomentRecorder();
            _faro.MarkRequested += () =>
            {
                try
                {
                    var saved = PacketParser.Recorder?.Dump(AppPaths.CapturesDir);
                    Logger.Info(saved is { } s ? $"capture saved: {s.Path} ({s.Count} packets)" : "capture: nothing to save yet");
                    _faro?.SendMark(saved?.Path, saved?.Count ?? 0);
                }
                catch (Exception ex)
                {
                    Logger.Error("capture", ex);
                    _faro?.SendMark(null, 0);
                }
            };
        }

        _meter = new MeterWindow(this, _vm, _settings);
        if (_faroMode)
        {
            // --faro: this meter is Faro's built-in engine. No panel, no tray icon and no hotkeys of its
            // own (Faro shows everything and has the hotkeys). It closes when Faro asks, or when Faro's
            // process (--parent-pid) is gone, so it never stays behind after a crash.
            _faro.QuitRequested += () => Dispatcher.BeginInvoke(ExitApp);
            WatchParent(e.Args);
        }
        else
        {
            _meter.Show();
            _meter.SetClickThrough(_settings.ClickThrough);

            _tray = new TrayIcon(ToggleMeterVisibility, ShowHistory, ShowSettings, ResetMeter, ToggleClickThrough, ExitApp);

            if (_meter.Source is { } source)
            {
                _hotkeys = new HotkeyManager(source);
                RegisterHotkeys();
            }
        }

        if (demo)
        {
            _vm.Status = "○ Demo mode: made-up fight, nothing is captured";
            int snapshotArg = Array.FindIndex(e.Args, a => a.Equals("--snapshot", StringComparison.OrdinalIgnoreCase));
            if (snapshotArg >= 0 && snapshotArg + 1 < e.Args.Length)
            {
                // A short fight, so it ends and reaches the history within the snapshot run.
                new DemoFeeder(_engine.Store, bossHp: 6_000_000).Start();
                RunSnapshots(e.Args[snapshotArg + 1]);
            }
            else
            {
                new DemoFeeder(_engine.Store).Start();
            }
        }
        else if (!_engine.StartLive(out string error))
        {
            Logger.Error(error);
            _vm.SetStatus(_engine.Dispatcher.Status);
            // After startup, so the panel keeps running while the message is open.
            // As Faro's engine there is no message box: Faro shows the status it receives.
            if (!_faroMode)
                Dispatcher.BeginInvoke(() => MessageBox.Show(error + "\n\nNpcap: https://npcap.com/#download", "AION 2 DPS Meter",
                    MessageBoxButton.OK, MessageBoxImage.Warning));
        }
        else
        {
            ApplyRecording();
        }

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();

        // Fork (Faro): as Faro's engine the meter does not look for releases of the original project (Faro updates
        // itself, with the engine inside, from its own releases).
        if (!demo && !_faroMode)
        {
            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _updateTimer.Tick += async (_, _) =>
            {
                _updateTimer.Interval = TimeSpan.FromHours(6);
                await CheckForUpdatesAsync();
            };
            _updateTimer.Start();
        }
    }

    private DispatcherTimer? _updateTimer;
    private UpdateInfo? _update;

    private async Task CheckForUpdatesAsync()
    {
        if (!_settings.CheckForUpdates)
            return;
        try
        {
            var update = await UpdateChecker.CheckAsync();
            if (update is null || update.Version == _update?.Version)
                return;
            _update = update;
            Logger.Info($"Update available: {update.Tag}");
            _vm.UpdateLabel = $"Version {update.Tag} is available — click to download";
            _tray?.ShowUpdate($"Download update {update.Tag}…", OpenUpdate);
            _tray?.ShowBalloon("Update available", $"AION 2 DPS Meter {update.Tag} is out. Click to download it.", OpenUpdate);
        }
        catch (Exception ex)
        {
            Logger.Info("Update check failed: " + ex.Message);
        }
    }

    public void OpenUpdate()
    {
        var url = _update?.Url ?? UpdateChecker.ReleasesPage;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void Refresh()
    {
        if (_engine is null)
            return;
        if (!_replaying)
            _engine.Tick();
        _vm.Update(_engine.Store.GetDisplayRecord(), _settings.MaxRows);
        _vm.IsRecording = _engine.IsRecording;
        string state = _demo ? "Demo" : _engine.Dispatcher.Status.State.ToString();
        _faro?.SendLive(_vm.Current, state, _vm.Status, _update?.Tag, _engine.Store.GetParty());
        // Fork: the players in view («Enemigos cerca» in Faro), when the list changes and every few seconds.
        if (_faro is not null && _engine.Pvp.Nearby.TakeForSend(Environment.TickCount64) is { } nearby)
            _faro.SendNearby(nearby);
        // Fork: send the progress again now and then, in case Faro missed it (it keeps only newer values).
        if (_faro is not null && Environment.TickCount64 - _progressSentAt > 30_000)
        {
            _progressSentAt = Environment.TickCount64;
            if (_engine.Progress.Current() is { } progress)
                _faro.SendProgress(progress);
            if (_engine.Progress.CurrentPets() is { } pets)
                _faro.SendPets(pets);
        }
    }

    private long _progressSentAt;

    /// <summary>--parent-pid N: exit when that process (Faro) is gone.</summary>
    private void WatchParent(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--parent-pid", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid))
            return;
        _parentWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _parentWatch.Tick += (_, _) =>
        {
            bool alive;
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(pid);
                alive = !parent.HasExited;
            }
            catch (ArgumentException)
            {
                alive = false; // no process with that id any more
            }
            catch
            {
                alive = true;  // cannot tell (access denied): keep running
            }
            if (!alive)
            {
                Logger.Info("Faro is gone; closing the engine.");
                ExitApp();
            }
        };
        _parentWatch.Start();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null)
            return;
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        void Bind(string binding, Action action)
        {
            if (!string.IsNullOrWhiteSpace(binding) && !_hotkeys.Register(binding, action))
                failed.Add(binding);
        }
        Bind(_settings.Hotkeys.Reset, ResetMeter);
        Bind(_settings.Hotkeys.ToggleVisibility, ToggleMeterVisibility);
        Bind(_settings.Hotkeys.ToggleClickThrough, ToggleClickThrough);
        Bind(_settings.Hotkeys.ToggleMode, ToggleMode);
        if (failed.Count > 0)
        {
            Logger.Error("Hotkeys could not be registered: " + string.Join(", ", failed));
            _tray?.ShowBalloon("Hotkeys unavailable", $"Already used by another program: {string.Join(", ", failed)}");
        }
    }

    private void ApplyRecording()
    {
        if (_engine is null)
            return;
        if (_settings.RecordPackets)
            _engine.StartRecording(AppPaths.RecordingsDir);
        else
            _engine.StopRecording();
    }

    // ───── commands (panel buttons, hotkeys, tray) ─────

    public void ResetMeter() => _engine?.Store.Reset();

    public void ToggleMode()
    {
        _vm.Mode = _vm.Mode == MeterMode.Damage ? MeterMode.Heal : MeterMode.Damage;
        _settings.Mode = _vm.Mode;
        _settings.Save();
        Refresh();
    }

    public void ToggleClickThrough()
    {
        _settings.ClickThrough = !_settings.ClickThrough;
        _settings.Save();
        _meter?.SetClickThrough(_settings.ClickThrough);
        _tray?.ShowBalloon("Click-through " + (_settings.ClickThrough ? "on" : "off"),
            _settings.ClickThrough
                ? $"The mouse now goes through the panel. {_settings.Hotkeys.ToggleClickThrough} turns it off."
                : "The panel can be moved and clicked again.");
    }

    public void ToggleMeterVisibility()
    {
        if (_meter is null)
            return;
        if (_meter.IsVisible)
            _meter.Hide();
        else
            _meter.Show();
    }

    public void ShowHistory()
    {
        if (_engine is null)
            return;
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow(_engine.History);
            var p = _settings.HistoryWindow;
            _historyWindow.Width = p.Width;
            _historyWindow.Height = p.Height;
            if (!double.IsNaN(p.Left) && !double.IsNaN(p.Top) && MeterWindow.IsOnAnyScreen(p.Left, p.Top))
            {
                _historyWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                _historyWindow.Left = p.Left;
                _historyWindow.Top = p.Top;
            }
            else
            {
                _historyWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            _historyWindow.Closing += (_, _) =>
            {
                if (_historyWindow.WindowState == WindowState.Normal)
                {
                    _settings.HistoryWindow.Left = _historyWindow.Left;
                    _settings.HistoryWindow.Top = _historyWindow.Top;
                    _settings.HistoryWindow.Width = _historyWindow.Width;
                    _settings.HistoryWindow.Height = _historyWindow.Height;
                    _settings.Save();
                }
            };
            _historyWindow.Closed += (_, _) => _historyWindow = null;
            _historyWindow.Show();
        }
        else
        {
            _historyWindow.Reload();
        }
        _historyWindow.WindowState = WindowState.Normal;
        _historyWindow.Activate();
    }

    public void ShowLiveDetails(int actorId)
    {
        if (_engine is null)
            return;
        var record = _engine.Store.GetDisplayRecord();
        if (_detailsWindow is null)
        {
            _detailsWindow = new DetailsWindow(() => _engine.Store.GetDisplayRecord(), actorId);
            _detailsWindow.Closed += (_, _) => _detailsWindow = null;
            _detailsWindow.Show();
        }
        else
        {
            _detailsWindow.Select(actorId, record);
        }
        _detailsWindow.Activate();
    }

    public void ShowSettings()
    {
        var window = new SettingsWindow(this, _settings);
        window.ShowDialog();
    }

    public void PreviewOpacity(double opacity) => _meter?.ApplyBackgroundOpacity(opacity);

    public void ApplySettings()
    {
        _meter?.ApplyBackgroundOpacity(_settings.BackgroundOpacity);
        RegisterHotkeys();
        ApplyRecording();
        _settings.Save();
        Refresh();
    }

    /// <summary>Plays a .pcap recording through the parser, so a fight can be re-examined (or the meter tested).</summary>
    public async void ReplayRecording(string path)
    {
        if (_engine is not { } engine || _replaying)
            return;
        _replaying = true;
        try
        {
            engine.Store.Reset();
            _vm.Status = $"○ Replaying {Path.GetFileName(path)}…";
            int packets = await Task.Run(() => engine.Replay(path));
            Logger.Info($"Replayed {packets} packets from {path}");
            _vm.Status = $"○ Replayed {Path.GetFileName(path)} ({packets} packets)";
            Refresh();
            ShowHistory();
        }
        catch (Exception ex)
        {
            Logger.Error("Replay failed", ex);
            MessageBox.Show("The recording could not be replayed: " + ex.Message, "Replay", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _replaying = false;
        }
    }

    /// <summary>
    /// <c>--demo --snapshot &lt;dir&gt;</c>: renders the meter, details and history windows to PNG files
    /// (from the app's own visuals, not a screen capture) and exits. Used to check the UI.
    /// </summary>
    private async void RunSnapshots(string dir)
    {
        Directory.CreateDirectory(dir);
        await Task.Delay(TimeSpan.FromSeconds(7));
        Snapshot.Save(_meter!, Path.Combine(dir, "meter.png"));

        ShowLiveDetails(10001);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Snapshot.Save(_detailsWindow!, Path.Combine(dir, "details.png"));
        _detailsWindow?.Close();

        ToggleMode();
        await Task.Delay(TimeSpan.FromSeconds(1));
        Snapshot.Save(_meter!, Path.Combine(dir, "meter-heal.png"));
        ToggleMode();

        // Wait for the boss to die and the fight to be saved.
        await Task.Delay(TimeSpan.FromSeconds(16));
        Snapshot.Save(_meter!, Path.Combine(dir, "meter-killed.png"));
        ShowHistory();
        await Task.Delay(TimeSpan.FromSeconds(2));
        Snapshot.Save(_historyWindow!, Path.Combine(dir, "history.png"));
        _historyWindow?.Close();

        var settings = new SettingsWindow(this, _settings);
        settings.Show();
        await Task.Delay(TimeSpan.FromSeconds(1));
        Snapshot.Save(settings, Path.Combine(dir, "settings.png"));
        settings.Close();
        ExitApp();
    }

    private void ExitApp()
    {
        if (_exiting)
            return;
        _exiting = true;
        _parentWatch?.Stop();
        _settings.Save();
        _refresh?.Stop();
        _hotkeys?.Dispose();
        _engine?.Dispose();
        _faro?.Dispose();
        _tray?.Dispose();
        if (_meter is not null)
        {
            _meter.AllowClose = true;
            _meter.Close();
        }
        Logger.Info("Exit");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows logging off: save settings and the fight in progress.
        _settings.Save();
        _engine?.Dispose();
        base.OnSessionEnding(e);
    }
}
