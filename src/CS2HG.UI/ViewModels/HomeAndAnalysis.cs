using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2HG.Application;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.Core.Utilities;
using CS2HG.DemoParser;
using CS2HG.DemoPlayback;
using CS2HG.HighlightDetection;
using Microsoft.Win32;

namespace CS2HG.UI.ViewModels;

public sealed record RecentDemoItem(string Path, string Name, string Details, bool Exists);

public sealed record StatusItem(string Name, string Detail, bool Ok);

public sealed record LocalMatchItem(string ShareCode, string Title, string Details, string? DemoPath);

public sealed record HistoryRow(string Map, string Player, int Count, string Status, string When);

public sealed partial class HomeViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<RecentDemoItem> RecentDemos { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];
    public ObservableCollection<StatusItem> Environment { get; } = [];
    public ObservableCollection<LocalMatchItem> LocalMatches { get; } = [];

    [ObservableProperty] private bool _checkingEnvironment;
    [ObservableProperty] private string _shareCodeText = "";
    [ObservableProperty] private string _queueSummary = "";

    public void Refresh()
    {
        RecentDemos.Clear();
        var known = main.Host.Analysis.RecentDemos().ToDictionary(d => d.OriginalPath, StringComparer.OrdinalIgnoreCase);
        foreach (var path in main.Host.Settings.Settings.RecentDemos)
        {
            var details = known.TryGetValue(path, out var d)
                ? L.F("home.recentDetails", d.Map, d.ScoreCt, d.ScoreT, d.Rounds, TimeFormat.Clock(d.Duration))
                : L.T("home.notAnalyzed");
            RecentDemos.Add(new RecentDemoItem(path, Path.GetFileName(path), details, File.Exists(path)));
        }

        History.Clear();
        foreach (var h in main.Host.Analysis.History().Take(20))
        {
            var when = DateTime.TryParse(h.StartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                ? t.ToLocalTime().ToString("g", Localizer.Instance.Culture)
                : h.StartedAt;
            var player = h.PlayerSteamId is null ? L.T("analysis.allPlayers") : h.PlayerName;
            History.Add(new HistoryRow(h.Map, player, h.HighlightCount, AnalysisStatusText(h.Status), when));
        }

        LocalMatches.Clear();
        foreach (var (m, demo) in main.Host.ShareCodes.LocalMatches().Take(12))
        {
            var date = m.MatchTime?.ToString("g", Localizer.Instance.Culture) ?? "";
            var title = m.ScoreText.Length > 0 ? L.F("home.localMatchTitle", date, m.ScoreText) : date;
            LocalMatches.Add(new LocalMatchItem(m.ShareCode.ToString(), title,
                L.T(demo is null ? "home.localNotOnDisk" : "home.localOnDisk"), demo));
        }

        var jobs = main.Host.Queue.Jobs;
        QueueSummary = L.F("home.queueSummary",
            jobs.Count(j => j.Status == RenderJobStatus.Pending),
            jobs.Count(j => j.Status == RenderJobStatus.Running),
            jobs.Count(j => j.Status == RenderJobStatus.Completed),
            jobs.Count(j => j.Status == RenderJobStatus.Failed));
    }

    /// <summary>Status is stored as a code ("done", "running", "failed"); older rows hold Portuguese text.</summary>
    private static string AnalysisStatusText(string status) => status switch
    {
        AnalysisStatus.Done or "Concluída" => L.T("analysis.statusDone"),
        AnalysisStatus.Running or "Em andamento" => L.T("analysis.statusRunning"),
        AnalysisStatus.Failed or "Falhou" => L.T("analysis.statusFailed"),
        _ => status,
    };

    [RelayCommand]
    private async Task CheckEnvironmentAsync()
    {
        if (CheckingEnvironment) return;
        CheckingEnvironment = true;
        try
        {
            var s = await main.Host.CheckEnvironmentAsync(CancellationToken.None);
            Environment.Clear();
            Environment.Add(new StatusItem("Counter-Strike 2", s.Cs2Message, s.Cs2Found));
            Environment.Add(new StatusItem(L.T("env.cs2Running"),
                L.T(!s.Cs2Running ? "env.cs2Closed" : s.Cs2Controllable ? "env.cs2Controllable" : "env.cs2Manual"),
                !s.Cs2Running || s.Cs2Controllable));
            Environment.Add(new StatusItem("FFmpeg", s.FfmpegMessage, s.FfmpegFound));
            Environment.Add(new StatusItem(L.T("env.capture"), L.T(s.WindowCaptureSupported ? "env.captureOk" : "env.captureMissing"), s.WindowCaptureSupported));
            var enc = s.Encoders.Where(kv => kv.Value).Select(kv => kv.Key).ToList();
            Environment.Add(new StatusItem(L.T("env.encoders"), enc.Count > 0 ? string.Join(", ", enc) : L.T("env.none"), enc.Count > 0));
        }
        catch (Exception ex)
        {
            main.Notify(ex.Message, true);
        }
        finally
        {
            CheckingEnvironment = false;
        }
    }

    [RelayCommand]
    private async Task OpenShareCodeAsync()
    {
        if (ShareCode.Find(ShareCodeText) is null)
        {
            main.Notify(L.T("home.shareCodeInvalid"), true);
            return;
        }

        await main.OpenShareCodeAsync(ShareCodeText);
    }

    [RelayCommand]
    private async Task PasteShareCodeAsync()
    {
        var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
        if (ShareCode.Find(text) is { } code)
        {
            ShareCodeText = code;
            await main.OpenShareCodeAsync(code);
        }
        else
        {
            main.Notify(L.T("home.clipboardNoCode"), true);
        }
    }

    [RelayCommand]
    private async Task OpenLocalMatchAsync(LocalMatchItem? item)
    {
        if (item is null) return;
        if (item.DemoPath is not null) await main.OpenDemoAsync(item.DemoPath);
        else await main.OpenShareCodeAsync(item.ShareCode);
    }

    [RelayCommand]
    private void CopyShareCode(LocalMatchItem? item)
    {
        if (item is null) return;
        System.Windows.Clipboard.SetText(item.ShareCode);
        main.Notify(L.F("home.codeCopied", item.ShareCode));
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("home.importDialog"),
            Filter = L.T("home.importFilter"),
            InitialDirectory = DefaultDemoFolder(),
        };
        if (dialog.ShowDialog() == true) await main.OpenDemoAsync(dialog.FileName);
    }

    public async Task DropFilesAsync(string[] files)
    {
        var demo = files.FirstOrDefault(DemoImporter.IsSupportedExtension);
        if (demo is null)
        {
            main.Notify(L.T("home.dropInvalid"), true);
            return;
        }

        await main.OpenDemoAsync(demo);
    }

    [RelayCommand]
    private async Task OpenRecentAsync(RecentDemoItem? item)
    {
        if (item is null) return;
        if (!File.Exists(item.Path))
        {
            main.Notify(L.F("home.fileMissing", item.Path), true);
            return;
        }

        await main.OpenDemoAsync(item.Path);
    }

    private string DefaultDemoFolder()
    {
        try
        {
            var replays = Path.Combine(main.Host.Playback.Installation.CsgoFolder, "replays");
            if (Directory.Exists(replays)) return replays;
        }
        catch
        {
            // CS2 not found
        }

        return main.Host.Settings.Settings.Paths.Demos;
    }
}

