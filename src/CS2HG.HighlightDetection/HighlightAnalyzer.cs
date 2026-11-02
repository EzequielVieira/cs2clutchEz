using CS2HG.Core.Abstractions;
using CS2HG.Core.Models;
using CS2HG.Core.Settings;
using CS2HG.Core.Utilities;
using CS2HG.HighlightDetection.Detectors;

namespace CS2HG.HighlightDetection;

/// <summary>
/// Runs every registered detector per (player, round) and merges the evidence into scored highlights.
/// </summary>
public sealed class HighlightAnalyzer(IEnumerable<IHighlightDetector> detectors) : IHighlightAnalyzer
{
    public static IReadOnlyList<IHighlightDetector> DefaultDetectors() =>
    [
        new KillDetector(),
        new HeadshotDetector(),
        new FirstBloodDetector(),
        new MultiKillDetector(),
        new NoScopeDetector(),
        new WallbangDetector(),
        new ThroughSmokeDetector(),
        new BlindKillDetector(),
        new KnifeKillDetector(),
        new ZeusKillDetector(),
        new AwpKillDetector(),
        new QuickKillsDetector(),
        new KillStreakDetector(),
        new ClutchDetector(),
        new RetakeDetector(),
        new DecisiveDefuseDetector(),
        new DecisiveRoundDetector(),
        new HighDamageDetector(),
        new UtilityDamageDetector(),
    ];

    private static readonly HighlightKind[] HeadlinePriority =
    [
        HighlightKind.Ace, HighlightKind.Clutch1v5, HighlightKind.Clutch1v4, HighlightKind.Clutch1v3, HighlightKind.Clutch1v2,
        HighlightKind.QuadKill, HighlightKind.TripleKill, HighlightKind.Clutch1v1, HighlightKind.DoubleKill,
        HighlightKind.DecisiveDefuse, HighlightKind.Retake, HighlightKind.KnifeKill, HighlightKind.ZeusKill, HighlightKind.NoScope,
        HighlightKind.Wallbang, HighlightKind.DecisiveRound, HighlightKind.UtilityDamage, HighlightKind.HighDamage,
        HighlightKind.FirstBlood, HighlightKind.Headshot, HighlightKind.Kill,
    ];

    private readonly IReadOnlyList<IHighlightDetector> _detectors = detectors.ToList();

    public IReadOnlyList<IHighlightDetector> Detectors => _detectors;

    public IReadOnlyList<Highlight> Analyze(MatchData match, ulong? playerSteamId, DetectionSettings detection, ScoreSettings scores)
    {
        var players = playerSteamId is { } id
            ? match.Players.Where(p => p.SteamId == id).ToList()
            : match.Players.Where(p => !p.IsBot).ToList();

        var killsByRound = match.Kills.GroupBy(k => k.Round).ToDictionary(g => g.Key, g => (IReadOnlyList<KillEvent>)g.OrderBy(k => k.Tick).ToList());
        var damageByRound = match.Damages.GroupBy(k => k.Round).ToDictionary(g => g.Key, g => (IReadOnlyList<DamageEvent>)g.ToList());
        var bombByRound = match.BombEvents.GroupBy(k => k.Round).ToDictionary(g => g.Key, g => (IReadOnlyList<BombEvent>)g.ToList());

        var result = new List<Highlight>();
        foreach (var round in match.Rounds)
        {
            var roundKills = killsByRound.GetValueOrDefault(round.Number) ?? [];
            foreach (var player in players)
            {
                var team = round.TeamOf(player.SteamId);
                if (!team.IsPlaying())
                {
                    // Player joined after freeze end: fall back to the team seen on their kills.
                    team = roundKills.FirstOrDefault(k => k.AttackerSteamId == player.SteamId)?.AttackerTeam ?? Team.Unassigned;
                    if (!team.IsPlaying()) continue;
                }

                var context = new DetectionContext
                {
                    Match = match,
                    Round = round,
                    PlayerSteamId = player.SteamId,
                    PlayerTeam = team,
                    RoundKills = roundKills,
                    PlayerKills = roundKills.Where(k => k.AttackerSteamId == player.SteamId && k.IsEnemyKill).ToList(),
                    RoundDamages = damageByRound.GetValueOrDefault(round.Number) ?? [],
                    RoundBombEvents = bombByRound.GetValueOrDefault(round.Number) ?? [],
                    Settings = detection,
                    Scores = scores,
                };

                var components = _detectors.SelectMany(d => d.Detect(context)).OrderBy(c => c.Tick).ToList();
                if (components.Count == 0) continue;

                var highlight = Compose(match, round, player, team, context, components);
                if (highlight.Score >= detection.MinimumScore) result.Add(highlight);
            }
        }

        return result.OrderByDescending(h => h.Score).ThenBy(h => h.Round).ToList();
    }

