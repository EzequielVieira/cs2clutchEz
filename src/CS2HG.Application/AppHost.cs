using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.DemoParser;
using CS2HG.DemoPlayback;
using CS2HG.HighlightDetection;
using CS2HG.RenderingQueue;
using CS2HG.Storage;
using CS2HG.VideoCapture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CS2HG.Application;

public sealed record EnvironmentStatus(
    bool Cs2Found,
    string Cs2Message,
    bool Cs2Running,
    bool Cs2Controllable,
    bool FfmpegFound,
    string FfmpegMessage,
    bool WindowCaptureSupported,
    IReadOnlyDictionary<string, bool> Encoders);

/// <summary>Composition root shared by the WPF app, the CLI and the integration tests.</summary>
public sealed class AppHost : IAsyncDisposable
{
    private AppHost(ServiceProvider services)
    {
        Services = services;
    }

    public ServiceProvider Services { get; }

    public SettingsStore Settings => Services.GetRequiredService<SettingsStore>();
    public AnalysisService Analysis => Services.GetRequiredService<AnalysisService>();
    public RenderService Render => Services.GetRequiredService<RenderService>();
    public ShareCodeService ShareCodes => Services.GetRequiredService<ShareCodeService>();
    public RenderQueue Queue => Services.GetRequiredService<RenderQueue>();
    public MediaToolkit Media => Services.GetRequiredService<MediaToolkit>();
    public Cs2PlaybackController Playback => Services.GetRequiredService<Cs2PlaybackController>();
    public SystemMonitor Monitor => Services.GetRequiredService<SystemMonitor>();
    public ILoggerFactory LoggerFactory => Services.GetRequiredService<ILoggerFactory>();

    public static async Task<AppHost> CreateAsync(SettingsStore? settingsStore = null)
    {
        var store = settingsStore ?? new SettingsStore();
        var paths = store.Settings.Paths;
        foreach (var dir in paths.All) Directory.CreateDirectory(dir);
        var loggerFactory = LoggingSetup.Create(paths.Logs);

        var services = new ServiceCollection();
        services.AddSingleton(store);
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(new AppDatabase(paths.Database));
        services.AddSingleton<DemoRepository>();
        services.AddSingleton<AnalysisRepository>();
        services.AddSingleton<HighlightRepository>();
        services.AddSingleton<IRenderJobRepository, RenderJobRepository>();
        services.AddSingleton(new MatchCache(paths.Cache));
        services.AddSingleton<IDemoImporter>(sp => new DemoImporter(paths.Demos, sp.GetRequiredService<ILogger<DemoImporter>>()));
        services.AddSingleton<IDemoParser, DemoFileNetParser>();
        services.AddSingleton<IHighlightAnalyzer>(_ => new HighlightAnalyzer(HighlightAnalyzer.DefaultDetectors()));
        services.AddSingleton<MediaToolkit>();
        services.AddSingleton(sp => new Cs2PlaybackController(store.Settings.Cs2,
            Path.Combine(SettingsStore.AppDataFolder, "cs2-config-backup"), sp.GetRequiredService<ILogger<Cs2PlaybackController>>()));
        services.AddSingleton<IDemoPlaybackController>(sp => sp.GetRequiredService<Cs2PlaybackController>());
        services.AddSingleton<IRenderPipeline, HighlightRenderPipeline>();
        services.AddSingleton<RenderQueue>();
        services.AddSingleton<AnalysisService>();
        services.AddSingleton<RenderService>();
        services.AddSingleton<ShareCodeService>();
        services.AddSingleton<SystemMonitor>();

        var provider = services.BuildServiceProvider();
        var host = new AppHost(provider);

        // Crash recovery: restore the user's CS2 config if a previous session could not, and reload the queue.
        host.Playback.RecoverPendingRestore();
        await host.Queue.InitializeAsync();
        loggerFactory.CreateLogger<AppHost>().LogInformation("{App} started (root {Root})", Core.Brand.Name, paths.RootFolder);
        return host;
    }

    public async Task<EnvironmentStatus> CheckEnvironmentAsync(CancellationToken ct)
    {
        bool cs2Found;
        string cs2Message;
        try
        {
            var install = Playback.Installation;
            cs2Found = true;
            cs2Message = install.GameFolder;
        }
        catch (Exception ex)
        {
            cs2Found = false;
            cs2Message = ex.Message;
        }

        var running = Cs2PlaybackController.FindRunningGame();
        var controllable = running is not null && Cs2PlaybackController.IsControllable(running.Id);

        var ffmpegFound = Media.IsAvailable;
        var ffMessage = ffmpegFound ? Media.Paths!.Ffmpeg : L.T("error.ffmpegMissingShort");
        var wgc = false;
        IReadOnlyDictionary<string, bool> encoders = new Dictionary<string, bool>();
        if (ffmpegFound)
        {
            wgc = await WindowCaptureRecorder.IsSupportedAsync(Media.Paths!, ct);
            try
            {
                encoders = await Media.Encoders.ProbeAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ffMessage = ex.Message;
            }
        }

        return new EnvironmentStatus(cs2Found, cs2Message, running is not null, controllable, ffmpegFound, ffMessage, wgc, encoders);
    }

    public async ValueTask DisposeAsync()
    {
        await Queue.DisposeAsync();
        await Playback.DisposeAsync();
        await Services.DisposeAsync();
        Serilog.Log.CloseAndFlush();
    }
}
