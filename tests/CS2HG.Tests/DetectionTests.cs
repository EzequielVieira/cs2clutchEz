using CS2HG.Core.Models;
using CS2HG.Core.Settings;
using CS2HG.HighlightDetection;
using static CS2HG.Tests.MatchBuilder;

namespace CS2HG.Tests;

public class DetectionTests
{
    private static readonly HighlightAnalyzer Analyzer = new(HighlightAnalyzer.DefaultDetectors());

    private static IReadOnlyList<Highlight> Analyze(MatchData m, ulong player, ScoreSettings? scores = null, DetectionSettings? detection = null) =>
        Analyzer.Analyze(m, player, detection ?? new DetectionSettings { MinimumScore = 0 }, scores ?? new ScoreSettings());

    [Theory]
    [InlineData(1, null)]
    [InlineData(2, HighlightKind.DoubleKill)]
    [InlineData(3, HighlightKind.TripleKill)]
    [InlineData(4, HighlightKind.QuadKill)]
    [InlineData(5, HighlightKind.Ace)]
    public void Multi_kill_kind_matches_kill_count(int kills, HighlightKind? expected)
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000);
        for (var i = 0; i < kills; i++) b.Kill(Ct[0], T[i], 10 + i * 20);
        var h = Analyze(b.Build(), Ct[0]).Single();

        Assert.Equal(kills, h.KillCount);
        var multi = h.Kinds.Where(k => k.IsMultiKill()).ToList();
        if (expected is null) Assert.Empty(multi);
        else Assert.Equal([expected.Value], multi);
    }

    [Fact]
    public void Ace_is_categorized_and_scored_with_defaults()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000);
        for (var i = 0; i < 5; i++) b.Kill(Ct[0], T[i], 5 + i * 3, headshot: i < 2);
        var h = Analyze(b.Build(), Ct[0]).Single();

        Assert.Equal(HighlightCategory.Ace, h.Category);
        Assert.StartsWith("Ace", h.Title);
        // 5 kills (50) + 2 HS (20) + ace (100) + first blood (10) + streak (15)
        Assert.Equal(50 + 20 + 100 + 10 + 15, h.Score);
    }

    [Fact]
    public void Score_values_are_configurable()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000).Kill(Ct[0], T[0], 30, "knife_karambit");
        var scores = new ScoreSettings();
        scores.Points[HighlightKind.KnifeKill] = 500;
        scores.Points[HighlightKind.FirstBlood] = 0;
        var h = Analyze(b.Build(), Ct[0], scores).Single();

        Assert.True(h.Has(HighlightKind.KnifeKill));
        Assert.Equal(10 + 500, h.Score);
    }

    [Fact]
    public void Special_kills_are_detected_from_event_fields_only()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000)
            .Kill(Ct[0], T[0], 10, "awp", noscope: true)
            .Kill(Ct[0], T[1], 40, "ak47", penetrated: 1)
            .Kill(Ct[0], T[2], 70, "taser")
            .Kill(Ct[1], T[3], 80, "m4a1", smoke: true, blind: true);
        var mine = Analyze(b.Build(), Ct[0]).Single();
        Assert.True(mine.Has(HighlightKind.NoScope));
        Assert.True(mine.Has(HighlightKind.AwpKill));
        Assert.True(mine.Has(HighlightKind.Wallbang));
        Assert.True(mine.Has(HighlightKind.ZeusKill));
        Assert.False(mine.Has(HighlightKind.ThroughSmoke));

        var other = Analyze(b.Build(), Ct[1]).Single();
        Assert.True(other.Has(HighlightKind.ThroughSmoke));
        Assert.True(other.Has(HighlightKind.BlindKill));
        Assert.False(other.Has(HighlightKind.Wallbang));
    }

    [Fact]
    public void Team_kills_and_suicides_are_not_highlights()
    {
        var b = new MatchBuilder().Round(1, Team.Terrorist, 1000).Kill(Ct[0], Ct[1], 10);
        Assert.Empty(Analyze(b.Build(), Ct[0]));
    }

    [Fact]
    public void First_blood_only_for_first_enemy_kill_of_round()
    {
        var m = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000).Kill(Ct[1], T[0], 5).Kill(Ct[0], T[1], 10).Build();
        Assert.False(Analyze(m, Ct[0]).Single().Has(HighlightKind.FirstBlood));
        Assert.True(Analyze(m, Ct[1]).Single().Has(HighlightKind.FirstBlood));
    }

    [Fact]
    public void Clutch_1v3_won_is_detected_with_start_tick()
    {
        // CT loses 4 players while T loses 2 → CT[0] alone vs 3, then kills all 3; CT wins.
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000)
            .Kill(Ct[0], T[0], 5).Kill(Ct[1], T[1], 8)
            .Kill(T[2], Ct[1], 10).Kill(T[2], Ct[2], 12).Kill(T[3], Ct[3], 14).Kill(T[4], Ct[4], 16)
            .Kill(Ct[0], T[2], 30).Kill(Ct[0], T[3], 35).Kill(Ct[0], T[4], 50);
        var h = Analyze(b.Build(), Ct[0]).Single();

        var clutch = Assert.Single(h.Components, c => c.Kind.IsClutch());
        Assert.Equal(HighlightKind.Clutch1v3, clutch.Kind);
        Assert.Equal(b.Build().Kills[5].Tick, clutch.StartTick);
        Assert.Contains(clutch.StartTick!.Value, h.ActionTicks);
        Assert.Equal(HighlightCategory.Clutch, h.Category);
    }

    [Fact]
    public void Lost_clutch_is_not_a_highlight_component()
    {
        var b = new MatchBuilder().Round(1, Team.Terrorist, 1000)
            .Kill(T[0], Ct[1], 5).Kill(T[0], Ct[2], 6).Kill(T[0], Ct[3], 7).Kill(T[0], Ct[4], 8)
            .Kill(Ct[0], T[0], 20).Kill(Ct[0], T[1], 25)
            .Kill(T[2], Ct[0], 30);
        var h = Analyze(b.Build(), Ct[0]).Single();
        Assert.DoesNotContain(h.Components, c => c.Kind.IsClutch());
    }

    [Fact]
    public void Clutch_situation_finder_counts_played_and_won()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000)
            .Kill(T[0], Ct[1], 5).Kill(T[0], Ct[2], 6).Kill(T[0], Ct[3], 7).Kill(T[0], Ct[4], 8)
            .Kill(Ct[0], T[0], 20).Kill(Ct[0], T[1], 21).Kill(Ct[0], T[2], 22).Kill(Ct[0], T[3], 23).Kill(Ct[0], T[4], 24);
        var m = b.Build();
        var situations = MatchAnalysis.FindClutches(m);
        var clutch = Assert.Single(situations, c => c.Team == Team.CounterTerrorist);
        Assert.Equal(Ct[0], clutch.SteamId);
        Assert.Equal(5, clutch.Enemies);
        // The last T standing (1v1, lost) is also a clutch situation.
        Assert.Contains(situations, c => c.Team == Team.Terrorist && c.Enemies == 1 && !c.Won);
        Assert.True(clutch.Won);
        Assert.Equal(HighlightKind.Clutch1v5, HighlightKindInfo.ClutchKind(clutch.Enemies));
    }

    [Fact]
    public void Quick_kills_and_kill_streak_use_time_windows()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000)
            .Kill(Ct[0], T[0], 10).Kill(Ct[0], T[1], 10.8).Kill(Ct[0], T[2], 18);
        var h = Analyze(b.Build(), Ct[0]).Single();
        var quick = Assert.Single(h.Components, c => c.Kind == HighlightKind.QuickKills);
        Assert.Contains("2 kills", quick.Description);
        Assert.True(h.Has(HighlightKind.KillStreak)); // 3 kills within 10 s

        var slow = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000)
            .Kill(Ct[0], T[0], 10).Kill(Ct[0], T[1], 25).Kill(Ct[0], T[2], 40);
        var h2 = Analyze(slow.Build(), Ct[0]).Single();
        Assert.False(h2.Has(HighlightKind.QuickKills));
        Assert.False(h2.Has(HighlightKind.KillStreak));
    }

    [Fact]
    public void Retake_and_decisive_defuse()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000, RoundEndReason.BombDefused)
            .Kill(T[0], Ct[1], 5).Kill(T[0], Ct[2], 6).Kill(T[0], Ct[3], 7).Kill(T[0], Ct[4], 8)
            .Bomb(BombEventType.Planted, T[1], 20)
            .Kill(Ct[0], T[0], 40).Kill(Ct[0], T[1], 45)
            .Bomb(BombEventType.Defused, Ct[0], 58.5); // 38.5 s after plant → 1.5 s left
        var h = Analyze(b.Build(), Ct[0]).Single();
        Assert.True(h.Has(HighlightKind.Retake));
        var defuse = Assert.Single(h.Components, c => c.Kind == HighlightKind.DecisiveDefuse);
        Assert.Contains("restantes", defuse.Description);
        Assert.Contains("último CT", defuse.Description);
    }

    [Fact]
    public void Routine_defuse_is_not_decisive()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000, RoundEndReason.BombDefused)
            .Kill(Ct[0], T[0], 5).Kill(Ct[0], T[1], 6).Kill(Ct[1], T[2], 7).Kill(Ct[1], T[3], 8)
            .Bomb(BombEventType.Planted, T[4], 20).Kill(Ct[2], T[4], 21)
            .Bomb(BombEventType.Defused, Ct[0], 35);
        var h = Analyze(b.Build(), Ct[0]).Single();
        Assert.False(h.Has(HighlightKind.DecisiveDefuse));
    }

    [Fact]
    public void Match_winning_and_match_point_saving_rounds_are_decisive()
    {
        // 12:11 for CT, then CT wins round 24 → 13:11 (match winning round).
        var b = new MatchBuilder().Filler(12, Team.CounterTerrorist).Filler(11, Team.Terrorist);
        b.Round(24, Team.CounterTerrorist, b.NextRoundStart).Kill(Ct[0], T[0], 10);
        var h = Analyze(b.Build(), Ct[0]).Single(x => x.Round == 24);
        Assert.Contains(h.Components, c => c.Kind == HighlightKind.DecisiveRound && c.Description.Contains("vitória"));

        // T at 12, CT saves it.
        var s = new MatchBuilder().Filler(10, Team.CounterTerrorist).Filler(12, Team.Terrorist);
        s.Round(23, Team.CounterTerrorist, s.NextRoundStart).Kill(Ct[0], T[0], 10);
        var h2 = Analyze(s.Build(), Ct[0]).Single(x => x.Round == 23);
        Assert.Contains(h2.Components, c => c.Kind == HighlightKind.DecisiveRound && c.Description.Contains("match point"));
    }

    [Theory]
    [InlineData(24, 0, 13)]
    [InlineData(24, 23, 13)]
    [InlineData(24, 24, 16)]
    [InlineData(24, 29, 16)]
    [InlineData(24, 30, 19)]
    [InlineData(16, 10, 9)]
    public void Win_target_follows_mr12_and_mr3_overtime(int regulation, int played, int target) =>
        Assert.Equal(target, MatchAnalysis.WinTarget(regulation, played));

    [Fact]
    public void High_damage_and_utility_damage()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000)
            .Damage(Ct[0], T[0], 10, 60, "hegrenade").Damage(Ct[0], T[1], 10.2, 45, "hegrenade")
            .Damage(Ct[0], T[2], 30, 100).Damage(Ct[0], T[3], 40, 100);
        var h = Analyze(b.Build(), Ct[0]).Single();
        Assert.True(h.Has(HighlightKind.UtilityDamage));
        Assert.True(h.Has(HighlightKind.HighDamage));
        Assert.Equal(HighlightCategory.Utility, h.Category);
    }

    [Fact]
    public void Analyze_all_players_returns_highlights_sorted_by_score()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000)
            .Kill(Ct[0], T[0], 5).Kill(Ct[1], T[1], 10).Kill(Ct[1], T[2], 11).Kill(Ct[1], T[3], 12);
        var all = Analyzer.Analyze(b.Build(), null, new DetectionSettings { MinimumScore = 0 }, new ScoreSettings());
        Assert.Equal(2, all.Count);
        Assert.Equal(Ct[1], all[0].PlayerSteamId);
        Assert.True(all[0].Score >= all[1].Score);
    }

    [Fact]
    public void Minimum_score_filters_low_value_plays()
    {
        var b = new MatchBuilder().Round(1, Team.CounterTerrorist, 1000).Kill(Ct[1], T[0], 1).Kill(Ct[0], T[1], 5);
        Assert.Empty(Analyzer.Analyze(b.Build(), Ct[0], new DetectionSettings { MinimumScore = 20 }, new ScoreSettings()));
    }

    [Fact]
    public void Every_detector_documents_its_rule()
    {
        foreach (var d in HighlightAnalyzer.DefaultDetectors())
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
        }
    }
}
