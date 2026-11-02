using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;

namespace CS2HG.HighlightDetection.Detectors;

public sealed class ClutchDetector : IHighlightDetector
{
    public string Name => L.T("det.clutch.name");
    public string Description => L.T("det.clutch.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var round = context.Round;
        var team = context.PlayerTeam;
        int? startTick = null;
        var enemies = 0;

        var roster = team == Team.CounterTerrorist ? round.CtPlayers : round.TPlayers;
        var enemyRoster = team == Team.CounterTerrorist ? round.TPlayers : round.CtPlayers;
        if (roster.Count == 1 && roster[0] == context.PlayerSteamId && enemyRoster.Count >= 1)
        {
            startTick = round.FreezeEndTick;
            enemies = enemyRoster.Count;
        }
        else
        {
            foreach (var k in context.RoundKills.OrderBy(k => k.Tick))
            {
                if (k.Tick > round.EndTick) break;
                if (k.VictimTeam != team) continue;
                var alive = k.AliveAfter(team);
                var enemyAlive = k.AliveAfter(team.Opponent());
                if (alive.Count == 1 && enemyAlive.Count >= 1)
                {
                    if (alive[0] == context.PlayerSteamId)
                    {
                        startTick = k.Tick;
                        enemies = enemyAlive.Count;
                    }

                    break;
                }
            }
        }

        if (startTick is null || round.Winner != team) yield break;

        var clutchKills = context.PlayerKills.Where(k => k.Tick >= startTick && k.Tick <= round.EndTick).ToList();
        var defuse = context.RoundBombEvents.FirstOrDefault(b => b.Type == BombEventType.Defused && b.PlayerSteamId == context.PlayerSteamId);
        var endTick = new[] { clutchKills.Select(k => k.Tick).DefaultIfEmpty(startTick.Value).Max(), defuse?.Tick ?? 0 }.Max();
        var kind = HighlightKindInfo.ClutchKind(enemies);
        yield return new HighlightComponent
        {
            Kind = kind,
            Tick = endTick,
            StartTick = startTick,
            Score = context.Scores.Get(kind),
            Description = L.F(defuse is null ? "comp.clutch" : "comp.clutchDefuse", enemies, clutchKills.Count),
        };
    }
}

public sealed class RetakeDetector : IHighlightDetector
{
    public string Name => L.T("det.retake.name");
    public string Description => L.T("det.retake.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var round = context.Round;
        if (context.PlayerTeam != Team.CounterTerrorist || round.BombPlantTick is not { } plant || round.Winner != Team.CounterTerrorist)
            yield break;

        var after = context.PlayerKills.Where(k => k.Tick >= plant).ToList();
        if (after.Count == 0) yield break;
        yield return new HighlightComponent
        {
            Kind = HighlightKind.Retake,
            Tick = after.Max(k => k.Tick),
            StartTick = after.Min(k => k.Tick),
            Score = context.Scores.Get(HighlightKind.Retake),
            Description = L.F("comp.retake", round.BombSite ?? "?", after.Count),
        };
    }
}

public sealed class DecisiveDefuseDetector : IHighlightDetector
{
    public string Name => L.T("det.defuse.name");
    public string Description => L.T("det.defuse.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var defuse = context.RoundBombEvents.FirstOrDefault(b => b.Type == BombEventType.Defused && b.PlayerSteamId == context.PlayerSteamId);
        if (defuse is null) yield break;

        var round = context.Round;
        var lastKill = context.RoundKills.Where(k => k.Tick <= defuse.Tick).OrderBy(k => k.Tick).LastOrDefault();
        var enemiesAlive = lastKill?.AliveTAfter.Count ?? round.TPlayers.Count;
        var teammatesAlive = lastKill?.AliveCtAfter.Count ?? round.CtPlayers.Count;
        double? secondsLeft = round.BombPlantTick is { } plant
            ? context.Settings.BombTimerSeconds - (defuse.Tick - plant) / (double)context.TickRate
            : null;

        var reasons = new List<string>();
        if (enemiesAlive > 0) reasons.Add(L.F("comp.defuseEnemies", enemiesAlive));
        if (secondsLeft is { } left && left <= context.Settings.LastSecondDefuseSeconds) reasons.Add(L.F("comp.defuseTime", left));
        if (teammatesAlive <= 1) reasons.Add(L.T("comp.defuseLastCt"));
        if (reasons.Count == 0) yield break;

        yield return new HighlightComponent
        {
            Kind = HighlightKind.DecisiveDefuse,
            Tick = defuse.Tick,
            Score = context.Scores.Get(HighlightKind.DecisiveDefuse),
            Description = L.F("comp.defuse", string.Join(", ", reasons)),
        };
    }
}

