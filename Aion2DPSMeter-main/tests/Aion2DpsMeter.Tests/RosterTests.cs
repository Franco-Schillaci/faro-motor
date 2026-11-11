using Aion2DpsMeter.Core.Protocol;

namespace Aion2DpsMeter.Tests;

/// <summary>
/// Fork (Faro): party roster (02 97) with bytes captured in the game (27/10/2026, player names replaced by letters
/// of the same length). A party still forming sends its empty slots as «00 slot + zero dbid + empty name»: that list
/// is complete, so it replaces the previous party.
/// </summary>
public class RosterTests
{
    private const string Forming = "0297A465080008494A4B4C4D4E4F5005012809000203B8C5040000007E050F0200050C01B8C5040000007E05094A4B4C4D4E4F5051521E0000002D000000DF0700007E057E0504CB600100000000000001010C0269CD040000007E050C4D4E4F505152535455565758210000002D000000DC0700007E057E05045F740100000000000001010C03307F040000007E0508494A4B4C4D4E4F50120000002D000000700700007E057E0504A5690100000000000001010004000000000000000000000000000000000000000000040000000000000000000000000500000000000000000000000000000000000000000004000000000000000000000000020E00363B3A7F80A1010000";
    private const string Full = "0297A465080008494A4B4C4D4E4F5005012809000203B8C5040000007E05FF0202050C01B8C5040000007E05094A4B4C4D4E4F5051521E0000002D000000DF0700007E057E0504CB600100000000000001010E0269CD040000007E050C4D4E4F505152535455565758210000002D000000DC0700007E057E05045F740100000000000001010E03307F040000007E0508494A4B4C4D4E4F50120000002D000000700700007E057E0504A5690100000000000001010E0447990300000063090748494A4B4C4D4E170000002D0000006B0800006309630904F4BC0100000000000001010E0536AE0300000062090748494A4B4C4D4E0B0000002D000000C50800000362096209041C60010000000000000101000A0E00362B6B8180A1010000";

    // 28/10/2026, a whole dungeon run: the fourth member's id says server 2404 but its tail says 2405, so the list
    // stopped there and the last two never had combat power.
    private const string OtherServerInTail = "0297E06A0000052B322049510501280900020362C6030000006509FF0202051E0162C603000000650908494A4B4C4D4E4F500C0000002D000000BB0700006509311104769201000000000000E90000000000000001021E0277D40300000065090C4D4E4F505152535455565758080000002D000000AF0700006509311104209601000000000000210000000000000001011E03307F040000007E05084A4B4C4D4E4F5051120000002D000000D20700007E05311104AF77010000000000003D0000000000000001021E0428B3030000006409094B4C4D4E4F50515253240000002D00000096070000076509311104038D010000000000008D0000000000000001011E053FC2040000007905094C4D4E4F50515253541E0000002D0000008A0800007905311104AF9701000000000000F000000000000000010100090E003694FCFC83A1010000";

    [Fact]
    public void A_member_whose_tail_shows_another_server_is_read_and_so_are_the_ones_after()
    {
        var r = PartyRosterParser.Parse(Convert.FromHexString(OtherServerInTail), 2);
        Assert.NotNull(r);
        Assert.True(r!.Complete);
        Assert.Equal(5, r.Members.Count);
        Assert.Equal(new long[] { 103030, 103968, 96175, 101635, 104367 }, r.Members.Select(m => m.CombatPower).ToArray());
        // The server stays the one in the character's id (its profile is there).
        Assert.Equal(new ushort[] { 2405, 2405, 1406, 2404, 1401 }, r.Members.Select(m => m.ServerId).ToArray());
        Assert.Equal(Aion2DpsMeter.Core.Data.JobClass.Chanter, r.Members[3].Job);
    }

    [Fact]
    public void A_party_still_forming_is_complete_with_its_three_members()
    {
        var r = PartyRosterParser.Parse(Convert.FromHexString(Forming), 2);
        Assert.NotNull(r);
        Assert.True(r!.Complete);
        Assert.Equal(3, r.Members.Count);
        Assert.Equal(new long[] { 90315, 95327, 92581 }, r.Members.Select(m => m.CombatPower).ToArray());
        Assert.All(r.Members, m => Assert.Equal(1406, m.ServerId));
        Assert.Equal(600065, r.DungeonId);
    }

    [Fact]
    public void A_full_party_reads_all_five_with_their_servers()
    {
        var r = PartyRosterParser.Parse(Convert.FromHexString(Full), 2);
        Assert.NotNull(r);
        Assert.True(r!.Complete);
        Assert.Equal(5, r.Members.Count);
        Assert.Equal(113908, r.Members[3].CombatPower);
        Assert.Equal(2403, r.Members[3].ServerId);
        Assert.Equal(2402, r.Members[4].ServerId);
    }
}