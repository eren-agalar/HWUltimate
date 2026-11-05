using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NovaHardware.App.Services;
using NovaHardware.App.ViewModels;
using NovaHardware.App.Views;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Alerts;
using NovaHardware.Core.History;
using NovaHardware.Core.Settings;
using NovaHardware.Infrastructure;
using NovaHardware.UI.Localization;
using NovaHardware.UI.Themes;
using Serilog;

namespace NovaHardware.App;

public partial class App : Application
{
    private const string MutexName = "Global\\NovaHardware.SingleInstance";
    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private Microsoft.Extensions.Logging.ILogger? _logger;

    public static IServiceProvider Services => ((App)Current)._services ?? throw new InvalidOperationException("Services not built.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, MutexName, out var created);
        if (!created && !e.Args.Contains("--allow-multiple"))
        {
            MessageBox.Show("Nova Hardware is already running.", "Nova Hardware", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var paths = new AppPaths();
        Log.Logger = Logging.CreateLogger(paths, e.Args.Contains("--verbose"));
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error(args.Exception, "Unobserved task exception"); args.SetObserved(); };

        var bootstrapSettings = await Bootstrapper.LoadSettingsAsync(paths);
        Loc.Instance.Apply(bootstrapSettings.Language);
        ThemeManager.Apply(bootstrapSettings.Theme == Core.Settings.ThemeMode.Light ? AppTheme.Light : AppTheme.Dark);

        _services = Bootstrapper.Build(paths, bootstrapSettings);
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("Nova Hardware {Version} starting (admin: {Admin})", Bootstrapper.Version, Hardware.Elevation.IsAdministrator());

        var main = _services.GetRequiredService<MainWindow>();
        MainWindow = main;
        main.Show();

        await _services.GetRequiredService<MainViewModel>().InitializeAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI exception");
        MessageBox.Show($"An unexpected error occurred and was logged:\n\n{e.Exception.Message}", "Nova Hardware", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services is not null)
            {
                var recorder = _services.GetRequiredService<HistoryRecorder>();
                var polling = _services.GetRequiredService<ISensorPollingService>();
                Task.Run(async () =>
                {
                    await polling.StopAsync();
                    await recorder.DisposeAsync();
                    await polling.DisposeAsync();
                }).Wait(TimeSpan.FromSeconds(5));
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
