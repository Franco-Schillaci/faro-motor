using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Aion2DpsMeter.App.Infrastructure;
using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.History;
using Microsoft.Win32;

namespace Aion2DpsMeter.App.Views;

public sealed record PlayerDisplay(PlayerRecord Record, int Rank)
{
    public string Name => Record.Name + (Record.IsLocal ? " (you)" : "");
    public string Class => Format.ClassName(Record.Job);
    public long Damage => Record.Damage;
    public double Dps => Record.Dps;
    public double Share => Record.DamageShare;
    public long Heal => Record.Heal;
    public double Hps => Record.Hps;
    public double CritRate => Record.CritRate;
    public long MaxHit => Record.MaxHit;
    public string DamageText => Format.Number(Record.Damage);
    public string DpsText => Format.Number(Record.Dps);
    public string ShareText => Format.Percent(Record.DamageShare);
    public string HealText => Record.Heal > 0 ? Format.Number(Record.Heal) : "";
    public string HpsText => Record.Heal > 0 ? Format.Number(Record.Hps) : "";
    public string CritText => Format.Percent(Record.CritRate);
    public string MaxHitText => Format.Number(Record.MaxHit);
}

public sealed record SkillDisplay(SkillRecord Record)
{
    public string Name => Record.Name + (Record.IsDot ? " (DoT)" : "") + (Record.IsSummon ? " (summon)" : "");
    public long Damage => Record.Damage;
    public double Share => Record.Share;
    public int Hits => Record.Hits;
    public double CritRate => Record.CritRate;
    public double Average => Record.Average;
    public long Max => Record.Max;
    public int Perfect => Record.Perfect;
    public int Double => Record.Double;
    public string DamageText => Format.Number(Record.Damage);
    public string ShareText => Format.Percent(Record.Share);
    public string CritText => Format.Percent(Record.CritRate);
    public string AvgText => Format.Number(Record.Average);
    public string MaxText => Format.Number(Record.Max);
    public string BackText => Record.Hits == 0 ? "" : Format.Percent((double)Record.Back / Record.Hits);
}

public sealed record HealDisplay(HealSkillRecord Record)
{
    public string Name => Record.Name + (Record.IsHot ? " (HoT)" : "");
    public long Amount => Record.Amount;
    public int Ticks => Record.Ticks;
    public string AmountText => Format.Number(Record.Amount);
}

/// <summary>A fight's per-player table with the selected player's skill and healing breakdown.</summary>
public partial class EncounterDetailsView : UserControl
{
    private EncounterRecord? _record;
    private int? _selectedActor;

    public EncounterDetailsView()
    {
        InitializeComponent();
        Show(null);
    }

    public EncounterRecord? Record => _record;

    /// <summary>Shows a fight, keeping the selected player when the same fight is refreshed.</summary>
    public void Show(EncounterRecord? record, int? selectActor = null)
    {
        bool sameFight = record is not null && _record?.Id == record.Id;
        _record = record;
        CopyButton.IsEnabled = ExportButton.IsEnabled = record is not null;
        if (record is null)
        {
            TitleText.Text = "No fight selected";
            SubtitleText.Text = "";
            PlayersGrid.ItemsSource = null;
            SkillsGrid.ItemsSource = null;
            HealsGrid.ItemsSource = null;
            return;
        }

        TitleText.Text = record.Name;
        var parts = new List<string>
        {
            record.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            Format.Duration(record.DurationMs),
            $"{Format.Number(record.TotalDamage)} damage",
            $"{Format.Number(record.TotalDps)} DPS",
        };
        if (record.DungeonName.Length > 0)
            parts.Insert(0, record.DungeonName);
        if (record.IsBoss)
            parts.Add(record.Killed ? "killed" : record.InProgress ? "in progress" : "not killed");
        SubtitleText.Text = string.Join("  ·  ", parts);

        int? keep = selectActor ?? (sameFight ? _selectedActor : null);
        var rows = record.Players.Select((p, i) => new PlayerDisplay(p, i + 1)).ToList();
        PlayersGrid.ItemsSource = rows;
        var selected = rows.FirstOrDefault(r => r.Record.ActorId == keep)
            ?? rows.FirstOrDefault(r => r.Record.IsLocal)
            ?? rows.FirstOrDefault();
        PlayersGrid.SelectedItem = selected;
        ShowPlayer(selected);
    }

    private void PlayersGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlayersGrid.SelectedItem is PlayerDisplay p)
            ShowPlayer(p);
    }

    private void ShowPlayer(PlayerDisplay? p)
    {
        _selectedActor = p?.Record.ActorId;
        SkillsTitle.Text = p is null ? "Skills" : $"Skills — {p.Record.Name}";
        SkillsGrid.ItemsSource = p?.Record.Skills.Select(s => new SkillDisplay(s)).ToList();
        HealsGrid.ItemsSource = p?.Record.HealSkills.Select(h => new HealDisplay(h)).ToList();
    }

    public static string Summary(EncounterRecord r)
    {
        var sb = new StringBuilder();
        sb.Append($"**{r.Name}** — {Format.Duration(r.DurationMs)}");
        if (r.IsBoss)
            sb.Append(r.Killed ? " — killed" : " — not killed");
        sb.AppendLine($" — {Format.Number(r.TotalDps)} DPS");
        int i = 1;
        foreach (var p in r.Players.Where(p => p.Damage > 0))
            sb.AppendLine($"{i++}. {p.Name} ({Format.ClassName(p.Job)})  {Format.Number(p.Damage)}  {Format.Number(p.Dps)}/s  {Format.Percent(p.DamageShare)}");
        var healers = r.Players.Where(p => p.Heal > 0).OrderByDescending(p => p.Heal).ToList();
        if (healers.Count > 0)
            sb.AppendLine("Healing: " + string.Join(", ", healers.Select(h => $"{h.Name} {Format.Number(h.Heal)}")));
        return sb.ToString();
    }

    private void CopySummary_Click(object sender, RoutedEventArgs e)
    {
        if (_record is null)
            return;
        try
        {
            Clipboard.SetText(Summary(_record));
        }
        catch (Exception ex)
        {
            Logger.Error("Clipboard is busy", ex);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_record is null)
            return;
        var dialog = new SaveFileDialog
        {
            FileName = $"{_record.StartedAt.ToLocalTime():yyyyMMdd-HHmm} {string.Concat(_record.Name.Split(Path.GetInvalidFileNameChars()))}.json",
            Filter = "JSON|*.json",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            File.WriteAllText(dialog.FileName, HistoryRepository.ToJson(_record));
    }
}
