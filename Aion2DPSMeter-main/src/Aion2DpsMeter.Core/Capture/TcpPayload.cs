using SharpPcap;

namespace Aion2DpsMeter.Core.Capture;

/// <summary>The payload of one captured TCP segment.</summary>
public sealed class TcpPayload
{
    public required string Device { get; init; }
    public required bool IsLoopbackDevice { get; init; }
    public required string SrcIp { get; init; }
    public required string DstIp { get; init; }
    public required ushort SrcPort { get; init; }
    public required ushort DstPort { get; init; }
    public required uint Sequence { get; init; }
    public required byte[] Data { get; init; }
    public required long TimestampMs { get; init; }
    /// <summary>The raw frame, kept so a packet recording can be written.</summary>
    public RawCapture? Raw { get; init; }

    public string FlowKey => $"{SrcIp}:{SrcPort}>{DstIp}:{DstPort}";
}
