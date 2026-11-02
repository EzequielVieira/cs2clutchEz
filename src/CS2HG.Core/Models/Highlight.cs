namespace CS2HG.Core.Models;

/// <summary>Every kind of event the detectors can prove from demo data.</summary>
public enum HighlightKind
{
    Kill,
    Headshot,
    FirstBlood,
    DoubleKill,
    TripleKill,
    QuadKill,
    Ace,
    NoScope,
    Wallbang,
    ThroughSmoke,
    BlindKill,
    KnifeKill,
    ZeusKill,
    AwpKill,
    Clutch1v1,
    Clutch1v2,
    Clutch1v3,
    Clutch1v4,
    Clutch1v5,
    Retake,
    DecisiveDefuse,
    DecisiveRound,
    KillStreak,
    QuickKills,
    HighDamage,
    UtilityDamage,
}

/// <summary>Folder/category a highlight is filed under.</summary>
public enum HighlightCategory
{
    Kills,
    MultiKill,
    Clutch,
    Ace,
    Objective,
    Utility,
}

public static class HighlightKindInfo
{
    public static string DisplayName(this HighlightKind kind) => Localization.L.T("kind." + kind);

    public static HighlightKind ClutchKind(int enemies) => enemies switch
    {
        <= 1 => HighlightKind.Clutch1v1,
        2 => HighlightKind.Clutch1v2,
        3 => HighlightKind.Clutch1v3,
        4 => HighlightKind.Clutch1v4,
        _ => HighlightKind.Clutch1v5,
    };

    public static bool IsClutch(this HighlightKind kind) =>
        kind is HighlightKind.Clutch1v1 or HighlightKind.Clutch1v2 or HighlightKind.Clutch1v3
            or HighlightKind.Clutch1v4 or HighlightKind.Clutch1v5;

    public static bool IsMultiKill(this HighlightKind kind) =>
        kind is HighlightKind.DoubleKill or HighlightKind.TripleKill or HighlightKind.QuadKill or HighlightKind.Ace;
}

/// <summary>One scored piece of evidence for a highlight (e.g. "headshot on X at tick Y").</summary>
public sealed class HighlightComponent
{
    public HighlightKind Kind { get; set; }
    public int Tick { get; set; }
    public int Score { get; set; }
    public string Description { get; set; } = "";

    /// <summary>Tick at which the situation started (clutch start), when different from <see cref="Tick"/>.</summary>
    public int? StartTick { get; set; }
}

public readonly record struct TickRange(int Start, int End)
{
    public int Length => End - Start;

    public double Seconds(int tickRate) => (double)Length / tickRate;

    public bool Overlaps(TickRange other) => Start <= other.End && other.Start <= End;
}

/// <summary>A scored play by one player in one round. Rendering turns its action ticks into clip segments.</summary>
public sealed class Highlight
{
    public string Id { get; set; } = "";
    public string DemoHash { get; set; } = "";
    public int Round { get; set; }
    public ulong PlayerSteamId { get; set; }
    public string PlayerName { get; set; } = "";
    public Team Team { get; set; }
    public string Title { get; set; } = "";
    public HighlightCategory Category { get; set; }
    public int Score { get; set; }
    public List<HighlightComponent> Components { get; set; } = [];

    /// <summary>Ticks (demo ticks) where something worth showing happens. Sorted.</summary>
    public List<int> ActionTicks { get; set; } = [];
    public int KeyTick { get; set; }
    public int RoundStartTick { get; set; }
    public int RoundEndTick { get; set; }
    public List<string> Weapons { get; set; } = [];
    public int KillCount { get; set; }
    public int HeadshotCount { get; set; }
    public List<ulong> VictimSteamIds { get; set; } = [];

    /// <summary>For each kill in the play: tick, victim, attacker (used for killer/victim POV).</summary>
    public List<HighlightKillRef> Kills { get; set; } = [];
    public string? ThumbnailPath { get; set; }

    public bool Has(HighlightKind kind) => Components.Any(c => c.Kind == kind);

    public IEnumerable<HighlightKind> Kinds => Components.Select(c => c.Kind).Distinct();

    public string PrimaryWeapon => Weapons.GroupBy(w => w).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "";
}

public sealed class HighlightKillRef
{
    public int Tick { get; set; }
    public ulong AttackerSteamId { get; set; }
    public ulong VictimSteamId { get; set; }
    public string VictimName { get; set; } = "";
    public string Weapon { get; set; } = "";
    public bool Headshot { get; set; }
}