public sealed record PlayerRow(PlayerInfo Player)
{
    public string Name => Player.Name;
    public string Team => Player.Team.ShortName();
    public int Kills => Player.Stats.Kills;
    public int Deaths => Player.Stats.Deaths;
    public int Assists => Player.Stats.Assists;
    public string Hs => $"{Player.Stats.HeadshotPercentage:0}%";
    public string Adr => $"{Player.Stats.Adr:0}";
    public string Multi => $"{Player.Stats.ThreeKills}/{Player.Stats.FourKills}/{Player.Stats.Aces}";
    public string Clutches => $"{Player.Stats.ClutchesWon}/{Player.Stats.ClutchesPlayed}";
    public string SteamId => Player.IsBot ? "BOT" : Player.SteamId.ToString();
}

public sealed record RoundRow(int Number, string Winner, string Reason, string Score, string Bomb, int Kills);

public sealed partial class AnalysisViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<PlayerRow> Players { get; } = [];
    public ObservableCollection<RoundRow> Rounds { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _hasDemo;
    [ObservableProperty] private string _matchTitle = "";
    [ObservableProperty] private string _mapName = "";
    [ObservableProperty] private string _duration = "";
    [ObservableProperty] private string _score = "";
    [ObservableProperty] private int _roundCount;
    [ObservableProperty] private int _killCount;
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private PlayerRow? _selectedPlayer;
    [ObservableProperty] private bool _analyzeAllPlayers;

    public Task LoadAsync(string path) =>
        LoadCoreAsync(progress => main.Host.Analysis.OpenDemoAsync(path, progress, CancellationToken.None));

    public Task LoadShareCodeAsync(string code) =>
        LoadCoreAsync(progress => main.Host.ShareCodes.OpenAsync(code, progress, CancellationToken.None));

    private async Task LoadCoreAsync(Func<IProgress<AnalysisStep>, Task<OpenedDemo>> open)
    {
        if (IsBusy) return;
        IsBusy = true;
        HasDemo = false;
        Progress = 0;
        ProgressText = L.T("analysis.opening");
        try
        {
            var demo = await open(new Progress<AnalysisStep>(s => Ui.Post(() =>
            {
                Progress = s.Fraction * 100;
                ProgressText = s.Stage;
            })));
            main.Session.Demo = demo;
            Show(demo);
            ProgressText = L.T(demo.FromCache ? "analysis.fromCache" : "analysis.parsed");
            Progress = 100;

            // Default selection: logged-in Steam user if present, else top fragger.
            var suggested = AnalysisService.SuggestPlayer(demo.Match);
            SelectedPlayer = Players.FirstOrDefault(p => p.Player.SteamId == suggested) ?? Players.FirstOrDefault();
            main.Home.Refresh();
        }
        catch (Exception ex) when (ex is DemoFormatException or GameCoordinatorException or FormatException or System.Net.Http.HttpRequestException)
        {
            main.Notify(ex.Message, true);
            ProgressText = ex.Message;
        }
        catch (Exception ex)
        {
            main.Notify(L.F("analysis.failed", ex.Message), true);
            ProgressText = L.T("common.failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Rebuilds the texts of the open demo in the current language, keeping the selected player.</summary>
    public void RefreshTexts()
    {
        if (IsBusy || main.Session.Demo is not { } demo) return;
        var selected = SelectedPlayer?.Player.SteamId;
        Show(demo);
        SelectedPlayer = Players.FirstOrDefault(p => p.Player.SteamId == selected) ?? Players.FirstOrDefault();
        ProgressText = L.T(demo.FromCache ? "analysis.fromCache" : "analysis.parsed");
    }

    private void Show(OpenedDemo demo)
    {
        var m = demo.Match;
        HasDemo = true;
        MatchTitle = $"{m.TeamNameCt} {m.FinalScoreCt} × {m.FinalScoreT} {m.TeamNameT}";
        MapName = m.Demo.MapName;
        Duration = TimeFormat.Clock(m.Demo.DurationSeconds);
        Score = $"{m.FinalScoreCt}:{m.FinalScoreT}";
        RoundCount = m.Rounds.Count;
        KillCount = m.Kills.Count(k => k.IsEnemyKill);
        Details = $"{Path.GetFileName(demo.Record.OriginalPath)} • tickrate {m.Demo.TickRate} • {(m.Demo.IsGotv ? "GOTV" : "POV")} • {m.Demo.ServerName}";

        Players.Clear();
        foreach (var p in m.Players.OrderBy(p => p.Team).ThenByDescending(p => p.Stats.Kills)) Players.Add(new PlayerRow(p));
        Rounds.Clear();
        foreach (var r in m.Rounds)
        {
            var bomb = r.BombDefuseTick is not null ? L.F("analysis.bombDefused", r.BombSite)
                : r.BombExplodeTick is not null ? L.F("analysis.bombExploded", r.BombSite)
                : r.BombPlantTick is not null ? L.F("analysis.bombPlanted", r.BombSite) : "";
            Rounds.Add(new RoundRow(r.Number, r.Winner.ShortName(), ReasonText(r.Reason), $"{r.ScoreCtAfter}:{r.ScoreTAfter}", bomb,
                m.Kills.Count(k => k.Round == r.Number && k.IsEnemyKill)));
        }

        Warnings.Clear();
        foreach (var w in m.Warnings) Warnings.Add(L.T(w));
    }

    private static string ReasonText(RoundEndReason reason) => reason switch
    {
        RoundEndReason.TargetBombed => L.T("reason.bombed"),
        RoundEndReason.BombDefused => L.T("reason.defused"),
        RoundEndReason.CtsWin => L.T("reason.ctsWin"),
        RoundEndReason.TerroristsWin => L.T("reason.tsWin"),
        RoundEndReason.TargetSaved => L.T("reason.time"),
        RoundEndReason.TerroristsSurrender or RoundEndReason.CtSurrender => L.T("reason.surrender"),
        _ => reason.ToString(),
    };

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        var demo = main.Session.Demo;
        if (demo is null) return;
        if (!AnalyzeAllPlayers && SelectedPlayer is null)
        {
            main.Notify(L.T("analysis.choosePlayer"), true);
            return;
        }

        IsBusy = true;
        ProgressText = L.T("analysis.detecting");
        try
        {
            ulong? player = AnalyzeAllPlayers ? null : SelectedPlayer!.Player.SteamId;
            var highlights = await Task.Run(() => main.Host.Analysis.DetectHighlights(demo, player));
            main.Session.PlayerSteamId = player;
            main.Highlights.Load(highlights);
            ProgressText = L.F("analysis.found", highlights.Count);
            main.Notify(L.F("analysis.foundFor", highlights.Count, player is null ? L.T("analysis.allPlayers") : SelectedPlayer!.Name));
            main.NavigateCommand.Execute("Highlights");
        }
        catch (Exception ex)
        {
            main.Notify(L.F("analysis.detectFailed", ex.Message), true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public static string DetectorsDescription() =>
        string.Join("\n", HighlightAnalyzer.DefaultDetectors().Select(d => $"• {d.Name}: {d.Description}"));
}
