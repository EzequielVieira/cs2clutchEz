using CS2HG.Core.Models;
using CS2HG.Core.Settings;

namespace CS2HG.Core.Abstractions;

/// <summary>Parses a demo into <see cref="MatchData"/>. Wraps a third-party parser so it can be swapped.</summary>
public interface IDemoParser
{
    string ParserName { get; }

    Task<MatchData> ParseAsync(string demoPath, IProgress<double>? progress, CancellationToken cancellationToken);
}

public sealed record ImportedDemo(string OriginalPath, string DemoPath, string Hash, long Size, bool WasCompressed);

/// <summary>Validates demo files, decompresses .dem.bz2 and computes a stable content hash.</summary>
public interface IDemoImporter
{
    Task<ImportedDemo> ImportAsync(string path, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Everything a detector needs about one player in one round.</summary>
public sealed class DetectionContext
{
    public required MatchData Match { get; init; }
    public required RoundInfo Round { get; init; }
    public required ulong PlayerSteamId { get; init; }
    public required Team PlayerTeam { get; init; }
    public required IReadOnlyList<KillEvent> RoundKills { get; init; }
    public required IReadOnlyList<KillEvent> PlayerKills { get; init; }
    public required IReadOnlyList<DamageEvent> RoundDamages { get; init; }
    public required IReadOnlyList<BombEvent> RoundBombEvents { get; init; }
    public required DetectionSettings Settings { get; init; }
    public required ScoreSettings Scores { get; init; }

    public int TickRate => Match.Demo.TickRate <= 0 ? 64 : Match.Demo.TickRate;

    public int SecondsToTicks(double seconds) => (int)Math.Round(seconds * TickRate);
}

/// <summary>A pluggable rule that finds scored evidence (components) for a player in a round.</summary>
public interface IHighlightDetector
{
    string Name { get; }

    /// <summary>Human readable rule and its data limitations.</summary>
    string Description { get; }

    IEnumerable<HighlightComponent> Detect(DetectionContext context);
}

public interface IHighlightAnalyzer
{
    IReadOnlyList<Highlight> Analyze(MatchData match, ulong? playerSteamId, DetectionSettings detection, ScoreSettings scores);
}

public sealed record PovTarget(ulong? SteamId, string Name, PovMode Mode);

public sealed record PlaybackState(bool GameRunning, bool ControlReady, string? LoadedDemo, string Message);

/// <summary>Drives the CS2 client: launch, load demo, seek, choose POV, resume/pause.</summary>
public interface IDemoPlaybackController : IAsyncDisposable
{
    PlaybackState State { get; }

    IntPtr GameWindow { get; }

    int GameProcessId { get; }

    Task EnsureSessionAsync(IProgress<string>? status, CancellationToken cancellationToken);

    Task LoadDemoAsync(string demoPath, CancellationToken cancellationToken);

    Task SeekAsync(int demoTick, CancellationToken cancellationToken);

    Task<(bool Verified, string? Observed)> SetPovAsync(PovTarget target, CancellationToken cancellationToken);

    Task ApplyHudAsync(HudMode mode, CancellationToken cancellationToken);

    /// <summary>Resumes playback; returns the Stopwatch timestamp at which the command was sent.</summary>
    Task<long> ResumeAsync(CancellationToken cancellationToken);

    Task<long> PauseAsync(CancellationToken cancellationToken);

    Task EndSessionAsync(bool closeGame, CancellationToken cancellationToken);
}

/// <summary>A running capture of the game window. Times are Stopwatch timestamps.</summary>
public interface ICaptureSession : IAsyncDisposable
{
    string VideoPath { get; }

    string? AudioPath { get; }

    /// <summary>Stopwatch timestamp that corresponds to t=0 in the video file.</summary>
    long VideoOrigin { get; }

    long? AudioOrigin { get; }

    Task WaitUntilRecordingAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public sealed record CaptureOptions(IntPtr Window, int ProcessId, int Fps, string VideoPath, string? AudioPath, string Encoder);

public interface IGameRecorder
{
    string BackendName { get; }

    Task<ICaptureSession> StartAsync(CaptureOptions options, CancellationToken cancellationToken);
}

public sealed record MediaInfo(double DurationSeconds, int Width, int Height, double Fps, string VideoCodec, bool HasAudio);

public sealed record SegmentInput(string VideoPath, double VideoStart, string? AudioPath, double AudioStart, double Duration);

public interface IVideoProcessor
{
    Task<MediaInfo> ProbeAsync(string path, CancellationToken cancellationToken);

    /// <summary>Builds a single master clip from captured segments (cut + concat + audio mux), lossless-ish.</summary>
    Task BuildMasterAsync(IReadOnlyList<SegmentInput> segments, string outputPath, double transitionSeconds, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Final export with resolution/fps/codec/bitrate and user edits.</summary>
    Task ExportAsync(string masterPath, string outputPath, ExportSettings export, EditSettings edit, IProgress<double>? progress, CancellationToken cancellationToken);

    Task CreateThumbnailAsync(string videoPath, double atSeconds, string outputPath, CancellationToken cancellationToken);
}

public interface IRenderPipeline
{
    Task RenderAsync(RenderJob job, Func<RenderJob, Task> saveState, IProgress<RenderProgress> progress, CancellationToken cancellationToken);

    /// <summary>Called when the queue becomes idle (closes the game session, restores configs).</summary>
    Task OnQueueIdleAsync(CancellationToken cancellationToken);
}

public interface IRenderJobRepository
{
    Task<IReadOnlyList<RenderJob>> GetAllAsync();

    Task<RenderJob?> GetAsync(string id);

    Task SaveAsync(RenderJob job);

    Task DeleteAsync(string id);
}
