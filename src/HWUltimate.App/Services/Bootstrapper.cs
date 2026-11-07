using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HWUltimate.App.ViewModels;
using HWUltimate.App.Views;
using HWUltimate.Core.Abstractions;
using HWUltimate.Core.Explorer;
using HWUltimate.Core.Identity;
using HWUltimate.Core.Reports;
using HWUltimate.Core.Sensors;
using HWUltimate.Core.Settings;
using HWUltimate.Hardware;
using HWUltimate.Hardware.Amd;
using HWUltimate.Hardware.Nvidia;
using HWUltimate.Infrastructure;
using HWUltimate.Infrastructure.Cpu;
using HWUltimate.Infrastructure.Discovery;
using HWUltimate.Infrastructure.Gpu;
using HWUltimate.Infrastructure.Preferences;
using HWUltimate.Infrastructure.Settings;
using HWUltimate.Infrastructure.Smbios;
using Serilog;

namespace HWUltimate.App.Services;

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

        // Hardware providers — each isolated behind IHardwareProvider and read by ONE central polling loop.
        services.AddSingleton<IHardwareProvider, LibreHardwareMonitorProvider>();
        services.AddSingleton<IHardwareProvider, WindowsMemoryProvider>();
        if (settings.General.EnableNvidiaProvider) services.AddSingleton<IHardwareProvider, NvApiProvider>();
        if (settings.General.EnableAmdProvider) services.AddSingleton<IHardwareProvider, AdlxProvider>();

        services.AddSingleton<IDeviceIdentityResolver>(_ => new PnpIdentityResolver());
        services.AddSingleton<SensorStatisticsTracker>();
        services.AddSingleton<ISensorPollingService>(sp => new SensorPollingService(
            sp.GetServices<IHardwareProvider>(), sp.GetRequiredService<SensorStatisticsTracker>(),
            sp.GetRequiredService<ILogger<SensorPollingService>>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IDeviceIdentityResolver>())
        {
            Interval = TimeSpan.FromMilliseconds(settings.Sensors.PollingIntervalMs),
        });

        // Discovery. Detailed, sectioned sources first so their values win when devices are merged.
        services.AddSingleton<IDeviceDiscoveryService, CpuDiscoveryService>();
        services.AddSingleton<IDeviceDiscoveryService, GpuDiscoveryService>();
        if (settings.General.EnableNvidiaProvider) services.AddSingleton<IDeviceDiscoveryService, NvidiaGpuDetailSource>();
        services.AddSingleton<SmbiosDiscoveryService>();
        services.AddSingleton<IDeviceDiscoveryService, MotherboardDiscoveryService>();
        services.AddSingleton<WmiDeviceDiscoveryService>();
        services.AddSingleton<IDeviceDiscoveryService>(sp => sp.GetRequiredService<WmiDeviceDiscoveryService>());
        services.AddSingleton<ISystemSummaryProvider>(sp => sp.GetRequiredService<WmiDeviceDiscoveryService>());
        services.AddSingleton<IDeviceDiscoveryService>(sp => sp.GetRequiredService<SmbiosDiscoveryService>());
        services.AddSingleton<IDeviceDiscoveryService, SetupApiDiscoveryService>();
        services.AddSingleton<IHardwareInventoryService, HardwareInventoryService>();

        services.AddSingleton<IReportExportService, ReportExportService>();

        services.AddSingleton<IPreferenceStore<SensorExplorerState>>(sp =>
            new JsonPreferenceStore<SensorExplorerState>(Path.Combine(paths.Root, "sensor-explorer.json"), sp.GetRequiredService<ILogger<SensorExplorerState>>()));

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<SensorExplorerViewModel>();
        services.AddSingleton<HardwareViewModel>();
        services.AddSingleton<ReportsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
