using CS2HG.Core.Models;
using CS2HG.Core.Settings;
using CS2HG.VideoProcessing;

namespace CS2HG.Tests;

/// <summary>
/// Builds small, explicit match timelines for detector unit tests. Integration tests use real demos
/// (see <see cref="TestDemos"/>); this builder only exists so each rule can be checked in isolation.
/// </summary>
public sealed class MatchBuilder
{
    public const int TickRate = 64;
    private readonly MatchData _m = new();
    private RoundInfo? _round;

    public static readonly ulong[] Ct = [76561198000000001, 76561198000000002, 76561198000000003, 76561198000000004, 76561198000000005];
    public static readonly ulong[] T = [76561198000000011, 76561198000000012, 76561198000000013, 76561198000000014, 76561198000000015];

    public MatchBuilder(int maxRounds = 24)
    {
        _m.Demo = new DemoInfo { MapName = "de_test", TickRate = TickRate, TotalDemoTicks = 500_000, FileHash = "testhash", MaxRounds = maxRounds };
        foreach (var id in Ct) _m.Players.Add(new PlayerInfo { SteamId = id, Name = "ct" + (Array.IndexOf(Ct, id) + 1), Team = Team.CounterTerrorist, StartingTeam = Team.CounterTerrorist });
        foreach (var id in T) _m.Players.Add(new PlayerInfo { SteamId = id, Name = "t" + (Array.IndexOf(T, id) + 1), Team = Team.Terrorist, StartingTeam = Team.Terrorist });
    }

    private readonly HashSet<ulong> _dead = [];

    public MatchBuilder Round(int number, Team winner, int startTick, RoundEndReason reason = RoundEndReason.Unknown, int lengthTicks = 115 * TickRate)
    {
        _dead.Clear();
        _round = new RoundInfo
        {
            Number = number,
            StartTick = startTick,
            FreezeEndTick = startTick + 15 * TickRate,
            EndTick = startTick + lengthTicks,
            OfficialEndTick = startTick + lengthTicks + 7 * TickRate,
            Winner = winner,
            Reason = reason == RoundEndReason.Unknown ? (winner == Team.CounterTerrorist ? RoundEndReason.CtsWin : RoundEndReason.TerroristsWin) : reason,
            CtPlayers = [.. Ct],
            TPlayers = [.. T],
        };
        _m.Rounds.Add(_round);
        return this;
    }

    /// <summary>Adds a kill <paramref name="seconds"/> after freeze end; alive lists are derived automatically.</summary>
    public MatchBuilder Kill(ulong attacker, ulong victim, double seconds, string weapon = "ak47", bool headshot = false,
        bool noscope = false, int penetrated = 0, bool smoke = false, bool blind = false)
    {
        var r = _round!;
        _dead.Add(victim);
        _m.Kills.Add(new KillEvent
        {
            Tick = r.FreezeEndTick + (int)(seconds * TickRate),
            Round = r.Number,
            AttackerSteamId = attacker,
            AttackerName = Name(attacker),
            AttackerTeam = TeamOf(attacker),
            VictimSteamId = victim,
            VictimName = Name(victim),
            VictimTeam = TeamOf(victim),
            Weapon = weapon,
            Headshot = headshot,
            NoScope = noscope,
            Penetrated = penetrated,
            ThroughSmoke = smoke,
            AttackerBlind = blind,
            AliveCtAfter = Ct.Where(id => !_dead.Contains(id)).ToList(),
            AliveTAfter = T.Where(id => !_dead.Contains(id)).ToList(),
        });
        return this;
    }

    public MatchBuilder Damage(ulong attacker, ulong victim, double seconds, int damage, string weapon = "ak47")
    {
        var r = _round!;
        _m.Damages.Add(new DamageEvent
        {
            Tick = r.FreezeEndTick + (int)(seconds * TickRate),
            Round = r.Number,
            AttackerSteamId = attacker,
            AttackerTeam = TeamOf(attacker),
            VictimSteamId = victim,
            VictimTeam = TeamOf(victim),
            Weapon = weapon,
            HealthDamage = damage,
        });
        return this;
    }

