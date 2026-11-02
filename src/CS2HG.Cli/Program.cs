using System.Globalization;
using System.Text;
using CS2HG.Application;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.Core.Planning;
using CS2HG.Core.Settings;
using CS2HG.HighlightDetection;

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var options = Options.Parse(args.Skip(1));
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

await using var host = await AppHost.CreateAsync();
// Messages coming from the app (stages, errors, highlight titles) follow --lang, else the app's language setting.
Localizer.Instance.SetLanguage(options.Get("lang") ?? host.Settings.Settings.Language);
try
{
    return args[0] switch
    {
        "probe" => await Probe(host, cts.Token),
        "analyze" => await Analyze(host, options, cts.Token),
        "render" => await Render(host, options, cts.Token),
        "queue" => QueueList(host),
        "matches" => await Matches(host, cts.Token),
        "demoinfo" => DemoInfoCommand(options),
        "resolve" => await ResolveCommand(host, options, cts.Token),
        "sharecode" => await ShareCodeCommand(host, options, cts.Token),
        "resume" => await ResumeQueue(host, cts.Token),
        "install-ffmpeg" => await InstallFfmpeg(host, cts.Token),
        "restore-config" => RestoreConfig(host),
        _ => Fail($"Unknown command: {args[0]}"),
    };
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    PrintHelp();
    return 2;
}

static void PrintHelp()
{
    Console.WriteLine("""
        cs2clutchez-cli — cs2clutchEz • cs2clutch.ez.com.br

          cs2clutchez-cli probe                              Checks CS2, FFmpeg, window capture and encoders
          cs2clutchez-cli analyze <demo> [--player <name|steamid|all>] [--top N]
          cs2clutchez-cli render  <demo> [--player X] [--top N | --rounds 3,5] [--before 5] [--after 5]
                                  [--res 720p|1080p|1440p|4K] [--fps 60] [--codec h264|h265] [--bitrate 16000]
                                  [--pov selected|killer|victim|third|director] [--hud killfeed|full|none]
                                  [--no-audio] [--no-overlay] [--keep-open] [--force]
          cs2clutchez-cli sharecode CSGO-xxxxx-xxxxx-xxxxx-xxxxx-xxxxx [--player X] [--top N] [--render]
                                                   Downloads the demo from the match share code and analyzes it
          cs2clutchez-cli matches                            Matches downloaded by CS2 (replays folder) with their share codes
          cs2clutchez-cli resolve CSGO-...                   Shows Valve's demo URL for a share code
          cs2clutchez-cli queue                              Lists the render queue
          cs2clutchez-cli resume                             Runs pending/interrupted jobs
          cs2clutchez-cli install-ffmpeg                     Downloads FFmpeg (BtbN, GPL) to the user folder
          cs2clutchez-cli restore-config                     Restores the backup of the CS2 configuration

        Any command accepts --lang pt-BR|en|es (default: the app's language).
        """);
}

static async Task<int> Probe(AppHost host, CancellationToken ct)
{
    var s = await host.CheckEnvironmentAsync(ct);
    Console.WriteLine($"CS2 found:             {s.Cs2Found}  {s.Cs2Message}");
    Console.WriteLine($"CS2 running:           {s.Cs2Running} (controllable by the app: {s.Cs2Controllable})");
    Console.WriteLine($"FFmpeg:                {s.FfmpegFound}  {s.FfmpegMessage}");
    Console.WriteLine($"WGC capture (gfxcapture): {s.WindowCaptureSupported}");
    Console.WriteLine("Encoders:              " + string.Join(", ", s.Encoders.Select(kv => $"{kv.Key}={(kv.Value ? "ok" : "-")}")));
    Console.WriteLine($"Folders:               {host.Settings.Settings.Paths.RootFolder}");
    return s.Cs2Found && s.FfmpegFound && s.WindowCaptureSupported ? 0 : 3;
}

