using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using CS2HG.Core.Abstractions;
using CS2HG.Core.Localization;
using CS2HG.Core.Settings;
using CS2HG.Core.Utilities;
using Microsoft.Extensions.Logging;

namespace CS2HG.DemoPlayback;

public sealed class Cs2ControlException(string message) : Exception(message);

/// <summary>CS2 is running but was not started by this app, so it cannot be controlled.</summary>
public sealed class Cs2NotControllableException(string message) : Exception(message);

/// <summary>
/// Controls CS2 demo playback without injecting anything into the game:
/// <list type="bullet">
/// <item>commands are written to cfg/cs2hg/cmd.cfg and executed by pressing a key bound to "exec cs2hg/cmd"
/// (binds do not fire in the main menu, so the first "playdemo" is typed into the developer console);</item>
/// <item>feedback comes from console.log (-condebug): cfg execs, demo loads and seeks are confirmed there;</item>
/// <item>the spectated player is confirmed through Game State Integration.</item>
/// </list>
/// Requires CS2 in the foreground while commands are sent (SendInput).
/// </summary>
public sealed partial class Cs2PlaybackController(Cs2Settings settings, string backupFolder, ILogger<Cs2PlaybackController> logger)
    : IDemoPlaybackController
{
    private const int SpecModeFirstPerson = 1;
    private const int SpecModeThirdPerson = 3;
    private const int SpecModeDirector = 5;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Cs2Installation? _install;
    private Cs2ConfigManager? _config;
    private ConsoleLogWatcher? _log;
    private GsiListener? _gsi;
    private Process? _game;
    private IntPtr _window;
    private bool _demoLoaded;
    private string? _loadedDemo;
    private bool _demoUiHidden;
    private string _status = L.T("cs2.notStarted");

    public PlaybackState State => new(_game is { HasExited: false }, _log is not null && _window != IntPtr.Zero, _loadedDemo, _status);

    public IntPtr GameWindow => _window;

    public int GameProcessId => _game is { HasExited: false } g ? g.Id : 0;

    public Cs2Installation Installation => _install ??= SteamLocator.Locate(settings.SteamFolder, settings.GameFolder);

    private Cs2ConfigManager Config => _config ??= new Cs2ConfigManager(Installation, backupFolder, settings.GsiPort, logger);

    public static Process? FindRunningGame() => Process.GetProcessesByName("cs2").FirstOrDefault();

    public static string? GetCommandLine(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (var o in searcher.Get()) return o["CommandLine"] as string;
        }
        catch
        {
            // WMI unavailable: treat as unknown
        }

        return null;
    }

    public static bool IsControllable(int processId)
    {
        var cmd = GetCommandLine(processId);
        return cmd is not null && cmd.Contains("cs2hg/boot", StringComparison.OrdinalIgnoreCase) &&
               cmd.Contains("-condebug", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Restores the user's CS2 config if a previous session ended abnormally and the game is closed.</summary>
    public bool RecoverPendingRestore()
    {
        try
        {
            if (FindRunningGame() is not null) return false;
            return Config.RestoreUserConfig();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restore CS2 config backup");
            return false;
        }
    }

    public async Task EnsureSessionAsync(IProgress<string>? status, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_game is { HasExited: false } && _window != IntPtr.Zero && Native.IsWindow(_window) && _log is not null) return;

            Config.InstallHelperFiles();
            StartGsi();

            var running = FindRunningGame();
            if (running is not null)
            {
                if (!IsControllable(running.Id))
                {
                    throw new Cs2NotControllableException(L.F("cs2.notControllable", Core.Brand.Name));
                }

                Report(status, L.T("cs2.connecting"));
                _game = running;
                _log ??= new ConsoleLogWatcher(Installation.ConsoleLog, fromEnd: true);
                _window = await WaitForWindowAsync(running, TimeSpan.FromSeconds(30), cancellationToken);
                _demoLoaded = true; // unknown: assume a demo may be loaded; LoadDemo falls back to the console if needed
                _demoUiHidden = false;
                Report(status, L.T("cs2.connected"));
                return;
            }

            if (Config.HasPendingRestore) Config.RestoreUserConfig();
            Config.BackupUserConfig();
            Config.ApplyCaptureVideoSettings(settings.CaptureWidth, settings.CaptureHeight);

            if (_log is not null) await _log.DisposeAsync();
            _log = new ConsoleLogWatcher(Installation.ConsoleLog, fromEnd: true);
            var bootMark = _log.Mark;

            var args = $"-applaunch 730 -condebug +exec cs2hg/boot {settings.ExtraLaunchOptions}".Trim();
            Report(status, L.T("cs2.launching"));
            logger.LogInformation("Launching CS2: {Steam} {Args}", Installation.SteamExe, args);
            Process.Start(new ProcessStartInfo(Installation.SteamExe, args) { UseShellExecute = false });

            var sw = Stopwatch.StartNew();
            Process? game = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(120) && (game = FindRunningGame()) is null)
                await Task.Delay(500, cancellationToken);
            _game = game ?? throw new Cs2ControlException(L.T("cs2.launchTimeout"));

            Report(status, L.T("cs2.waitingWindow"));
            _window = await WaitForWindowAsync(_game, TimeSpan.FromSeconds(90), cancellationToken);

            Report(status, L.T("cs2.waitingMenu"));
            var boot = await _log.WaitForAsync(BootRegex(), bootMark, TimeSpan.FromSeconds(180), cancellationToken);
            if (boot is null) throw new Cs2ControlException(L.T("cs2.bootNotRun"));

            // The main menu keeps loading for a few seconds after the boot cfg runs.
            await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            _demoLoaded = false;
            _loadedDemo = null;
            _demoUiHidden = false;
            _status = "CS2 pronto";
            Report(status, L.T("cs2.ready"));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes a CS2 instance the app cannot control (after the user agreed) and starts a controlled one.</summary>
    public async Task RestartGameAsync(IProgress<string>? status, CancellationToken cancellationToken)
    {
        var running = FindRunningGame();
        if (running is not null)
        {
            Report(status, L.T("cs2.closing"));
            running.CloseMainWindow();
            if (!running.WaitForExit(20000)) running.Kill();
            await Task.Delay(3000, cancellationToken);
        }

        _game = null;
        _window = IntPtr.Zero;
        await EnsureSessionAsync(status, cancellationToken);
    }

    private void StartGsi()
    {
        if (_gsi is { IsRunning: true }) return;
        try
        {
            _gsi = new GsiListener(settings.GsiPort, logger);
            _gsi.Start();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GSI listener could not start on port {Port}; POV verification disabled", settings.GsiPort);
            _gsi = null;
        }
    }

    private static async Task<IntPtr> WaitForWindowAsync(Process game, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            game.Refresh();
            if (game.HasExited) throw new Cs2ControlException(L.T("cs2.closedDuringStart"));
            if (game.MainWindowHandle != IntPtr.Zero) return game.MainWindowHandle;
            await Task.Delay(500, ct);
        }

        throw new Cs2ControlException(L.T("cs2.noWindow"));
    }

    private void EnsureFocus()
    {
        if (_game is null || _game.HasExited) throw new Cs2ControlException(L.T("cs2.closed"));
        _game.Refresh();
        if (_game.MainWindowHandle != IntPtr.Zero) _window = _game.MainWindowHandle;
        if (!InputInjector.Focus(_window))
            throw new Cs2ControlException(L.T("cs2.focusFailed"));
    }

    /// <summary>Executes commands through the bound key and waits for console.log to confirm the exec.</summary>
    private async Task<long> ExecAsync(IReadOnlyList<string> commands, CancellationToken ct, bool confirm = true)
    {
        var log = _log ?? throw new Cs2ControlException(L.T("cs2.noSession"));
        Config.WriteCommands(commands);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var mark = log.Mark;
            EnsureFocus();
            InputInjector.TapScanCode(Cs2ConfigManager.CommandKeyScanCode);
            var sent = Stopwatch.GetTimestamp();
            if (!confirm) return sent;
            if (await log.WaitForAsync(ExecRegex(), mark, TimeSpan.FromSeconds(2.5), ct) is not null)
            {
                logger.LogDebug("CS2 exec: {Commands}", string.Join("; ", commands));
                return sent;
            }

            logger.LogWarning("CS2 did not confirm exec (attempt {Attempt}): {Commands}", attempt, string.Join("; ", commands));
            await Task.Delay(300, ct);
        }

        throw new Cs2ControlException(L.T("cs2.noResponse"));
    }

    /// <summary>Types one command into the developer console (used in the main menu, where binds don't fire).</summary>
    private async Task ConsoleCommandAsync(string command, CancellationToken ct)
    {
        EnsureFocus();
        InputInjector.TapScanCode(Cs2ConfigManager.ConsoleKeyScanCode);
        await Task.Delay(400, ct);
        InputInjector.TypeText(command);
        await Task.Delay(80, ct);
        InputInjector.PressEnter();
        await Task.Delay(250, ct);
        InputInjector.TapScanCode(Cs2ConfigManager.ConsoleKeyScanCode);
        await Task.Delay(250, ct);
    }

    public async Task LoadDemoAsync(string demoPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var log = _log ?? throw new Cs2ControlException(L.T("cs2.noSession"));
            var slug = FileNames.AsciiSlug(Path.GetFileNameWithoutExtension(demoPath), 40) + "_" + Hashing.Sha1Hex(Path.GetFullPath(demoPath))[..8];
            var staged = Config.StageDemo(demoPath, slug);
            if (_demoLoaded && string.Equals(_loadedDemo, staged, StringComparison.OrdinalIgnoreCase)) return;

            var command = $"playdemo {Config.RelativeDemoPath(staged)}";
            var loaded = false;
            for (var attempt = 1; attempt <= 4 && !loaded; attempt++)
            {
                var mark = log.Mark;
                if (_demoLoaded && attempt <= 2)
                {
                    try
                    {
                        await ExecAsync([command], cancellationToken);
                    }
                    catch (Cs2ControlException)
                    {
                        _demoLoaded = false; // binds not firing: we are probably in the menu
                        continue;
                    }
                }
                else
                {
                    // Console toggling is stateless from our side; a wrong guess is fixed by the next attempt.
                    await ConsoleCommandAsync(command, cancellationToken);
                }

                loaded = await log.WaitForAsync(DemoLoadedRegex(), mark, TimeSpan.FromSeconds(attempt == 1 ? 30 : 20), cancellationToken) is not null;
                if (!loaded) logger.LogWarning("Demo load not confirmed (attempt {Attempt})", attempt);
            }

            if (!loaded) throw new Cs2ControlException(L.T("cs2.demoNotLoaded"));

            await Task.Delay(TimeSpan.FromSeconds(2.5), cancellationToken);
            _demoLoaded = true;
            _loadedDemo = staged;

            var setup = new List<string> { "demo_pause", "spec_show_xray 0", "r_show_build_info 0" };
            if (!_demoUiHidden)
            {
                setup.Add("demoui");
                _demoUiHidden = true;
            }

            await ExecAsync(setup, cancellationToken);
            logger.LogInformation("Demo loaded in CS2: {Demo}", staged);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SeekAsync(int demoTick, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var log = _log ?? throw new Cs2ControlException(L.T("cs2.noSession"));
            var mark = log.Mark;
            await ExecAsync([$"demo_gototick {demoTick}", "demo_pause"], cancellationToken);
            var done = await log.WaitForAsync(SeekDoneRegex(), mark, TimeSpan.FromSeconds(20), cancellationToken);
            if (done is null)
                logger.LogWarning("Seek to tick {Tick} not confirmed by console.log", demoTick);
            else if (int.TryParse(done.Groups[1].Value, out var reached) && Math.Abs(reached - demoTick) > 2)
                logger.LogWarning("Seek requested {Tick} but CS2 reported {Reached}", demoTick, reached);

            // Texture streaming / entity interpolation settle while paused.
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.3, settings.SettleSecondsAfterSeek)), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(bool Verified, string? Observed)> SetPovAsync(PovTarget target, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (target.Mode == PovMode.DirectorCamera)
            {
                await ExecAsync([$"spec_mode {SpecModeDirector}"], cancellationToken);
                return (true, "director");
            }

            var commands = new List<string>();
            var name = target.Name;
            if (!string.IsNullOrEmpty(name) && !name.Contains('"') && !name.Contains(';') && !name.Contains('\n'))
                commands.Add($"spec_player \"{name}\"");
            commands.Add($"spec_mode {(target.Mode == PovMode.ThirdPerson ? SpecModeThirdPerson : SpecModeFirstPerson)}");
            await ExecAsync(commands, cancellationToken);

            if (_gsi is null || target.SteamId is not { } steamId) return (false, null);
            if (await _gsi.WaitForObservedAsync(steamId, TimeSpan.FromSeconds(1.5), cancellationToken))
                return (true, _gsi.Latest?.ObservedName);

            // Name lookup failed (special characters, duplicates): cycle through players until GSI confirms.
            for (var i = 0; i < 12; i++)
            {
                await ExecAsync(["spec_next"], cancellationToken);
                if (await _gsi.WaitForObservedAsync(steamId, TimeSpan.FromSeconds(0.7), cancellationToken))
                    return (true, _gsi.Latest?.ObservedName);
            }

            logger.LogWarning("Could not confirm POV {Name} ({SteamId}); observing {Observed}", name, steamId, _gsi.Latest?.ObservedName);
            return (false, _gsi.Latest?.ObservedName);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyHudAsync(HudMode mode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string[] commands = mode switch
            {
                HudMode.KillFeedOnly => ["cl_drawhud 1", "cl_draw_only_deathnotices 1"],
                HudMode.None => ["cl_draw_only_deathnotices 0", "cl_drawhud 0"],
                _ => ["cl_drawhud 1", "cl_draw_only_deathnotices 0"],
            };
            await ExecAsync(commands, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> ResumeAsync(CancellationToken cancellationToken) => await TimedExecAsync("demo_resume", cancellationToken);

    public async Task<long> PauseAsync(CancellationToken cancellationToken) => await TimedExecAsync("demo_pause", cancellationToken);

    private async Task<long> TimedExecAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ExecAsync([command], ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task EndSessionAsync(bool closeGame, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_game is { HasExited: false } && _log is not null)
            {
                var commands = new List<string> { "cl_drawhud 1", "cl_draw_only_deathnotices 0", "spec_show_xray 1", "r_show_build_info 1" };
                if (closeGame || settings.CleanupBindsOnExit) commands.Add($"unbind {Cs2ConfigManager.CommandKeyName}");
                if (closeGame) commands.Add("quit");
                try
                {
                    await ExecAsync(commands, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not send cleanup commands to CS2");
                }

                if (closeGame)
                {
                    if (!_game.WaitForExit(30000))
                    {
                        logger.LogWarning("CS2 did not quit; killing process");
                        _game.Kill();
                    }

                    await Task.Delay(1500, cancellationToken);
                }
            }

            if (closeGame || _game is null || _game.HasExited)
            {
                Config.RestoreUserConfig();
                Config.CleanStagedDemos();
                _game = null;
                _window = IntPtr.Zero;
                _demoLoaded = false;
                _loadedDemo = null;
                _demoUiHidden = false;
                _status = "CS2 fechado";
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Report(IProgress<string>? status, string message) => status?.Report(message);

    public async ValueTask DisposeAsync()
    {
        var log = Interlocked.Exchange(ref _log, null);
        if (log is not null) await log.DisposeAsync();
        var gsi = Interlocked.Exchange(ref _gsi, null);
        if (gsi is not null) await gsi.DisposeAsync();
    }

    [GeneratedRegex(@"execing cs2hg/boot")]
    private static partial Regex BootRegex();

    [GeneratedRegex(@"execing cs2hg/cmd")]
    private static partial Regex ExecRegex();

    [GeneratedRegex(@"Signon traffic ""DEMO""|\[Demo\] Demo Skipping finished")]
    private static partial Regex DemoLoadedRegex();

    [GeneratedRegex(@"Demo Skipping finished at tick (\d+)")]
    private static partial Regex SeekDoneRegex();
}
