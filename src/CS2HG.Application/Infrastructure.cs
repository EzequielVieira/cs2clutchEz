using System.Runtime.InteropServices;
using System.Text.Json;
using CS2HG.Core.Localization;
using CS2HG.Core.Settings;
using CS2HG.Core.Utilities;
using CS2HG.VideoCapture;
using CS2HG.VideoProcessing;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace CS2HG.Application;

/// <summary>
/// Loads and saves settings.json in %APPDATA%\cs2clutchEz (migrating the pre-rename folder once).
/// When CS2CLUTCHEZ_HOME is set, settings and (by default) all data live in that folder instead — an isolated
/// profile for development and tests that never touches the regular installation.
/// </summary>
public sealed class SettingsStore
{
    public const string HomeVariable = "CS2CLUTCHEZ_HOME";

    private static readonly string? IsolatedHome = Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home ? Path.GetFullPath(home) : null;

    public static readonly string AppDataFolder = IsolatedHome ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Core.Brand.FolderName);

    /// <summary>Folder used before the product was renamed; its settings and CS2 config backup are carried over.</summary>
    private static readonly string LegacyAppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CS2DemoHighlightGenerator");

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        if (path is null && IsolatedHome is null) MigrateLegacyFolder();
        _path = path ?? Path.Combine(AppDataFolder, "settings.json");
        var fresh = !File.Exists(_path);
        Settings = Load();
        if (path is null && IsolatedHome is not null && fresh) Settings.Paths.RootFolder = Path.Combine(IsolatedHome, "data");
    }

    public AppSettings Settings { get; private set; }

    private static void MigrateLegacyFolder()
    {
        try
        {
            if (File.Exists(Path.Combine(AppDataFolder, "settings.json")) || !Directory.Exists(LegacyAppDataFolder)) return;
            Directory.CreateDirectory(AppDataFolder);
            // Old settings keep pointing to the old root folder, so existing clips, database and queue stay available.
            foreach (var name in new[] { "settings.json" })
            {
                var src = Path.Combine(LegacyAppDataFolder, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(AppDataFolder, name), false);
            }

            var backup = Path.Combine(LegacyAppDataFolder, "cs2-config-backup");
            if (Directory.Exists(backup))
            {
                var target = Path.Combine(AppDataFolder, "cs2-config-backup");
                Directory.CreateDirectory(target);
                foreach (var file in Directory.EnumerateFiles(backup)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
        }
        catch
        {
            // migration is best effort; defaults are used otherwise
        }
    }

    public event Action<AppSettings>? Saved;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json.Indented);
                if (loaded is not null)
                {
                    // New score kinds added in later versions get their defaults.
                    foreach (var (kind, value) in ScoreSettings.Defaults())
                        loaded.Scores.Points.TryAdd(kind, value);
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // corrupted settings: start with defaults, keep a copy for inspection
            try { File.Copy(_path, _path + ".bad", true); } catch { /* ignore */ }
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Settings, Json.Indented));
        File.Move(tmp, _path, true);
        Saved?.Invoke(Settings);
    }

    public void AddRecentDemo(string path)
    {
        Settings.RecentDemos.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Settings.RecentDemos.Insert(0, path);
        if (Settings.RecentDemos.Count > 15) Settings.RecentDemos.RemoveRange(15, Settings.RecentDemos.Count - 15);
        Save();
    }
}

public static class LoggingSetup
{
    public static ILoggerFactory Create(string logsFolder, bool console = false)
    {
        Directory.CreateDirectory(logsFolder);
        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(logsFolder, "cs2clutchez-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        Log.Logger = config.CreateLogger();
        var factory = new SerilogLoggerFactory(Log.Logger, dispose: true);
        return factory;
    }
}

/// <summary>
/// Lazily resolves FFmpeg. The app starts without it; it can be auto-installed later
/// (Settings → "Baixar FFmpeg") and every FFmpeg-based service is rebuilt.
/// </summary>
public sealed class MediaToolkit(SettingsStore settings, ILoggerFactory loggerFactory)
{
    public static readonly string UserToolsFolder = Path.Combine(SettingsStore.AppDataFolder, "tools", "ffmpeg");