    /// <summary>Title in the current language, e.g. "Clutch 1v3 + Triple Kill — AK-47".</summary>
    public static string TitleFor(Highlight highlight) =>
        TitleFor(highlight.Kinds, highlight.Kills.Count > 0 ? highlight.Kills.Select(k => k.Weapon) : highlight.Weapons);

    public static string TitleFor(IEnumerable<HighlightKind> kinds, IEnumerable<string> killWeapons)
    {
        var set = kinds.ToHashSet();
        var headline = HeadlinePriority.Where(set.Contains).ToList();
        // Show "Headshot" in the title only when it is the whole story (single headshot kill).
        if (headline.Count > 1) headline.Remove(HighlightKind.Kill);
        if (headline.Count > 1 && headline[0] != HighlightKind.Headshot) headline.Remove(HighlightKind.Headshot);

        var primaryWeapon = killWeapons.GroupBy(w => w).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        var title = string.Join(" + ", headline.Take(2).Select(k => k.DisplayName()));
        if (primaryWeapon is not null) title += $" — {WeaponInfo.DisplayName(primaryWeapon)}";
        return title;
    }

    private static Highlight Compose(MatchData match, RoundInfo round, PlayerInfo player, Team team, DetectionContext context, List<HighlightComponent> components)
    {
        var actionTicks = new SortedSet<int>(components.Select(c => c.Tick));
        foreach (var c in components.Where(c => c.Kind.IsClutch() && c.StartTick.HasValue))
            actionTicks.Add(c.StartTick!.Value);

        var kinds = components.Select(c => c.Kind).ToHashSet();
        var weapons = context.PlayerKills.Select(k => k.Weapon).ToList();
        var title = TitleFor(kinds, weapons);

        var keyComponent = components.OrderByDescending(c => c.Score).ThenByDescending(c => c.Tick).First();
        return new Highlight
        {
            Id = Hashing.HighlightId(match.Demo.FileHash, player.SteamId, round.Number),
            DemoHash = match.Demo.FileHash,
            Round = round.Number,
            PlayerSteamId = player.SteamId,
            PlayerName = player.Name,
            Team = team,
            Title = title,
            Category = Categorize(kinds),
            Score = components.Sum(c => c.Score),
            Components = components,
            ActionTicks = actionTicks.ToList(),
            KeyTick = keyComponent.Tick,
            RoundStartTick = round.FreezeEndTick > 0 ? round.FreezeEndTick : round.StartTick,
            RoundEndTick = round.OfficialEndTick > 0 ? round.OfficialEndTick : round.EndTick,
            Weapons = weapons.Distinct().ToList(),
            KillCount = context.PlayerKills.Count,
            HeadshotCount = context.PlayerKills.Count(k => k.Headshot),
            VictimSteamIds = context.PlayerKills.Select(k => k.VictimSteamId).ToList(),
            Kills = context.PlayerKills.Select(k => new HighlightKillRef
            {
                Tick = k.Tick,
                AttackerSteamId = player.SteamId,
                VictimSteamId = k.VictimSteamId,
                VictimName = k.VictimName,
                Weapon = k.Weapon,
                Headshot = k.Headshot,
            }).ToList(),
        };
    }

    public static HighlightCategory Categorize(IReadOnlySet<HighlightKind> kinds)
    {
        if (kinds.Contains(HighlightKind.Ace)) return HighlightCategory.Ace;
        if (kinds.Any(k => k.IsClutch())) return HighlightCategory.Clutch;
        if (kinds.Any(k => k.IsMultiKill())) return HighlightCategory.MultiKill;
        if (kinds.Contains(HighlightKind.DecisiveDefuse) || kinds.Contains(HighlightKind.Retake)) return HighlightCategory.Objective;
        if (!kinds.Contains(HighlightKind.Kill) && (kinds.Contains(HighlightKind.UtilityDamage) || kinds.Contains(HighlightKind.HighDamage)))
            return HighlightCategory.Utility;
        return HighlightCategory.Kills;
    }
}
