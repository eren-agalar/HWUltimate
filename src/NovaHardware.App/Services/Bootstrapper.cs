using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NovaHardware.App.ViewModels;
using NovaHardware.App.Views;
using NovaHardware.Core.Abstractions;
using NovaHardware.Core.Alerts;
using NovaHardware.Core.History;
using NovaHardware.Core.Reports;
using NovaHardware.Core.Sensors;
using NovaHardware.Core.Settings;
using NovaHardware.Core.Stress;
using NovaHardware.Hardware;
using NovaHardware.Hardware.Amd;
using NovaHardware.Hardware.Nvidia;
using NovaHardware.Infrastructure;
using NovaHardware.Core.Explorer;
using NovaHardware.Infrastructure.Cpu;
using NovaHardware.Infrastructure.Discovery;
using NovaHardware.Infrastructure.Gpu;
using NovaHardware.Infrastructure.Preferences;
using NovaHardware.Infrastructure.Settings;
using NovaHardware.Infrastructure.Smbios;
using NovaHardware.Storage;
using Serilog;

namespace NovaHardware.App.Services;

/// <summary>Composition root. The only place that knows concrete hardware / storage implementations.</summary>
public static class Bootstrapper
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static Task<AppSettings> LoadSettingsAsync(AppPaths paths) =>
        new JsonSettingsService(paths.SettingsFile, NullLogger<JsonSettingsService>.Instance).LoadAsync();

    public static ServiceProvider Build(AppPaths paths, AppSettings settings)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddSerilog(Log.Logger, dispose: false).SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);

        services.AddSingleton<ISettingsService>(sp =>
        {
            var s = new JsonSettingsService(paths.SettingsFile, sp.GetRequiredService<ILogger<JsonSettingsService>>());
            s.LoadAsync().GetAwaiter().GetResult();
            return s;
        });

        // Hardware providers — each isolated behind IHardwareProvider.
        services.AddSingleton<IHardwareProvider, LibreHardwareMonitorProvider>();
        services.AddSingleton<IHardwareProvider, WindowsMemoryProvider>();
        if (settings.EnableNvidiaProvider)
        {
            services.AddSingleton<IHardwareProvider, NvApiProvider>();
        }

        if (settings.EnableAmdProvider)
        {
            services.AddSingleton<IHardwareProvider, AdlxProvider>();
        }

        services.AddSingleton<SensorStatisticsTracker>();
        services.AddSingleton<ISensorPollingService>(sp => new SensorPollingService(
            sp.GetServices<IHardwareProvider>(), sp.GetRequiredService<SensorStatisticsTracker>(),
            sp.GetRequiredService<ILogger<SensorPollingService>>(), sp.GetRequiredService<TimeProvider>())
        {
            Interval = TimeSpan.FromMilliseconds(settings.PollingIntervalMs),
        });

        // Discovery. Order matters: detailed, sectioned sources come first so their values win when devices are merged.
        services.AddSingleton<IDeviceDiscoveryService, CpuDiscoveryService>();
        services.AddSingleton<IDeviceDiscoveryService, GpuDiscoveryService>();
        if (settings.EnableNvidiaProvider)
        {
            services.AddSingleton<IDeviceDiscoveryService, NvidiaGpuDetailSource>();
        }

        services.AddSingleton<SmbiosDiscoveryService>();
        services.AddSingleton<IDeviceDiscoveryService, MotherboardDiscoveryService>();
        services.AddSingleton<WmiDeviceDiscoveryService>();
        services.AddSingleton<IDeviceDiscoveryService>(sp => sp.GetRequiredService<WmiDeviceDiscoveryService>());
        services.AddSingleton<ISystemSummaryProvider>(sp => sp.GetRequiredService<WmiDeviceDiscoveryService>());
        services.AddSingleton<IDeviceDiscoveryService>(sp => sp.GetRequiredService<SmbiosDiscoveryService>());
        services.AddSingleton<IDeviceDiscoveryService, SetupApiDiscoveryService>();

        // Local user preferences.
        services.AddSingleton<IPreferenceStore<SensorExplorerState>>(sp =>
            new JsonPreferenceStore<SensorExplorerState>(Path.Combine(paths.Root, "sensor-explorer.json"), sp.GetRequiredService<ILogger<SensorExplorerState>>()));
        services.AddSingleton<IHardwareInventoryService, HardwareInventoryService>();

        // Storage, analytics, alerts, reporting, stress.
        services.AddSingleton<IHistoricalDataService>(sp => new SqliteHistoricalDataService(paths.HistoryDatabase, sp.GetRequiredService<ILogger<SqliteHistoricalDataService>>()));
        services.AddSingleton<HistoryRecorder>();
        services.AddSingleton<IAlertService, AlertService>();
        services.AddSingleton<IReportExportService, ReportExportService>();
        services.AddSingleton<IStressTestService>(sp => new StressTestService(sp.GetRequiredService<ISensorPollingService>(), sp.GetRequiredService<ILogger<StressTestService>>()));

        // UI.
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SensorExplorerViewModel>();
        services.AddSingleton<HardwareViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<StabilityViewModel>();
        services.AddSingleton<AlertsViewModel>();
        services.AddSingleton<ReportsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