static async Task<(OpenedDemo Demo, ulong? Player, IReadOnlyList<Highlight> Highlights)> Load(AppHost host, Options o, CancellationToken ct)
{
    var path = o.Positional.FirstOrDefault() ?? throw new ArgumentException("Pass a demo file or a match share code.");
    var last = "";
    var progress = new Progress<AnalysisStep>(p =>
    {
        var line = $"{p.Stage} {p.Fraction:P0}";
        if (line != last) Console.Error.Write($"\r{line,-70}");
        last = line;
    });
    var demo = ShareCode.Find(path) is not null && !File.Exists(path)
        ? await host.ShareCodes.OpenAsync(path, progress, ct, o.Has("force-download"))
        : await host.Analysis.OpenDemoAsync(path, progress, ct);
    Console.Error.WriteLine();

    ulong? player;
    var wanted = o.Get("player");
    if (wanted is null) player = AnalysisService.SuggestPlayer(demo.Match);
    else if (wanted.Equals("all", StringComparison.OrdinalIgnoreCase)) player = null;
    else if (ulong.TryParse(wanted, out var sid)) player = sid;
    else
    {
        player = demo.Match.Players.FirstOrDefault(p => p.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))?.SteamId
                 ?? demo.Match.Players.FirstOrDefault(p => p.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))?.SteamId
                 ?? throw new ArgumentException($"Player '{wanted}' is not in the demo.");
    }

    var highlights = host.Analysis.DetectHighlights(demo, player);
    return (demo, player, highlights);
}

