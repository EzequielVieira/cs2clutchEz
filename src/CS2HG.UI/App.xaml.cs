using System.IO;
using System.Windows;
using System.Windows.Threading;
using CS2HG.Application;
using CS2HG.Core.Localization;
using CS2HG.UI.ViewModels;
using Microsoft.Extensions.Logging;

namespace CS2HG.UI;

public partial class App
{
    private AppHost? _host;
    private MainViewModel? _main;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogFatal(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogFatal(args.Exception);
            args.SetObserved();
        };

        try
        {
            _host = await AppHost.CreateAsync();
            Localizer.Instance.SetLanguage(_host.Settings.Settings.Language);
            _main = new MainViewModel(_host);
            var window = new MainWindow { DataContext = _main };
            MainWindow = window;
            window.Show();

            _main.Home.Refresh();
            _main.Queue.Sync();
            _ = _main.Home.CheckEnvironmentCommand.ExecuteAsync(null);

            // Jobs interrupted by a crash/close are resumed only on user request (the game needs focus).
            if (_host.Queue.Jobs.Any(j => j.Status == Core.Models.RenderJobStatus.Pending))
                _main.Notify(L.T("app.pendingJobs"));

            // A demo path, a share code, or a cs2clutchez://CSGO-... link (website button) can be passed on the command line.
            // Wait until the window is loaded and idle, so navigation is not undone by the initial binding pass.
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var demoArg = e.Args.FirstOrDefault(a => File.Exists(a));
            var codeArg = e.Args.Select(Core.Models.ShareCode.Find).FirstOrDefault(c => c is not null);
            if (demoArg is not null) await _main.OpenDemoAsync(demoArg);
            else if (codeArg is not null)
            {
                _main.Home.ShareCodeText = codeArg;
                await _main.OpenShareCodeAsync(codeArg);
            }
        }
        catch (Exception ex)
        {
            LogFatal(ex);
            MessageBox.Show(L.F("app.startFailed", Core.Brand.Name, ex.Message), L.T("app.error"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogFatal(e.Exception);
        _main?.Notify(L.F("app.unexpectedError", e.Exception.Message), true);
        e.Handled = true;
    }

    private void LogFatal(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            _host?.LoggerFactory.CreateLogger("App").LogError(ex, "Unhandled exception");
        }
        catch
        {
            // logging must never crash the app
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _main?.Shutdown();
        if (_host is not null)
        {
            try
            {
                if (_host.Queue.IsBusy) _host.Queue.Pause();
                await _host.DisposeAsync();
            }
            catch
            {
                // best effort on shutdown
            }
        }

        base.OnExit(e);
    }
}
