using System.Diagnostics;

namespace Aion2DpsMeter.App.Infrastructure;

public static class Links
{
    public const string KoFi = "https://ko-fi.com/domel1998";
    public const string PayPal = "https://paypal.me/DominikMat12";
    public static string Project => $"https://github.com/{UpdateChecker.Repository}";

    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not open {url}", ex);
        }
    }

    public static void OpenKoFi() => Open(KoFi);
    public static void OpenPayPal() => Open(PayPal);
    public static void OpenProject() => Open(Project);
}
