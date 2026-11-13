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

    [Fact]
    public void Packet_loss_on_the_game_stream_goes_to_the_log_summed()
    {
        var store = new CombatStore(new GameData());
        var dispatcher = new CaptureDispatcher(store, new GameData(), Opcodes.Default) { RequireGameProcess = false };
        var log = new List<string>();
        dispatcher.Trace += log.Add;
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        uint seq = 1;
        var idle = Frame([0x23, 0x36, 0x05, 0x0E, 0x00, 0x36, 0x01]);
        void Send(long at) { dispatcher.Process(Segment(seq, idle, at)); seq += (uint)idle.Length; }
        void Lose(int frames) => seq += (uint)(idle.Length * frames);

        for (int i = 0; i < 60; i++)
            Send(t + i * 50);
        Assert.Equal(CaptureState.Locked, dispatcher.Status.State);

        // Ten seconds later the capture misses two frames: after 500 ms the stream goes on without them.
        t += 10_000;
        Lose(2);
        for (int i = 0; i < 12; i++)
            Send(t + i * 100);
        var line = Assert.Single(log);
        Assert.StartsWith($"capture: packets lost: 1 gap(s), {idle.Length * 2} bytes missing, the first at ", line);

        // More loss within the minute is summed into the next line, a minute after the first.
        Lose(1);
        for (int i = 0; i < 12; i++)
            Send(t + 2_000 + i * 100);
        Lose(3);
        for (int i = 0; i < 12; i++)
            Send(t + 10_000 + i * 100);
        Assert.Single(log);
        Send(t + 61_500);
        Assert.Equal(2, log.Count);
        Assert.StartsWith($"capture: packets lost: 2 gap(s), {idle.Length * 4} bytes missing", log[1]);
    }
}
