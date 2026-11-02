using CS2HG.Application;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Models;
using CS2HG.Core.Planning;
using CS2HG.Core.Settings;
using CS2HG.HighlightDetection;
using CS2HG.VideoProcessing;
using Microsoft.Extensions.Logging.Abstractions;
using static CS2HG.Tests.MatchBuilder;

namespace CS2HG.Tests;

public class FastForwardPlanningTests
{
    private static (Highlight Highlight, MatchData Match) TwoKills(int secondKillAt)
    {
        var m = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000)
            .Kill(Ct[0], T[0], 1).Kill(Ct[0], T[1], secondKillAt).Build();
        var h = new HighlightAnalyzer(HighlightAnalyzer.DefaultDetectors()).Analyze(m, Ct[0], new DetectionSettings(), new ScoreSettings()).Single();
        return (h, m);
    }

    [Fact]
    public void Fast_forward_keeps_one_segment_and_speeds_up_the_gap()
    {
        var (h, m) = TwoKills(60);
        var export = new ExportSettings { SecondsBefore = 10, SecondsAfter = 5, Pov = PovMode.Victim, BetweenPlays = BetweenPlays.FastForward, FastForwardSpeed = 4 };
        var cut = ClipPlanner.Plan(h, m, export.WithCut(), new DetectionSettings { SegmentMergeGapSeconds = 8 });
        var plan = ClipPlanner.Plan(h, m, export, new DetectionSettings { SegmentMergeGapSeconds = 8 });

        var segment = Assert.Single(plan);
        Assert.Equal(cut[0].StartTick, segment.StartTick);
        Assert.Equal(cut[1].EndTick, segment.EndTick);
        var ff = Assert.Single(segment.FastForward);
        Assert.Equal(cut[0].EndTick, ff.StartTick);
        Assert.Equal(cut[1].StartTick, ff.EndTick);
        // The gap (~44 s) would take 11 s at 4x, so it goes up to 8x (5.5 s).
        Assert.Equal(8, ff.Speed);
        // Victim POV: the second play is watched from the second victim.
        Assert.Equal(T[0], segment.PovSteamId);
        Assert.Equal(T[1], ff.NextPovSteamId);

        var tickRate = m.Demo.TickRate;
        var gap = (ff.EndTick - ff.StartTick) / (double)tickRate;
        Assert.Equal(segment.DurationSeconds(tickRate) - gap + gap / 8, segment.PlaybackSeconds(tickRate), 3);
        Assert.Equal(cut.Sum(s => s.DurationSeconds(tickRate)) + gap / 8, ClipPlanner.TotalSeconds(plan, tickRate), 3);
    }

    [Fact]
    public void Same_pov_needs_no_switch()
    {
        var (h, m) = TwoKills(40);
        var plan = ClipPlanner.Plan(h, m, new ExportSettings { Pov = PovMode.SelectedPlayer }, new DetectionSettings());
        Assert.Null(Assert.Single(Assert.Single(plan).FastForward).NextPovSteamId);
    }

    [Theory]
    [InlineData(10, 4, 4)]
    [InlineData(20, 2, 4)]
    [InlineData(44, 4, 8)]
    [InlineData(200, 8, 16)]
    [InlineData(5, 8, 8)]
    public void Fast_forward_speed_grows_for_long_gaps(double gap, int baseSpeed, double expected) =>
        Assert.Equal(expected, ClipPlanner.FastForwardSpeed(gap, baseSpeed));
}

internal static class ExportTestExtensions
{
    public static ExportSettings WithCut(this ExportSettings e)
    {
        var c = e.Clone();
        c.BetweenPlays = BetweenPlays.Cut;
        return c;
    }
}

public class MasterTimelineTests
{
    [Fact]
    public void Ticks_map_through_measured_speed_changes()
    {
        var planned = new ClipSegment { Index = 0, StartTick = 1000, EndTick = 1000 + 30 * 64 };
        var captured = new CapturedSegment
        {
            Index = 0,
            DurationSeconds = 16,
            // 5 s normal, 20 s of demo at 4x (5 s), then normal again.
            Timeline = [new TimelinePoint { Tick = 1000 + 5 * 64, Seconds = 5 }, new TimelinePoint { Tick = 1000 + 25 * 64, Seconds = 10 }],
        };

        Assert.Equal(2, MasterTimeline.SegmentSeconds(captured, planned, 1000 + 2 * 64, 64), 3);
        Assert.Equal(7.5, MasterTimeline.SegmentSeconds(captured, planned, 1000 + 15 * 64, 64), 3);
        Assert.Equal(13, MasterTimeline.SegmentSeconds(captured, planned, 1000 + 28 * 64, 64), 3);
    }