    public MatchBuilder Bomb(BombEventType type, ulong player, double seconds, string site = "A")
    {
        var r = _round!;
        var tick = r.FreezeEndTick + (int)(seconds * TickRate);
        _m.BombEvents.Add(new BombEvent { Tick = tick, Round = r.Number, Type = type, PlayerSteamId = player, PlayerName = Name(player), Site = site });
        if (type == BombEventType.Planted)
        {
            r.BombPlantTick = tick;
            r.BombSite = site;
        }

        if (type == BombEventType.Defused) r.BombDefuseTick = tick;
        return this;
    }

    /// <summary>Adds <paramref name="count"/> filler rounds won by <paramref name="winner"/> (for score / match point tests).</summary>
    public MatchBuilder Filler(int count, Team winner)
    {
        for (var i = 0; i < count; i++)
        {
            var n = _m.Rounds.Count + 1;
            Round(n, winner, 1000 + n * 200 * TickRate);
        }

        return this;
    }

    public int NextRoundStart => 1000 + (_m.Rounds.Count + 1) * 200 * TickRate;

    public MatchData Build() => _m;

    private string Name(ulong id) => _m.FindPlayer(id)?.Name ?? id.ToString();

    private static Team TeamOf(ulong id) => Ct.Contains(id) ? Team.CounterTerrorist : Team.Terrorist;

    public static DetectionSettings Detection() => new();

    public static ScoreSettings Scores() => new();
}

/// <summary>Finds real CS2 demos for integration tests.</summary>
public static class TestDemos
{
    /// <summary>
    /// Sources, in order: CS2HG_TEST_DEMOS (paths separated by ';'), tests/TestData/demos (filled by
    /// tests/download-test-demos.ps1 with a publicly available demo), and the local CS2 "replays" folder.
    /// </summary>
    public static IReadOnlyList<string> All()
    {
        var list = new List<string>();
        var env = Environment.GetEnvironmentVariable("CS2HG_TEST_DEMOS");
        if (!string.IsNullOrWhiteSpace(env)) list.AddRange(env.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists));

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var data = Path.Combine(dir, "tests", "TestData", "demos");
            if (Directory.Exists(data))
            {
                list.AddRange(Directory.EnumerateFiles(data, "*.dem"));
                break;
            }
        }

        try
        {
            var install = DemoPlayback.SteamLocator.Locate();
            var replays = Path.Combine(install.CsgoFolder, "replays");
            if (Directory.Exists(replays)) list.AddRange(Directory.EnumerateFiles(replays, "*.dem").OrderByDescending(File.GetLastWriteTimeUtc).Take(2));
        }
        catch
        {
            // no CS2 on this machine
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The largest available demo — the best candidate for a full match (13+ rounds).</summary>
    public static string? First() => All().OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();

    /// <summary>A full competitive match (the public samples are short excerpts).</summary>
    public static string? FullMatch() => First() is { } f && new FileInfo(f).Length > 20 * 1024 * 1024 ? f : null;
}

/// <summary>Fact that is skipped when no real demo is available on the machine.</summary>
public sealed class DemoFactAttribute : FactAttribute
{
    public DemoFactAttribute()
    {
        if (TestDemos.First() is null) Skip = "Nenhuma demo real disponível (defina CS2HG_TEST_DEMOS ou rode tests/download-test-demos.ps1).";
    }
}

/// <summary>Fact that needs a full match demo (local CS2 replays or CS2HG_TEST_DEMOS).</summary>
public sealed class FullMatchFactAttribute : FactAttribute
{
    public FullMatchFactAttribute()
    {
        if (TestDemos.FullMatch() is null) Skip = "Nenhuma demo de partida completa disponível (defina CS2HG_TEST_DEMOS).";
    }
}

/// <summary>Fact that is skipped when FFmpeg is not available.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (TestFfmpeg.Paths is null) Skip = "FFmpeg não encontrado.";
    }
}

public static class TestFfmpeg
{
    public static readonly FfmpegPaths? Paths = FfmpegLocator.Find(null, Application.MediaToolkit.UserToolsFolder);
}

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs2hg-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { /* ignore */ }
    }
}
