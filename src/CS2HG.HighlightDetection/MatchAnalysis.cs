using CS2HG.Core.Models;

namespace CS2HG.HighlightDetection;

public sealed record ClutchSituation(ulong SteamId, int Round, Team Team, int Enemies, int StartTick, bool Won, int EndTick);

/// <summary>Match-level facts shared by several detectors (clutches, scores, match points).</summary>
public static class MatchAnalysis
{
    /// <summary>
    /// Finds 1vN situations: the moment a player becomes the last one alive on their side while
    /// enemies are still alive. Uses the alive lists captured from entity state at each kill.
    /// </summary>
    public static List<ClutchSituation> FindClutches(MatchData match)
    {
        var result = new List<ClutchSituation>();
        foreach (var round in match.Rounds)
        {
            var kills = match.Kills.Where(k => k.Round == round.Number).OrderBy(k => k.Tick).ToList();
            foreach (var team in new[] { Team.CounterTerrorist, Team.Terrorist })
            {
                ClutchSituation? found = null;
                var roster = team == Team.CounterTerrorist ? round.CtPlayers : round.TPlayers;
                var enemies = team == Team.CounterTerrorist ? round.TPlayers : round.CtPlayers;
                if (roster.Count == 1 && enemies.Count >= 1)
                {
                    found = new ClutchSituation(roster[0], round.Number, team, enemies.Count, round.FreezeEndTick, round.Winner == team, round.EndTick);
                }
                else
                {
                    foreach (var k in kills)
                    {
                        if (k.Tick > round.EndTick) break;
                        if (k.VictimTeam != team) continue;
                        var alive = k.AliveAfter(team);
                        var enemyAlive = k.AliveAfter(team.Opponent());
                        if (alive.Count == 1 && enemyAlive.Count >= 1)
                        {
                            found = new ClutchSituation(alive[0], round.Number, team, enemyAlive.Count, k.Tick, round.Winner == team, round.EndTick);
                            break;
                        }
                    }
                }

                if (found is not null) result.Add(found);
            }
        }

        return result;
    }

    public static void FillClutchStats(MatchData match)
    {
        var byId = match.Players.ToDictionary(p => p.SteamId);
        foreach (var c in FindClutches(match))
        {
            if (!byId.TryGetValue(c.SteamId, out var p)) continue;
            p.Stats.ClutchesPlayed++;
            if (c.Won) p.Stats.ClutchesWon++;
        }
    }

    /// <summary>Rounds won by the given player's team before the given round (handles side switches).</summary>
    public static (int Own, int Enemy) ScoreBefore(MatchData match, ulong steamId, int roundNumber)
    {
        int own = 0, enemy = 0;
        foreach (var r in match.Rounds.Where(r => r.Number < roundNumber))
        {
            var side = r.TeamOf(steamId);
            if (!side.IsPlaying() || !r.Winner.IsPlaying()) continue;
            if (r.Winner == side) own++;
            else enemy++;
        }

        return (own, enemy);
    }

    public static int RegulationRounds(MatchData match)
    {
        if (match.Demo.MaxRounds > 0) return match.Demo.MaxRounds;
        var total = match.Rounds.Count;
        return total <= 16 && Math.Max(match.FinalScoreCt, match.FinalScoreT) <= 9 ? 16 : 24;
    }

    /// <summary>Rounds needed to win at the given total of rounds already played (overtime aware, MR3 OT).</summary>
    public static int WinTarget(int regulationRounds, int roundsPlayed, int overtimeRounds = 6)
    {
        var half = regulationRounds / 2;
        if (roundsPlayed < regulationRounds) return half + 1;
        var otIndex = (roundsPlayed - regulationRounds) / overtimeRounds;
        return half + otIndex * (overtimeRounds / 2) + overtimeRounds / 2 + 1;
    }

    /// <summary>True when one more round won makes <paramref name="score"/> win the match.</summary>
    public static bool IsMatchPoint(int score, int otherScore, int regulationRounds) =>
        score == WinTarget(regulationRounds, score + otherScore) - 1;
}
