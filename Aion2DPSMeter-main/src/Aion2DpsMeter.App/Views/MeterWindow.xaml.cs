using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Aion2DpsMeter.App.Infrastructure;
using Aion2DpsMeter.App.ViewModels;

namespace Aion2DpsMeter.App.Views;

/// <summary>
/// The overlay panel: a borderless, transparent, always-on-top window that does not take focus
/// from the game. It can be dragged anywhere, including onto another monitor.
/// </summary>
public partial class MeterWindow : Window
{
    private readonly App _app;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _topmostTimer;
    private IntPtr _hwnd;

    public MeterViewModel ViewModel { get; }
    public bool AllowClose { get; set; }

    public MeterWindow(App app, MeterViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();
        _app = app;
        _settings = settings;
        ViewModel = viewModel;
        DataContext = viewModel;

        RestorePlacement();
        ApplyBackgroundOpacity(settings.BackgroundOpacity);

        // Fullscreen games may push topmost windows down; put the panel back on top regularly.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero && IsVisible)
                NativeMethods.ForceTopmost(_hwnd);
        };
        _topmostTimer.Start();

        // Write position and size to disk shortly after a move or resize ends, so they survive
        // the meter being closed any way at all (not only through Exit).
        _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveDelay.Tick += (_, _) =>
        {
            _saveDelay.Stop();
            _settings.Save();
        };
        LocationChanged += (_, _) => SavePlacement();
        SizeChanged += (_, _) => SavePlacement();
    }

    private readonly DispatcherTimer _saveDelay;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Not in Alt+Tab, and clicking the panel does not steal focus from the game.
        NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE, true);
        SetClickThrough(_settings.ClickThrough);
    }

    public HwndSource? Source => _hwnd == IntPtr.Zero ? null : HwndSource.FromHwnd(_hwnd);

    public void SetClickThrough(bool on)
    {
        ViewModel.ClickThrough = on;
        if (_hwnd != IntPtr.Zero)
            NativeMethods.SetExStyle(_hwnd, NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED, on);
        Buttons.Opacity = on ? 0.35 : 1;
    }

    public void ApplyBackgroundOpacity(double opacity)
    {
        var c = (Color)FindResource("PanelColor");
        Root.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(opacity, 0.05, 1) * 255), c.R, c.G, c.B));
    }

    private void RestorePlacement()
    {
        var p = _settings.Meter;
        Width = Math.Max(MinWidth, p.Width);
        Height = Math.Max(MinHeight, p.Height);
        if (double.IsNaN(p.Left) || double.IsNaN(p.Top) || !IsOnAnyScreen(p.Left, p.Top))
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 40;
            Top = area.Top + 120;
        }
        else
        {
            Left = p.Left;
            Top = p.Top;
        }
    }

    /// <summary>A saved position on a monitor that is no longer connected would put the panel out of reach.</summary>
    public static bool IsOnAnyScreen(double left, double top)
    {
        // WPF positions are in DIPs, screen bounds in pixels.
        double scale;
        using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            scale = g.DpiX / 96.0;
        var header = new System.Drawing.Rectangle((int)(left * scale), (int)(top * scale), (int)(80 * scale), (int)(20 * scale));
        return System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(header));
    }

    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal)
            return;
        _settings.Meter.Left = Left;
        _settings.Meter.Top = Top;
        _settings.Meter.Width = Width;
        _settings.Meter.Height = Height;
        if (IsLoaded)
        {
            _saveDelay.Stop();
            _saveDelay.Start();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // the button was released before the drag started
            }
        }
    }

    private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: PlayerRowViewModel row })
            _app.ShowLiveDetails(row.ActorId);
    }

    private void Status_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.Status.Contains("Npcap", StringComparison.OrdinalIgnoreCase))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://npcap.com/#download") { UseShellExecute = true });
    }

    private void Mode_Click(object sender, RoutedEventArgs e) => _app.ToggleMode();
    private void Update_Click(object sender, RoutedEventArgs e) => _app.OpenUpdate();
    private void Reset_Click(object sender, RoutedEventArgs e) => _app.ResetMeter();
    private void History_Click(object sender, RoutedEventArgs e) => _app.ShowHistory();
    private void Settings_Click(object sender, RoutedEventArgs e) => _app.ShowSettings();
    private void Lock_Click(object sender, RoutedEventArgs e) => _app.ToggleClickThrough();
    private void Hide_Click(object sender, RoutedEventArgs e) => _app.ToggleMeterVisibility();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _topmostTimer.Stop();
        base.OnClosed(e);
    }
}
