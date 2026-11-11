using PacketDotNet;
using SharpPcap;

namespace Aion2DpsMeter.Core.Capture;

/// <summary>Turns a raw captured frame into a <see cref="TcpPayload"/>, or null when it carries no TCP data.</summary>
public static class PacketDecoder
{
    public static TcpPayload? Decode(RawCapture raw, string device, bool isLoopback)
    {
        Packet packet;
        try
        {
            packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
        }
        catch
        {
            return null;
        }

        var tcp = packet.Extract<TcpPacket>();
        if (tcp?.PayloadData is not { Length: > 0 } data)
            return null;
        if (tcp.ParentPacket is not IPPacket ip)
            return null;

        return new TcpPayload
        {
            Device = device,
            IsLoopbackDevice = isLoopback,
            SrcIp = ip.SourceAddress.ToString(),
            DstIp = ip.DestinationAddress.ToString(),
            SrcPort = tcp.SourcePort,
            DstPort = tcp.DestinationPort,
            Sequence = tcp.SequenceNumber,
            Data = data,
            TimestampMs = new DateTimeOffset(raw.Timeval.Date).ToUnixTimeMilliseconds(),
            Raw = raw,
        };
    }
}
