using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;
using CS2HG.DemoPlayback;
using Microsoft.Win32;

namespace CS2HG.UI.ViewModels;

public sealed partial class JobItemViewModel(RenderJob job) : ObservableObject
{
    public RenderJob Job { get; private set; } = job;

    public string Id => Job.Id;
    public string Title => Job.Title;
    public string Subtitle => L.F("queue.jobSubtitle", Job.PlayerName, Job.Round, Job.MatchName, Job.Export.Width, Job.Export.Height, Job.Export.Fps, Job.Export.Codec);
    public RenderJobStatus Status => Job.Status;
    public string StatusText => L.T("jobStatus." + Job.Status);
    public double Progress => Job.Progress * 100;
    public string Stage => Job.Error is { Length: > 0 } e ? e : Job.Status switch
    {
        RenderJobStatus.Completed => L.T("stage.done"),
        RenderJobStatus.Cancelled => L.T("stage.cancelled"),
        _ => Job.Stage,
    };
    public string? OutputPath => Job.OutputPath;
    public string? Thumbnail => Job.ThumbnailPath;
    public string Segments => L.F("queue.segments", Job.Captured.Count, Job.Segments.Count);
    public string PovText => Job.Captured.Count == 0 ? "" :
        Job.Captured.All(c => c.PovVerified) ? L.T("queue.povVerified") : L.F("queue.povNotVerified", Job.Captured.Count(c => !c.PovVerified));
    public bool CanRetry => Job.Status is RenderJobStatus.Failed or RenderJobStatus.Cancelled;
    public bool CanCancel => Job.Status is RenderJobStatus.Pending or RenderJobStatus.Running or RenderJobStatus.Paused;
    public bool HasOutput => Job.Status == RenderJobStatus.Completed && Job.OutputPath is not null && File.Exists(Job.OutputPath);

    [ObservableProperty] private bool _isSelected;

    public void Update(RenderJob job)
    {
        Job = job;
        OnPropertyChanged(string.Empty);
    }
}

public sealed partial class QueueViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<JobItemViewModel> Jobs { get; } = [];

    [ObservableProperty] private double _totalProgress;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _summary = "";

    public void Sync()
    {
        var jobs = main.Host.Queue.Jobs;
        var byId = Jobs.ToDictionary(j => j.Id);
        foreach (var job in jobs)
        {
            if (byId.TryGetValue(job.Id, out var vm)) vm.Update(job);
            else Jobs.Add(new JobItemViewModel(job));
        }

        foreach (var gone in Jobs.Where(j => jobs.All(x => x.Id != j.Id)).ToList()) Jobs.Remove(gone);
        var ordered = jobs.Select(j => j.Id).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Jobs.IndexOf(Jobs.First(j => j.Id == ordered[i]));
            if (current != i) Jobs.Move(current, i);
        }

        TotalProgress = main.Host.Queue.TotalProgress * 100;
        IsPaused = main.Host.Queue.IsPaused;
        var failed = jobs.Count(j => j.Status == RenderJobStatus.Failed);
        Summary = L.F("queue.summary", jobs.Count(j => j.Status == RenderJobStatus.Completed), jobs.Count(j => j.Status != RenderJobStatus.Cancelled)) +
                  (failed > 0 ? L.F("queue.summaryFailed", failed) : "");
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!await EnsureGameControllableAsync()) return;
        if (main.Host.Queue.IsPaused) await main.Host.Queue.ResumeAsync();
        main.Host.Queue.Start();
        Sync();
    }

    /// <summary>A CS2 opened by the user can't be driven; offer to restart it in render mode.</summary>
    public async Task<bool> EnsureGameControllableAsync()
    {
        var running = Cs2PlaybackController.FindRunningGame();
        if (running is null || Cs2PlaybackController.IsControllable(running.Id)) return true;
        var answer = MessageBox.Show(L.T("queue.restartCs2Text"), L.T("queue.restartCs2Title"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        try
        {
            running.CloseMainWindow();
            await Task.Run(() =>
            {
                if (!running.WaitForExit(20000)) running.Kill();
            });
            return true;
        }
        catch (Exception ex)
        {
            main.Notify(L.F("queue.closeCs2Failed", ex.Message), true);
            return false;
        }
    }

    [RelayCommand]
    private void Pause()
    {
        main.Host.Queue.Pause();
        Sync();
    }

    [RelayCommand]
    private async Task CancelAllAsync()
    {
        await main.Host.Queue.CancelAllAsync();
        Sync();
    }

    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        await main.Host.Queue.ClearFinishedAsync();
        Sync();
    }

    [RelayCommand]
    private async Task CancelAsync(JobItemViewModel? job)
    {
        if (job is not null) await main.Host.Queue.CancelAsync(job.Id);
        Sync();
    }

    [RelayCommand]
    private async Task RetryAsync(JobItemViewModel? job)
    {
        if (job is null) return;
        if (!await EnsureGameControllableAsync()) return;
        await main.Host.Queue.RetryAsync(job.Id);
        main.Host.Queue.Start();
        Sync();
    }

    [RelayCommand]
    private async Task RemoveAsync(JobItemViewModel? job)
    {
        if (job is not null) await main.Host.Queue.RemoveAsync(job.Id);
        Sync();
    }

    [RelayCommand]
    private async Task MoveUpAsync(JobItemViewModel? job)
    {
        if (job is not null) await main.Host.Queue.MoveAsync(job.Id, -1);
        Sync();
    }

    [RelayCommand]
    private async Task MoveDownAsync(JobItemViewModel? job)
    {
        if (job is not null) await main.Host.Queue.MoveAsync(job.Id, 1);
        Sync();
    }

    [RelayCommand]
    private void Play(JobItemViewModel? job) => Shell.Open(job?.OutputPath);

    [RelayCommand]
    private void OpenFolder(JobItemViewModel? job) => Shell.Reveal(job?.OutputPath);
}

