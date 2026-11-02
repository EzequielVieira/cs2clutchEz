namespace CS2HG.Core.Models;

public enum Team
{
    Unassigned = 0,
    Spectator = 1,
    Terrorist = 2,
    CounterTerrorist = 3,
}

public static class TeamExtensions
{
    public static Team Opponent(this Team team) => team switch
    {
        Team.Terrorist => Team.CounterTerrorist,
        Team.CounterTerrorist => Team.Terrorist,
        _ => Team.Unassigned,
    };

    public static string ShortName(this Team team) => team switch
    {
        Team.Terrorist => "T",
        Team.CounterTerrorist => "CT",
        Team.Spectator => "SPEC",
        _ => "-",
    };

    public static bool IsPlaying(this Team team) => team is Team.Terrorist or Team.CounterTerrorist;
}

public readonly record struct Vec3(float X, float Y, float Z)
{
    public float DistanceTo(Vec3 other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        var dz = Z - other.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public override string ToString() => $"({X:0},{Y:0},{Z:0})";
}

/// <summary>Static information about a demo file and the match it contains.</summary>
public sealed class DemoInfo
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string FileHash { get; set; } = "";
    public string MapName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ClientName { get; set; } = "";
    public int BuildNumber { get; set; }
    public int NetworkProtocol { get; set; }
    public bool IsGotv { get; set; }
    public int TickRate { get; set; } = 64;
    public int TotalDemoTicks { get; set; }
    public double DurationSeconds { get; set; }

    /// <summary>Game tick that corresponds to demo tick 0 (CS2 seeks by demo tick).</summary>
    public int GameTickOffset { get; set; }

    public int MaxRounds { get; set; }
    public string GameMode { get; set; } = "";
}

public sealed class PlayerStats
{
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int Headshots { get; set; }
    public int Damage { get; set; }
    public int UtilityDamage { get; set; }
    public int FirstKills { get; set; }
    public int TeamKills { get; set; }
    public int RoundsPlayed { get; set; }
    public int TwoKills { get; set; }
    public int ThreeKills { get; set; }
    public int FourKills { get; set; }
    public int Aces { get; set; }
    public int ClutchesWon { get; set; }
    public int ClutchesPlayed { get; set; }
    public int BombPlants { get; set; }
    public int BombDefuses { get; set; }
    public int GrenadesThrown { get; set; }

    public double HeadshotPercentage => Kills == 0 ? 0 : 100.0 * Headshots / Kills;
    public double Adr => RoundsPlayed == 0 ? 0 : (double)Damage / RoundsPlayed;
    public double KdRatio => Deaths == 0 ? Kills : (double)Kills / Deaths;
}

public sealed class PlayerInfo
{
    public ulong SteamId { get; set; }
    public string Name { get; set; } = "";
    public string ClanTag { get; set; } = "";
    public Team Team { get; set; }
    public Team StartingTeam { get; set; }
    public bool IsBot { get; set; }
    public PlayerStats Stats { get; set; } = new();

    /// <summary>32-bit Steam account id (used by CS2 spectator commands).</summary>
    public uint AccountId => SteamId >= 76561197960265728UL ? (uint)(SteamId - 76561197960265728UL) : 0;

    public override string ToString() => Name;
}

public enum RoundEndReason
{
    Unknown = 0,
    TargetBombed = 1,
    VipEscaped = 2,
    VipKilled = 3,
    TerroristsEscaped = 4,
    CtsPreventEscape = 5,
    EscapingTerroristsNeutralized = 6,
    BombDefused = 7,
    CtsWin = 8,
    TerroristsWin = 9,
    Draw = 10,
    HostagesRescued = 11,
    TargetSaved = 12,
    HostagesNotRescued = 13,
    TerroristsNotEscaped = 14,
    VipNotEscaped = 15,
    GameStart = 16,
    TerroristsSurrender = 17,
    CtSurrender = 18,
    TerroristsPlanted = 19,
    CtsReachedHostage = 20,
}

public sealed class RoundInfo
{
    public int Number { get; set; }
    public int StartTick { get; set; }
    public int FreezeEndTick { get; set; }
    public int EndTick { get; set; }
    public int OfficialEndTick { get; set; }
    public Team Winner { get; set; }
    public RoundEndReason Reason { get; set; }
    public int ScoreCtAfter { get; set; }
    public int ScoreTAfter { get; set; }
    public int? BombPlantTick { get; set; }
    public string? BombSite { get; set; }
    public int? BombDefuseTick { get; set; }
    public int? BombExplodeTick { get; set; }

    /// <summary>Players of each side that were alive when the freeze time ended.</summary>
    public List<ulong> CtPlayers { get; set; } = [];
    public List<ulong> TPlayers { get; set; } = [];