    [Fact]
    public void Kill_times_include_previous_segments_and_crossfades()
    {
        var job = new RenderJob
        {
            TickRate = 64,
            Segments = [new ClipSegment { Index = 0, StartTick = 0, EndTick = 640 }, new ClipSegment { Index = 1, StartTick = 5000, EndTick = 5640 }],
            Captured = [new CapturedSegment { Index = 0, DurationSeconds = 10 }, new CapturedSegment { Index = 1, DurationSeconds = 10 }],
            KillTicks = [320, 5000 + 128],
        };

        var timeline = MasterTimeline.Build(job, 0.3);
        Assert.Equal([5.0, 10 - 0.3 + 2], timeline.KillTimes.Select(t => Math.Round(t, 3)));
    }

    [Fact]
    public void Fast_windows_are_moved_to_master_time()
    {
        var job = new RenderJob
        {
            Segments = [new ClipSegment { Index = 0, StartTick = 0, EndTick = 6400 }],
            Captured = [new CapturedSegment { Index = 0, DurationSeconds = 40, FastWindows = [new TimeWindow { Start = 8, End = 12, Speed = 8 }] }],
        };
        var w = Assert.Single(MasterTimeline.Build(job, 0.3).FastWindows);
        Assert.Equal((8, 12, 8), (w.Start, w.End, w.Speed));
    }
}

public class SpeedRangeTests
{
    [Fact]
    public void Kill_slow_motion_merges_close_kills_and_skips_fast_forward()
    {
        var ranges = EditPlanner.KillSlowMotion([2, 2.5, 9, 20], [new TimeWindow { Start = 8, End = 10, Speed = 4 }]);
        Assert.Equal(2, ranges.Count);
        Assert.Equal((1.65, 3.15), (Math.Round(ranges[0].Start, 2), Math.Round(ranges[0].End, 2)));
        Assert.Equal(19.65, ranges[1].Start, 3);
    }

    [Fact]
    public void Speed_ranges_split_pieces_and_map_output_time()
    {
        var edit = new EditSettings { SpeedRanges = [new SpeedRange(2, 4, 0.5)] };
        var pieces = EditPlanner.BuildPieces(10, edit);
        Assert.Equal([(0.0, 2.0, 1.0), (2.0, 4.0, 0.5), (4.0, 10.0, 1.0)], pieces.Select(p => (p.Start, p.End, p.Speed)));
        Assert.Equal(12, EditPlanner.OutputDuration(pieces), 3);
        Assert.Equal(1, EditPlanner.MapToOutput(pieces, 1), 3);
        Assert.Equal(4, EditPlanner.MapToOutput(pieces, 3), 3);
        Assert.Equal(8, EditPlanner.MapToOutput(pieces, 6), 3);
    }

    [Fact]
    public void Vertical_layout_uses_9_by_16()
    {
        Assert.Equal((1080, 1920), FfmpegVideoProcessor.OutputSize(new ExportSettings { Width = 1920, Height = 1080, Layout = VideoLayout.Vertical }));
        Assert.Equal((720, 1280), FfmpegVideoProcessor.OutputSize(new ExportSettings { Width = 1280, Height = 720, Layout = VideoLayout.Vertical }));
        Assert.Equal((1920, 1080), FfmpegVideoProcessor.OutputSize(new ExportSettings { Width = 1920, Height = 1080 }));
    }
}

public class UpdateTests
{
    [Theory]
    [InlineData("v1.1.0", "1.0.1", true)]
    [InlineData("v1.0.1", "1.0.1", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("v2.0", "1.9.9", true)]
    [InlineData("V1.0.2", "1.0.1.0", true)]
    public void Newer_releases_are_detected(string tag, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(UpdateService.ParseTag(tag)!, Version.Parse(current)));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void Odd_tags_are_ignored(string? tag) => Assert.Null(UpdateService.ParseTag(tag));

    [Fact]
    public void Auto_mode_picks_the_best_plays()
    {
        var hs = new[] { (1, 40), (2, 90), (3, 90), (4, 10) }.Select(x => new Highlight { Round = x.Item1, Score = x.Item2 }).ToList();
        Assert.Equal([2, 3], AutoModeService.PickTop(hs, 2).Select(h => h.Round));
        Assert.Single(AutoModeService.PickTop(hs, 0));
    }
}

public class EffectsExportTests
{
    private static FfmpegVideoProcessor Processor() => new(new FfmpegRunner(TestFfmpeg.Paths!, NullLogger.Instance),
        new EncoderCatalog(new FfmpegRunner(TestFfmpeg.Paths!, NullLogger.Instance), NullLogger.Instance), new EffectRegistry(),
        NullLogger<FfmpegVideoProcessor>.Instance);

