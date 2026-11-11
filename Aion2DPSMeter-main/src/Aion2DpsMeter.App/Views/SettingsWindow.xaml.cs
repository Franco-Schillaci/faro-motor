using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Aion2DpsMeter.App.Infrastructure;
using Aion2DpsMeter.Core.Protocol;
using Microsoft.Win32;

namespace Aion2DpsMeter.App.Views;

public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly AppSettings _settings;
    private readonly double _originalOpacity;

    public SettingsWindow(App app, AppSettings settings)
    {
        InitializeComponent();
        _app = app;
        _settings = settings;
        _originalOpacity = settings.BackgroundOpacity;

        OpacitySlider.Value = settings.BackgroundOpacity;
        MaxRowsBox.Text = settings.MaxRows.ToString();
        IdleBox.Text = (settings.Combat.IdleTimeoutMs / 1000).ToString();
        BossOnlyBox.IsChecked = settings.Combat.BossDamageOnly;
        PartyOnlyBox.IsChecked = settings.Combat.PartyOnly;
        SaveTrashBox.IsChecked = settings.HistorySaving.SaveTrash;
        SaveDummyBox.IsChecked = settings.HistorySaving.SaveTrainingDummy;
        MinTrashBox.Text = settings.HistorySaving.MinTrashFightSeconds.ToString();
        HkReset.Text = settings.Hotkeys.Reset;
        HkToggle.Text = settings.Hotkeys.ToggleVisibility;
        HkClick.Text = settings.Hotkeys.ToggleClickThrough;
        HkMode.Text = settings.Hotkeys.ToggleMode;
        RecordBox.IsChecked = settings.RecordPackets;
        UpdatesBox.IsChecked = settings.CheckForUpdates;
        VersionText.Text = $"This is version {UpdateChecker.CurrentVersion.ToString(3)}.";
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Also raised while the window is still being built.
        if (IsLoaded)
            _app.PreviewOpacity(e.NewValue);
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete)
        {
            box.Text = "";
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;
        if (Keyboard.Modifiers == ModifierKeys.None)
            return; // a bare key would fire while typing in the game chat
        box.Text = HotkeyManager.Format(Keyboard.Modifiers, key);
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, out int v) ? Math.Clamp(v, min, max) : fallback;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.BackgroundOpacity = OpacitySlider.Value;
        _settings.MaxRows = ParseInt(MaxRowsBox.Text, _settings.MaxRows, 1, 40);
        _settings.Combat.IdleTimeoutMs = ParseInt(IdleBox.Text, _settings.Combat.IdleTimeoutMs / 1000, 5, 600) * 1000;
        _settings.Combat.BossDamageOnly = BossOnlyBox.IsChecked == true;
        _settings.Combat.PartyOnly = PartyOnlyBox.IsChecked == true;
        _settings.HistorySaving.SaveTrash = SaveTrashBox.IsChecked == true;
        _settings.HistorySaving.SaveTrainingDummy = SaveDummyBox.IsChecked == true;
        _settings.HistorySaving.MinTrashFightSeconds = ParseInt(MinTrashBox.Text, _settings.HistorySaving.MinTrashFightSeconds, 0, 3600);
        _settings.Hotkeys.Reset = HkReset.Text;
        _settings.Hotkeys.ToggleVisibility = HkToggle.Text;
        _settings.Hotkeys.ToggleClickThrough = HkClick.Text;
        _settings.Hotkeys.ToggleMode = HkMode.Text;
        _settings.RecordPackets = RecordBox.IsChecked == true;
        _settings.CheckForUpdates = UpdatesBox.IsChecked == true;
        _app.ApplySettings();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _app.PreviewOpacity(_originalOpacity);
        DialogResult = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true)
            _app.PreviewOpacity(_originalOpacity);
        base.OnClosed(e);
    }

    private void KoFi_Click(object sender, RoutedEventArgs e) => Links.OpenKoFi();
    private void PayPal_Click(object sender, RoutedEventArgs e) => Links.OpenPayPal();
    private void Project_Click(object sender, RoutedEventArgs e) => Links.OpenProject();

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Packet capture|*.pcap;*.pcapng|All files|*.*",
            InitialDirectory = Directory.Exists(AppPaths.RecordingsDir) ? AppPaths.RecordingsDir : AppPaths.DataDir,
        };
        if (dialog.ShowDialog(this) == true)
            _app.ReplayRecording(dialog.FileName);
    }

    private void Opcodes_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        if (!File.Exists(AppPaths.OpcodesFile))
            Opcodes.Default.Save(AppPaths.OpcodesFile);
        Process.Start(new ProcessStartInfo(AppPaths.OpcodesFile) { UseShellExecute = true });
        MessageBox.Show(this, "Changes to opcodes.json take effect after restarting the meter.", "Opcodes",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