    public Team TeamOf(ulong steamId) =>
        CtPlayers.Contains(steamId) ? Team.CounterTerrorist :
        TPlayers.Contains(steamId) ? Team.Terrorist : Team.Unassigned;
}

public sealed class KillEvent
{
    public int Tick { get; set; }
    public int Round { get; set; }
    public ulong? AttackerSteamId { get; set; }
    public string AttackerName { get; set; } = "";
    public Team AttackerTeam { get; set; }
    public ulong VictimSteamId { get; set; }
    public string VictimName { get; set; } = "";
    public Team VictimTeam { get; set; }
    public ulong? AssisterSteamId { get; set; }
    public string? AssisterName { get; set; }
    public bool AssistedFlash { get; set; }
    public string Weapon { get; set; } = "";
    public bool Headshot { get; set; }
    public bool NoScope { get; set; }
    public int Penetrated { get; set; }
    public bool ThroughSmoke { get; set; }
    public bool AttackerBlind { get; set; }
    public bool AttackerInAir { get; set; }
    public float Distance { get; set; }
    public Vec3? AttackerPosition { get; set; }
    public Vec3? VictimPosition { get; set; }

    /// <summary>Alive players of each side right after this kill (from entity state).</summary>
    public List<ulong> AliveCtAfter { get; set; } = [];
    public List<ulong> AliveTAfter { get; set; } = [];

    public bool IsTeamKill => AttackerSteamId.HasValue && AttackerTeam == VictimTeam && AttackerSteamId != VictimSteamId;
    public bool IsSuicide => !AttackerSteamId.HasValue || AttackerSteamId == VictimSteamId;
    public bool IsEnemyKill => AttackerSteamId.HasValue && !IsSuicide && !IsTeamKill;

    public List<ulong> AliveAfter(Team team) => team == Team.CounterTerrorist ? AliveCtAfter : AliveTAfter;
}

public sealed class DamageEvent
{
    public int Tick { get; set; }
    public int Round { get; set; }
    public ulong? AttackerSteamId { get; set; }
    public Team AttackerTeam { get; set; }
    public ulong VictimSteamId { get; set; }
    public Team VictimTeam { get; set; }
    public string Weapon { get; set; } = "";

    /// <summary>Health actually removed (clamped to the victim's remaining health).</summary>
    public int HealthDamage { get; set; }
    public int ArmorDamage { get; set; }
    public int HitGroup { get; set; }
    public int VictimHealthAfter { get; set; }
}

public enum GrenadeType
{
    HighExplosive,
    Flashbang,
    Smoke,
    Molotov,
    Decoy,
}

public sealed class GrenadeEvent
{
    public int Tick { get; set; }
    public int Round { get; set; }
    public ulong? ThrowerSteamId { get; set; }
    public string ThrowerName { get; set; } = "";
    public GrenadeType Type { get; set; }
    public Vec3 Position { get; set; }
}

public enum BombEventType
{
    BeginPlant,
    Planted,
    BeginDefuse,
    Defused,
    Exploded,
}

public sealed class BombEvent
{
    public int Tick { get; set; }
    public int Round { get; set; }
    public BombEventType Type { get; set; }
    public ulong? PlayerSteamId { get; set; }
    public string PlayerName { get; set; } = "";
    public string? Site { get; set; }
    public bool HasKit { get; set; }
}

/// <summary>Everything extracted from a demo. Produced by the parser, consumed by detection and rendering.</summary>
public sealed class MatchData
{
    public int SchemaVersion { get; set; } = 1;
    public DemoInfo Demo { get; set; } = new();
    public List<PlayerInfo> Players { get; set; } = [];
    public List<RoundInfo> Rounds { get; set; } = [];
    public List<KillEvent> Kills { get; set; } = [];
    public List<DamageEvent> Damages { get; set; } = [];
    public List<GrenadeEvent> Grenades { get; set; } = [];
    public List<BombEvent> BombEvents { get; set; } = [];
    public string TeamNameCt { get; set; } = "";
    public string TeamNameT { get; set; } = "";
    public int FinalScoreCt { get; set; }
    public int FinalScoreT { get; set; }

    /// <summary>Notes about data that could not be extracted reliably.</summary>
    public List<string> Warnings { get; set; } = [];

    public PlayerInfo? FindPlayer(ulong steamId) => Players.FirstOrDefault(p => p.SteamId == steamId);

    public RoundInfo? FindRound(int number) => Rounds.FirstOrDefault(r => r.Number == number);

    public string DisplayName => $"{Demo.MapName} — {FinalScoreCt}:{FinalScoreT}";
}