    private static async Task<double> MaxVolumeAsync(string path, double start, double duration)
    {
        using var process = FfmpegRunner.Start(TestFfmpeg.Paths!.Ffmpeg, ["-hide_banner", "-ss", FfmpegRunner.F(start), "-t", FfmpegRunner.F(duration), "-i", path,
            "-af", "volumedetect", "-vn", "-f", "null", "-"], redirectInput: false);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var output = await process.StandardError.ReadToEndAsync();
        await stdout;
        await process.WaitForExitAsync();
        var m = System.Text.RegularExpressions.Regex.Match(output, @"max_volume:\s*(-?[\d.]+|-inf) dB");
        Assert.True(m.Success, output);
        return m.Groups[1].Value == "-inf" ? double.NegativeInfinity : double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [FfmpegFact]
    public async Task Fast_forward_windows_are_silent_in_the_master()
    {
        using var dir = new TempDir();
        var a = await VideoProcessingTests.FakeCapture(dir, "a", 10);
        var p = Processor();
        var master = dir.File("master.mp4");
        await p.BuildMasterAsync([new SegmentInput(a.Video, 0, a.Audio, 0, 9, [new TimeWindow { Start = 3, End = 6, Speed = 4 }])],
            master, 0, null, CancellationToken.None);

        Assert.True(await MaxVolumeAsync(master, 0.5, 2) > -30);
        Assert.True(await MaxVolumeAsync(master, 3.5, 2) < -60);
        Assert.True(await MaxVolumeAsync(master, 6.5, 2) > -30);
    }

    [FfmpegFact]
    public async Task Vertical_export_with_counter_watermark_badge_slow_motion_and_music()
    {
        using var dir = new TempDir();
        var a = await VideoProcessingTests.FakeCapture(dir, "a", 12);
        var music = dir.File("music.wav");
        await new FfmpegRunner(TestFfmpeg.Paths!, NullLogger.Instance).RunAsync(
            ["-f", "lavfi", "-i", "sine=f=220:d=3", "-ac", "2", "-ar", "44100", music], null, null, CancellationToken.None);
        var p = Processor();
        var master = dir.File("m.mp4");
        await p.BuildMasterAsync([new SegmentInput(a.Video, 0, a.Audio, 0, 12)], master, 0, null, CancellationToken.None);

        var export = new ExportSettings
        {
            Width = 1280, Height = 720, Fps = 30, BitrateKbps = 6000, Layout = VideoLayout.Vertical,
            SlowMotionKills = true, KillCounter = true, Watermark = "@player हॉट", MusicPath = music, MusicVolume = 40,
        };
        var edit = new EditSettings { Title = "Triple Kill — AK-47", PlayerLabel = "Tester", RoundLabel = "Round 4" };
        var timeline = new ClipTimeline([2, 5, 10], [new TimeWindow { Start = 6, End = 8, Speed = 4 }]);
        var output = dir.File("vertical.mp4");
        await p.ExportAsync(master, output, export, edit, null, CancellationToken.None, timeline);

        var info = await p.ProbeAsync(output, CancellationToken.None);
        Assert.Equal((720, 1280), (info.Width, info.Height));
        Assert.True(info.HasAudio);
        // Three kills in slow motion (1 s each at 0.5x) add 3 s; the music (3 s file) loops for the whole clip.
        Assert.Equal(15, info.DurationSeconds, 0);
        Assert.True(await MaxVolumeAsync(output, 12, 2) > -40);
    }

    [FfmpegFact]
    public async Task Montage_with_music_keeps_the_video_and_adds_audio()
    {
        using var dir = new TempDir();
        var a = await VideoProcessingTests.FakeCapture(dir, "a", 4);
        var b = await VideoProcessingTests.FakeCapture(dir, "b", 4);
        var music = dir.File("m.wav");
        await new FfmpegRunner(TestFfmpeg.Paths!, NullLogger.Instance).RunAsync(
            ["-f", "lavfi", "-i", "sine=f=330:d=10", "-ac", "2", "-ar", "48000", music], null, null, CancellationToken.None);
        var p = Processor();
        var output = dir.File("montage.mp4");
        await p.BuildMontageAsync([a.Video, b.Video], output, 0.4, null, CancellationToken.None, music, 50);

        var info = await p.ProbeAsync(output, CancellationToken.None);
        Assert.True(info.HasAudio);
        Assert.Equal(4 + 4 - 0.4, info.DurationSeconds, 0);
        Assert.False(File.Exists(Path.Combine(dir.Path, ".montage.joined.mp4")));
    }
}
