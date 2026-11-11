using System.Threading.Channels;
using SharpPcap;
using SharpPcap.LibPcap;

namespace Aion2DpsMeter.Core.Capture;

public sealed record CaptureDeviceInfo(string Name, string Description, bool IsLoopback);

/// <summary>
/// Captures TCP traffic on every network adapter through Npcap and hands the segments to one
/// consumer thread. The capture is passive: nothing is sent and the game is not touched.
/// </summary>
public sealed class LiveCapture : IDisposable
{
    private readonly Channel<TcpPayload> _channel = Channel.CreateBounded<TcpPayload>(
        new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly List<LibPcapLiveDevice> _open = new();

    public ChannelReader<TcpPayload> Reader => _channel.Reader;

    /// <summary>Whether Npcap (wpcap.dll in WinPcap-compatible mode) can be loaded.</summary>
    public static bool IsNpcapAvailable(out string error)
    {
        try
        {
            _ = CaptureDeviceList.Instance.Count;
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static IReadOnlyList<CaptureDeviceInfo> ListDevices() =>
        CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>()
            .Where(d => d.Addresses.Count > 0)
            .Select(d => new CaptureDeviceInfo(d.Name, Label(d), IsLoopback(d)))
            .ToList();

    /// <summary>Opens every adapter that has an address (or only <paramref name="onlyDevice"/>) and starts capturing.</summary>
    public int Start(string? onlyDevice = null)
    {
        foreach (var device in CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>())
        {
            if (device.Addresses.Count == 0)
                continue;
            if (onlyDevice is not null && device.Name != onlyDevice)
                continue;
            try
            {
                string label = Label(device);
                bool loopback = IsLoopback(device);
                device.OnPacketArrival += (_, e) =>
                {
                    var payload = PacketDecoder.Decode(e.GetPacket(), label, loopback);
                    if (payload is not null)
                        _channel.Writer.TryWrite(payload);
                };
                device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 100, Snaplen = 65535 });
                device.Filter = "tcp";
                device.StartCapture();
                _open.Add(device);
            }
            catch
            {
                // Adapters that cannot be opened (disabled, virtual without a driver) are skipped.
            }
        }
        return _open.Count;
    }

    public void Dispose()
    {
        foreach (var d in _open)
        {
            try
            {
                d.StopCapture();
                d.Close();
            }
            catch
            {
                // already gone
            }
        }
        _open.Clear();
        _channel.Writer.TryComplete();
    }

    private static string Label(LibPcapLiveDevice d) =>
        !string.IsNullOrWhiteSpace(d.Interface?.FriendlyName) ? d.Interface!.FriendlyName!
        : !string.IsNullOrWhiteSpace(d.Description) ? d.Description!
        : d.Name;

    private static bool IsLoopback(LibPcapLiveDevice d) =>
        d.Loopback || d.Name.Contains("loopback", StringComparison.OrdinalIgnoreCase)
        || (d.Description?.Contains("loopback", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>Reads a .pcap / .pcapng recording, for replaying a fight or testing the parser.</summary>
public static class PcapFileReader
{
    public static IEnumerable<TcpPayload> Read(string path)
    {
        using var reader = new CaptureFileReaderDevice(path);
        reader.Open();
        while (reader.GetNextPacket(out PacketCapture e) == GetPacketStatus.PacketRead)
        {
            var payload = PacketDecoder.Decode(e.GetPacket(), "file", isLoopback: false);
            if (payload is not null)
                yield return payload;
        }
    }
}

/// <summary>Writes the locked game stream to a .pcap file, for bug reports and replays.</summary>
public sealed class PacketRecorder : IDisposable
{
    private readonly string _directory;
    private CaptureFileWriterDevice? _writer;
    private PacketDotNet.LinkLayers? _linkType;

    public string? CurrentFile { get; private set; }

    public PacketRecorder(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void Write(TcpPayload p)
    {
        if (p.Raw is not { } raw)
            return;
        if (_writer is null || _linkType != raw.LinkLayerType)
        {
            _writer?.Close();
            CurrentFile = Path.Combine(_directory, $"aion2-{DateTime.Now:yyyyMMdd-HHmmss}.pcap");
            _writer = new CaptureFileWriterDevice(CurrentFile);
            _writer.Open(new DeviceConfiguration { LinkLayerType = raw.LinkLayerType });
            _linkType = raw.LinkLayerType;
        }
        _writer.Write(raw);
    }

    public void Dispose()
    {
        _writer?.Close();
        _writer = null;
    }
}
