using System.Windows;
using System.Windows.Threading;
using Aion2DpsMeter.Core.Combat;

namespace Aion2DpsMeter.App.Views;

/// <summary>Live breakdown of the fight on the meter, refreshed every second.</summary>
public partial class DetailsWindow : Window
{
    private readonly DispatcherTimer _timer;

    public DetailsWindow(Func<EncounterRecord?> source, int? actorId)
    {
        InitializeComponent();
        Details.Show(source(), actorId);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            // Follow the meter: refresh a live fight, switch when a new one starts.
            var record = source();
            if (record?.Id != Details.Record?.Id || record?.InProgress == true || Details.Record?.InProgress == true)
                Details.Show(record);
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public void Select(int actorId, EncounterRecord? record) => Details.Show(record, actorId);
}
