using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2HG.Application;
using CS2HG.Core.Localization;
using CS2HG.Core.Models;

namespace CS2HG.UI.ViewModels;

public static class Ui
{
    public static void Post(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}

/// <summary>State shared by the pages: the open demo and its detected highlights.</summary>
public sealed partial class SessionState : ObservableObject
{
    [ObservableProperty] private OpenedDemo? _demo;
    [ObservableProperty] private ulong? _playerSteamId;

    public ObservableCollection<HighlightItemViewModel> Highlights { get; } = [];
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _notificationTimer;
    private (int Pid, bool Controllable)? _controllablePid;

    public MainViewModel(AppHost host)
    {
        Host = host;
        Session = new SessionState();
        Home = new HomeViewModel(this);
        Analysis = new AnalysisViewModel(this);
        Highlights = new HighlightsViewModel(this);
        Export = new ExportViewModel(this);
        Queue = new QueueViewModel(this);
        Library = new LibraryViewModel(this);
        Settings = new SettingsViewModel(this);
        Auto = new AutoViewModel(this);
        _currentPage = Home;
        _queueText = L.T("queue.statusEmpty");
        _cs2Text = L.T("status.checking");
        _ffmpegText = L.T("status.checking");

        host.Queue.QueueChanged += () => Ui.Post(RefreshQueueStatus);
        host.Queue.JobUpdated += _ => Ui.Post(RefreshQueueStatus);
        Localizer.Instance.LanguageChanged += () => Ui.Post(OnLanguageChanged);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    public AppHost Host { get; }
    public SessionState Session { get; }
    public HomeViewModel Home { get; }
    public AnalysisViewModel Analysis { get; }
    public HighlightsViewModel Highlights { get; }
    public ExportViewModel Export { get; }
    public QueueViewModel Queue { get; }
    public LibraryViewModel Library { get; }
    public SettingsViewModel Settings { get; }
    public AutoViewModel Auto { get; }

    /// <summary>Raised when the render queue becomes idle after finishing jobs: (completed, failed).</summary>
    public event Action<int, int>? QueueFinished;

    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private string _cpuText = "CPU —";
    [ObservableProperty] private string _ramText = "RAM —";
    [ObservableProperty] private string _queueText;
    [ObservableProperty] private double _queueProgress;
    [ObservableProperty] private bool _queueActive;
    [ObservableProperty] private string _cs2Text;
    [ObservableProperty] private string _ffmpegText;
    [ObservableProperty] private string? _notification;
    [ObservableProperty] private bool _notificationIsError;
    [ObservableProperty] private string? _updateText;

    private UpdateInfo? _update;
    private readonly HashSet<string> _runJobs = [];

    public string AppVersion => "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

    [RelayCommand]
    private void Navigate(string page)
    {
        CurrentPage = page switch
        {
            "Analysis" => Analysis,
            "Highlights" => Highlights,
            "Export" => Export,
            "Queue" => Queue,
            "Library" => Library,
            "Settings" => Settings,
            "Auto" => Auto,
            _ => Home,
        };
        if (CurrentPage == Home) Home.Refresh();
        if (CurrentPage == Library) Library.Refresh();
        if (CurrentPage == Export) Export.RefreshPlayers();
        if (CurrentPage == Auto) Auto.Refresh();
        OnPropertyChanged(nameof(CurrentPageKey));
    }

    public string CurrentPageKey => CurrentPage switch
    {
        AnalysisViewModel => "Analysis",
        HighlightsViewModel => "Highlights",
        ExportViewModel => "Export",
        QueueViewModel => "Queue",
        LibraryViewModel => "Library",
        SettingsViewModel => "Settings",
        AutoViewModel => "Auto",
        _ => "Home",
    };

    partial void OnCurrentPageChanged(ObservableObject? value) => OnPropertyChanged(nameof(CurrentPageKey));

    /// <summary>
    /// Texts bound with {l:Tr} switch by themselves; texts built in view models and combo box items
    /// are rebuilt by refreshing the data and re-creating the current page.
    /// </summary>
    private void OnLanguageChanged()
    {
        var key = CurrentPageKey;
        Home.Refresh();
        _ = Home.CheckEnvironmentCommand.ExecuteAsync(null);
        Analysis.RefreshTexts();
        Highlights.RefreshTexts();
        Library.Refresh();
        Auto.Refresh();
        if (_update is not null) UpdateText = L.F("update.available", _update.Version.ToString(3));
        RefreshQueueStatus();
        Tick();
        CurrentPage = null;
        Navigate(key);
    }

    public void Notify(string message, bool error = false)
    {
        Notification = message;
        NotificationIsError = error;
        _notificationTimer?.Stop();
        _notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(error ? 12 : 6) };
        _notificationTimer.Tick += (_, _) =>
        {
            Notification = null;
            _notificationTimer?.Stop();
        };
        _notificationTimer.Start();
    }

