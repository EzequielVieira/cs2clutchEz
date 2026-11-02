using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2HG.Application;
using CS2HG.Core.Localization;
using CS2HG.Core.Settings;
using Microsoft.Win32;

namespace CS2HG.UI.ViewModels;

public sealed partial class AutoItemViewModel(string path) : ObservableObject
{
    public string Path { get; } = path;
    public string FileName => System.IO.Path.GetFileName(Path);

    [ObservableProperty] private string _status = L.T("auto.waiting");
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isDone;
}

/// <summary>Auto mode: several demos in, the best plays of each one queued for rendering.</summary>
public sealed partial class AutoViewModel(MainViewModel main) : ObservableObject
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<AutoItemViewModel> Items { get; } = [];
    public IReadOnlyList<int> TopValues { get; } = [1, 2, 3, 5, 10];

    [ObservableProperty] private int _top = main.Host.Settings.Settings.AutoTopPerMatch;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _summary = "";

    /// <summary>The Export page settings every queued play will use.</summary>
    public string ExportSummary
    {
        get
        {
            var e = main.Export.Build();
            var res = ExportSettings.Resolutions.FirstOrDefault(r => r.Height == e.Height).Name ?? $"{e.Height}p";
            var between = e.BetweenPlays == BetweenPlays.Cut ? L.T("between.cut") : L.T($"between.ff{e.FastForwardSpeed}");
            var layout = L.T(e.Layout == VideoLayout.Vertical ? "layout.vertical" : "layout.landscape");
            return L.F("auto.exportSummary", res, e.Fps, between, layout);
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(ExportSummary));
        foreach (var item in Items.Where(i => !i.IsDone && !IsRunning)) item.Status = L.T("auto.waiting");
    }

    partial void OnTopChanged(int value)
    {
        main.Host.Settings.Settings.AutoTopPerMatch = value;
        main.Host.Settings.Save();
    }

    [RelayCommand]
    private void AddDemos()
    {
        var dialog = new OpenFileDialog { Title = L.T("auto.addDialog"), Filter = L.T("home.importFilter"), Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        foreach (var file in dialog.FileNames) Add(file);
    }

    /// <summary>The matches CS2 already downloaded (replays folder), newest first.</summary>
    [RelayCommand]
    private void AddRecent()
    {
        var added = 0;
        foreach (var (_, demo) in main.Host.ShareCodes.LocalMatches())
        {
            if (demo is not null && Add(demo)) added++;
        }

        main.Notify(added > 0 ? L.F("auto.addedRecent", added) : L.T("auto.noRecent"), added == 0);
    }

    private bool Add(string path)
    {
        if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return false;
        Items.Add(new AutoItemViewModel(path));
        return true;
    }

    [RelayCommand]
    private void Remove(AutoItemViewModel? item)
    {
        if (item is not null && !IsRunning) Items.Remove(item);
    }

    [RelayCommand]
    private void Clear()
    {
        if (!IsRunning) Items.Clear();
        Summary = "";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        var pending = Items.Where(i => !i.IsDone).ToList();
        if (pending.Count == 0)
        {
            main.Notify(L.T("auto.empty"), true);
            return;
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        var export = main.Export.Build();
        int queued = 0, done = 0;
        try
        {
            foreach (var item in pending)
            {
                item.IsError = false;
                var current = item;
                var progress = new Progress<AnalysisStep>(s => Ui.Post(() =>
                {
                    current.Status = L.F("auto.analyzing", s.Fraction);
                    Progress = (done + s.Fraction) / pending.Count * 100;
                }));
                var result = await main.Host.Auto.QueueBestAsync(item.Path, Top, export, progress, _cts.Token);
                done++;
                Progress = (double)done / pending.Count * 100;
                queued += result.Queued;
                item.IsError = result.Error is not null;
                item.IsDone = result.Error is null;
                item.Status = result.Error is { } error ? error
                    : result.Found == 0 ? L.F("auto.noneFor", result.PlayerName ?? "—")
                    : result.Duplicates > 0 ? L.F("auto.queuedDup", result.Queued, result.PlayerName, result.Duplicates)
                    : L.F("auto.queued", result.Queued, result.PlayerName);
            }

            Summary = L.F("auto.done", queued, done);
            main.Notify(Summary);
        }
        catch (OperationCanceledException)
        {
            Summary = L.F("auto.cancelled", queued);
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenQueue() => main.NavigateCommand.Execute("Queue");

    [RelayCommand]
    private void OpenExport() => main.NavigateCommand.Execute("Export");
}
