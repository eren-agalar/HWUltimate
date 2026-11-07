using Microsoft.Extensions.Logging.Abstractions;
using HWUltimate.Core.Appearance;
using HWUltimate.Core.Settings;
using HWUltimate.Infrastructure.Settings;

namespace HWUltimate.Tests;

public class SettingsV2Tests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hwu-settings-v2-{Guid.NewGuid():N}.json");

    private JsonSettingsService NewService() => new(_path, NullLogger<JsonSettingsService>.Instance);

    [Fact]
    public async Task Migrates_v01_flat_settings()
    {
        await File.WriteAllTextAsync(_path, """{ "PollingIntervalMs": 500, "Language": "tr-TR", "Theme": "Light", "EnableNvidiaProvider": false, "HistoryEnabled": true }""", TestContext.Current.CancellationToken);
        var s = await NewService().LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(500, s.Sensors.PollingIntervalMs);
        Assert.Equal("tr-TR", s.General.Language);
        Assert.Equal(ThemeChoice.Light, s.Appearance.Theme);
        Assert.False(s.General.EnableNvidiaProvider);
        Assert.True(File.Exists(Path.ChangeExtension(_path, ".v1.json")));
        Assert.Contains("\"SchemaVersion\": 2", await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Earlier_v02_file_with_overlay_and_alert_sections_still_loads()
    {
        // Settings written before the Mini Overlay and Alerts were removed keep every remaining value.
        await File.WriteAllTextAsync(_path, """
            { "SchemaVersion": 2,
              "General": { "Language": "tr-TR" },
              "Appearance": { "Theme": "Ocean" },
              "Sensors": { "PollingIntervalMs": 2000 },
              "Overlay": { "Hotkey": "Ctrl+Shift+F10", "Metrics": ["Fps"] },
              "Alerts": { "NotificationsEnabled": true, "Rules": [] } }
            """, TestContext.Current.CancellationToken);
        var s = await NewService().LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("tr-TR", s.General.Language);
        Assert.Equal(ThemeChoice.Ocean, s.Appearance.Theme);
        Assert.Equal(2000, s.Sensors.PollingIntervalMs);
        Assert.Empty(SettingsValidator.Validate(s));
    }

    [Fact]
    public async Task Theme_settings_persist_and_defaults_validate()
    {
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
        var settings = new AppSettings
        {
            Appearance = new() { Theme = ThemeChoice.Ocean, AccentColor = "#2DD4BF", AnimationsEnabled = false, Density = InterfaceDensity.Comfortable },
        };
        await NewService().SaveAsync(settings, TestContext.Current.CancellationToken);
        var loaded = await NewService().LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ThemeChoice.Ocean, loaded.Appearance.Theme);
        Assert.Equal("#2DD4BF", loaded.Appearance.AccentColor);
        Assert.False(loaded.Appearance.AnimationsEnabled);
        Assert.Equal(InterfaceDensity.Comfortable, loaded.Appearance.Density);
    }

    public void Dispose()
    {
        foreach (var f in new[] { _path, Path.ChangeExtension(_path, ".v1.json") }) if (File.Exists(f)) File.Delete(f);
    }
}

public class RenameMigrationTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), $"hwu-rename-{Guid.NewGuid():N}");

    [Fact]
    public void Legacy_data_folder_is_copied_once_without_logs_and_left_in_place()
    {
        var legacy = Path.Combine(_base, "NovaHardware");
        var target = Path.Combine(_base, "HWUltimate");
        Directory.CreateDirectory(Path.Combine(legacy, "logs"));
        Directory.CreateDirectory(Path.Combine(legacy, "reports"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "sensor-explorer.json"), "{\"Hidden\":[\"a\"]}");
        File.WriteAllText(Path.Combine(legacy, "reports", "r.html"), "x");
        File.WriteAllText(Path.Combine(legacy, "logs", "old.log"), "x");

        Assert.True(HWUltimate.Infrastructure.AppPaths.MigrateLegacyData(legacy, target));
        Assert.Equal("{\"Hidden\":[\"a\"]}", File.ReadAllText(Path.Combine(target, "sensor-explorer.json")));
        Assert.True(File.Exists(Path.Combine(target, "reports", "r.html")));
        Assert.False(Directory.Exists(Path.Combine(target, "logs")));
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));

        // Second start: the new folder exists, nothing is copied again.
        File.WriteAllText(Path.Combine(target, "settings.json"), "{\"new\":1}");
        Assert.False(HWUltimate.Infrastructure.AppPaths.MigrateLegacyData(legacy, target));
        Assert.Equal("{\"new\":1}", File.ReadAllText(Path.Combine(target, "settings.json")));
    }

    [Fact]
    public void No_legacy_folder_means_no_migration() =>
        Assert.False(HWUltimate.Infrastructure.AppPaths.MigrateLegacyData(Path.Combine(_base, "missing"), Path.Combine(_base, "HWUltimate")));

    [Fact]
    public async Task Pre_rename_theme_names_still_load()
    {
        Directory.CreateDirectory(_base);
        var path = Path.Combine(_base, "settings.json");
        await File.WriteAllTextAsync(path, """{ "SchemaVersion": 2, "Appearance": { "Theme": "NovaOcean" } }""", TestContext.Current.CancellationToken);
        var s = await new JsonSettingsService(path, NullLogger<JsonSettingsService>.Instance).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ThemeChoice.Ocean, s.Appearance.Theme);
    }

    public void Dispose()
    {
        if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true);
    }
}
