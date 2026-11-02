using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2HG.Application;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.Core.Planning;
using CS2HG.Core.Settings;
using CS2HG.HighlightDetection;
using Microsoft.Win32;

namespace CS2HG.UI.ViewModels;

/// <summary>Combo box item. <paramref name="Text"/> is a localization key when <paramref name="Localized"/> is true.</summary>
public sealed record Option<T>(T Value, string Text, bool Localized = true)
{
    public string Label => Localized ? L.T(Text) : Text;

    public override string ToString() => Label;
}

public static class Options
{
    public static readonly IReadOnlyList<Option<PovMode>> PovModes =
    [
        new(PovMode.SelectedPlayer, "pov.selected"),
        new(PovMode.Killer, "pov.killer"),
        new(PovMode.Victim, "pov.victim"),
        new(PovMode.SpecificPlayer, "pov.specific"),
        new(PovMode.ThirdPerson, "pov.third"),
        new(PovMode.DirectorCamera, "pov.director"),
    ];

    public static readonly IReadOnlyList<Option<HudMode>> HudModes =
    [
        new(HudMode.KillFeedOnly, "hud.killfeed"),
        new(HudMode.Full, "hud.full"),
        new(HudMode.None, "hud.none"),
    ];

    public static readonly IReadOnlyList<Option<VideoCodec>> Codecs =
    [
        new(VideoCodec.H264, "MP4 H.264", false),
        new(VideoCodec.H265, "MP4 H.265 (HEVC)", false),
    ];

    public static readonly IReadOnlyList<int> FpsValues = [30, 60, 120];
}

public sealed partial class HighlightItemViewModel : ObservableObject
{
    private readonly double _seconds;
    private readonly int _segments;

    public HighlightItemViewModel(Highlight highlight, MatchData match, ExportSettings export, DetectionSettings detection, string? thumbnail)
    {
        Highlight = highlight;
        var plan = ClipPlanner.Plan(highlight, match, export, detection);
        _seconds = ClipPlanner.TotalSeconds(plan, match.Demo.TickRate);
        _segments = plan.Count;
        TimeText = Core.Utilities.TimeFormat.Clock((double)highlight.KeyTick / Math.Max(1, match.Demo.TickRate));
        _thumbnail = thumbnail;
        _pov = Options.PovModes.First(o => o.Value == export.Pov);
    }

    public Highlight Highlight { get; }
    public int Round => Highlight.Round;
    public string Player => Highlight.PlayerName;
    public string Title => HighlightAnalyzer.TitleFor(Highlight);
    public HighlightCategory Category => Highlight.Category;
    public string CategoryName => L.T("category." + Highlight.Category);
    public int Score => Highlight.Score;
    public int Tick => Highlight.KeyTick;
    public string TimeText { get; }
    public string Weapons => RenderService.WeaponsText(Highlight);
    public string Events => string.Join(" • ", Highlight.Kinds.Where(k => k != HighlightKind.Kill).Select(k => k.DisplayName()));
    public string Details => string.Join("\n", Highlight.Components.Select(c => $"+{c.Score}  {c.Description}"));
    public string DurationText => _segments > 1 ? L.F("highlights.durationCuts", _seconds, _segments) : L.F("highlights.duration", _seconds);
    public IReadOnlyList<Option<PovMode>> PovModes => Options.PovModes;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private Option<PovMode> _pov;
    [ObservableProperty] private string? _thumbnail;

    public void RefreshTexts() => OnPropertyChanged(string.Empty);
}

