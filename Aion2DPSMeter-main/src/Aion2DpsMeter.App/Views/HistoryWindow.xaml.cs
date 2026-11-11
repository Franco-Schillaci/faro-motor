using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Aion2DpsMeter.App.Infrastructure;
using Aion2DpsMeter.Core.History;

namespace Aion2DpsMeter.App.Views;

public sealed record FightRow(EncounterSummary Summary)
{
    public string When => Summary.StartedAt.ToString("yyyy-MM-dd HH:mm");
    public string Name => Summary.Name + (Summary.IsTrainingDummy ? " (dummy)" : "");
    public string Result => Summary.IsBoss ? (Summary.Killed ? "Kill" : "Wipe") : (Summary.Killed ? "Cleared" : "");
    public string DurationText => Format.Duration(Summary.DurationMs);
    public string DpsText => Format.Number(Summary.TotalDps);
    public string LocalDpsText => Summary.LocalDps > 0 ? Format.Number(Summary.LocalDps) : "";
    public string DamageText => Format.Number(Summary.TotalDamage);
}

public sealed record MonsterRow(MonsterSummary Summary)
{
    public string Title => (Summary.IsBoss ? "★ " : "") + Summary.Name;
    public string Stats
    {
        get
        {
            var parts = new List<string> { $"{Summary.Fights} fight{(Summary.Fights == 1 ? "" : "s")}" };
            if (Summary.IsBoss)
                parts.Add($"{Summary.Kills} kill{(Summary.Kills == 1 ? "" : "s")}");
            if (Summary.BestKillMs > 0)
                parts.Add("best " + Format.Duration(Summary.BestKillMs));
            if (Summary.BestLocalDps > 0)
                parts.Add("your best " + Format.Number(Summary.BestLocalDps) + "/s");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Browse saved fights, filtered by monster, with the full breakdown of the selected one.</summary>
public partial class HistoryWindow : Window
{
    private readonly HistoryRepository _history;
    private readonly DispatcherTimer _searchDelay;
    private bool _suppressMonsterSelection;

    public HistoryWindow(HistoryRepository history)
    {
        InitializeComponent();
        _history = history;
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            LoadFights();
        };
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        LoadMonsters();
        LoadFights();
    }

    private void LoadMonsters()
    {
        try
        {
            _suppressMonsterSelection = true;
            MonsterList.ItemsSource = _history.Monsters().Select(m => new MonsterRow(m)).ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("History could not be read", ex);
        }
        finally
        {
            _suppressMonsterSelection = false;
        }
    }

    private void LoadFights()
    {
        try
        {
            var selectedId = (FightsGrid.SelectedItem as FightRow)?.Summary.Id;
            var rows = _history.List(new HistoryQuery
            {
                NameContains = SearchBox.Text,
                BossesOnly = BossesOnly.IsChecked == true,
                KillsOnly = KillsOnly.IsChecked == true,
            }).Select(s => new FightRow(s)).ToList();
            FightsGrid.ItemsSource = rows;
            CountText.Text = $"{rows.Count} fight{(rows.Count == 1 ? "" : "s")}";
            FightsGrid.SelectedItem = rows.FirstOrDefault(r => r.Summary.Id == selectedId) ?? rows.FirstOrDefault();
            DeleteButton.IsEnabled = rows.Count > 0;
        }
        catch (Exception ex)
        {
            Logger.Error("History could not be read", ex);
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void MonsterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressMonsterSelection || MonsterList.SelectedItem is not MonsterRow m)
            return;
        SearchBox.Text = m.Summary.Name;
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        MonsterList.SelectedItem = null;
        SearchBox.Text = "";
        BossesOnly.IsChecked = KillsOnly.IsChecked = false;
        LoadFights();
    }

    private void FightsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FightsGrid.SelectedItem is not FightRow row)
        {
            Details.Show(null);
            return;
        }
        try
        {
            Details.Show(_history.Get(row.Summary.Id));
        }
        catch (Exception ex)
        {
            Logger.Error("Fight could not be loaded", ex);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (FightsGrid.SelectedItem is not FightRow row)
            return;
        var answer = MessageBox.Show(this, $"Delete the fight \"{row.Summary.Name}\" from {row.When}?", "Delete fight",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;
        _history.Delete(row.Summary.Id);
        Reload();
    }
}
