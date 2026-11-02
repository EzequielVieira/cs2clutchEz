using System.Text.RegularExpressions;
using CS2HG.Application;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Models;
using CS2HG.Core.Settings;
using CS2HG.DemoPlayback;
using CS2HG.VideoProcessing;
using Microsoft.Extensions.Logging.Abstractions;

namespace CS2HG.Tests;

/// <summary>Real FFmpeg runs on synthetic footage (test pattern + tone) — no game required.</summary>
public class VideoProcessingTests
{
    private static FfmpegVideoProcessor Processor()
    {
        var runner = new FfmpegRunner(TestFfmpeg.Paths!, NullLogger.Instance);
        return new FfmpegVideoProcessor(runner, new EncoderCatalog(runner, NullLogger.Instance), new EffectRegistry(), NullLogger<FfmpegVideoProcessor>.Instance);
    }

    /// <summary>Simulates a raw capture: <paramref name="seconds"/> of 1280x720@60 video and a separate WAV.</summary>
    public static async Task<(string Video, string Audio)> FakeCapture(TempDir dir, string name, double seconds)
    {
        var runner = new FfmpegRunner(TestFfmpeg.Paths!, NullLogger.Instance);
        var video = dir.File(name + ".mp4");
        var audio = dir.File(name + ".wav");
        await runner.RunAsync(["-f", "lavfi", "-i", $"testsrc2=s=1280x720:r=60:d={seconds}", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", video], null, null, CancellationToken.None);
        await runner.RunAsync(["-f", "lavfi", "-i", $"sine=f=440:d={seconds}", "-ac", "2", "-ar", "48000", audio], null, null, CancellationToken.None);
        return (video, audio);
    }

    [FfmpegFact]
    public async Task Encoders_are_detected()
    {
        var runner = new FfmpegRunner(TestFfmpeg.Paths!, NullLogger.Instance);
        var catalog = new EncoderCatalog(runner, NullLogger.Instance);
        var probe = await catalog.ProbeAsync(CancellationToken.None);
        Assert.True(probe["libx264"]);
        Assert.StartsWith("h264", (await catalog.BestAsync(VideoCodec.H264, CancellationToken.None)).Replace("libx264", "h264"));
    }

    [FfmpegFact]
    public async Task Master_cuts_segments_at_offsets_and_crossfades()
    {
        using var dir = new TempDir();
        var a = await FakeCapture(dir, "a", 8);
        var b = await FakeCapture(dir, "b", 6);
        var p = Processor();
        var master = dir.File("master.mp4");
        await p.BuildMasterAsync(
            [new SegmentInput(a.Video, 1.0, a.Audio, 1.2, 5.0), new SegmentInput(b.Video, 0.5, b.Audio, 0.5, 4.0)],
            master, 0.3, null, CancellationToken.None);

        var info = await p.ProbeAsync(master, CancellationToken.None);
        Assert.Equal(5.0 + 4.0 - 0.3, info.DurationSeconds, 1);
        Assert.Equal(1280, info.Width);
        Assert.True(info.HasAudio);
    }

    [FfmpegFact]
    public async Task Master_without_audio_gets_silent_track()
    {
        using var dir = new TempDir();
        var a = await FakeCapture(dir, "a", 4);
        var p = Processor();
        var master = dir.File("m.mp4");
        await p.BuildMasterAsync([new SegmentInput(a.Video, 0.5, null, 0, 3)], master, 0, null, CancellationToken.None);
        var info = await p.ProbeAsync(master, CancellationToken.None);
        Assert.True(info.HasAudio);
        Assert.Equal(3, info.DurationSeconds, 1);
    }

    [FfmpegFact]
    public async Task Export_applies_resolution_fps_trim_slowmo_and_overlays()
    {
        using var dir = new TempDir();
        var a = await FakeCapture(dir, "a", 10);
        var p = Processor();
        var master = dir.File("m.mp4");
        await p.BuildMasterAsync([new SegmentInput(a.Video, 0, a.Audio, 0, 10)], master, 0, null, CancellationToken.None);

        var output = dir.File("out.mp4");
        var export = new ExportSettings { Width = 1920, Height = 1080, Fps = 30, BitrateKbps = 8000 };
        var edit = new EditSettings
        {
            TrimStartSeconds = 1, TrimEndSeconds = 9, SlowMotionStart = 4, SlowMotionEnd = 6, SlowMotionFactor = 0.5,
            Title = "Ace — teste", PlayerLabel = "player हॉट", RoundLabel = "Round 3",
            Effects = [new EffectSpec { Name = "vignette" }],
        };
        await p.ExportAsync(master, output, export, edit, null, CancellationToken.None);

        var info = await p.ProbeAsync(output, CancellationToken.None);
        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(30, info.Fps, 0);
        Assert.Equal("h264", info.VideoCodec);
        Assert.Equal(10, info.DurationSeconds, 1); // 8 s kept + 2 s extra from 0.5x slow motion
    }

    [FfmpegFact]
    public async Task Export_h265_when_supported()
    {
        var runner = new FfmpegRunner(TestFfmpeg.Paths!, NullLogger.Instance);
        if (!await new EncoderCatalog(runner, NullLogger.Instance).SupportsH265Async(CancellationToken.None)) return;
        using var dir = new TempDir();
        var a = await FakeCapture(dir, "a", 3);
        var p = Processor();
        var output = dir.File("hevc.mp4");
        await p.ExportAsync(a.Video, output, new ExportSettings { Width = 1280, Height = 720, Fps = 60, Codec = VideoCodec.H265 }, new EditSettings { ShowOverlay = false }, null, CancellationToken.None);
        Assert.Equal("hevc", (await p.ProbeAsync(output, CancellationToken.None)).VideoCodec);
    }

    [FfmpegFact]
    public async Task Thumbnail_and_montage()
    {
        using var dir = new TempDir();
        var a = await FakeCapture(dir, "a", 3);
        var b = await FakeCapture(dir, "b", 3);
        var p = Processor();
        await p.CreateThumbnailAsync(a.Video, 1, dir.File("t.jpg"), CancellationToken.None);
        Assert.True(new FileInfo(dir.File("t.jpg")).Length > 1000);

        var ma = dir.File("ma.mp4");
        var mb = dir.File("mb.mp4");
        await p.BuildMasterAsync([new SegmentInput(a.Video, 0, a.Audio, 0, 3)], ma, 0, null, CancellationToken.None);
        await p.BuildMasterAsync([new SegmentInput(b.Video, 0, b.Audio, 0, 3)], mb, 0, null, CancellationToken.None);
        await p.BuildMontageAsync([ma, mb], dir.File("montage.mp4"), 0.4, null, CancellationToken.None);
        Assert.Equal(5.6, (await p.ProbeAsync(dir.File("montage.mp4"), CancellationToken.None)).DurationSeconds, 1);
    }
}

/// <summary>Playback double that fails if the pipeline tries to use the game.</summary>
public sealed class NoGamePlayback : IDemoPlaybackController
{
    public PlaybackState State => new(false, false, null, "test");
    public IntPtr GameWindow => IntPtr.Zero;
    public int GameProcessId => 0;
    public Task EnsureSessionAsync(IProgress<string>? status, CancellationToken cancellationToken) => throw new InvalidOperationException("game must not be used");
    public Task LoadDemoAsync(string demoPath, CancellationToken cancellationToken) => throw new InvalidOperationException();
    public Task SeekAsync(int demoTick, CancellationToken cancellationToken) => throw new InvalidOperationException();
    public Task<(bool Verified, string? Observed)> SetPovAsync(PovTarget target, CancellationToken cancellationToken) => throw new InvalidOperationException();
    public Task ApplyHudAsync(HudMode mode, CancellationToken cancellationToken) => throw new InvalidOperationException();
    public Task<long> ResumeAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
    public Task<long> PauseAsync(CancellationToken cancellationToken) => throw new InvalidOperationException();
    public Task EndSessionAsync(bool closeGame, CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class PipelineRecoveryTests
{
    [FfmpegFact]
    public async Task Job_with_all_segments_captured_resumes_without_the_game()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Settings.Paths.RootFolder = dir.Path;
        store.Settings.FfmpegPath = TestFfmpeg.Paths!.Ffmpeg;
        var media = new MediaToolkit(store, NullLoggerFactory.Instance);
        var pipeline = new HighlightRenderPipeline(new NoGamePlayback(), media, store, NullLogger<HighlightRenderPipeline>.Instance);

        var a = await VideoProcessingTests.FakeCapture(dir, "seg00", 6);
        var b = await VideoProcessingTests.FakeCapture(dir, "seg01", 6);
        var demo = dir.File("fake.dem");
        File.WriteAllText(demo, "placeholder: capture already done");
        var job = new RenderJob
        {
            Title = "Recuperação",
            PlayerName = "Tester",
            MatchName = "de_test",
            Round = 7,
            DemoPath = demo,
            Category = HighlightCategory.MultiKill,
            Export = new ExportSettings { Width = 1280, Height = 720, Fps = 60, SecondsBefore = 2, OutputFolder = dir.Path },
            Segments = [new ClipSegment { Index = 0, StartTick = 0, EndTick = 320 }, new ClipSegment { Index = 1, StartTick = 1000, EndTick = 1320 }],
            Captured =
            [
                new CapturedSegment { Index = 0, VideoPath = a.Video, AudioPath = a.Audio, VideoOffsetSeconds = 1, AudioOffsetSeconds = 1, DurationSeconds = 5 },
                new CapturedSegment { Index = 1, VideoPath = b.Video, AudioPath = b.Audio, VideoOffsetSeconds = 1, AudioOffsetSeconds = 1, DurationSeconds = 5 },
            ],
        };

        var saves = 0;
        await pipeline.RenderAsync(job, _ => { saves++; return Task.CompletedTask; }, new Progress<RenderProgress>(), CancellationToken.None);

        Assert.True(File.Exists(job.OutputPath));
        Assert.Contains(Path.Combine("Tester", "de_test", "MultiKill"), job.OutputPath);
        Assert.Null(job.SourceMasterPath);
        Assert.False(Directory.Exists(Path.Combine(store.Settings.Paths.Work, job.Id)));
        Assert.True(File.Exists(job.ThumbnailPath));
        Assert.True(saves >= 1);
        var info = await media.Processor.ProbeAsync(job.OutputPath!, CancellationToken.None);
        Assert.Equal(5 + 5 - 0.3, info.DurationSeconds, 1);
    }
}

public class PlaybackInfrastructureTests
{
    // Shape of a payload captured from CS2 during demo playback (trimmed, identities replaced).
    private const string GsiSample = """
        {"provider":{"name":"Counter-Strike: Global Offensive","appid":730,"version":14194,"steamid":"76561198000000099"},
         "map":{"mode":"competitive","name":"de_mirage","phase":"live","round":3},
         "round":{"phase":"live"},
         "player":{"steamid":"76561198000000042","clan":"CLAN","name":"Player42","observer_slot":2,"team":"CT","activity":"playing"},
         "allplayers":{"76561198000000077":{"name":"O&apos;Neil_  77","observer_slot":6,"team":"T"}}}
        """;

    [Fact]
    public void Gsi_payload_reports_spectated_player()
    {
        var s = GsiListener.Parse(GsiSample)!;
        Assert.Equal(76561198000000042UL, s.ObservedSteamId);
        Assert.Equal("Player42", s.ObservedName);
        Assert.Equal("de_mirage", s.Map);
        Assert.Equal(3, s.Round);
        Assert.Equal("live", s.Phase);
    }

    [Fact]
    public async Task Console_log_watcher_waits_for_new_lines_only()
    {
        using var dir = new TempDir();
        var log = dir.File("console.log");
        File.WriteAllText(log, "11/01 21:35:36 [Demo] Demo Skipping finished at tick 1\n");
        await using var watcher = new ConsoleLogWatcher(log, fromEnd: true);
        var mark = watcher.Mark;
        var regex = new Regex(@"Demo Skipping finished at tick (\d+)");
        var waiting = watcher.WaitForAsync(regex, mark, TimeSpan.FromSeconds(5), CancellationToken.None);
        await Task.Delay(200);
        await File.AppendAllTextAsync(log, "11/01 21:35:37 [InputService] execing cs2hg/cmd\n11/01 21:35:37 [Demo] Demo Skipping finished at tick 20000\n");
        var m = await waiting;
        Assert.NotNull(m);
        Assert.Equal("20000", m!.Groups[1].Value);
        Assert.Null(await watcher.WaitForAsync(new Regex("never"), watcher.Mark, TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public void Config_manager_writes_cfgs_and_restores_user_files()
    {
        using var dir = new TempDir();
        var csgo = Path.Combine(dir.Path, "game", "csgo");
        var userCfg = Path.Combine(dir.Path, "userdata", "1", "730", "local", "cfg");
        Directory.CreateDirectory(Path.Combine(csgo, "cfg"));
        Directory.CreateDirectory(userCfg);
        var video = Path.Combine(userCfg, "cs2_video.txt");
        const string original = "\"video.cfg\"\n{\n\t\"setting.defaultres\"\t\t\"1280\"\n\t\"setting.defaultresheight\"\t\t\"960\"\n\t\"setting.fullscreen\"\t\t\"1\"\n}\n";
        File.WriteAllText(video, original);
        File.WriteAllText(Path.Combine(userCfg, "cs2_user_keys_0_slot0.vcfg"), "keys-original");

        var install = new Cs2Installation(dir.Path, "steam.exe", dir.Path, csgo, "cs2.exe", userCfg);
        var cfg = new Cs2ConfigManager(install, Path.Combine(dir.Path, "backup"), 31982, NullLogger.Instance);
        cfg.InstallHelperFiles();
        Assert.Contains("bind F13 \"exec cs2hg/cmd\"", File.ReadAllText(Path.Combine(csgo, "cfg", "cs2hg", "boot.cfg")));
        Assert.Contains("127.0.0.1:31982", File.ReadAllText(Path.Combine(csgo, "cfg", "gamestate_integration_cs2hg.cfg")));

        cfg.WriteCommands(["demo_gototick 123", "spec_player \"player हॉट\""]);
        Assert.Contains("player हॉट", File.ReadAllText(cfg.CommandCfgPath));

        cfg.BackupUserConfig();
        cfg.ApplyCaptureVideoSettings(1920, 1080);
        var applied = File.ReadAllText(video);
        Assert.Contains("\"setting.defaultres\"\t\t\"1920\"", applied);
        Assert.Contains("\"setting.fullscreen\"\t\t\"0\"", applied);
        Assert.Contains("setting.fullscreen_min_on_focus_loss", applied);
        File.WriteAllText(Path.Combine(userCfg, "cs2_user_keys_0_slot0.vcfg"), "keys-modified-by-game");

        Assert.True(cfg.RestoreUserConfig());
        Assert.Equal(original, File.ReadAllText(video));
        Assert.Equal("keys-original", File.ReadAllText(Path.Combine(userCfg, "cs2_user_keys_0_slot0.vcfg")));
        Assert.False(cfg.HasPendingRestore);
    }

    [Fact]
    public void Staged_demo_path_is_relative_to_csgo_folder()
    {
        using var dir = new TempDir();
        var csgo = Path.Combine(dir.Path, "game", "csgo");
        Directory.CreateDirectory(csgo);
        var demo = dir.File("my demo.dem");
        File.WriteAllBytes(demo, new byte[1024]);
        var install = new Cs2Installation(dir.Path, "steam.exe", dir.Path, csgo, "cs2.exe", null);
        var cfg = new Cs2ConfigManager(install, dir.File("b"), 31982, NullLogger.Instance);
        var staged = cfg.StageDemo(demo, "my_demo_abc");
        Assert.True(File.Exists(staged));
        Assert.Equal("cs2hg/demos/my_demo_abc", cfg.RelativeDemoPath(staged));
    }

    [Fact]
    public void Steam_and_cs2_are_located_when_installed()
    {
        Cs2Installation install;
        try
        {
            install = SteamLocator.Locate();
        }
        catch (Cs2NotFoundException)
        {
            return; // machine without CS2
        }

        Assert.True(File.Exists(install.Cs2Exe));
        Assert.True(Directory.Exists(install.CsgoFolder));
    }
}
