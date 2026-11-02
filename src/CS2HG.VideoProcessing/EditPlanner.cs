using CS2HG.Core.Models;

namespace CS2HG.VideoProcessing;

/// <summary>A source time range of the master clip played at a given speed.</summary>
public readonly record struct EditPiece(double Start, double End, double Speed)
{
    public double SourceDuration => End - Start;
    public double OutputDuration => (End - Start) / Speed;
}

/// <summary>Pure timeline math for the editor: trim, cuts and slow motion → ordered pieces.</summary>
public static class EditPlanner
{
    public const double MinPieceSeconds = 0.05;

    public static List<EditPiece> BuildPieces(double masterDuration, EditSettings edit)
    {
        if (masterDuration <= 0) return [];
        var speed = Math.Clamp(edit.Speed <= 0 ? 1 : edit.Speed, 0.1, 4);
        var start = Math.Clamp(edit.TrimStartSeconds, 0, masterDuration);
        var end = Math.Clamp(edit.TrimEndSeconds ?? masterDuration, start, masterDuration);

        // keep = [start,end] minus cuts
        var keep = new List<(double S, double E)> { (start, end) };
        foreach (var cut in edit.Cuts.Where(c => c.End > c.Start).OrderBy(c => c.Start))
        {
            var next = new List<(double S, double E)>();
            foreach (var (s, e) in keep)
            {
                if (cut.End <= s || cut.Start >= e)
                {
                    next.Add((s, e));
                    continue;
                }

                if (cut.Start > s) next.Add((s, cut.Start));
                if (cut.End < e) next.Add((cut.End, e));
            }

            keep = next;
        }

        var ranges = edit.SpeedRanges.Where(r => r.End > r.Start && r.Factor > 0).ToList();
        if (edit.SlowMotionStart is { } slowStart && edit.SlowMotionEnd is { } slowEnd && slowEnd > slowStart)
            ranges.Add(new SpeedRange(slowStart, slowEnd, edit.SlowMotionFactor <= 0 ? 0.5 : edit.SlowMotionFactor));

        var pieces = new List<EditPiece>();
        foreach (var (s, e) in keep)
        {
            // Split at every speed-range boundary; where ranges overlap the slowest one wins.
            var bounds = ranges.SelectMany(r => new[] { r.Start, r.End }).Where(t => t > s && t < e).Append(s).Append(e).Distinct().Order().ToList();
            for (var i = 0; i < bounds.Count - 1; i++)
            {
                var mid = (bounds[i] + bounds[i + 1]) / 2;
                var factor = ranges.Where(r => r.Start <= mid && mid < r.End).Select(r => Math.Clamp(r.Factor, 0.1, 4)).DefaultIfEmpty(1).Min();
                Add(pieces, bounds[i], bounds[i + 1], speed * factor);
            }
        }

        return pieces;
    }

    private static void Add(List<EditPiece> pieces, double s, double e, double speed)
    {
        if (e - s < MinPieceSeconds) return;
        if (pieces.Count > 0 && Math.Abs(pieces[^1].End - s) < 1e-9 && Math.Abs(pieces[^1].Speed - speed) < 1e-9)
            pieces[^1] = pieces[^1] with { End = e };
        else
            pieces.Add(new EditPiece(s, e, speed));
    }

    public static double OutputDuration(IEnumerable<EditPiece> pieces) => pieces.Sum(p => p.OutputDuration);

    /// <summary>Where a moment of the master ends up in the exported video (removed moments map to the next kept one).</summary>
    public static double MapToOutput(IReadOnlyList<EditPiece> pieces, double masterTime)
    {
        var output = 0.0;
        foreach (var p in pieces)
        {
            if (masterTime < p.Start) return output;
            if (masterTime <= p.End) return output + (masterTime - p.Start) / p.Speed;
            output += p.OutputDuration;
        }

        return output;
    }

    /// <summary>Slow motion around each kill: from <paramref name="before"/> s before to <paramref name="after"/> s after.</summary>
    public static List<SpeedRange> KillSlowMotion(IEnumerable<double> killTimes, IEnumerable<TimeWindow> fastWindows,
        double factor = 0.5, double before = 0.35, double after = 0.65)
    {
        var fast = fastWindows.ToList();
        var ranges = new List<SpeedRange>();
        foreach (var t in killTimes.Order())
        {
            // A kill during a fast-forward stretch stays fast.
            if (fast.Any(w => t >= w.Start && t <= w.End)) continue;
            var start = Math.Max(0, t - before);
            var end = t + after;
            if (ranges.Count > 0 && start <= ranges[^1].End) ranges[^1] = ranges[^1] with { End = end };
            else ranges.Add(new SpeedRange(start, end, factor));
        }

        return ranges;
    }

    /// <summary>atempo accepts 0.5..100 per instance; chain instances for slower speeds.</summary>
    public static List<double> AtempoChain(double speed)
    {
        var chain = new List<double>();
        var s = speed;
        while (s < 0.5)
        {
            chain.Add(0.5);
            s /= 0.5;
        }

        while (s > 100)
        {
            chain.Add(100);
            s /= 100;
        }

        if (Math.Abs(s - 1) > 1e-6 || chain.Count == 0) chain.Add(s);
        return chain;
    }
}