public sealed partial class HighlightsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private IReadOnlyList<Highlight> _last = [];

    public HighlightsViewModel(MainViewModel main)
    {
        _main = main;
        View = CollectionViewSource.GetDefaultView(main.Session.Highlights);
        View.Filter = o => o is HighlightItemViewModel h && h.Score >= MinScore &&
                           (CategoryFilter is null || h.Category == CategoryFilter.Value) &&
                           (string.IsNullOrWhiteSpace(Search) || h.Title.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                            h.Player.Contains(Search, StringComparison.OrdinalIgnoreCase));
        _selectedCategory = Categories[0];
        _summary = L.T("highlights.empty");
    }

    public ICollectionView View { get; }

    public IReadOnlyList<Option<HighlightCategory?>> Categories { get; } =
    [
        new(null, "category.All"),
        new(HighlightCategory.Ace, "category.Ace"),
        new(HighlightCategory.Clutch, "category.Clutch"),
        new(HighlightCategory.MultiKill, "category.MultiKill"),
        new(HighlightCategory.Objective, "category.Objective"),
        new(HighlightCategory.Utility, "category.Utility"),
        new(HighlightCategory.Kills, "category.Kills"),
    ];

    [ObservableProperty] private int _minScore;
    [ObservableProperty] private Option<HighlightCategory?>? _selectedCategory;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _summary;
    [ObservableProperty] private HighlightItemViewModel? _current;

    private HighlightCategory? CategoryFilter => SelectedCategory?.Value;

    partial void OnMinScoreChanged(int value) => View.Refresh();
    partial void OnSelectedCategoryChanged(Option<HighlightCategory?>? value) => View.Refresh();
    partial void OnSearchChanged(string value) => View.Refresh();

    public void Load(IReadOnlyList<Highlight> highlights)
    {
        var demo = _main.Session.Demo!;
        var s = _main.Host.Settings.Settings;
        var thumbs = _main.Host.Queue.Jobs.Where(j => j.Status == RenderJobStatus.Completed && j.ThumbnailPath is not null)
            .GroupBy(j => j.HighlightId).ToDictionary(g => g.Key, g => g.Last().ThumbnailPath);
        foreach (var old in _main.Session.Highlights) old.PropertyChanged -= ItemChanged;
        _main.Session.Highlights.Clear();
        foreach (var h in highlights)
        {
            var item = new HighlightItemViewModel(h, demo.Match, s.Export, s.Detection, thumbs.GetValueOrDefault(h.Id));
            item.PropertyChanged += ItemChanged;
            _main.Session.Highlights.Add(item);
        }

        // Preselect the top plays so a one-click render produces something good.
        foreach (var item in _main.Session.Highlights.Where(h => h.Highlight.Category != HighlightCategory.Kills).Take(5)) item.IsSelected = true;
        UpdateCount();
        _last = highlights;
        RefreshTexts();
        View.Refresh();
    }

    public void RefreshTexts()
    {
        foreach (var item in _main.Session.Highlights) item.RefreshTexts();
        if (_last.Count == 0)
        {
            Summary = L.T("highlights.empty");
            return;
        }

        Summary = L.F("highlights.summary", _last.Count, _last.Count(h => h.Category == HighlightCategory.Ace),
            _last.Count(h => h.Category == HighlightCategory.Clutch), HighlightAnalyzer.TitleFor(_last[0]));
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HighlightItemViewModel.IsSelected)) UpdateCount();
    }

    private void UpdateCount() => SelectedCount = _main.Session.Highlights.Count(h => h.IsSelected);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var h in View.Cast<HighlightItemViewModel>()) h.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var h in _main.Session.Highlights) h.IsSelected = false;
    }

    [RelayCommand]
    private void SelectTop(string count)
    {
        SelectNone();
        foreach (var h in View.Cast<HighlightItemViewModel>().Take(int.Parse(count))) h.IsSelected = true;
    }

    [RelayCommand]
    private void GoToExport() => _main.NavigateCommand.Execute("Export");

    [RelayCommand]
    private Task RenderSelectedAsync() => _main.Export.RenderAsync();

    public IReadOnlyList<HighlightItemViewModel> Selected => _main.Session.Highlights.Where(h => h.IsSelected).ToList();
}

