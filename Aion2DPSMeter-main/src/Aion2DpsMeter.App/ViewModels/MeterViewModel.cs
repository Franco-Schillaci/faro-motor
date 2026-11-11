using System.Collections.ObjectModel;
using System.Windows.Media;
using Aion2DpsMeter.App.Infrastructure;
using Aion2DpsMeter.Core.Capture;
using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;

namespace Aion2DpsMeter.App.ViewModels;

public sealed class PlayerRowViewModel : ObservableObject
{
    private int _rank;
    private string _name = "";
    private string _value = "";
    private string _perSecond = "";
    private string _share = "";
    private double _barFraction;
    private Brush _barBrush = ClassColors.For(JobClass.Unknown);
    private bool _isLocal;
    private string _className = "";

    public int ActorId { get; init; }
    public int Rank { get => _rank; set => Set(ref _rank, value); }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string ClassName { get => _className; set => Set(ref _className, value); }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string PerSecond { get => _perSecond; set => Set(ref _perSecond, value); }
    public string Share { get => _share; set => Set(ref _share, value); }
    /// <summary>0-1: bar length relative to the top row.</summary>
    public double BarFraction { get => _barFraction; set => Set(ref _barFraction, value); }
    public Brush BarBrush { get => _barBrush; set => Set(ref _barBrush, value); }
    public bool IsLocal { get => _isLocal; set => Set(ref _isLocal, value); }
}

/// <summary>State of the overlay panel, refreshed from the live fight a few times a second.</summary>
public sealed class MeterViewModel : ObservableObject
{
    private string _title = "Waiting for combat";
    private string _timer = "0:00";
    private string _totalLine = "";
    private string _status = "Starting…";
    private bool _statusIsProblem;
    private MeterMode _mode;
    private bool _showBossHp;
    private double _bossHpFraction;
    private string _bossHpText = "";
    private bool _clickThrough;
    private bool _inProgress;
    private bool _isRecording;

    public ObservableCollection<PlayerRowViewModel> Rows { get; } = new();

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Timer { get => _timer; set => Set(ref _timer, value); }
    public string TotalLine { get => _totalLine; set => Set(ref _totalLine, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool StatusIsProblem { get => _statusIsProblem; set => Set(ref _statusIsProblem, value); }
    public bool ShowBossHp { get => _showBossHp; set => Set(ref _showBossHp, value); }
    public double BossHpFraction { get => _bossHpFraction; set => Set(ref _bossHpFraction, value); }
    public string BossHpText { get => _bossHpText; set => Set(ref _bossHpText, value); }
    public bool InProgress { get => _inProgress; set => Set(ref _inProgress, value); }
    public bool IsRecording { get => _isRecording; set => Set(ref _isRecording, value); }

    private string? _updateLabel;
    /// <summary>Set when a newer release exists, e.g. "v0.2.0"; shows the Update button.</summary>
    public string? UpdateLabel
    {
        get => _updateLabel;
        set
        {
            if (Set(ref _updateLabel, value))
                OnPropertyChanged(nameof(UpdateAvailable));
        }
    }
    public bool UpdateAvailable => _updateLabel is not null;

    public bool ClickThrough { get => _clickThrough; set => Set(ref _clickThrough, value); }

    public MeterMode Mode
    {
        get => _mode;
        set
        {
            if (Set(ref _mode, value))
                OnPropertyChanged(nameof(ModeLabel));
        }
    }

    public string ModeLabel => Mode == MeterMode.Damage ? "DMG" : "HEAL";

    /// <summary>The record currently shown, for the details window.</summary>
    public EncounterRecord? Current { get; private set; }

    public void SetStatus(CaptureStatus status)
    {
        Status = status.State switch
        {
            CaptureState.Locked => "● " + status.Message,
            _ => "○ " + status.Message,
        };
        StatusIsProblem = status.State is CaptureState.Error or CaptureState.WaitingForGame;
    }

    public void Update(EncounterRecord? record, int maxRows)
    {
        Current = record;
        if (record is null)
        {
            Title = "Waiting for combat";
            Timer = "0:00";
            TotalLine = "";
            ShowBossHp = false;
            InProgress = false;
            Rows.Clear();
            return;
        }

        Title = record.Name;
        Timer = Format.Duration(record.DurationMs);
        InProgress = record.InProgress;

        ShowBossHp = record.IsBoss && record.BossMaxHp > 0;
        if (ShowBossHp)
        {
            double frac = Math.Clamp((double)record.BossCurrentHp / record.BossMaxHp, 0, 1);
            BossHpFraction = frac;
            BossHpText = record.Killed ? "Killed" : Format.Percent(frac);
        }

        bool damage = Mode == MeterMode.Damage;
        var players = record.Players
            .Where(p => damage ? p.Damage > 0 : p.Heal > 0)
            .OrderByDescending(p => damage ? p.Damage : p.Heal)
            .Take(Math.Max(1, maxRows))
            .ToList();

        TotalLine = damage
            ? $"Total {Format.Number(record.TotalDamage)}  ·  {Format.Number(record.TotalDps)}/s"
            : $"Total heal {Format.Number(record.TotalHeal)}  ·  {Format.Number(record.TotalHeal / record.DurationSeconds)}/s";

        double top = players.Count == 0 ? 1 : Math.Max(1, damage ? players[0].Damage : players[0].Heal);

        // Update rows in place so the list does not flicker.
        for (int i = 0; i < players.Count; i++)
        {
            var p = players[i];
            var row = i < Rows.Count && Rows[i].ActorId == p.ActorId ? Rows[i] : null;
            if (row is null)
            {
                row = Rows.FirstOrDefault(r => r.ActorId == p.ActorId) ?? new PlayerRowViewModel { ActorId = p.ActorId };
                int existing = Rows.IndexOf(row);
                if (existing >= 0)
                    Rows.Move(existing, i);
                else
                    Rows.Insert(i, row);
            }
            long amount = damage ? p.Damage : p.Heal;
            row.Rank = i + 1;
            row.Name = p.Name;
            row.ClassName = Format.ClassName(p.Job);
            row.Value = Format.Number(amount);
            row.PerSecond = Format.Number(damage ? p.Dps : p.Hps) + "/s";
            row.Share = Format.Percent(damage ? p.DamageShare : p.HealShare);
            row.BarFraction = amount / top;
            row.BarBrush = ClassColors.For(p.Job);
            row.IsLocal = p.IsLocal;
        }
        while (Rows.Count > players.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }
}