public static class Shell
{
    public static void Open(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    public static void Reveal(string? path)
    {
        if (path is null) return;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    public static void CopyFile(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        Clipboard.SetFileDropList(new StringCollection { path });
    }
}

public sealed partial class LibraryViewModel(MainViewModel main) : ObservableObject
{
    public ObservableCollection<JobItemViewModel> Clips { get; } = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "";

    public void Refresh()
    {
        Clips.Clear();
        foreach (var job in main.Host.Queue.Jobs.Where(j => j.Status == RenderJobStatus.Completed).OrderByDescending(j => j.FinishedAt))
            Clips.Add(new JobItemViewModel(job));
        Summary = L.F("clips.summary", Clips.Count, main.Host.Settings.Settings.Paths.Highlights);
    }

    [RelayCommand]
    private void Play(JobItemViewModel? clip) => Shell.Open(clip?.OutputPath);

    [RelayCommand]
    private void OpenFolder(JobItemViewModel? clip) => Shell.Reveal(clip?.OutputPath);

    [RelayCommand]
    private void OpenRoot() => Shell.Reveal(main.Host.Settings.Settings.Paths.Highlights);

    [RelayCommand]
    private void Copy(JobItemViewModel? clip)
    {
        Shell.CopyFile(clip?.OutputPath);
        if (clip is not null) main.Notify(L.T("clips.copied"));
    }

    [RelayCommand]
    private async Task MontageAsync()
    {
        var selected = Clips.Where(c => c.IsSelected).Select(c => c.Job).ToList();
        if (selected.Count < 2)
        {
            main.Notify(L.T("clips.montageSelect"), true);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = L.T("clips.montageDialog"),
            Filter = L.T("clips.montageFilter"),
            FileName = L.F("clips.montageFile", DateTime.Now),
            InitialDirectory = main.Host.Settings.Settings.Paths.Highlights,
        };
        if (dialog.ShowDialog() != true) return;
        IsBusy = true;
        try
        {
            var path = await main.Host.Render.BuildMontageAsync(selected, dialog.FileName, null, CancellationToken.None);
            main.Notify(L.F("clips.montageDone", path));
            Shell.Reveal(path);
        }
        catch (Exception ex)
        {
            main.Notify(L.F("clips.montageFailed", ex.Message), true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
