using CS2HG.Core.Models;
using CS2HG.Core.Settings;

namespace CS2HG.Core.Planning;

/// <summary>
/// Turns a highlight's action ticks into the tick ranges that will be played back and captured.
/// Pure logic: no game, no I/O.
/// </summary>
public static class ClipPlanner
{
    /// <summary>
    /// Pads each action tick with [before, after] seconds, merges ranges that overlap or are closer
    /// than <paramref name="mergeGapSeconds"/>, and clamps to the round and demo bounds.
    /// </summary>
    public static List<TickRange> BuildRanges(
        IReadOnlyList<int> actionTicks,
        int tickRate,
        double secondsBefore,
        double secondsAfter,
        double mergeGapSeconds,
        int minTick,
        int maxTick)
    {
        if (tickRate <= 0) throw new ArgumentOutOfRangeException(nameof(tickRate));
        if (actionTicks.Count == 0) return [];

        var before = (int)Math.Round(secondsBefore * tickRate);
        var after = (int)Math.Round(secondsAfter * tickRate);
        var gap = (int)Math.Round(mergeGapSeconds * tickRate);

        var ranges = new List<TickRange>();
        foreach (var tick in actionTicks.Order())
        {
            var start = Math.Max(minTick, tick - before);
            var end = Math.Min(maxTick, tick + after);
            if (end <= start) continue;

            if (ranges.Count > 0 && start <= ranges[^1].End + gap)
            {
                var last = ranges[^1];
                ranges[^1] = new TickRange(last.Start, Math.Max(last.End, end));
            }
            else
            {
                ranges.Add(new TickRange(start, end));
            }
        }

        return ranges;
    }

    /// <summary>Full plan for a highlight: ranges plus the POV for each one.</summary>
    public static List<ClipSegment> Plan(Highlight highlight, MatchData match, ExportSettings export, DetectionSettings detection)
    {
        var tickRate = match.Demo.TickRate <= 0 ? 64 : match.Demo.TickRate;
        var minTick = Math.Max(0, highlight.RoundStartTick);
        var maxTick = match.Demo.TotalDemoTicks > 0 ? match.Demo.TotalDemoTicks - 1 : int.MaxValue;
        if (highlight.RoundEndTick > 0)
            maxTick = Math.Min(maxTick, highlight.RoundEndTick + (int)(export.SecondsAfter * tickRate));

        var ranges = BuildRanges(highlight.ActionTicks, tickRate, export.SecondsBefore, export.SecondsAfter,
            detection.SegmentMergeGapSeconds, minTick, maxTick);
        var povs = ranges.Select(r => PovResolver.Resolve(highlight, match, r, export.Pov, export.SpecificPlayerSteamId)).ToList();

        if (export.BetweenPlays == BetweenPlays.FastForward && ranges.Count > 1)
        {
            // One continuous segment: the plays at normal speed, the gaps between them fast-forwarded.
            var segment = new ClipSegment
            {
                Index = 0,
                StartTick = ranges[0].Start,
                EndTick = ranges[^1].End,
                PovSteamId = povs[0].SteamId,
                PovName = povs[0].Name,
                Mode = export.Pov,
            };
            for (var i = 0; i < ranges.Count - 1; i++)
            {
                var switchPov = povs[i + 1].SteamId != povs[i].SteamId;
                var gapSeconds = (double)(ranges[i + 1].Start - ranges[i].End) / tickRate;
                segment.FastForward.Add(new FastForwardRange
                {
                    StartTick = ranges[i].End,
                    EndTick = ranges[i + 1].Start,
                    Speed = FastForwardSpeed(gapSeconds, export.FastForwardSpeed),
                    NextPovSteamId = switchPov ? povs[i + 1].SteamId : null,
                    NextPovName = switchPov ? povs[i + 1].Name : null,
                });
            }

            return [segment];
        }

        var segments = new List<ClipSegment>();
        for (var i = 0; i < ranges.Count; i++)
        {
            segments.Add(new ClipSegment
            {
                Index = i,
                StartTick = ranges[i].Start,
                EndTick = ranges[i].End,
                PovSteamId = povs[i].SteamId,
                PovName = povs[i].Name,
                Mode = export.Pov,
            });
        }

        return segments;
    }