public sealed class DecisiveRoundDetector : IHighlightDetector
{
    public string Name => L.T("det.round.name");
    public string Description => L.T("det.round.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var round = context.Round;
        if (round.Winner != context.PlayerTeam) yield break;

        var actions = context.PlayerKills.Select(k => k.Tick)
            .Concat(context.RoundBombEvents.Where(b => b.PlayerSteamId == context.PlayerSteamId && b.Type is BombEventType.Planted or BombEventType.Defused).Select(b => b.Tick))
            .ToList();
        if (actions.Count == 0) yield break;

        var (own, enemy) = MatchAnalysis.ScoreBefore(context.Match, context.PlayerSteamId, round.Number);
        var regulation = MatchAnalysis.RegulationRounds(context.Match);
        string? reason = null;
        if (MatchAnalysis.IsMatchPoint(own, enemy, regulation) && round.Number == context.Match.Rounds.Max(r => r.Number))
            reason = L.F("comp.roundWin", own + 1, enemy);
        else if (MatchAnalysis.IsMatchPoint(enemy, own, regulation))
            reason = L.F("comp.roundSave", own, enemy, own + 1);

        if (reason is null) yield break;
        yield return new HighlightComponent
        {
            Kind = HighlightKind.DecisiveRound,
            Tick = actions.Max(),
            Score = context.Scores.Get(HighlightKind.DecisiveRound),
            Description = L.F("comp.round", reason),
        };
    }
}

public sealed class HighDamageDetector : IHighlightDetector
{
    public string Name => L.T("det.damage.name");
    public string Description => L.T("det.damage.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var hits = context.RoundDamages
            .Where(d => d.AttackerSteamId == context.PlayerSteamId && d.VictimTeam != d.AttackerTeam)
            .OrderBy(d => d.Tick)
            .ToList();
        var total = 0;
        foreach (var hit in hits)
        {
            total += hit.HealthDamage;
            if (total < context.Settings.HighDamageThreshold) continue;
            yield return new HighlightComponent
            {
                Kind = HighlightKind.HighDamage,
                Tick = hit.Tick,
                Score = context.Scores.Get(HighlightKind.HighDamage),
                Description = L.F("comp.damage", hits.Sum(h => h.HealthDamage)),
            };
            yield break;
        }
    }
}

public sealed class UtilityDamageDetector : IHighlightDetector
{
    public string Name => L.T("det.utility.name");
    public string Description => L.T("det.utility.desc");

    public IEnumerable<HighlightComponent> Detect(DetectionContext context)
    {
        var mine = context.RoundDamages
            .Where(d => d.AttackerSteamId == context.PlayerSteamId && d.VictimTeam != d.AttackerTeam)
            .OrderBy(d => d.Tick)
            .ToList();

        foreach (var (predicate, windowSeconds, label) in new (Func<string, bool>, double, string)[]
                 {
                     (WeaponInfo.IsHe, 0.5, "HE"),
                     (WeaponInfo.IsFire, 7.0, "Molotov"),
                 })
        {
            var hits = mine.Where(d => predicate(d.Weapon)).ToList();
            var window = context.SecondsToTicks(windowSeconds);
            var i = 0;
            while (i < hits.Count)
            {
                var j = i;
                var sum = 0;
                var victims = new HashSet<ulong>();
                while (j < hits.Count && hits[j].Tick - hits[i].Tick <= window)
                {
                    sum += hits[j].HealthDamage;
                    victims.Add(hits[j].VictimSteamId);
                    j++;
                }

                if (sum >= context.Settings.UtilityDamageThreshold)
                {
                    yield return new HighlightComponent
                    {
                        Kind = HighlightKind.UtilityDamage,
                        Tick = hits[i].Tick,
                        Score = context.Scores.Get(HighlightKind.UtilityDamage),
                        Description = L.F("comp.utility", label, sum, victims.Count),
                    };
                }

                i = j;
            }
        }
    }
}
