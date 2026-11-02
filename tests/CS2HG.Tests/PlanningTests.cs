using CS2HG.Core.Models;
using CS2HG.Core.Planning;
using CS2HG.Core.Settings;
using CS2HG.Core.Utilities;
using CS2HG.HighlightDetection;
using CS2HG.VideoProcessing;
using static CS2HG.Tests.MatchBuilder;

namespace CS2HG.Tests;

public class PlanningTests
{
    [Fact]
    public void Ranges_pad_each_action_with_before_and_after()
    {
        var r = ClipPlanner.BuildRanges([10_000], 64, 15, 10, 0, 0, int.MaxValue);
        Assert.Equal([new TickRange(10_000 - 15 * 64, 10_000 + 10 * 64)], r);
        Assert.Equal(25, r[0].Seconds(64));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    [InlineData(30)]
    public void All_allowed_pre_roll_values_produce_exact_durations(int before)
    {
        Assert.Contains(before, ExportSettings.AllowedSecondsBefore);
        foreach (var after in ExportSettings.AllowedSecondsAfter)
        {
            var r = ClipPlanner.BuildRanges([100_000], 64, before, after, 0, 0, int.MaxValue).Single();
            Assert.Equal(before + after, r.Seconds(64), 3);
        }
    }

    [Fact]
    public void Close_actions_merge_and_distant_actions_split()
    {
        var ticks = new[] { 10_000, 10_000 + 3 * 64, 10_000 + 60 * 64 };
        var r = ClipPlanner.BuildRanges(ticks, 64, 5, 5, 8, 0, int.MaxValue);
        Assert.Equal(2, r.Count);
        Assert.Equal(10_000 - 5 * 64, r[0].Start);
        Assert.Equal(10_000 + 8 * 64, r[0].End);
    }

    [Fact]
    public void Ranges_are_clamped_to_round_and_demo_bounds()
    {
        var r = ClipPlanner.BuildRanges([100, 999_900], 64, 10, 10, 0, 50, 1_000_000);
        Assert.Equal(50, r[0].Start);
        Assert.Equal(1_000_000, r[^1].End);
    }

    [Fact]
    public void Plan_uses_round_bounds_and_resolves_pov_per_segment()
    {
        var m = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000)
            .Kill(Ct[0], T[0], 1).Kill(Ct[0], T[1], 60).Build();
        var h = new HighlightAnalyzer(HighlightAnalyzer.DefaultDetectors()).Analyze(m, Ct[0], new DetectionSettings(), new ScoreSettings()).Single();

        var export = new ExportSettings { SecondsBefore = 10, SecondsAfter = 5, Pov = PovMode.Victim, BetweenPlays = BetweenPlays.Cut };
        var plan = ClipPlanner.Plan(h, m, export, new DetectionSettings { SegmentMergeGapSeconds = 8 });

        Assert.Equal(2, plan.Count);
        Assert.Equal(m.Rounds[0].FreezeEndTick, plan[0].StartTick); // first kill 1 s after freeze end: clamped
        Assert.Equal(T[0], plan[0].PovSteamId);
        Assert.Equal(T[1], plan[1].PovSteamId);
        Assert.All(plan, s => Assert.True(s.EndTick > s.StartTick));
    }

    [Theory]
    [InlineData(PovMode.SelectedPlayer, 0)]
    [InlineData(PovMode.Killer, 0)]
    [InlineData(PovMode.Victim, 1)]
    [InlineData(PovMode.SpecificPlayer, 2)]
    [InlineData(PovMode.ThirdPerson, 0)]
    [InlineData(PovMode.DirectorCamera, 0)]
    public void Pov_resolver_modes(PovMode mode, int expected)
    {
        var m = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000).Kill(Ct[0], T[0], 5).Build();
        var h = new HighlightAnalyzer(HighlightAnalyzer.DefaultDetectors()).Analyze(m, Ct[0], new DetectionSettings(), new ScoreSettings()).Single();
        var range = new TickRange(0, int.MaxValue);
        var ids = new[] { Ct[0], T[0], Ct[3] };
        var (steamId, _) = PovResolver.Resolve(h, m, range, mode, Ct[3]);
        Assert.Equal(ids[expected], steamId);
    }

    [Fact]
    public void Edit_planner_trim_cut_and_slow_motion()
    {
        var edit = new EditSettings
        {
            TrimStartSeconds = 2,
            TrimEndSeconds = 40,
            Cuts = [new CutRange(10, 15)],
            SlowMotionStart = 20,
            SlowMotionEnd = 23,
            SlowMotionFactor = 0.5,
        };
        var pieces = EditPlanner.BuildPieces(46.3, edit);
        Assert.Equal(4, pieces.Count);
        Assert.Equal(new EditPiece(2, 10, 1), pieces[0]);
        Assert.Equal(new EditPiece(20, 23, 0.5), pieces[2]);
        // 38 s kept − 5 s cut + 3 s extra from slow motion
        Assert.Equal(36, EditPlanner.OutputDuration(pieces), 3);
    }

    [Fact]
    public void Edit_planner_global_speed()
    {
        var pieces = EditPlanner.BuildPieces(30, new EditSettings { Speed = 2 });
        Assert.Equal(15, EditPlanner.OutputDuration(pieces), 3);
    }

    [Theory]
    [InlineData(0.5, new[] { 0.5 })]
    [InlineData(0.25, new[] { 0.5, 0.5 })]
    [InlineData(2.0, new[] { 2.0 })]
    [InlineData(1.0, new[] { 1.0 })]
    public void Atempo_chain_stays_in_supported_range(double speed, double[] expected) =>
        Assert.Equal(expected, EditPlanner.AtempoChain(speed).Select(x => Math.Round(x, 4)).ToArray());

    [Fact]
    public void Dedupe_key_changes_with_settings_only()
    {
        var a = new RenderJob { HighlightId = "h1", Export = new ExportSettings() };
        var b = new RenderJob { HighlightId = "h1", Export = new ExportSettings() };
        Assert.Equal(Hashing.RenderDedupeKey(a), Hashing.RenderDedupeKey(b));
        b.Export.Fps = 30;
        Assert.NotEqual(Hashing.RenderDedupeKey(a), Hashing.RenderDedupeKey(b));
    }

    [Theory]
    [InlineData("player हॉट और गॉ", "Nirmala")]
    [InlineData("플레이어", "malgun")]
    [InlineData("選手", "msyh")]
    [InlineData("Player One", "segoeui")]
    public void Overlay_font_covers_the_script(string text, string expectedFont)
    {
        var font = FfmpegVideoProcessor.FontFor(text);
        Assert.NotNull(font);
        Assert.Contains(expectedFont, Path.GetFileName(font!), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void File_names_are_sanitized()
    {
        Assert.Equal("Clutch 1v3 _ Ace", FileNames.Sanitize("Clutch 1v3 : Ace"));
        Assert.Equal("match730_00384_201", FileNames.AsciiSlug("match730 00384 (201)"));
        Assert.Equal(@"C\:/Program Files/a.ttf", FfmpegVideoProcessor.EscapePath(@"C:\Program Files\a.ttf"));
    }
}