    /// <summary>
    /// Speed for a gap: the chosen base speed, raised (in powers of two) when the gap would still take
    /// longer than <see cref="ExportSettings.MaxFastForwardSeconds"/>.
    /// </summary>
    public static double FastForwardSpeed(double gapSeconds, int baseSpeed)
    {
        var speed = Math.Clamp(baseSpeed, 2, ExportSettings.MaxFastForwardSpeed);
        while (gapSeconds / speed > ExportSettings.MaxFastForwardSeconds && speed < ExportSettings.MaxFastForwardSpeed)
            speed *= 2;
        return Math.Min(speed, ExportSettings.MaxFastForwardSpeed);
    }

    /// <summary>Clip length before editing: crossfades not counted, fast-forward stretches at their speed.</summary>
    public static double TotalSeconds(IEnumerable<ClipSegment> segments, int tickRate) =>
        segments.Sum(s => s.PlaybackSeconds(tickRate));
}

/// <summary>Maps demo ticks and recorded windows onto the master clip built from the captured segments.</summary>
public static class MasterTimeline
{
    /// <summary>Crossfade actually used between segments (the master builder shortens it for short segments).</summary>
    public static double EffectiveTransition(IReadOnlyList<double> durations, double requested)
    {
        if (durations.Count < 2) return 0;
        var t = Math.Min(requested, durations.Min() / 3);
        return t < 0.05 ? 0 : t;
    }

    /// <summary>Seconds into a captured segment's footage at which <paramref name="tick"/> is on screen.</summary>
    public static double SegmentSeconds(CapturedSegment captured, ClipSegment planned, int tick, int tickRate)
    {
        var points = captured.Timeline.OrderBy(p => p.Tick).ToList();
        if (points.Count == 0 || points[0].Tick > planned.StartTick)
            points.Insert(0, new TimelinePoint { Tick = planned.StartTick, Seconds = 0 });

        // Between two measured points the demo ran at a constant speed; after the last one, at normal speed.
        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (tick > b.Tick) continue;
            if (b.Tick == a.Tick) return a.Seconds;
            return a.Seconds + (b.Seconds - a.Seconds) * (tick - a.Tick) / (b.Tick - a.Tick);
        }

        var last = points[^1];
        return last.Seconds + (double)(tick - last.Tick) / tickRate;
    }

    public static ClipTimeline Build(RenderJob job, double transitionSeconds)
    {
        var tickRate = job.TickRate <= 0 ? 64 : job.TickRate;
        var captured = job.Captured.OrderBy(c => c.Index).ToList();
        var transition = EffectiveTransition(captured.Select(c => c.DurationSeconds).ToList(), transitionSeconds);

        var kills = new List<double>();
        var windows = new List<TimeWindow>();
        var start = 0.0;
        foreach (var c in captured)
        {
            if (job.Segments.FirstOrDefault(s => s.Index == c.Index) is { } planned)
            {
                foreach (var tick in job.KillTicks.Where(t => t >= planned.StartTick && t <= planned.EndTick))
                {
                    var at = start + SegmentSeconds(c, planned, tick, tickRate);
                    if (at <= start + c.DurationSeconds) kills.Add(at);
                }
            }

            windows.AddRange(c.FastWindows.Select(w => new TimeWindow { Start = start + w.Start, End = start + w.End, Speed = w.Speed }));
            start += c.DurationSeconds - transition;
        }

        return new ClipTimeline(kills.Order().ToList(), windows);
    }
}

public static class PovResolver
{
    /// <summary>Picks whose eyes a segment is watched through.</summary>
    public static (ulong? SteamId, string Name) Resolve(Highlight highlight, MatchData match, TickRange range, PovMode mode, ulong? specificSteamId)
    {
        string NameOf(ulong id) => match.FindPlayer(id)?.Name ?? id.ToString();
        var firstKill = highlight.Kills.Where(k => k.Tick >= range.Start && k.Tick <= range.End).OrderBy(k => k.Tick).FirstOrDefault();

        switch (mode)
        {
            case PovMode.Killer:
                if (firstKill is not null) return (firstKill.AttackerSteamId, NameOf(firstKill.AttackerSteamId));
                break;
            case PovMode.Victim:
                if (firstKill is not null) return (firstKill.VictimSteamId, NameOf(firstKill.VictimSteamId));
                break;
            case PovMode.SpecificPlayer:
                if (specificSteamId is { } sid) return (sid, NameOf(sid));
                break;
        }

        // SelectedPlayer, ThirdPerson and DirectorCamera all follow the highlight's player.
        return (highlight.PlayerSteamId, highlight.PlayerName);
    }
}
