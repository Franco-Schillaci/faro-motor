using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Aion2DpsMeter.App.Infrastructure;

/// <summary>Notification-area icon with the meter's main commands, so a hidden panel can always be brought back.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _image;

    public TrayIcon(Action toggleMeter, Action history, Action settings, Action reset, Action toggleClickThrough, Action exit)
    {
        _image = DrawIcon();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide meter", null, (_, _) => toggleMeter());
        menu.Items.Add("Reset meter", null, (_, _) => reset());
        menu.Items.Add("Toggle click-through", null, (_, _) => toggleClickThrough());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Fight history…", null, (_, _) => history());
        menu.Items.Add("Settings…", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var support = new Forms.ToolStripMenuItem("☕ Support the project");
        support.DropDownItems.Add("Ko-fi…", null, (_, _) => Links.OpenKoFi());
        support.DropDownItems.Add("PayPal…", null, (_, _) => Links.OpenPayPal());
        menu.Items.Add(support);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _menu = menu;
        _icon = new Forms.NotifyIcon
        {
            Icon = _image,
            Text = $"AION 2 DPS Meter {UpdateChecker.CurrentVersion.ToString(3)}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => toggleMeter();
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonClick;
            _balloonClick = null;
            action?.Invoke();
        };
    }

    private readonly Forms.ContextMenuStrip _menu;
    private Forms.ToolStripItem? _updateItem;
    private Action? _balloonClick;

    /// <summary>Shows a notification; <paramref name="onClick"/> runs when the user clicks it.</summary>
    public void ShowBalloon(string title, string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);
    }

    /// <summary>Adds (once) a menu entry at the top that opens the new release.</summary>
    public void ShowUpdate(string label, Action open)
    {
        if (_updateItem is not null)
        {
            _updateItem.Text = label;
            return;
        }
        _updateItem = new Forms.ToolStripMenuItem(label, null, (_, _) => open()) { Font = new Font(_menu.Font, FontStyle.Bold) };
        _menu.Items.Insert(0, _updateItem);
        _menu.Items.Insert(1, new Forms.ToolStripSeparator());
    }

    /// <summary>A small bar-chart glyph, drawn at runtime so the repository carries no binary icon.</summary>
    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(255, 24, 27, 34));
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var b1 = new SolidBrush(Color.FromArgb(255, 217, 137, 63));
            using var b2 = new SolidBrush(Color.FromArgb(255, 79, 143, 216));
            using var b3 = new SolidBrush(Color.FromArgb(255, 108, 191, 90));
            g.FillRectangle(b1, 7, 9, 18, 4);
            g.FillRectangle(b2, 7, 15, 13, 4);
            g.FillRectangle(b3, 7, 21, 8, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
