using CS2HG.Core.Models;

namespace CS2HG.Core.Settings;

public sealed class AppSettings
{
    public PathSettings Paths { get; set; } = new();
    public ScoreSettings Scores { get; set; } = new();
    public DetectionSettings Detection { get; set; } = new();
    public ExportSettings Export { get; set; } = new();
    public Cs2Settings Cs2 { get; set; } = new();
    public string? FfmpegPath { get; set; }

    /// <summary>UI language (pt-BR, en, es). Null = Windows display language.</summary>
    public string? Language { get; set; }
    public List<string> RecentDemos { get; set; } = [];

    /// <summary>Look for a newer release on GitHub when the app starts.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Windows notification when the render queue finishes.</summary>
    public bool NotifyWhenQueueDone { get; set; } = true;

    /// <summary>Auto mode: how many of the best plays of each match are queued.</summary>
    public int AutoTopPerMatch { get; set; } = 3;

    /// <summary>Background music for montages (null = none) and its volume in percent.</summary>
    public string? MontageMusicPath { get; set; }
    public int MontageMusicVolume { get; set; } = 35;
}

public sealed class PathSettings
{
    /// <summary>Root folder. Sub folders (Demos, Highlights, Thumbnails, Logs, Database) are created inside.</summary>
    public string RootFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Brand.FolderName);

    public string? DemosFolder { get; set; }
    public string? HighlightsFolder { get; set; }
    public string? ThumbnailsFolder { get; set; }
    public string? LogsFolder { get; set; }
    public string? DatabaseFolder { get; set; }

    public string Demos => DemosFolder ?? Path.Combine(RootFolder, "Demos");
    public string Highlights => HighlightsFolder ?? Path.Combine(RootFolder, "Highlights");
    public string Thumbnails => ThumbnailsFolder ?? Path.Combine(RootFolder, "Thumbnails");
    public string Logs => LogsFolder ?? Path.Combine(RootFolder, "Logs");
    public string Database => DatabaseFolder ?? Path.Combine(RootFolder, "Database");
    public string Cache => Path.Combine(Database, "cache");
    public string Work => Path.Combine(RootFolder, "Work");

    public IEnumerable<string> All => [RootFolder, Demos, Highlights, Thumbnails, Logs, Database, Cache, Work];
}

/// <summary>Points awarded per highlight component. Every value is user adjustable.</summary>
public sealed class ScoreSettings
{
    public Dictionary<HighlightKind, int> Points { get; set; } = Defaults();

    public int Get(HighlightKind kind) => Points.TryGetValue(kind, out var v) ? v : Defaults().GetValueOrDefault(kind);

    public static Dictionary<HighlightKind, int> Defaults() => new()
    {
        [HighlightKind.Kill] = 10,
        [HighlightKind.Headshot] = 10,
        [HighlightKind.DoubleKill] = 20,
        [HighlightKind.TripleKill] = 40,
        [HighlightKind.QuadKill] = 60,
        [HighlightKind.Ace] = 100,
        [HighlightKind.Clutch1v1] = 15,
        [HighlightKind.Clutch1v2] = 30,
        [HighlightKind.Clutch1v3] = 50,
        [HighlightKind.Clutch1v4] = 80,
        [HighlightKind.Clutch1v5] = 100,
        [HighlightKind.KnifeKill] = 30,
        [HighlightKind.NoScope] = 30,
        [HighlightKind.DecisiveRound] = 25,
        [HighlightKind.FirstBlood] = 10,
        [HighlightKind.Wallbang] = 20,
        [HighlightKind.ThroughSmoke] = 15,
        [HighlightKind.BlindKill] = 15,
        [HighlightKind.ZeusKill] = 25,
        [HighlightKind.AwpKill] = 5,
        [HighlightKind.Retake] = 20,
        [HighlightKind.DecisiveDefuse] = 30,
        [HighlightKind.KillStreak] = 15,
        [HighlightKind.QuickKills] = 15,
        [HighlightKind.HighDamage] = 15,
        [HighlightKind.UtilityDamage] = 15,
    };
}

public sealed class DetectionSettings
{
    /// <summary>Max seconds between consecutive kills to count as "quick succession".</summary>
    public double QuickKillMaxGapSeconds { get; set; } = 1.5;

    /// <summary>Window in seconds for a kill streak (3+ kills).</summary>
    public double KillStreakWindowSeconds { get; set; } = 10;
    public int KillStreakMinKills { get; set; } = 3;

    /// <summary>Health damage to enemies in one round to be flagged as high damage.</summary>
    public int HighDamageThreshold { get; set; } = 300;

    /// <summary>Damage from one HE grenade / molotov to be flagged as utility damage.</summary>
    public int UtilityDamageThreshold { get; set; } = 80;

