using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CS2HG.Core;
using CS2HG.Core.Localization;
using Microsoft.Win32;

namespace CS2HG.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        var s = main.Host.Settings.Settings;
        _language = Languages.FirstOrDefault(l => l.Code == Localizer.Normalize(s.Language)) ?? Languages[0];
        _rootFolder = s.Paths.RootFolder;
        _ffmpegPath = s.FfmpegPath ?? main.Host.Media.Paths?.Ffmpeg ?? "";
        _steamFolder = s.Cs2.SteamFolder ?? "";
        _gameFolder = s.Cs2.GameFolder ?? "";
        _captureWidth = s.Cs2.CaptureWidth;
        _captureHeight = s.Cs2.CaptureHeight;
        _captureFps = s.Cs2.CaptureFps;
        _gsiPort = s.Cs2.GsiPort;
        _closeGameWhenDone = s.Cs2.CloseGameWhenDone;
        _cleanupBinds = s.Cs2.CleanupBindsOnExit;
        _settleSeconds = s.Cs2.SettleSecondsAfterSeek;
        _quickKillGap = s.Detection.QuickKillMaxGapSeconds;
        _streakWindow = s.Detection.KillStreakWindowSeconds;
        _highDamage = s.Detection.HighDamageThreshold;
        _utilityDamage = s.Detection.UtilityDamageThreshold;
        _minimumScore = s.Detection.MinimumScore;
        _mergeGap = s.Detection.SegmentMergeGapSeconds;
    }

    /// <summary>"Automatic" (Windows language) plus every translated language.</summary>
    public IReadOnlyList<LanguageInfo> Languages { get; } =
        [new LanguageInfo("", "Automático / Auto"), .. Localizer.Languages];

    public string DetectorsHelp => AnalysisViewModel.DetectorsDescription();
    public string LogsFolder => _main.Host.Settings.Settings.Paths.Logs;
    public string Website => Brand.Website;
    public string GitHub => Brand.GitHubUrl;

    [ObservableProperty] private LanguageInfo _language;
    [ObservableProperty] private string _rootFolder;
    [ObservableProperty] private string _ffmpegPath;
    [ObservableProperty] private string _steamFolder;
    [ObservableProperty] private string _gameFolder;
    [ObservableProperty] private int _captureWidth;
    [ObservableProperty] private int _captureHeight;
    [ObservableProperty] private int _captureFps;
    [ObservableProperty] private int _gsiPort;
    [ObservableProperty] private bool _closeGameWhenDone;
    [ObservableProperty] private bool _cleanupBinds;
    [ObservableProperty] private double _settleSeconds;
    [ObservableProperty] private double _quickKillGap;
    [ObservableProperty] private double _streakWindow;
    [ObservableProperty] private int _highDamage;
    [ObservableProperty] private int _utilityDamage;
    [ObservableProperty] private int _minimumScore;
    [ObservableProperty] private double _mergeGap;
    [ObservableProperty] private bool _downloading;
    [ObservableProperty] private double _downloadProgress;

    /// <summary>Language applies immediately (and is saved), without waiting for "Save".</summary>
    partial void OnLanguageChanged(LanguageInfo value)
    {
        var store = _main.Host.Settings;
        store.Settings.Language = string.IsNullOrEmpty(value.Code) ? null : value.Code;
        store.Save();
        Localizer.Instance.SetLanguage(store.Settings.Language);
    }

    [RelayCommand]
    private void Save()
    {
        var store = _main.Host.Settings;
        var s = store.Settings;
        var rootChanged = !string.Equals(s.Paths.RootFolder, RootFolder, StringComparison.OrdinalIgnoreCase);
        s.Paths.RootFolder = RootFolder;
        s.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegPath) ? null : FfmpegPath;
        s.Cs2.SteamFolder = string.IsNullOrWhiteSpace(SteamFolder) ? null : SteamFolder;
        s.Cs2.GameFolder = string.IsNullOrWhiteSpace(GameFolder) ? null : GameFolder;
        s.Cs2.CaptureWidth = CaptureWidth;
        s.Cs2.CaptureHeight = CaptureHeight;
        s.Cs2.CaptureFps = CaptureFps;
        s.Cs2.GsiPort = GsiPort;
        s.Cs2.CloseGameWhenDone = CloseGameWhenDone;
        s.Cs2.CleanupBindsOnExit = CleanupBinds;
        s.Cs2.SettleSecondsAfterSeek = SettleSeconds;
        s.Detection.QuickKillMaxGapSeconds = QuickKillGap;
        s.Detection.KillStreakWindowSeconds = StreakWindow;
        s.Detection.HighDamageThreshold = HighDamage;
        s.Detection.UtilityDamageThreshold = UtilityDamage;
        s.Detection.MinimumScore = MinimumScore;
        s.Detection.SegmentMergeGapSeconds = MergeGap;
        store.Save();
        _main.Host.Media.Refresh();
        _main.Notify(L.T(rootChanged ? "settings.savedRestart" : "settings.saved"));
    }

    [RelayCommand]
    private void BrowseRoot()
    {
        var d = new OpenFolderDialog { Title = L.F("settings.rootDialog", Brand.Name), InitialDirectory = RootFolder };
        if (d.ShowDialog() == true) RootFolder = d.FolderName;
    }

    [RelayCommand]
    private void BrowseFfmpeg()
    {
        var d = new OpenFileDialog { Title = L.T("settings.ffmpegDialog"), Filter = "ffmpeg.exe|ffmpeg.exe" };
        if (d.ShowDialog() == true) FfmpegPath = d.FileName;
    }

    [RelayCommand]
    private async Task DownloadFfmpegAsync()
    {
        Downloading = true;
        try
        {
            FfmpegPath = await _main.Host.Media.InstallAsync(new Progress<double>(p => DownloadProgress = p * 100), CancellationToken.None);
            _main.Notify(L.F("settings.ffmpegInstalled", FfmpegPath));
        }
        catch (Exception ex)
        {
            _main.Notify(L.F("settings.ffmpegFailed", ex.Message), true);
        }
        finally
        {
            Downloading = false;
        }
    }

    [RelayCommand]
    private void RestoreCs2Config() =>
        _main.Notify(L.T(_main.Host.Playback.RecoverPendingRestore() ? "settings.restored" : "settings.nothingToRestore"));

    [RelayCommand]
    private void OpenLogs() => Shell.Reveal(LogsFolder);

    [RelayCommand]
    private void OpenRoot() => Process.Start("explorer.exe", $"\"{RootFolder}\"");

    [RelayCommand]
    private void OpenWebsite() => Shell.OpenUrl(Brand.Website);

    [RelayCommand]
    private void OpenGitHub() => Shell.OpenUrl(Brand.GitHubUrl);
}
