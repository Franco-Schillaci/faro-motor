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
    public void A_lost_segment_is_reported_with_its_size()
    {
        var (store, stream) = Setup();
        var lost = new List<(long Bytes, long At)>();
        stream.BytesLost += (bytes, at) => lost.Add((bytes, at));
        var data = Hits(10);
        var chunks = data.Chunk(37).ToList();
        uint seq = 1000;
        var segments = chunks.Select(c => { var s = (Seq: seq, Data: c); seq += (uint)c.Length; return s; }).ToList();
        // The capture never gets segment 2: the stream waits up to 500 ms for it, then goes on without it.
        long t = 1000;
        foreach (var (s, i) in segments.Select((s, i) => (s, i)).Where(x => x.i != 2))
            stream.Add(s.Seq, s.Data, t += 300);
        var gap = Assert.Single(lost);
        Assert.Equal(segments[2].Data.Length, gap.Bytes);
        Assert.Equal(2500, gap.At);   // segment 5, the first to arrive more than 500 ms after segment 3 was held
    }

    [Fact]
    public void A_late_segment_that_arrives_in_time_is_not_reported_as_lost()
    {
        var (store, stream) = Setup();
        int lost = 0;
        stream.BytesLost += (_, _) => lost++;
        var data = Hits(4);
        stream.Add(1000, data[..37], 1000);
        stream.Add(1000 + 74, data[74..], 1100);     // from the future: held
        stream.Add(1000 + 37, data[37..74], 1400);   // the missing piece, within 500 ms
        Assert.Equal(0, lost);
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
