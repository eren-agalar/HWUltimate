using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NovaHardware.App.Services;
using NovaHardware.App.ViewModels;
using NovaHardware.App.Views;
using NovaHardware.Core.Abstractions;
using NovaHardware.Infrastructure;
using NovaHardware.UI.Controls;
using NovaHardware.UI.Localization;
using NovaHardware.UI.Themes;
using Serilog;

namespace NovaHardware.App;

public partial class App : Application
{
    private const string MutexName = "Global\\NovaHardware.SingleInstance";
    private Mutex? _singleInstance;
    private ServiceProvider? _services;

    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Services not built.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = new AppPaths();
        var settings = await Bootstrapper.LoadSettingsAsync(paths);

        // Theme first, so even the "already running" dialog is styled.
        ThemeManager.Apply(settings.Appearance);
        SystemTheme.StartWatching();
        Loc.Instance.Apply(settings.General.Language);

        _singleInstance = new Mutex(true, MutexName, out var created);
        if (!created && !e.Args.Contains("--allow-multiple"))
        {
            NovaDialog.Show(null, "Nova Hardware", Loc.Get("App_AlreadyRunning"));
            Shutdown();
            return;
        }

        Log.Logger = Logging.CreateLogger(paths, settings.General.VerboseLogging || e.Args.Contains("--verbose"));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error(args.Exception, "Unobserved task exception"); args.SetObserved(); };

        _services = Bootstrapper.Build(paths, settings);
        _services.GetRequiredService<ILogger<App>>().LogInformation("Nova Hardware {Version} starting (admin: {Admin})", Bootstrapper.Version, Hardware.Elevation.IsAdministrator());

        var main = _services.GetRequiredService<MainWindow>();
        MainWindow = main;
        if (settings.General.StartMinimized || e.Args.Contains("--minimized")) main.WindowState = WindowState.Minimized;
        main.Show();

        await _services.GetRequiredService<MainViewModel>().InitializeAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI exception");
        NovaDialog.Show(MainWindow, "Nova Hardware", $"{Loc.Get("App_UnexpectedError")}\n\n{e.Exception.Message}");
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services is not null)
            {
                // Stop the shared polling loop and release the hardware providers.
                var polling = _services.GetRequiredService<ISensorPollingService>();
                if (!Task.Run(async () => await polling.DisposeAsync()).Wait(TimeSpan.FromSeconds(5)))
                {
                    Log.Warning("Sensor providers did not shut down within 5 s");
                }

                _services.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Shutdown did not complete cleanly");
        }
        finally
        {
            Log.Information("Nova Hardware exited");
            Log.CloseAndFlush();
            _singleInstance?.Dispose();
        }

        base.OnExit(e);
    }
}
