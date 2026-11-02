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

        var segments = new List<ClipSegment>();
        for (var i = 0; i < ranges.Count; i++)
        {
            var pov = PovResolver.Resolve(highlight, match, ranges[i], export.Pov, export.SpecificPlayerSteamId);
            segments.Add(new ClipSegment
            {
                Index = i,
                StartTick = ranges[i].Start,
                EndTick = ranges[i].End,
                PovSteamId = pov.SteamId,
                PovName = pov.Name,
                Mode = export.Pov,
            });
        }

        return segments;
    }

    public static double TotalSeconds(IEnumerable<ClipSegment> segments, int tickRate) =>
        segments.Sum(s => s.DurationSeconds(tickRate));
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
