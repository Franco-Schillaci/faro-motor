using Aion2DpsMeter.Core.Capture;
using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.Protocol;
using static Aion2DpsMeter.Tests.PacketBuilder;
using static Aion2DpsMeter.Tests.ParserTests;

namespace Aion2DpsMeter.Tests;

public class DispatcherTests
{
    private const ushort ServerPort = 13328;

    private static TcpPayload Segment(uint seq, byte[] data, long ts) => new()
    {
        Device = "Wi-Fi",
        IsLoopbackDevice = false,
        SrcIp = "10.0.0.1",
        DstIp = "192.168.1.2",
        SrcPort = ServerPort,
        DstPort = 57742,
        Sequence = seq,
        Data = data,
        TimestampMs = ts,
    };

    [Fact]
    public void Locks_onto_the_game_stream_and_counts_its_damage()
    {
        var store = new CombatStore(new GameData());
        var dispatcher = new CaptureDispatcher(store, new GameData(), Opcodes.Default) { RequireGameProcess = false };
        // Real-time epoch timestamps, as the capture gives them.
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        uint seq = 1;
        // Idle game traffic: records ending in the 0E 00 36 terminator, ~20 a second.
        for (int i = 0; i < 60; i++)
        {
            var idle = Frame([0x23, 0x36, 0x05, 0x0E, 0x00, 0x36, 0x01]);
            dispatcher.Process(Segment(seq, idle, t + i * 50));
            seq += (uint)idle.Length;
        }
        Assert.Equal(CaptureState.Locked, dispatcher.Status.State);
        Assert.Equal(ServerPort, dispatcher.Status.Port);

        var hit = Damage(Me, Mob, Skill, 777);
        dispatcher.Process(Segment(seq, hit, t + 4000));
        Assert.Equal(777, store.GetDisplayRecord()!.TotalDamage);
    }
}
