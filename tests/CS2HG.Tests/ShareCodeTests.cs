using CS2HG.Core.Models;
using CS2HG.DemoPlayback;

namespace CS2HG.Tests;

public class ShareCodeTests
{
    // Real CS2 match from April 2026 (its demo has long expired on Valve's servers). The ids come from the
    // MatchInfo that Valve's Game Coordinator sent and CS2 saved as match730_003815629072952721454_1435339544_202.dem.info.
    private const string RealCode = "CSGO-MtRHs-Rwbhk-XdL3a-FmQ8x-J2GGG";
    private const ulong RealMatchId = 3815624120855429217;
    private const ulong RealOutcomeId = 3815629072952721454;
    private const ushort RealToken = 35608;

    [Fact]
    public void Decodes_a_real_share_code()
    {
        var code = ShareCode.Parse(RealCode);
        Assert.Equal(RealMatchId, code.MatchId);
        Assert.Equal(RealOutcomeId, code.OutcomeId);
        Assert.Equal(RealToken, code.Token);
    }

    [Fact]
    public void Encodes_back_to_the_same_code()
    {
        Assert.Equal(RealCode, new ShareCode(RealMatchId, RealOutcomeId, RealToken).ToString());
    }

    [Theory]
    [InlineData(0UL, 0UL, (ushort)0)]
    [InlineData(ulong.MaxValue, ulong.MaxValue, ushort.MaxValue)]
    [InlineData(3700000000000000001UL, 3700000000000000999UL, (ushort)27015)]
    public void Round_trips(ulong match, ulong outcome, ushort token)
    {
        var original = new ShareCode(match, outcome, token);
        var text = original.ToString();
        Assert.Matches("^CSGO(-[A-Za-z0-9]{5}){5}$", text);
        Assert.Equal(original, ShareCode.Parse(text));
    }

    [Theory]
    [InlineData("steam://rungame/730/76561202255233023/+csgo_download_match%20CSGO-MtRHs-Rwbhk-XdL3a-FmQ8x-J2GGG")]
    [InlineData("cs2clutchez://CSGO-MtRHs-Rwbhk-XdL3a-FmQ8x-J2GGG")]
    [InlineData("  CSGO-MtRHs-Rwbhk-XdL3a-FmQ8x-J2GGG \n")]
    public void Finds_code_inside_links_and_text(string text) => Assert.Equal(RealCode, ShareCode.Find(text));

    [Theory]
    [InlineData("")]
    [InlineData("CSGO-12345")]
    [InlineData("CSGO-MtRHs-Rwbhk-XdL3a-FmQ8x")]
    [InlineData("CSGO-MtRHs-Rwbhk-XdL3a-FmQ8x-J2GG0")] // '0' is not in the alphabet
    public void Rejects_invalid_codes(string text)
    {
        Assert.False(ShareCode.TryParse(text, out _));
        Assert.Throws<FormatException>(() => ShareCode.Parse(text));
    }

    [Fact]
    public void Proto_writer_and_reader_round_trip_the_full_game_info_request()
    {
        var body = new ProtoWriter().Varint(1, RealMatchId).Varint(2, RealOutcomeId).Varint(3, RealToken).ToArray();
        var reader = new ProtoReader(body);
        var values = new Dictionary<int, ulong>();
        while (reader.Next(out var field, out var wire))
        {
            Assert.Equal(0, wire);
            values[field] = reader.ReadVarint();
        }

        Assert.Equal(RealMatchId, values[1]);
        Assert.Equal(RealOutcomeId, values[2]);
        Assert.Equal(RealToken, values[3]);
    }

    [Fact]
    public void Match_info_parser_reads_url_scores_and_ids()
    {
        // CDataGCCStrike15_v2_MatchInfo { matchid=1, matchtime=2, watchablematchinfo=3{tv_port=2}, roundstatsall=5{reservationid=1, map=3, team_scores=12, match_duration=15} }
        var url = "http://replay202.valve.net/730/003815629072952721454_1435339544.dem.bz2";
        var watchable = new ProtoWriter().Varint(2, RealToken).ToArray();
        var round = Concat(new ProtoWriter().Varint(1, RealOutcomeId).ToArray(), Field(3, System.Text.Encoding.UTF8.GetBytes(url)),
            new ProtoWriter().Varint(12, 11).Varint(12, 13).Varint(15, 2498).ToArray());
        var info = Concat(new ProtoWriter().Varint(1, RealMatchId).Varint(2, 1793574420).ToArray(), Field(3, watchable), Field(5, round));
        var list = Field(4, info);

        var match = Assert.Single(MatchListParser.Parse(list));
        Assert.Equal(RealCode, match.ShareCode.ToString());
        Assert.Equal(url, match.DemoUrl);
        Assert.Equal("11:13", match.ScoreText);
        Assert.Equal(2498, match.DurationSeconds);
    }

    [Fact]
    public void Real_dem_info_files_produce_valid_share_codes()
    {
        string replays;
        try
        {
            replays = Path.Combine(SteamLocator.Locate().CsgoFolder, "replays");
        }
        catch (Cs2NotFoundException)
        {
            return;
        }

        if (!Directory.Exists(replays)) return;
        foreach (var info in Directory.EnumerateFiles(replays, "match730_*.dem.info"))
        {
            var m = MatchListParser.ParseMatch(File.ReadAllBytes(info));
            // Valve names replay files after the outcome (reservation) id.
            Assert.Contains($"match730_{m.OutcomeId:D21}_", Path.GetFileName(info));
            Assert.Equal(m.ShareCode, ShareCode.Parse(m.ShareCode.ToString()));
        }
    }

    private static byte[] Field(int number, byte[] payload)
    {
        var header = new List<byte>();
        var tag = (ulong)((number << 3) | 2);
        foreach (var v in new[] { tag, (ulong)payload.Length })
        {
            var x = v;
            while (x >= 0x80)
            {
                header.Add((byte)(x | 0x80));
                x >>= 7;
            }

            header.Add((byte)x);
        }

        return [.. header, .. payload];
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
}
