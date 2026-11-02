using CS2HG.Core.Models;
using CS2HG.Core.Planning;
using CS2HG.Core.Settings;
using CS2HG.DemoParser;
using CS2HG.HighlightDetection;
using ICSharpCode.SharpZipLib.BZip2;
using Microsoft.Extensions.Logging.Abstractions;

namespace CS2HG.Tests;

public class DemoImportTests
{
    private static DemoImporter Importer(TempDir dir) => new(dir.Path, NullLogger<DemoImporter>.Instance);

    [Fact]
    public async Task Missing_file_is_rejected()
    {
        using var dir = new TempDir();
        await Assert.ThrowsAsync<FileNotFoundException>(() => Importer(dir).ImportAsync(dir.File("nope.dem"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_extension_is_rejected()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("a.txt"), "x");
        await Assert.ThrowsAsync<DemoFormatException>(() => Importer(dir).ImportAsync(dir.File("a.txt"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Csgo_demo_is_rejected_with_explanation()
    {
        using var dir = new TempDir();
        var bytes = new byte[64];
        "HL2DEMO\0"u8.CopyTo(bytes);
        File.WriteAllBytes(dir.File("old.dem"), bytes);
        var ex = await Assert.ThrowsAsync<DemoFormatException>(() => Importer(dir).ImportAsync(dir.File("old.dem"), null, CancellationToken.None));
        Assert.Contains("CS:GO", ex.Message);
    }

    [Fact]
    public async Task Garbage_is_rejected()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("bad.dem"), new byte[100]);
        await Assert.ThrowsAsync<DemoFormatException>(() => Importer(dir).ImportAsync(dir.File("bad.dem"), null, CancellationToken.None));
    }

    [DemoFact]
    public async Task Compressed_demo_is_decompressed_validated_and_cached()
    {
        // A real CS2 demo prefix compressed with bzip2 (the full file would make the test slow).
        using var dir = new TempDir();
        var source = TestDemos.First()!;
        var prefix = new byte[2 * 1024 * 1024];
        await using (var fs = File.OpenRead(source)) _ = await fs.ReadAsync(prefix);
        var bz2 = dir.File("sample.dem.bz2");
        await using (var output = File.Create(bz2))
        await using (var bz = new BZip2OutputStream(output))
        {
            await bz.WriteAsync(prefix);
        }

        var importer = Importer(dir);
        var first = await importer.ImportAsync(bz2, null, CancellationToken.None);
        Assert.True(first.WasCompressed);
        Assert.EndsWith(".dem", first.DemoPath);
        Assert.Equal(prefix, await File.ReadAllBytesAsync(first.DemoPath));

        var again = await importer.ImportAsync(bz2, null, CancellationToken.None);
        Assert.Equal(first.DemoPath, again.DemoPath);
        Assert.Equal(first.Hash, again.Hash);
    }

    [Fact]
    public async Task Corrupted_bz2_reports_a_clear_error()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("broken.dem.bz2"), [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        await Assert.ThrowsAsync<DemoFormatException>(() => Importer(dir).ImportAsync(dir.File("broken.dem.bz2"), null, CancellationToken.None));
    }
}

/// <summary>End-to-end parsing and detection on real CS2 demos (no game needed).</summary>
public class DemoIntegrationTests
{
    [DemoFact]
    public async Task Every_available_demo_parses_without_errors()
    {
        // Includes the public sample demos in tests/TestData/demos (DemoFile.Net, MIT).
        var parser = new DemoFileNetParser(NullLogger<DemoFileNetParser>.Instance);
        foreach (var path in TestDemos.All())
        {
            DemoImporter.ValidateHeader(path);
            var m = await parser.ParseAsync(path, null, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(m.Demo.MapName), path);
            Assert.Equal(64, m.Demo.TickRate);
            Assert.True(m.Demo.TotalDemoTicks > 0, path);
            Assert.NotEmpty(m.Players);
            var analyzer = new HighlightAnalyzer(HighlightAnalyzer.DefaultDetectors());
            _ = analyzer.Analyze(m, null, new DetectionSettings { MinimumScore = 0 }, new ScoreSettings());
        }
    }

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static MatchData? _cached;

    private static async Task<MatchData> Parsed()
    {
        await Gate.WaitAsync();
        try
        {
            if (_cached is not null) return _cached;
            var path = TestDemos.FullMatch()!;
            var match = await new DemoFileNetParser(NullLogger<DemoFileNetParser>.Instance).ParseAsync(path, null, CancellationToken.None);
            match.Demo.FileHash = await DemoImporter.ComputeHashAsync(path, null, CancellationToken.None);
            MatchAnalysis.FillClutchStats(match);
            return _cached = match;
        }
        finally
        {
            Gate.Release();
        }
    }

    [FullMatchFact]
    public async Task Players_rounds_and_header_are_extracted()
    {
        var m = await Parsed();
        Assert.StartsWith("de_", m.Demo.MapName);
        Assert.Equal(64, m.Demo.TickRate);
        Assert.True(m.Demo.TotalDemoTicks > 10_000);
        Assert.InRange(m.Players.Count, 2, 12);
        Assert.All(m.Players, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.All(m.Players.Where(p => !p.IsBot), p => Assert.True(p.SteamId > 76561197960265728UL));
        Assert.True(m.Rounds.Count >= 13);
        Assert.Equal(Enumerable.Range(m.Rounds[0].Number, m.Rounds.Count), m.Rounds.Select(r => r.Number));
        Assert.All(m.Rounds, r =>
        {
            Assert.True(r.StartTick <= r.FreezeEndTick && r.FreezeEndTick <= r.EndTick && r.EndTick <= r.OfficialEndTick);
            Assert.True(r.Winner.IsPlaying());
        });
    }

    [FullMatchFact]
    public async Task Final_score_matches_round_winners()
    {
        var m = await Parsed();
        var last = m.Rounds[^1];
        Assert.Equal(m.Rounds.Count, last.ScoreCtAfter + last.ScoreTAfter);
        Assert.Equal(Math.Max(m.FinalScoreCt, m.FinalScoreT), Math.Max(last.ScoreCtAfter, last.ScoreTAfter));
    }

    [FullMatchFact]
    public async Task Kill_events_are_consistent()
    {
        var m = await Parsed();
        Assert.NotEmpty(m.Kills);
        var rounds = m.Rounds.ToDictionary(r => r.Number);
        Assert.All(m.Kills, k =>
        {
            Assert.True(rounds.ContainsKey(k.Round));
            Assert.True(k.Tick >= rounds[k.Round].StartTick);
            Assert.False(string.IsNullOrWhiteSpace(k.Weapon));
            Assert.DoesNotContain(k.VictimSteamId, k.AliveAfter(k.VictimTeam));
        });
        Assert.Equal(m.Kills.OrderBy(k => k.Tick).Select(k => k.Tick), m.Kills.Select(k => k.Tick));
        Assert.Contains(m.Kills, k => k.Headshot);
        Assert.Contains(m.Kills, k => k.AttackerPosition is not null && k.VictimPosition is not null);
        Assert.Equal(m.Kills.Count(k => k.IsEnemyKill), m.Players.Sum(p => p.Stats.Kills));
    }

    [FullMatchFact]
    public async Task Damage_grenades_and_bomb_events_are_extracted()
    {
        var m = await Parsed();
        Assert.NotEmpty(m.Damages);
        Assert.All(m.Damages, d => Assert.InRange(d.HealthDamage, 0, 100));
        Assert.NotEmpty(m.Grenades);
        Assert.Contains(m.Grenades, g => g.Type == GrenadeType.Smoke);
        var plants = m.BombEvents.Where(b => b.Type == BombEventType.Planted).ToList();
        if (plants.Count > 0) Assert.All(plants, p => Assert.Contains(p.Site, new[] { "A", "B" }));
    }

    [FullMatchFact]
    public async Task Highlights_for_every_player_have_valid_ticks_and_segments()
    {
        var m = await Parsed();
        var analyzer = new HighlightAnalyzer(HighlightAnalyzer.DefaultDetectors());
        var all = analyzer.Analyze(m, null, new DetectionSettings { MinimumScore = 0 }, new ScoreSettings());
        Assert.NotEmpty(all);
        Assert.Equal(all.OrderByDescending(h => h.Score).Select(h => h.Score), all.Select(h => h.Score));

        var export = new ExportSettings { SecondsBefore = 5, SecondsAfter = 5 };
        foreach (var h in all)
        {
            var round = m.FindRound(h.Round)!;
            Assert.All(h.ActionTicks, t => Assert.InRange(t, round.StartTick, round.OfficialEndTick + 64 * 30));
            var plan = ClipPlanner.Plan(h, m, export, new DetectionSettings());
            Assert.NotEmpty(plan);
            Assert.All(plan, s =>
            {
                Assert.True(s.StartTick >= 0 && s.EndTick <= m.Demo.TotalDemoTicks);
                Assert.True(s.EndTick > s.StartTick);
                Assert.NotNull(s.PovSteamId);
            });
        }

        // Multi-kill counters from the parser agree with the detector.
        foreach (var p in m.Players)
        {
            var mine = all.Where(h => h.PlayerSteamId == p.SteamId).ToList();
            Assert.Equal(p.Stats.Aces, mine.Count(h => h.Has(Core.Models.HighlightKind.Ace)));
            Assert.Equal(p.Stats.FourKills, mine.Count(h => h.Has(Core.Models.HighlightKind.QuadKill)));
            Assert.Equal(p.Stats.ThreeKills, mine.Count(h => h.Has(Core.Models.HighlightKind.TripleKill)));
        }
    }

    [FullMatchFact]
    public async Task Clutch_statistics_match_detected_clutch_highlights()
    {
        var m = await Parsed();
        var analyzer = new HighlightAnalyzer(HighlightAnalyzer.DefaultDetectors());
        var all = analyzer.Analyze(m, null, new DetectionSettings { MinimumScore = 0 }, new ScoreSettings());
        foreach (var p in m.Players)
        {
            var won = all.Count(h => h.PlayerSteamId == p.SteamId && h.Kinds.Any(k => k.IsClutch()));
            Assert.Equal(p.Stats.ClutchesWon, won);
        }
    }
}