static async Task<int> Analyze(AppHost host, Options o, CancellationToken ct)
{
    var (demo, player, highlights) = await Load(host, o, ct);
    var m = demo.Match;
    Console.WriteLine($"Map {m.Demo.MapName} | {m.Rounds.Count} rounds | {m.TeamNameCt} {m.FinalScoreCt} x {m.FinalScoreT} {m.TeamNameT} | " +
                      $"{TimeSpan.FromSeconds(m.Demo.DurationSeconds):hh\\:mm\\:ss} | tickrate {m.Demo.TickRate} | cache={demo.FromCache}");
    Console.WriteLine();
    Console.WriteLine($"{"Player",-26} {"Team",-5} {"K",3} {"D",3} {"A",3} {"HS%",5} {"ADR",5} {"3K",3} {"4K",3} {"ACE",3} {"Clutch",7}");
    foreach (var p in m.Players.OrderByDescending(p => p.Stats.Kills))
        Console.WriteLine($"{Trunc(p.Name, 26),-26} {p.Team.ShortName(),-5} {p.Stats.Kills,3} {p.Stats.Deaths,3} {p.Stats.Assists,3} {p.Stats.HeadshotPercentage,5:0} {p.Stats.Adr,5:0} " +
                          $"{p.Stats.ThreeKills,3} {p.Stats.FourKills,3} {p.Stats.Aces,3} {p.Stats.ClutchesWon,3}/{p.Stats.ClutchesPlayed,-3}");
    Console.WriteLine();
    var who = player is { } id ? m.FindPlayer(id)?.Name : "all players";
    Console.WriteLine($"Highlights for {who}: {highlights.Count}");
    var export = host.Settings.Settings.Export;
    var top = o.GetInt("top") ?? 50;
    Console.WriteLine($"{"#",3} {"Rnd",3} {"Score",5} {"Tick",7} {"Category",-10} {"Duration",-22} Title / components");
    var i = 0;
    foreach (var h in highlights.Take(top))
    {
        i++;
        Console.WriteLine($"{i,3} {h.Round,3} {h.Score,5} {h.KeyTick,7} {h.Category,-10} {RenderService.Describe(h, m, export, host.Settings.Settings.Detection),-22} {(player is null ? h.PlayerName + ": " : "")}{h.Title}");
        Console.WriteLine($"{"",48}{string.Join(" | ", RenderService.ComponentLines(h))}");
    }

    foreach (var w in m.Warnings) Console.WriteLine("Warning: " + L.T(w));
    return 0;
}

static async Task<int> Render(AppHost host, Options o, CancellationToken ct)
{
    var (demo, _, highlights) = await Load(host, o, ct);
    var export = host.Settings.Settings.Export.Clone();
    if (o.GetInt("before") is { } b) export.SecondsBefore = b;
    if (o.GetInt("after") is { } a) export.SecondsAfter = a;
    if (o.GetInt("fps") is { } fps) export.Fps = fps;
    if (o.GetInt("bitrate") is { } br) export.BitrateKbps = br;
    if (o.Get("res") is { } res)
    {
        var r = ExportSettings.Resolutions.FirstOrDefault(x => x.Name.Equals(res, StringComparison.OrdinalIgnoreCase));
        if (r.Name is null) throw new ArgumentException("Invalid resolution.");
        (export.Width, export.Height) = (r.Width, r.Height);
    }

    if (o.Get("codec") is { } codec) export.Codec = codec.Equals("h265", StringComparison.OrdinalIgnoreCase) ? VideoCodec.H265 : VideoCodec.H264;
    if (o.Get("pov") is { } pov)
    {
        export.Pov = pov.ToLowerInvariant() switch
        {
            "killer" => PovMode.Killer,
            "victim" => PovMode.Victim,
            "third" => PovMode.ThirdPerson,
            "director" => PovMode.DirectorCamera,
            _ => PovMode.SelectedPlayer,
        };
    }

    if (o.Get("hud") is { } hud)
        export.Hud = hud.ToLowerInvariant() switch { "full" => HudMode.Full, "none" => HudMode.None, _ => HudMode.KillFeedOnly };
    if (o.Has("no-audio")) export.RecordAudio = false;
    if (o.Has("no-overlay")) export.AddTitleOverlay = false;
    if (o.Has("keep-open")) host.Settings.Settings.Cs2.CloseGameWhenDone = false;

    IEnumerable<Highlight> selected = highlights;
    if (o.Get("rounds") is { } rounds)
    {
        var set = rounds.Split(',').Select(int.Parse).ToHashSet();
        selected = highlights.Where(h => set.Contains(h.Round));
    }
    else
    {
        selected = highlights.Take(o.GetInt("top") ?? 1);
    }

    var list = selected.ToList();
    if (list.Count == 0) throw new InvalidOperationException("No highlight selected.");
    foreach (var h in list)
    {
        var plan = ClipPlanner.Plan(h, demo.Match, export, host.Settings.Settings.Detection);
        Console.WriteLine($"→ R{h.Round} {h.Title} ({h.Score} pts): {string.Join(", ", plan.Select(s => $"[{s.StartTick}-{s.EndTick}] POV {s.PovName}"))}");
    }

    var results = await host.Render.EnqueueAsync(list, demo.Match, export, o.Has("force"));
    foreach (var r in results.Where(r => r.IsDuplicate))
        Console.WriteLine($"  (skipped: {r.Reason} — {r.Job.OutputPath})");
    return await RunQueue(host, results.Select(r => r.Job.Id).ToHashSet(), ct);
}

static async Task<int> RunQueue(AppHost host, HashSet<string>? watch, CancellationToken ct)
{
    var lastLine = "";
    host.Queue.JobUpdated += job =>
    {
        if (watch is not null && !watch.Contains(job.Id)) return;
        var line = $"[{job.Status}] R{job.Round} {Trunc(job.Title, 40)} {job.Progress:P0} {job.Stage}";
        if (line == lastLine) return;
        lastLine = line;
        Console.Error.Write($"\r{line,-110}");
    };
    host.Queue.Start();
    using var reg = ct.Register(() => host.Queue.Pause());
    await host.Queue.WaitForIdleAsync(CancellationToken.None);
    Console.Error.WriteLine();
    // give the idle hook (close CS2 + restore config) time to run
    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
    while (host.Playback.State.GameRunning && host.Settings.Settings.Cs2.CloseGameWhenDone)
        await Task.Delay(500, CancellationToken.None);

    var jobs = host.Queue.Jobs.Where(j => watch is null || watch.Contains(j.Id)).ToList();
    foreach (var j in jobs)
    {
        Console.WriteLine($"{j.Status,-10} {j.Id} R{j.Round} {j.Title}");
        if (j.OutputPath is not null) Console.WriteLine($"           video: {j.OutputPath}");
        if (j.ThumbnailPath is not null) Console.WriteLine($"           thumb: {j.ThumbnailPath}");
        foreach (var c in j.Captured) Console.WriteLine($"           seg {c.Index}: POV {c.PovObserved} verified={c.PovVerified} offset={c.VideoOffsetSeconds:0.000}s");
        if (j.Error is not null) Console.WriteLine($"           error: {j.Error}");
    }

    return jobs.All(j => j.Status == RenderJobStatus.Completed) ? 0 : 1;
}

static async Task<int> Matches(AppHost host, CancellationToken ct)
{
    await Task.CompletedTask;
    var matches = host.ShareCodes.LocalMatches();
    foreach (var (m, demo) in matches)
        Console.WriteLine($"{m.MatchTime:yyyy-MM-dd HH:mm}  {m.ScoreText,-6} {m.ShareCode}  {(demo is null ? "(.dem.info only — the demo can be downloaded with the code)" : Path.GetFileName(demo))}");
    if (matches.Count == 0) Console.WriteLine("No match downloaded by CS2 in the replays folder.");
    return 0;
}

static async Task<int> ResolveCommand(AppHost host, Options o, CancellationToken ct)
{
    var code = ShareCode.Parse(o.Positional.FirstOrDefault() ?? "");
    Console.WriteLine($"match {code.MatchId} outcome {code.OutcomeId} token {code.Token}");
    var m = await host.ShareCodes.ResolveAsync(code, ct);
    Console.WriteLine($"{m.MatchTime:yyyy-MM-dd HH:mm} score {m.ScoreText} duration {TimeSpan.FromSeconds(m.DurationSeconds)}");
    Console.WriteLine($"demo url: {m.DemoUrl}");
    return 0;
}

static int DemoInfoCommand(Options o)
{
    foreach (var path in o.Positional)
    {
        var info = path.EndsWith(".info", StringComparison.OrdinalIgnoreCase) ? path : path + ".info";
        var m = CS2HG.DemoPlayback.MatchListParser.ParseMatch(File.ReadAllBytes(info));
        Console.WriteLine($"{Path.GetFileName(info)}: match {m.MatchId} outcome {m.OutcomeId} token {m.Token} {m.MatchTime:yyyy-MM-dd HH:mm} {m.ScoreText}");
        Console.WriteLine($"  share code: {m.ShareCode}");
        Console.WriteLine($"  demo url:   {m.DemoUrl}");
    }

    return 0;
}

static async Task<int> ShareCodeCommand(AppHost host, Options o, CancellationToken ct)
{
    if (o.Has("render")) return await Render(host, o, ct);
    return await Analyze(host, o, ct);
}

static int QueueList(AppHost host)
{
    foreach (var j in host.Queue.Jobs)
        Console.WriteLine($"{j.Order,3} {j.Status,-10} {j.Progress,5:P0} {j.Id} R{j.Round} {j.PlayerName} — {j.Title} {j.OutputPath}");
    return 0;
}

static Task<int> ResumeQueue(AppHost host, CancellationToken ct) => RunQueue(host, null, ct);

static async Task<int> InstallFfmpeg(AppHost host, CancellationToken ct)
{
    var path = await host.Media.InstallAsync(new Progress<double>(p => Console.Error.Write($"\rDownloading FFmpeg {p:P0}   ")), ct);
    Console.Error.WriteLine();
    Console.WriteLine($"FFmpeg installed at {path}");
    return 0;
}

static int RestoreConfig(AppHost host)
{
    Console.WriteLine(host.Playback.RecoverPendingRestore() ? "CS2 configuration restored." : "Nothing to restore (or CS2 is running).");
    return 0;
}

static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

sealed class Options
{
    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = list[i][2..];
                var value = i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal) ? list[++i] : null;
                o._named[key] = value;
            }
            else
            {
                o.Positional.Add(list[i]);
            }
        }

        return o;
    }

    public bool Has(string key) => _named.ContainsKey(key);

    public string? Get(string key) => _named.TryGetValue(key, out var v) ? v : null;

    public int? GetInt(string key) => Get(key) is { } v && int.TryParse(v, out var i) ? i : null;

    public double? GetDouble(string key) => Get(key) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
