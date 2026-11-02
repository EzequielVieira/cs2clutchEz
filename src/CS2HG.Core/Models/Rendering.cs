using CS2HG.Core.Settings;

namespace CS2HG.Core.Models;

public enum RenderJobStatus
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A planned piece of the clip: a tick range played back from one player's POV.</summary>
public sealed class ClipSegment
{
    public int Index { get; set; }
    public int StartTick { get; set; }
    public int EndTick { get; set; }
    public ulong? PovSteamId { get; set; }
    public string PovName { get; set; } = "";
    public PovMode Mode { get; set; }

    /// <summary>Stretches between plays that are played back fast instead of being cut (sorted, inside the segment).</summary>
    public List<FastForwardRange> FastForward { get; set; } = [];

    /// <summary>Demo time covered by the segment.</summary>
    public double DurationSeconds(int tickRate) => (double)(EndTick - StartTick) / tickRate;

    /// <summary>Real time it takes to play (and record) the segment, counting the fast-forward stretches.</summary>
    public double PlaybackSeconds(int tickRate)
    {
        var fast = FastForward.Sum(f => (double)(f.EndTick - f.StartTick) / tickRate);
        var fastPlayed = FastForward.Sum(f => (double)(f.EndTick - f.StartTick) / tickRate / Math.Max(1, f.Speed));
        return DurationSeconds(tickRate) - fast + fastPlayed;
    }
}

/// <summary>A gap between two plays that the demo plays through at <see cref="Speed"/>.</summary>
public sealed class FastForwardRange
{
    public int StartTick { get; set; }
    public int EndTick { get; set; }
    public double Speed { get; set; } = 4;

    /// <summary>POV of the play that follows, when it is not the one being watched (killer/victim modes).</summary>
    public ulong? NextPovSteamId { get; set; }
    public string? NextPovName { get; set; }
}

/// <summary>Demo tick ↔ seconds into a segment's useful footage.</summary>
public sealed class TimelinePoint
{
    public int Tick { get; set; }
    public double Seconds { get; set; }
}

/// <summary>A time range of a video, played at <see cref="Speed"/> times the demo's real speed.</summary>
public sealed class TimeWindow
{
    public double Start { get; set; }
    public double End { get; set; }
    public double Speed { get; set; } = 1;
}

/// <summary>Where things happen in the master clip, used by the export overlays and slow motion.</summary>
public sealed record ClipTimeline(IReadOnlyList<double> KillTimes, IReadOnlyList<TimeWindow> FastWindows)
{
    public static readonly ClipTimeline Empty = new([], []);
}

/// <summary>A segment that was already captured. Persisted so interrupted jobs resume where they stopped.</summary>
public sealed class CapturedSegment
{
    public int Index { get; set; }
    public string VideoPath { get; set; } = "";
    public string? AudioPath { get; set; }

    /// <summary>Position in the raw video where demo playback resumed (start of the useful footage).</summary>
    public double VideoOffsetSeconds { get; set; }
    public double AudioOffsetSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public bool PovVerified { get; set; }
    public string? PovObserved { get; set; }

    /// <summary>
    /// Measured while recording: which demo tick was on screen at which second of the useful footage.
    /// Empty for segments recorded at constant speed (tick → seconds is then linear).
    /// </summary>
    public List<TimelinePoint> Timeline { get; set; } = [];

    /// <summary>Parts of the useful footage recorded while the demo played fast (seconds from its start).</summary>
    public List<TimeWindow> FastWindows { get; set; } = [];
}

/// <summary>User edits applied on export (editor). Future effects plug in through <see cref="Effects"/>.</summary>
public sealed class EditSettings
{
    public double TrimStartSeconds { get; set; }
    public double? TrimEndSeconds { get; set; }
    public List<CutRange> Cuts { get; set; } = [];
    public string? Title { get; set; }
    public string? PlayerLabel { get; set; }
    public string? RoundLabel { get; set; }
    public bool ShowOverlay { get; set; } = true;
    public double Speed { get; set; } = 1.0;
    public double? SlowMotionStart { get; set; }
    public double? SlowMotionEnd { get; set; }
    public double SlowMotionFactor { get; set; } = 0.5;

    /// <summary>Extra ranges of the master played at another speed (e.g. slow motion on each kill).</summary>
    public List<SpeedRange> SpeedRanges { get; set; } = [];
    public bool FadeIn { get; set; } = true;
    public bool FadeOut { get; set; } = true;
    public double FadeSeconds { get; set; } = 0.4;

    /// <summary>Crossfade between segments (seconds, 0 = hard cut).</summary>
    public double SegmentTransitionSeconds { get; set; } = 0.3;

    /// <summary>Extension point: named effects with parameters (zoom, kill overlay, music...).</summary>
    public List<EffectSpec> Effects { get; set; } = [];

    public EditSettings Clone()
    {
        var c = (EditSettings)MemberwiseClone();
        c.Cuts = Cuts.Select(x => x with { }).ToList();
        c.SpeedRanges = SpeedRanges.Select(x => x with { }).ToList();
        c.Effects = Effects.Select(e => new EffectSpec { Name = e.Name, Parameters = new(e.Parameters) }).ToList();
        return c;
    }
}

public sealed record CutRange(double Start, double End);

/// <summary>Master time range played at <see cref="Factor"/> × speed (0.5 = half speed).</summary>
public sealed record SpeedRange(double Start, double End, double Factor);

public sealed class EffectSpec
{
    public string Name { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = [];
}

public sealed class RenderJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string HighlightId { get; set; } = "";
    public string DemoHash { get; set; } = "";
    public string DemoPath { get; set; } = "";
    public string MatchName { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public ulong PlayerSteamId { get; set; }
    public int Round { get; set; }
    public string Title { get; set; } = "";
    public HighlightCategory Category { get; set; }
    public int Score { get; set; }
    public int TickRate { get; set; } = 64;

    public RenderJobStatus Status { get; set; } = RenderJobStatus.Pending;
    public double Progress { get; set; }
    public string Stage { get; set; } = "";
    public string? OutputPath { get; set; }
    public string? ThumbnailPath { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public int Order { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }

    public ExportSettings Export { get; set; } = new();
    public EditSettings Edit { get; set; } = new();
    public List<ClipSegment> Segments { get; set; } = [];
    public List<CapturedSegment> Captured { get; set; } = [];

    /// <summary>Demo ticks of the player's kills in this play (kill counter, slow motion).</summary>
    public List<int> KillTicks { get; set; } = [];

    /// <summary>Same highlight + same settings = same key; used to avoid duplicate renders.</summary>
    public string DedupeKey { get; set; } = "";

    /// <summary>Re-export of an existing master (editor) — skips game capture.</summary>
    public string? SourceMasterPath { get; set; }

    public bool IsFinished => Status is RenderJobStatus.Completed or RenderJobStatus.Failed or RenderJobStatus.Cancelled;
}

public sealed record RenderProgress(string JobId, double Fraction, string Stage);
