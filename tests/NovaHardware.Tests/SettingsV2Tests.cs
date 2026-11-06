using Microsoft.Extensions.Logging.Abstractions;
using NovaHardware.Core.Appearance;
using NovaHardware.Core.Settings;
using NovaHardware.Infrastructure.Settings;

namespace NovaHardware.Tests;

public class SettingsV2Tests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nova-settings-v2-{Guid.NewGuid():N}.json");

    private JsonSettingsService NewService() => new(_path, NullLogger<JsonSettingsService>.Instance);

    [Fact]
    public async Task Migrates_v01_flat_settings()
    {
        await File.WriteAllTextAsync(_path, """{ "PollingIntervalMs": 500, "Language": "tr-TR", "Theme": "Light", "EnableNvidiaProvider": false, "HistoryEnabled": true }""", TestContext.Current.CancellationToken);
        var s = await NewService().LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(500, s.Sensors.PollingIntervalMs);
        Assert.Equal("tr-TR", s.General.Language);
        Assert.Equal(ThemeChoice.NovaLight, s.Appearance.Theme);
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
              "Appearance": { "Theme": "NovaOcean" },
              "Sensors": { "PollingIntervalMs": 2000 },
              "Overlay": { "Hotkey": "Ctrl+Shift+F10", "Metrics": ["Fps"] },
              "Alerts": { "NotificationsEnabled": true, "Rules": [] } }
            """, TestContext.Current.CancellationToken);
        var s = await NewService().LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("tr-TR", s.General.Language);
        Assert.Equal(ThemeChoice.NovaOcean, s.Appearance.Theme);
        Assert.Equal(2000, s.Sensors.PollingIntervalMs);
        Assert.Empty(SettingsValidator.Validate(s));
    }

    [Fact]
    public async Task Theme_settings_persist_and_defaults_validate()
    {
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
        var settings = new AppSettings
        {
            Appearance = new() { Theme = ThemeChoice.NovaOcean, AccentColor = "#2DD4BF", AnimationsEnabled = false, Density = InterfaceDensity.Comfortable },
        };
        await NewService().SaveAsync(settings, TestContext.Current.CancellationToken);
        var loaded = await NewService().LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ThemeChoice.NovaOcean, loaded.Appearance.Theme);
        Assert.Equal("#2DD4BF", loaded.Appearance.AccentColor);
        Assert.False(loaded.Appearance.AnimationsEnabled);
        Assert.Equal(InterfaceDensity.Comfortable, loaded.Appearance.Density);
    }

    public void Dispose()
    {
        foreach (var f in new[] { _path, Path.ChangeExtension(_path, ".v1.json") }) if (File.Exists(f)) File.Delete(f);
    }
}
