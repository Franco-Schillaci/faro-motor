using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aion2DpsMeter.App.Infrastructure;

/// <summary>Renders a window's own visual tree to a PNG, without capturing anything else on screen.</summary>
public static class Snapshot
{
    public static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement root)
            return;
        var dpi = VisualTreeHelper.GetDpi(root);
        int w = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0)
            return;

        // The window background (for normal windows) is drawn under the content.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (window.Background is { } bg && !window.AllowsTransparency)
                dc.DrawRectangle(bg, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            else
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 44, 52)), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }

        var bitmap = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
