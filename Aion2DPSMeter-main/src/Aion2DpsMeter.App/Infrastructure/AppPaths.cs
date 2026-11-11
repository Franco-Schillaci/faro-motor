using System.IO;

namespace Aion2DpsMeter.App.Infrastructure;

public static class AppPaths
{
    /// <summary>%APPDATA%\Aion2DpsMeter: settings and opcode overrides.</summary>
    public static string ConfigDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Aion2DpsMeter");

    /// <summary>%LOCALAPPDATA%\Aion2DpsMeter: history database, logs and packet recordings.</summary>
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aion2DpsMeter");

    public static string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    /// <summary>Optional override of the protocol opcodes, for fixing the meter after a game patch.</summary>
    public static string OpcodesFile => Path.Combine(ConfigDir, "opcodes.json");
    public static string HistoryDb => Path.Combine(DataDir, "history.db");
    public static string LogFile => Path.Combine(DataDir, "logs", "meter.log");
    public static string RecordingsDir => Path.Combine(DataDir, "recordings");
    /// <summary>Fork (Faro): «captura de momentos» files.</summary>
    public static string CapturesDir => Path.Combine(DataDir, "capturas");
    /// <summary>Game data tables shipped next to the executable.</summary>
    public static string GameDataDir => Path.Combine(AppContext.BaseDirectory, "Data");
}
