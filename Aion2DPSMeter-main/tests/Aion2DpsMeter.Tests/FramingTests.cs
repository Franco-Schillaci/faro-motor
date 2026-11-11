using Aion2DpsMeter.Core.Protocol;
using static Aion2DpsMeter.Tests.PacketBuilder;

namespace Aion2DpsMeter.Tests;

public class FramingTests
{
    [Fact]
    public void Walks_back_to_back_packets()
    {
        byte[] buf = [.. Frame([0x23, 0x36, 0x01]), .. Frame([0x41, 0x36, 0x02, 0x03])];
        var f = Framing.Walk(buf);
        Assert.Equal(2, f.Frames.Count);
        Assert.Equal(buf.Length, f.Consumed);
        Assert.Equal(new byte[] { 0x23, 0x36, 0x01 }, f.Frames[0].Payload(buf).ToArray());
        Assert.Equal(new byte[] { 0x41, 0x36, 0x02, 0x03 }, f.Frames[1].Payload(buf).ToArray());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(122)]
    [InlineData(124)]
    [InlineData(200)]
    [InlineData(16_380)]
    [InlineData(20_000)]
    public void Length_counts_payload_plus_four_for_any_varint_width(int len)
    {
        var payload = Body(0x23, len);
        byte[] buf = [.. Frame(payload), .. Frame([0x41, 0x36, 0x07])];
        var f = Framing.Walk(buf);
        Assert.Equal(2, f.Frames.Count);
        Assert.Equal(payload, f.Frames[0].Payload(buf).ToArray());
        Assert.Equal(buf.Length, f.Consumed);
    }

    [Fact]
    public void A_tls_record_between_packets_is_skipped_whole()
    {
        var tls = new List<byte> { 0x17, 0x03, 0x03, 0x05, 0x7a, 0x88, 0x71 };
        tls.AddRange(Enumerable.Range(0, 0x057a - 2).Select(i => (byte)((i % 251) | 1)));
        byte[] buf = [.. Frame([0x23, 0x36, 0x01]), .. tls, .. Frame(Body(0x04, 200))];
        var f = Framing.Walk(buf);
        Assert.Equal(buf.Length, f.Consumed);
        Assert.Equal(2, f.Frames.Count);
        Assert.Equal(Body(0x04, 200), f.Frames[1].Payload(buf).ToArray());
    }

    [Fact]
    public void Stops_on_an_incomplete_trailing_packet()
    {
        var full = Frame([1, 2, 3]);
        byte[] buf = [.. full, 0x40];
        var f = Framing.Walk(buf);
        Assert.Single(f.Frames);
        Assert.Equal(full.Length, f.Consumed);
    }

    [Fact]
    public void Bundle_decompresses_into_its_packets()
    {
        var inner = new[] { Body(0x04, 5), Body(0x05, 300), Body(0x40, 40) };
        byte[] innerStream = inner.SelectMany(Frame).ToArray();
        byte[] buf = [.. Bundle(innerStream), .. Frame([0x41, 0x36, 0x09])];

        var f = Framing.Walk(buf);
        Assert.Equal(2, f.Frames.Count);
        Assert.Equal(FrameKind.Bundle, f.Frames[0].Kind);
        var data = Framing.DecompressBundle(f.Frames[0].Payload(buf));
        Assert.NotNull(data);
        Assert.Equal(innerStream, data);
        var w = Framing.WalkInner(data);
        Assert.Equal(inner.Length, w.Frames.Count);
        for (int i = 0; i < inner.Length; i++)
            Assert.Equal(inner[i], w.Frames[i].Payload(data).ToArray());
    }

    [Fact]
    public void A_stream_fed_in_tcp_sized_pieces_frames_the_same()
    {
        var payloads = new[] { Body(0x04, 30), Body(0x05, 126), Body(0x40, 700), Body(0x23, 3), Body(0x45, 16_380) };
        byte[] stream = payloads.SelectMany(Frame).ToArray();
        foreach (int step in new[] { 1, 7, 100, 1460 })
        {
            var pending = new List<byte>();
            var got = new List<byte[]>();
            foreach (var chunk in stream.Chunk(step))
            {
                pending.AddRange(chunk);
                var arr = pending.ToArray();
                var f = Framing.Walk(arr);
                got.AddRange(f.Frames.Select(fr => fr.Payload(arr).ToArray()));
                pending.RemoveRange(0, f.Consumed);
            }
            Assert.Empty(pending);
            Assert.Equal(payloads.Length, got.Count);
            for (int i = 0; i < payloads.Length; i++)
                Assert.Equal(payloads[i], got[i]);
        }
    }

    [Fact]
    public void An_id_is_read_whole_not_from_its_last_byte()
    {
        // 13978 = 9A 6D; the 6D alone is 109 and must not win.
        Assert.Equal(13978, VarInt.EndingAt(new byte[] { 0x01, 0x9A, 0x6D, 0xE2, 0x07 }, 3, 0, 100, 99_999));
        Assert.Equal(14957, VarInt.EndingAt(new byte[] { 0x01, 0xED, 0x74, 0x18, 0x05 }, 3, 0, 100, 99_999));
        Assert.Equal(109, VarInt.EndingAt(new byte[] { 0x01, 0x6D, 0xE2, 0x07 }, 2, 0, 100, 99_999));
        Assert.Equal(8765, VarInt.EndingAt(new byte[] { 0x01, 0xBD, 0x44, 0xE2, 0x07 }, 3, 0, 100, 99_999));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("é")]
    [InlineData("あ")]
    [InlineData("ApexZ")]
    [InlineData("Zoë")]
    [InlineData("桜子")]
    [InlineData("전사")]
    [InlineData("Abcdefghijkl")]
    public void Valid_names_are_one_to_twelve_letters_or_digits(string name) =>
        Assert.Equal(name, Names.Exact(System.Text.Encoding.UTF8.GetBytes(name)));

    [Theory]
    [InlineData("Abcdefghijklm")]
    [InlineData("12345")]
    [InlineData("Apex Z")]
    [InlineData("")]
    public void Invalid_names_are_rejected(string name) =>
        Assert.Null(Names.Exact(System.Text.Encoding.UTF8.GetBytes(name)));
}
