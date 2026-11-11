using Aion2DpsMeter.Core.Protocol;

namespace Aion2DpsMeter.Tests;

/// <summary>Fork (Faro): «captura de momentos» keeps only the last seconds and writes them as hex lines.</summary>
public class MomentRecorderTests
{
    [Fact]
    public void Keeps_only_the_window_and_writes_one_line_per_packet()
    {
        var rec = new MomentRecorder { WindowMs = 30_000 };
        long t0 = 1_800_000_000_000;
        rec.Add(t0, new byte[] { 0x01, 0x02 });             // queda afuera (más de 30 s antes del último)
        rec.Add(t0 + 20_000, new byte[] { 0x0A, 0x0B, 0x0C });
        rec.Add(t0 + 40_000, new byte[] { 0x02, 0x97, 0xFF });
        string dir = Path.Combine(Path.GetTempPath(), "faro-moment-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var saved = rec.Dump(dir);
            Assert.NotNull(saved);
            Assert.Equal(2, saved!.Value.Count);
            var lines = File.ReadAllLines(saved.Value.Path).Where(l => !l.StartsWith('#')).ToArray();
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("0A0B0C", lines[0]);
            Assert.EndsWith("0297FF", lines[1]);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Nothing_to_save_returns_null()
    {
        Assert.Null(new MomentRecorder().Dump(Path.GetTempPath()));
    }
}
