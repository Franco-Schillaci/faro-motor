using System.IO;

namespace Aion2DpsMeter.App.Infrastructure;

/// <summary>Minimal append-only log in %LOCALAPPDATA%\Aion2DpsMeter\logs, rotated at 5 MB.</summary>
public static class Logger
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly object Lock = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                var path = AppPaths.LogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the meter down.
        }
    }
}
