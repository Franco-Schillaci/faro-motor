using System.IO;
using System.Text.Json;
using Aion2DpsMeter.Core;
using Aion2DpsMeter.Core.Combat;

namespace Aion2DpsMeter.App.Infrastructure;

public enum MeterMode
{
    Damage,
    Heal,
}

public sealed class HotkeySettings
{
    public string Reset { get; set; } = "Ctrl+Shift+R";
    public string ToggleVisibility { get; set; } = "Ctrl+Shift+H";
    public string ToggleClickThrough { get; set; } = "Ctrl+Shift+K";
    public string ToggleMode { get; set; } = "Ctrl+Shift+M";
}

public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 260;
}

/// <summary>User settings, stored as JSON in %APPDATA%\Aion2DpsMeter\settings.json.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Window positions start as NaN ("not placed yet").
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public WindowPlacement Meter { get; set; } = new();
    public WindowPlacement HistoryWindow { get; set; } = new() { Width = 1100, Height = 700 };
    /// <summary>Opacity of the panel background (text stays opaque).</summary>
    public double BackgroundOpacity { get; set; } = 0.75;
    public bool ClickThrough { get; set; }
    public MeterMode Mode { get; set; } = MeterMode.Damage;
    public int MaxRows { get; set; } = 12;
    public bool RecordPackets { get; set; }
    /// <summary>Look for new releases on GitHub (reads the public release list only).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Your character name as the game last stated it; shown at once in the next session.</summary>
    public string? LastCharacterName { get; set; }
    public HotkeySettings Hotkeys { get; set; } = new();
    public CombatOptions Combat { get; set; } = new();
    public HistoryOptions HistorySaving { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json) ?? new();
        }
        catch (Exception ex)
        {
            Logger.Error("Settings could not be read; using defaults", ex);
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            // Write a temporary file and swap it in, so a crash mid-write cannot corrupt the settings.
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Settings could not be saved", ex);
        }
    }
}
