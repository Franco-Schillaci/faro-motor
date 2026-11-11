using Aion2DpsMeter.Core.Capture;
using Aion2DpsMeter.Core.Combat;
using Aion2DpsMeter.Core.Data;
using Aion2DpsMeter.Core.Protocol;
using static Aion2DpsMeter.Tests.PacketBuilder;
using static Aion2DpsMeter.Tests.ParserTests;

namespace Aion2DpsMeter.Tests;

public class TcpStreamTests
{
    private static (CombatStore Store, TcpStream Stream) Setup()
    {
        var store = new CombatStore(new GameData());
        return (store, new TcpStream(new PacketParser(store, new GameData())));
    }

    private static byte[] Hits(int n) => Enumerable.Range(0, n).SelectMany(_ => Damage(Me, Mob, Skill, 100)).ToArray();

    [Fact]
    public void Out_of_order_segments_are_reassembled()
    {
        var (store, stream) = Setup();
        var data = Hits(10);
        var chunks = data.Chunk(37).ToList();
        uint seq = 1000;
        var segments = chunks.Select(c => { var s = (Seq: seq, Data: c); seq += (uint)c.Length; return s; }).ToList();
        // Swap two segments.
        (segments[2], segments[3]) = (segments[3], segments[2]);
        foreach (var s in segments)
            stream.Add(s.Seq, s.Data, 1000);
        Assert.Equal(1000, store.GetDisplayRecord()!.TotalDamage);
    }

    [Fact]
    public void Retransmitted_segments_are_not_counted_twice()
    {
        var (store, stream) = Setup();
        var data = Hits(4);
        var half = data.Length / 2;
        stream.Add(1, data[..half], 1000);
        stream.Add(1, data[..half], 1000);          // full retransmit
        stream.Add((uint)(1 + half - 5), data[(half - 5)..], 1000); // overlapping retransmit
        Assert.Equal(400, store.GetDisplayRecord()!.TotalDamage);
    }

    [Fact]
    public void Sequence_wraparound_is_handled()
    {
        var (store, stream) = Setup();
        var data = Hits(2);
        uint start = uint.MaxValue - 10;
        stream.Add(start, data[..20], 1000);
        stream.Add(unchecked(start + 20), data[20..], 1000);
        Assert.Equal(200, store.GetDisplayRecord()!.TotalDamage);
    }
}