    /// <summary>Bomb timer remaining (seconds) under which a defuse is decisive.</summary>
    public double LastSecondDefuseSeconds { get; set; } = 3;
    public double BombTimerSeconds { get; set; } = 40;

    /// <summary>Highlights scoring below this are hidden.</summary>
    public int MinimumScore { get; set; } = 10;

    /// <summary>Gap between actions that splits a highlight into separate segments (jump cut).</summary>
    public double SegmentMergeGapSeconds { get; set; } = 8;
}

public enum PovMode
{
    SelectedPlayer,
    Killer,
    Victim,
    SpecificPlayer,
    ThirdPerson,
    DirectorCamera,
}

public enum HudMode
{
    KillFeedOnly,
    Full,
    None,
}

public enum VideoCodec
{
    H264,
    H265,
}

/// <summary>What the clip shows between two plays that are far apart.</summary>
public enum BetweenPlays
{
    /// <summary>Jump cut with a crossfade.</summary>
    Cut,

    /// <summary>The demo keeps playing at a higher speed (recorded fast with demo_timescale).</summary>
    FastForward,
}

public enum VideoLayout
{
    Landscape,

    /// <summary>9:16 for TikTok / Reels / Shorts: the game centred over a blurred copy of itself.</summary>
    Vertical,
}

public sealed class ExportSettings
{
    public int SecondsBefore { get; set; } = 5;
    public int SecondsAfter { get; set; } = 5;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public int BitrateKbps { get; set; } = 16000;
    public PovMode Pov { get; set; } = PovMode.SelectedPlayer;
    public ulong? SpecificPlayerSteamId { get; set; }
    public HudMode Hud { get; set; } = HudMode.KillFeedOnly;
    public bool RecordAudio { get; set; } = true;
    public bool AddTitleOverlay { get; set; } = true;
    public bool FadeTransitions { get; set; } = true;
    public string? OutputFolder { get; set; }

    public BetweenPlays BetweenPlays { get; set; } = BetweenPlays.FastForward;

    /// <summary>Base fast-forward speed; long gaps go faster so they never take more than a few seconds.</summary>
    public int FastForwardSpeed { get; set; } = 4;

    /// <summary>Each of the player's kills plays in slow motion for about a second.</summary>
    public bool SlowMotionKills { get; set; }

    public VideoLayout Layout { get; set; } = VideoLayout.Landscape;

    /// <summary>"2/5"-style counter shown as each kill happens (plays with 2+ kills).</summary>
    public bool KillCounter { get; set; } = true;

    /// <summary>Text (nick, channel) shown in a corner for the whole clip; null = none.</summary>
    public string? Watermark { get; set; }

    /// <summary>Background music file (null = none) and its volume in percent.</summary>
    public string? MusicPath { get; set; }
    public int MusicVolume { get; set; } = 35;

    public static readonly int[] AllowedSecondsBefore = [5, 10, 15, 20, 30];
    public static readonly int[] FastForwardSpeeds = [2, 4, 8];

    /// <summary>Fast-forward stretches are sped up beyond the base speed so they last at most this long.</summary>
    public const double MaxFastForwardSeconds = 6;
    public const int MaxFastForwardSpeed = 16;
    public static readonly int[] AllowedSecondsAfter = [5, 10, 15, 20];

    public static readonly (string Name, int Width, int Height)[] Resolutions =
    [
        ("720p", 1280, 720),
        ("1080p", 1920, 1080),
        ("1440p", 2560, 1440),
        ("4K", 3840, 2160),
    ];

    public ExportSettings Clone() => (ExportSettings)MemberwiseClone();
}

public sealed class Cs2Settings
{
    public string? GameFolder { get; set; }
    public string? SteamFolder { get; set; }
    public int GsiPort { get; set; } = 31982;

    /// <summary>CS2 window resolution used while capturing (game renders at this size).</summary>
    public int CaptureWidth { get; set; } = 1920;
    public int CaptureHeight { get; set; } = 1080;
    public int CaptureFps { get; set; } = 60;

    /// <summary>Remove the helper key bind from the user's CS2 config when the render session ends.</summary>
    public bool CleanupBindsOnExit { get; set; } = true;

    /// <summary>Close CS2 when the queue finishes.</summary>
    public bool CloseGameWhenDone { get; set; } = true;

    /// <summary>Extra launch options appended when the app starts CS2.</summary>
    public string ExtraLaunchOptions { get; set; } = "-novid";

    /// <summary>Seconds to wait after a seek before resuming (texture streaming / POV switch).</summary>
    public double SettleSecondsAfterSeek { get; set; } = 1.5;

    /// <summary>Compensation for key-press-to-frame latency when trimming captured video.</summary>
    public int InputLatencyMs { get; set; } = 50;
}
