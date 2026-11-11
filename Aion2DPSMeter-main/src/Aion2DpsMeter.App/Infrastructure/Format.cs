using System.Globalization;
using System.Windows.Media;
using Aion2DpsMeter.Core.Data;

namespace Aion2DpsMeter.App.Infrastructure;

public static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>12,345 / 123.4K / 12.35M / 1.23B.</summary>
    public static string Number(double v)
    {
        double a = Math.Abs(v);
        return a switch
        {
            >= 1e9 => (v / 1e9).ToString("0.00", Inv) + "B",
            >= 1e6 => (v / 1e6).ToString("0.00", Inv) + "M",
            >= 1e4 => (v / 1e3).ToString("0.0", Inv) + "K",
            _ => v.ToString("#,0", Inv),
        };
    }

    public static string Percent(double share) => (share * 100).ToString("0.0", Inv) + "%";

    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", Inv) : t.ToString(@"m\:ss", Inv);
    }

    public static string ClassName(JobClass job) => job switch
    {
        JobClass.Unknown => "",
        _ => job.ToString(),
    };
}

/// <summary>Bar colours per class.</summary>
public static class ClassColors
{
    private static readonly Dictionary<JobClass, SolidColorBrush> Brushes = new()
    {
        [JobClass.Gladiator] = Make("#D9893F"),
        [JobClass.Templar] = Make("#4F8FD8"),
        [JobClass.Ranger] = Make("#6CBF5A"),
        [JobClass.Assassin] = Make("#A86FD8"),
        [JobClass.Sorcerer] = Make("#3FB8D9"),
        [JobClass.Elementalist] = Make("#D95FA0"),
        [JobClass.Cleric] = Make("#E3D9A6"),
        [JobClass.Chanter] = Make("#D9C23F"),
        [JobClass.Fighter] = Make("#D94F4F"),
        [JobClass.Unknown] = Make("#7E8590"),
    };

    public static SolidColorBrush For(JobClass job) => Brushes.GetValueOrDefault(job) ?? Brushes[JobClass.Unknown];

    private static SolidColorBrush Make(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
