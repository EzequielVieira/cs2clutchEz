using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;

namespace CS2HG.HighlightDetection.Detectors;

/// <summary>Base for detectors that award a component for each kill matching a predicate.</summary>
public abstract class PerKillDetector : IHighlightDetector
{
    /// <summary>Localization key prefix: det.&lt;Key&gt;.name / det.&lt;Key&gt;.desc.</summary>
    protected abstract string Key { get; }

    public string Name => L.T($"det.{Key}.name");

    public string Description => L.T($"det.{Key}.desc");

    protected abstract HighlightKind Kind { get; }

    protected abstract bool Matches(KillEvent kill);

    protected virtual string Describe(KillEvent kill) =>
        L.F("comp.special", Kind.DisplayName(), kill.VictimName, WeaponInfo.DisplayName(kill.Weapon));

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var points = context.Scores.Get(Kind);
        foreach (var kill in context.PlayerKills)
        {
            if (!Matches(kill)) continue;
            yield return new HighlightComponent { Kind = Kind, Tick = kill.Tick, Score = points, Description = Describe(kill) };
        }
    }
}

public sealed class KillDetector : PerKillDetector
{
    protected override string Key => "kill";
    protected override HighlightKind Kind => HighlightKind.Kill;
    protected override bool Matches(KillEvent kill) => true;
    protected override string Describe(KillEvent kill) => L.F("comp.kill", kill.VictimName, WeaponInfo.DisplayName(kill.Weapon));
}

public sealed class HeadshotDetector : PerKillDetector
{
    protected override string Key => "headshot";
    protected override HighlightKind Kind => HighlightKind.Headshot;
    protected override bool Matches(KillEvent kill) => kill.Headshot;
}

public sealed class NoScopeDetector : PerKillDetector
{
    protected override string Key => "noscope";
    protected override HighlightKind Kind => HighlightKind.NoScope;
    protected override bool Matches(KillEvent kill) => kill.NoScope;
}

public sealed class WallbangDetector : PerKillDetector
{
    protected override string Key => "wallbang";
    protected override HighlightKind Kind => HighlightKind.Wallbang;
    protected override bool Matches(KillEvent kill) => kill.Penetrated > 0 && !WeaponInfo.IsHe(kill.Weapon) && !WeaponInfo.IsFire(kill.Weapon);
}

public sealed class ThroughSmokeDetector : PerKillDetector
{
    protected override string Key => "smoke";
    protected override HighlightKind Kind => HighlightKind.ThroughSmoke;
    protected override bool Matches(KillEvent kill) => kill.ThroughSmoke;
}

public sealed class BlindKillDetector : PerKillDetector
{
    protected override string Key => "blind";
    protected override HighlightKind Kind => HighlightKind.BlindKill;
    protected override bool Matches(KillEvent kill) => kill.AttackerBlind;
}

public sealed class KnifeKillDetector : PerKillDetector
{
    protected override string Key => "knife";
    protected override HighlightKind Kind => HighlightKind.KnifeKill;
    protected override bool Matches(KillEvent kill) => WeaponInfo.IsKnife(kill.Weapon);
}

public sealed class ZeusKillDetector : PerKillDetector
{
    protected override string Key => "zeus";
    protected override HighlightKind Kind => HighlightKind.ZeusKill;
    protected override bool Matches(KillEvent kill) => WeaponInfo.IsZeus(kill.Weapon);
}

public sealed class AwpKillDetector : PerKillDetector
{
    protected override string Key => "awp";
    protected override HighlightKind Kind => HighlightKind.AwpKill;
    protected override bool Matches(KillEvent kill) => WeaponInfo.IsAwp(kill.Weapon);
}

public sealed class FirstBloodDetector : IHighlightDetector
{
    public string Name => L.T("det.firstblood.name");
    public string Description => L.T("det.firstblood.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var first = context.RoundKills.Where(k => k.IsEnemyKill).OrderBy(k => k.Tick).FirstOrDefault();
        if (first?.AttackerSteamId == context.PlayerSteamId)
        {
            yield return new HighlightComponent
            {
                Kind = HighlightKind.FirstBlood,
                Tick = first.Tick,
                Score = context.Scores.Get(HighlightKind.FirstBlood),
                Description = L.F("comp.firstblood", first.VictimName),
            };
        }
    }
}

public sealed class MultiKillDetector : IHighlightDetector
{
    public string Name => L.T("det.multikill.name");
    public string Description => L.T("det.multikill.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var count = context.PlayerKills.Count;
        HighlightKind? kind = count switch
        {
            2 => HighlightKind.DoubleKill,
            3 => HighlightKind.TripleKill,
            4 => HighlightKind.QuadKill,
            >= 5 => HighlightKind.Ace,
            _ => null,
        };
        if (kind is null) yield break;

        var last = context.PlayerKills.Max(k => k.Tick);
        yield return new HighlightComponent
        {
            Kind = kind.Value,
            Tick = last,
            StartTick = context.PlayerKills.Min(k => k.Tick),
            Score = context.Scores.Get(kind.Value),
            Description = L.F("comp.multikill", kind.Value.DisplayName(), count),
        };
    }
}

public sealed class QuickKillsDetector : IHighlightDetector
{
    public string Name => L.T("det.quick.name");
    public string Description => L.T("det.quick.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var maxGap = context.SecondsToTicks(context.Settings.QuickKillMaxGapSeconds);
        var kills = context.PlayerKills.OrderBy(k => k.Tick).ToList();
        var runStart = 0;
        for (var i = 1; i <= kills.Count; i++)
        {
            if (i < kills.Count && kills[i].Tick - kills[i - 1].Tick <= maxGap) continue;
            var length = i - runStart;
            if (length >= 2)
            {
                var span = (kills[i - 1].Tick - kills[runStart].Tick) / (double)context.TickRate;
                yield return new HighlightComponent
                {
                    Kind = HighlightKind.QuickKills,
                    Tick = kills[i - 1].Tick,
                    StartTick = kills[runStart].Tick,
                    Score = context.Scores.Get(HighlightKind.QuickKills),
                    Description = L.F("comp.killsIn", length, span),
                };
            }

            runStart = i;
        }
    }
}

public sealed class KillStreakDetector : IHighlightDetector
{
    public string Name => L.T("det.streak.name");
    public string Description => L.T("det.streak.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var window = context.SecondsToTicks(context.Settings.KillStreakWindowSeconds);
        var min = Math.Max(2, context.Settings.KillStreakMinKills);
        var kills = context.PlayerKills.OrderBy(k => k.Tick).ToList();
        int bestCount = 0, bestStart = 0, bestEnd = 0;
        for (int i = 0, j = 0; i < kills.Count; i++)
        {
            while (kills[i].Tick - kills[j].Tick > window) j++;
            var count = i - j + 1;
            if (count > bestCount)
            {
                bestCount = count;
                bestStart = j;
                bestEnd = i;
            }
        }

        if (bestCount < min) yield break;
        var span = (kills[bestEnd].Tick - kills[bestStart].Tick) / (double)context.TickRate;
        yield return new HighlightComponent
        {
            Kind = HighlightKind.KillStreak,
            Tick = kills[bestEnd].Tick,
            StartTick = kills[bestStart].Tick,
            Score = context.Scores.Get(HighlightKind.KillStreak),
            Description = L.F("comp.killsIn", bestCount, span),
        };
    }
}