public sealed partial class ExportViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ExportViewModel(MainViewModel main)
    {
        _main = main;
        var e = main.Host.Settings.Settings.Export;
        _resolution = Resolutions.FirstOrDefault(r => r.Value.Width == e.Width) ?? Resolutions[1];
        _fps = e.Fps;
        _codec = Options.Codecs.First(c => c.Value == e.Codec);
        _bitrate = e.BitrateKbps;
        _pov = Options.PovModes.First(p => p.Value == e.Pov);
        _hud = Options.HudModes.First(h => h.Value == e.Hud);
        _secondsBefore = e.SecondsBefore;
        _secondsAfter = e.SecondsAfter;
        _recordAudio = e.RecordAudio;
        _addTitleOverlay = e.AddTitleOverlay;
        _fadeTransitions = e.FadeTransitions;
        _outputFolder = e.OutputFolder ?? main.Host.Settings.Settings.Paths.Highlights;
    }

    public IReadOnlyList<Option<(int Width, int Height)>> Resolutions { get; } =
        ExportSettings.Resolutions.Select(r => new Option<(int, int)>((r.Width, r.Height), $"{r.Name} ({r.Width}×{r.Height})", false)).ToList();

    public IReadOnlyList<int> FpsValues => Options.FpsValues;
    public IReadOnlyList<Option<VideoCodec>> Codecs => Options.Codecs;
    public IReadOnlyList<Option<PovMode>> PovModes => Options.PovModes;
    public IReadOnlyList<Option<HudMode>> HudModes => Options.HudModes;
    public IReadOnlyList<int> BeforeValues => ExportSettings.AllowedSecondsBefore;
    public IReadOnlyList<int> AfterValues => ExportSettings.AllowedSecondsAfter;
    public IReadOnlyList<int> Bitrates { get; } = [8000, 12000, 16000, 24000, 40000, 60000];
    public ObservableCollection<PlayerInfo> Players { get; } = [];

    [ObservableProperty] private Option<(int Width, int Height)> _resolution;
    [ObservableProperty] private int _fps;
    [ObservableProperty] private Option<VideoCodec> _codec;
    [ObservableProperty] private int _bitrate;
    [ObservableProperty] private Option<PovMode> _pov;
    [ObservableProperty] private PlayerInfo? _specificPlayer;
    [ObservableProperty] private Option<HudMode> _hud;
    [ObservableProperty] private int _secondsBefore;
    [ObservableProperty] private int _secondsAfter;
    [ObservableProperty] private bool _recordAudio;
    [ObservableProperty] private bool _addTitleOverlay;
    [ObservableProperty] private bool _fadeTransitions;
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private bool _isBusy;

    public string SelectionText => L.F("export.selection", _main.Highlights.Selected.Count);

    public string CaptureNote => L.F("export.captureNote", _main.Host.Settings.Settings.Cs2.CaptureWidth, _main.Host.Settings.Settings.Cs2.CaptureHeight);

    public void RefreshPlayers()
    {
        Players.Clear();
        foreach (var p in _main.Session.Demo?.Match.Players ?? []) Players.Add(p);
        SpecificPlayer ??= Players.FirstOrDefault(p => p.SteamId == _main.Session.PlayerSteamId);
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CaptureNote));
    }

    public ExportSettings Build() => new()
    {
        Width = Resolution.Value.Width,
        Height = Resolution.Value.Height,
        Fps = Fps,
        Codec = Codec.Value,
        BitrateKbps = Bitrate,
        Pov = Pov.Value,
        SpecificPlayerSteamId = SpecificPlayer?.SteamId,
        Hud = Hud.Value,
        SecondsBefore = SecondsBefore,
        SecondsAfter = SecondsAfter,
        RecordAudio = RecordAudio,
        AddTitleOverlay = AddTitleOverlay,
        FadeTransitions = FadeTransitions,
        OutputFolder = OutputFolder,
    };

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog { Title = L.T("export.folderDialog"), InitialDirectory = OutputFolder };
        if (dialog.ShowDialog() == true) OutputFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void SaveAsDefault()
    {
        _main.Host.Settings.Settings.Export = Build();
        _main.Host.Settings.Save();
        _main.Notify(L.T("export.savedDefault"));
    }

    [RelayCommand]
    public async Task RenderAsync()
    {
        var demo = _main.Session.Demo;
        var selected = _main.Highlights.Selected;
        if (demo is null || selected.Count == 0)
        {
            _main.Notify(L.T("export.selectOne"), true);
            return;
        }

        if (Pov.Value == PovMode.SpecificPlayer && SpecificPlayer is null)
        {
            _main.Notify(L.T("export.choosePovPlayer"), true);
            return;
        }

        IsBusy = true;
        try
        {
            var created = 0;
            var duplicates = 0;
            foreach (var item in selected)
            {
                var export = Build();
                // The per-highlight POV choice (Highlights page) wins over the page default when changed.
                if (item.Pov.Value != _main.Host.Settings.Settings.Export.Pov) export.Pov = item.Pov.Value;
                var results = await _main.Host.Render.EnqueueAsync([item.Highlight], demo.Match, export);
                created += results.Count(r => !r.IsDuplicate);
                duplicates += results.Count(r => r.IsDuplicate);
            }

            _main.Notify(duplicates > 0 ? L.F("export.enqueuedDup", created, duplicates) : L.F("export.enqueued", created));
            _main.NavigateCommand.Execute("Queue");
        }
        catch (Exception ex)
        {
            _main.Notify(ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