    private readonly Lock _gate = new();
    private FfmpegPaths? _paths;
    private FfmpegRunner? _runner;
    private EncoderCatalog? _encoders;
    private FfmpegVideoProcessor? _processor;
    private WindowCaptureRecorder? _recorder;

    public FfmpegPaths? Paths
    {
        get
        {
            lock (_gate)
            {
                _paths ??= FfmpegLocator.Find(settings.Settings.FfmpegPath, UserToolsFolder);
                return _paths;
            }
        }
    }

    public bool IsAvailable => Paths is not null;

    public void Refresh()
    {
        lock (_gate)
        {
            _paths = null;
            _runner = null;
            _encoders = null;
            _processor = null;
            _recorder = null;
        }
    }

    private FfmpegPaths Require() =>
        Paths ?? throw new FfmpegNotFoundException(L.T("error.ffmpegNotFound"));

    public FfmpegRunner Runner
    {
        get
        {
            var p = Require();
            lock (_gate) return _runner ??= new FfmpegRunner(p, loggerFactory.CreateLogger<FfmpegRunner>());
        }
    }

    public EncoderCatalog Encoders
    {
        get
        {
            var r = Runner;
            lock (_gate) return _encoders ??= new EncoderCatalog(r, loggerFactory.CreateLogger<EncoderCatalog>());
        }
    }

    public FfmpegVideoProcessor Processor
    {
        get
        {
            var r = Runner;
            var e = Encoders;
            lock (_gate) return _processor ??= new FfmpegVideoProcessor(r, e, new EffectRegistry(), loggerFactory.CreateLogger<FfmpegVideoProcessor>());
        }
    }

    public WindowCaptureRecorder Recorder
    {
        get
        {
            var p = Require();
            lock (_gate) return _recorder ??= new WindowCaptureRecorder(p, loggerFactory);
        }
    }

    public async Task<string> InstallAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var paths = await FfmpegInstaller.InstallAsync(UserToolsFolder, progress, ct);
        settings.Settings.FfmpegPath = paths.Ffmpeg;
        settings.Save();
        Refresh();
        return paths.Ffmpeg;
    }
}

/// <summary>CPU and memory usage of the system and of this process.</summary>
public sealed partial class SystemMonitor
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low;
        public uint High;

        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    private ulong _lastIdle, _lastKernel, _lastUser;
    private TimeSpan _lastProcCpu;
    private DateTime _lastSample = DateTime.MinValue;

    public sealed record Sample(double SystemCpu, double ProcessCpu, double SystemMemoryUsedGb, double SystemMemoryTotalGb, double ProcessMemoryMb);

    public Sample Read()
    {
        GetSystemTimes(out var idle, out var kernel, out var user);
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        double sysCpu = 0, procCpu = 0;
        if (_lastSample != DateTime.MinValue)
        {
            var idleD = idle.Value - _lastIdle;
            var total = kernel.Value - _lastKernel + (user.Value - _lastUser);
            if (total > 0) sysCpu = 100.0 * (total - idleD) / total;
            var wall = (now - _lastSample).TotalMilliseconds;
            if (wall > 0) procCpu = 100.0 * (proc.TotalProcessorTime - _lastProcCpu).TotalMilliseconds / (wall * Environment.ProcessorCount);
        }

        _lastIdle = idle.Value;
        _lastKernel = kernel.Value;
        _lastUser = user.Value;
        _lastProcCpu = proc.TotalProcessorTime;
        _lastSample = now;

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        const double gb = 1024.0 * 1024 * 1024;
        return new Sample(Math.Clamp(sysCpu, 0, 100), Math.Clamp(procCpu, 0, 100), (mem.ullTotalPhys - mem.ullAvailPhys) / gb,
            mem.ullTotalPhys / gb, proc.WorkingSet64 / (1024.0 * 1024));
    }
}
