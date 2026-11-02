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

        var slowStart = edit.SlowMotionStart;
        var slowEnd = edit.SlowMotionEnd;
        var hasSlow = slowStart is not null && slowEnd is not null && slowEnd > slowStart;
        var slowFactor = Math.Clamp(edit.SlowMotionFactor <= 0 ? 0.5 : edit.SlowMotionFactor, 0.1, 1);

        var pieces = new List<EditPiece>();
        foreach (var (s, e) in keep)
        {
            if (!hasSlow)
            {
                Add(pieces, s, e, speed);
                continue;
            }

            var a = Math.Clamp(slowStart!.Value, s, e);
            var b = Math.Clamp(slowEnd!.Value, s, e);
            Add(pieces, s, a, speed);
            Add(pieces, a, b, speed * slowFactor);
            Add(pieces, b, e, speed);
        }

        return pieces;
    }

    private static void Add(List<EditPiece> pieces, double s, double e, double speed)
    {
        if (e - s >= MinPieceSeconds) pieces.Add(new EditPiece(s, e, speed));
    }

    public static double OutputDuration(IEnumerable<EditPiece> pieces) => pieces.Sum(p => p.OutputDuration);

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