    [RelayCommand]
    private void DismissNotification() => Notification = null;

    public async Task OpenDemoAsync(string path)
    {
        Navigate("Analysis");
        await Analysis.LoadAsync(path);
    }

    /// <summary>Share code (CSGO-xxxxx-...) pasted by the user: download if needed, then analyse.</summary>
    public async Task OpenShareCodeAsync(string text)
    {
        Navigate("Analysis");
        await Analysis.LoadShareCodeAsync(text);
    }

    private void Tick()
    {
        try
        {
            var s = Host.Monitor.Read();
            CpuText = L.F("status.cpu", s.SystemCpu, s.ProcessCpu);
            RamText = L.F("status.ram", s.SystemMemoryUsedGb, s.SystemMemoryTotalGb, s.ProcessMemoryMb);
            var game = DemoPlayback.Cs2PlaybackController.FindRunningGame();
            if (game is null)
            {
                Cs2Text = L.T("status.cs2Closed");
            }
            else
            {
                if (_controllablePid?.Pid != game.Id)
                    _controllablePid = (game.Id, DemoPlayback.Cs2PlaybackController.IsControllable(game.Id));
                Cs2Text = L.T(Host.Queue.IsBusy ? "status.cs2Rendering" : _controllablePid.Value.Controllable ? "status.cs2Controllable" : "status.cs2User");
            }

            FfmpegText = L.T(Host.Media.IsAvailable ? "status.ffmpegOk" : "status.ffmpegMissing");
        }
        catch
        {
            // monitoring is best effort
        }
    }

    private void RefreshQueueStatus()
    {
        var jobs = Host.Queue.Jobs;
        var pending = jobs.Count(j => j.Status is RenderJobStatus.Pending or RenderJobStatus.Running);
        var running = jobs.FirstOrDefault(j => j.Status == RenderJobStatus.Running);
        QueueActive = running is not null;
        QueueProgress = Host.Queue.TotalProgress * 100;
        QueueText = running is not null ? L.F("queue.statusRunning", running.Title, running.Progress, pending)
            : Host.Queue.IsPaused ? L.F("queue.statusPaused", pending)
            : pending > 0 ? L.F("queue.statusPending", pending)
            : L.F("queue.statusDone", jobs.Count(j => j.Status == RenderJobStatus.Completed));
        Queue.Sync();
        TrackQueueRun(jobs, running is not null || (pending > 0 && !Host.Queue.IsPaused && _runJobs.Count > 0));
    }

    /// <summary>Remembers the jobs rendered since the queue started working and reports them once it is idle again.</summary>
    private void TrackQueueRun(IReadOnlyList<RenderJob> jobs, bool working)
    {
        foreach (var job in jobs.Where(j => j.Status == RenderJobStatus.Running)) _runJobs.Add(job.Id);
        if (working || _runJobs.Count == 0) return;

        var finished = jobs.Where(j => _runJobs.Contains(j.Id)).ToList();
        if (finished.Any(j => j.Status is RenderJobStatus.Pending or RenderJobStatus.Running)) return;
        var completed = finished.Count(j => j.Status == RenderJobStatus.Completed);
        var failed = finished.Count(j => j.Status == RenderJobStatus.Failed);
        _runJobs.Clear();
        if (completed + failed > 0) QueueFinished?.Invoke(completed, failed);
    }

    /// <summary>Asks GitHub for a newer release; <paramref name="manual"/> also reports "you are up to date".</summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        var update = await Host.Updates.CheckAsync(CancellationToken.None);
        _update = update;
        UpdateText = update is null ? null : L.F("update.available", update.Version.ToString(3));
        if (manual && update is null) Notify(L.F("update.latest", AppVersion));
    }

    [RelayCommand]
    private void OpenUpdate()
    {
        if (_update is not null) Shell.OpenUrl(_update.Url);
    }

    [RelayCommand]
    private void DismissUpdate() => UpdateText = null;

    public void Shutdown() => _timer.Stop();
}
