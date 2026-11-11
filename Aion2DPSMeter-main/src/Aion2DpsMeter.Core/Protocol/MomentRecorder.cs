namespace Aion2DpsMeter.Core.Protocol;

/// <summary>
/// Fork (Faro): «captura de momentos». Keeps the last <see cref="WindowMs"/> of server-to-client game packets, already
/// decompressed (what the parser reads), and writes them to a text file when Faro asks (Ctrl+Alt+K). It is how new
/// game data gets decoded: mark the moment something happens, then compare the bytes with what was seen in the game.
/// Only what the server sends to this PC (not encrypted); it never touches the game. The file stays on this PC.
/// </summary>
public sealed class MomentRecorder
{
    private const int MaxPacketBytes = 8_192;

    private readonly object _lock = new();
    private readonly Queue<(long Ms, byte[] Bytes)> _queue = new();
    private long _bytes;

    public int WindowMs { get; init; } = 30_000;
    public long MaxBytes { get; init; } = 16_000_000;

    public void Add(long timestampMs, ReadOnlySpan<byte> packet)
    {
        if (packet.Length == 0)
            return;
        long ms = timestampMs > 0 ? timestampMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        byte[] copy = (packet.Length > MaxPacketBytes ? packet[..MaxPacketBytes] : packet).ToArray();
        lock (_lock)
        {
            _queue.Enqueue((ms, copy));
            _bytes += copy.Length;
            while (_queue.Count > 0 && (_queue.Peek().Ms < ms - WindowMs || _bytes > MaxBytes))
                _bytes -= _queue.Dequeue().Bytes.Length;
        }
    }

    /// <summary>Writes what is kept now to <c>momento-AAAAMMDD-HHMMSS.txt</c> in <paramref name="folder"/>.</summary>
    /// <returns>The file and how many packets it has, or null if there is nothing yet.</returns>
    public (string Path, int Count)? Dump(string folder)
    {
        (long Ms, byte[] Bytes)[] items;
        lock (_lock)
            items = _queue.ToArray();
        if (items.Length == 0)
            return null;
        Directory.CreateDirectory(folder);
        var now = DateTime.Now;
        string path = Path.Combine(folder, $"momento-{now:yyyyMMdd-HHmmss}.txt");
        using var w = new StreamWriter(path);
        w.WriteLine($"# Faro · captura de momento · {now:yyyy-MM-dd HH:mm:ss} · {items.Length} mensajes del servidor (los últimos {WindowMs / 1000} s)");
        w.WriteLine("# hora local · largo · bytes en hex (ya descomprimidos)");
        foreach (var (ms, bytes) in items)
        {
            var t = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
            w.WriteLine($"{t:HH:mm:ss.fff} {bytes.Length,5} {Convert.ToHexString(bytes)}");
        }
        return (path, items.Length);
    }
}
