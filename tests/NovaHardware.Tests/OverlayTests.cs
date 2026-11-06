using Microsoft.Extensions.Logging.Abstractions;
using NovaHardware.Core.Overlay;
using NovaHardware.Core.Sensors;
using NovaHardware.Core.Settings;
using NovaHardware.Domain.Hardware;
using NovaHardware.Domain.Sensors;

namespace NovaHardware.Tests;

internal sealed class FakeFrameSource : IFrameSource
{
    public FrameSourceState State { get; set; } = FrameSourceState.Stopped;
    public string StatusMessage { get; set; } = "off";
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public bool Disposed { get; private set; }
    public bool Available { get; set; } = true;
    public event Action<FrameSample>? FrameReceived;
    public bool HasSubscribers => FrameReceived is not null;
    public void Start() { Starts++; State = Available ? FrameSourceState.Running : FrameSourceState.NotInstalled; StatusMessage = Available ? "running" : "PresentMon not found"; }
    public void Stop() { Stops++; State = FrameSourceState.Stopped; StatusMessage = "off"; }
    public void Emit(FrameSample f) => FrameReceived?.Invoke(f);
    public void Dispose() { Disposed = true; Stop(); }
}

public class OverlayEngineTests
{
    private static readonly HardwareDevice Cpu = new("cpu", "Ryzen", HardwareCategory.Cpu, "lhm");
    private static readonly HardwareDevice IGpu = new("igpu", "Radeon Graphics", HardwareCategory.Gpu, "lhm");
    private static readonly HardwareDevice DGpu = new("dgpu", "RTX 3080", HardwareCategory.Gpu, "lhm") { Providers = ["lhm", "nvapi"] };

    private static (OverlayEngine Engine, FakeFrameSource Frames, FakeProvider Provider, SensorPollingService Polling) Create(int? foreground = 4242)
    {
        var p = new FakeProvider("lhm").WithDevice("cpu", HardwareCategory.Cpu, "Ryzen").WithDevice("igpu", HardwareCategory.Gpu, "Radeon Graphics").WithDevice("dgpu", HardwareCategory.Gpu, "RTX 3080")
            .Set("c-t", "cpu", "CPU Package", SensorType.Temperature, 62.4)
            .Set("c-l", "cpu", "CPU Total", SensorType.Utilization, 33)
            .Set("i-t", "igpu", "GPU Core", SensorType.Temperature, 41)
            .Set("d-t", "dgpu", "GPU Core", SensorType.Temperature, 71)
            .Set("d-h", "dgpu", "GPU Hot Spot", SensorType.Temperature, 83)
            .Set("d-vt", "dgpu", "GPU Memory Total", SensorType.MemorySmall, 10240)
            .Set("d-vu", "dgpu", "GPU Memory Used", SensorType.MemorySmall, 2560)
            .Set("d-x", "dgpu", "GPU Slowdown Temperature Threshold", SensorType.Temperature, 90);
        var polling = new SensorPollingService([p], new SensorStatisticsTracker(), NullLogger<SensorPollingService>.Instance);
        polling.PollOnce();
        var frames = new FakeFrameSource();
        var engine = new OverlayEngine(polling, frames, () => foreground);
        return (engine, frames, p, polling);
    }

    [Fact]
    public void Show_hide_and_toggle_raise_visibility_events()
    {
        var (engine, _, _, _) = Create();
        var events = new List<bool>();
        engine.VisibilityChanged += (_, v) => events.Add(v);
        engine.Show();
        engine.Show();
        engine.Toggle();
        engine.Toggle();
        Assert.Equal([true, false, true], events);
        Assert.True(engine.IsVisible);
    }

    [Fact]
    public void Frame_source_runs_only_while_visible_and_fps_selected()
    {
        var (engine, frames, _, _) = Create();
        engine.Apply(new OverlaySettings { Metrics = [OverlayMetric.CpuTemperature] });
        engine.Show();
        Assert.Equal(0, frames.Starts); // no FPS metric → no PresentMon
        engine.Apply(engine.Settings with { Metrics = [OverlayMetric.CpuTemperature, OverlayMetric.Fps] });
        Assert.Equal(1, frames.Starts);
        Assert.True(frames.HasSubscribers);
        engine.Hide();
        Assert.Equal(1, frames.Stops);
        Assert.False(frames.HasSubscribers);
        engine.Show();
        Assert.Equal(2, frames.Starts);
        engine.Dispose();
        Assert.True(frames.Disposed);
        Assert.False(frames.HasSubscribers);
        Assert.Throws<ObjectDisposedException>(engine.Show);
    }

    [Fact]
    public void Metric_selection_and_order_are_respected_and_values_come_from_shared_snapshot()
    {
        var (engine, _, provider, polling) = Create();
        engine.Apply(new OverlaySettings { Metrics = [OverlayMetric.GpuHotspot, OverlayMetric.CpuTemperature, OverlayMetric.GpuVram, OverlayMetric.GpuTemperature, OverlayMetric.CpuTemperature] });
        engine.Show();
        var lines = engine.Tick(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal([OverlayMetric.GpuHotspot, OverlayMetric.CpuTemperature, OverlayMetric.GpuVram, OverlayMetric.GpuTemperature], lines.Select(l => l.Metric));
        Assert.Equal("83 °C", lines[0].Value.Text);
        Assert.Equal("62 °C", lines[1].Value.Text);
        Assert.Equal("2.5 GB", lines[2].Value.Text);
        Assert.Equal("25 %", lines[2].Value.Detail);
        Assert.Equal("71 °C", lines[3].Value.Text); // discrete GPU (most VRAM) picked, not the iGPU; threshold metadata ignored

        var updates = provider.Updates;
        engine.Tick();
        Assert.Equal(updates, provider.Updates); // the overlay never polls hardware itself
        provider.Set("c-t", "cpu", "CPU Package", SensorType.Temperature, 70);
        polling.PollOnce();
        Assert.Equal("70 °C", engine.Tick(System.Globalization.CultureInfo.InvariantCulture)[1].Value.Text);
        Assert.Equal([62.4, 62.4, 70], lines[1].History().Select(v => v!.Value));
    }

    [Fact]
    public void Explicit_gpu_choice_is_used()
    {
        var (engine, _, _, _) = Create();
        engine.Apply(new OverlaySettings { Metrics = [OverlayMetric.GpuTemperature], GpuDeviceId = "igpu" });
        engine.Show();
        Assert.Equal("41 °C", engine.Tick(System.Globalization.CultureInfo.InvariantCulture)[0].Value.Text);
    }

    [Fact]
    public void Missing_sensors_show_na()
    {
        var (engine, _, _, _) = Create();
        engine.Apply(new OverlaySettings { Metrics = [OverlayMetric.GpuFan, OverlayMetric.RamUsed] });
        engine.Show();
        Assert.All(engine.Tick(), l => Assert.Equal(("N/A", false), (l.Value.Text, l.Value.IsAvailable)));
    }

    [Fact]
    public void Fps_is_na_without_source_and_never_fabricated()
    {
        var (engine, frames, _, _) = Create();
        frames.Available = false;
        engine.Apply(new OverlaySettings { Metrics = [OverlayMetric.Fps, OverlayMetric.FrameTime] });
        engine.Show();
        var lines = engine.Tick();
        Assert.All(lines, l => Assert.Equal("N/A", l.Value.Text));
        Assert.Equal("PresentMon not found", lines[0].Value.Detail);
        Assert.Equal(FpsStatus.SourceUnavailable, engine.LastFps.Status);
    }

    [Fact]
    public void Fps_comes_from_the_foreground_process_frames()
    {
        var (engine, frames, _, _) = Create(foreground: 4242);
        engine.Apply(new OverlaySettings { Metrics = [OverlayMetric.Fps, OverlayMetric.FrameTime] });
        engine.Show();
        Assert.Equal(FpsStatus.Waiting, (engine.Tick(), engine.LastFps.Status).Item2);
        var now = DateTime.UtcNow.Ticks;
        for (var i = 0; i < 60; i++) frames.Emit(new FrameSample(4242, "game.exe", 1000.0 / 120, now - i * TimeSpan.TicksPerMillisecond * 8));
        for (var i = 0; i < 30; i++) frames.Emit(new FrameSample(999, "other.exe", 1000.0 / 30, now));
        var lines = engine.Tick(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("120", lines[0].Value.Text);
        Assert.Equal("game.exe", lines[0].Value.Detail);
        Assert.Equal("8.3 ms", lines[1].Value.Text);
    }

    [Fact]
    public void Desktop_frame_rate_is_labelled_as_desktop()
    {
        var tracker = new FrameRateTracker();
        var now = DateTime.UtcNow.Ticks;
        for (var i = 0; i < 10; i++) tracker.Add(new FrameSample(7, "dwm.exe", 16.6, now));
        var r = tracker.Read(7, FrameSourceState.Running, "");
        Assert.Equal(FpsStatus.DesktopOnly, r.Status);
        Assert.Contains("not a game", r.Message, StringComparison.Ordinal);
        Assert.Equal(FpsStatus.NoForegroundApplication, tracker.Read(null, FrameSourceState.Running, "").Status);
        Assert.Equal(FpsStatus.SourceUnavailable, tracker.Read(7, FrameSourceState.RequiresElevation, "admin").Status);
    }

    [Fact]
    public void Old_frames_expire_from_the_window()
    {
        var clock = DateTime.UtcNow.Ticks;
        var tracker = new FrameRateTracker(TimeSpan.FromSeconds(1), () => clock);
        tracker.Add(new FrameSample(1, "g.exe", 10, clock));
        clock += TimeSpan.FromSeconds(5).Ticks;
        Assert.Equal(FpsStatus.Waiting, tracker.Read(1, FrameSourceState.Running, "").Status);
        tracker.Add(new FrameSample(1, "g.exe", -5, clock)); // invalid samples ignored
        Assert.Equal(FpsStatus.Waiting, tracker.Read(1, FrameSourceState.Running, "").Status);
    }

    [Fact]
    public void Position_updates_are_persisted_once_per_change()
    {
        var (engine, _, _, _) = Create();
        var saved = new List<OverlaySettings>();
        engine.SettingsChanged += (_, s) => saved.Add(s);
        engine.UpdatePosition(100.4, 200.6);
        engine.UpdatePosition(100.2, 200.7);
        engine.UpdatePosition(double.NaN, 5);
        Assert.Single(saved);
        Assert.Equal((100d, 201d), (saved[0].Left!.Value, saved[0].Top!.Value));
    }

    [Fact]
    public void Auto_hide_rules()
    {
        var (engine, frames, _, _) = Create();
        engine.Apply(new OverlaySettings { Metrics = [OverlayMetric.Fps], AutoHideWhenNovaActive = true, AutoHideWithoutGame = true });
        engine.Show();
        engine.Tick();
        Assert.True(engine.ShouldAutoHide(novaWindowActive: true));
        Assert.True(engine.ShouldAutoHide(novaWindowActive: false)); // no game frames yet
        frames.Emit(new FrameSample(4242, "game.exe", 7, DateTime.UtcNow.Ticks));
        engine.Tick();
        Assert.False(engine.ShouldAutoHide(novaWindowActive: false));
    }

    [Fact]
    public void Presets_and_settings_validation()
    {
        Assert.Equal(3, OverlayPresets.For(OverlayPreset.Minimal).Count);
        Assert.Contains(OverlayMetric.FrameTime, OverlayPresets.For(OverlayPreset.Gaming));
        Assert.Equal(Enum.GetValues<OverlayMetric>().Length, OverlayPresets.For(OverlayPreset.FullHardware).Count);
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
        var bad = new AppSettings { Overlay = new OverlaySettings { BackgroundOpacity = 2, Scale = 9, Hotkey = "Q", TextColor = "red", Metrics = [] } };
        var errors = SettingsValidator.Validate(bad);
        Assert.True(errors.Count >= 5);
        Assert.Contains(errors, e => e.Contains("shortcut", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("metric", StringComparison.Ordinal));
    }
}

public class HotkeyAndParserTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F10", HotkeyGesture.Control | HotkeyGesture.Shift, 0x79u)]
    [InlineData("alt + o", HotkeyGesture.Alt, (uint)'O')]
    [InlineData("F12", 0u, 0x7Bu)]
    [InlineData("Win+Ctrl+Home", HotkeyGesture.Win | HotkeyGesture.Control, 0x24u)]
    public void Parses_and_round_trips(string text, uint mods, uint vk)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g, out _));
        Assert.Equal((mods, vk), (g.Modifiers, g.VirtualKey));
        Assert.True(HotkeyGesture.TryParse(g.ToString(), out var again, out _));
        Assert.Equal(g, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Q")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Banana")]
    public void Rejects_invalid_shortcuts(string text) => Assert.False(HotkeyGesture.TryParse(text, out _, out var error) || error is null);

    [Fact]
    public void Parses_presentmon_v1_and_v2_csv()
    {
        var v1 = new PresentMonCsvParser();
        Assert.Null(v1.Parse("Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,msInPresentAPI,MsBetweenPresents,msBetweenDisplayChange", 0));
        var f = v1.Parse("game.exe,1234,0x1,DXGI,0,0,0,1.5,0.2,6.94,6.9", 42)!.Value;
        Assert.Equal((1234, "game.exe", 6.94, 42L), (f.ProcessId, f.Application, f.FrameTimeMs, f.TimestampTicks));

        var v2 = new PresentMonCsvParser();
        v2.Parse("Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,FrameType,CPUStartTime,FrameTime,CPUBusy", 0);
        Assert.Equal(16.6, v2.Parse("app.exe,77,0x2,DXGI,1,0,0,Hardware: Independent Flip,Application,100.0,16.6,3.1", 1)!.Value.FrameTimeMs);
        Assert.Null(v2.Parse("garbage line", 1));
        Assert.Null(v2.Parse("app.exe,notanumber,0x2,DXGI,1,0,0,x,y,1,2,3", 1));
        Assert.Null(new PresentMonCsvParser().Parse("app.exe,1,2,3", 1)); // no header yet
    }
}

public class SettingsV2Tests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nova-settings-v2-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Migrates_v01_flat_settings()
    {
        await File.WriteAllTextAsync(_path, """{ "PollingIntervalMs": 500, "Language": "tr-TR", "Theme": "Light", "EnableNvidiaProvider": false, "HistoryEnabled": true }""", TestContext.Current.CancellationToken);
        var svc = new NovaHardware.Infrastructure.Settings.JsonSettingsService(_path, NullLogger<NovaHardware.Infrastructure.Settings.JsonSettingsService>.Instance);
        var s = await svc.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(500, s.Sensors.PollingIntervalMs);
        Assert.Equal("tr-TR", s.General.Language);
        Assert.Equal(NovaHardware.Core.Appearance.ThemeChoice.NovaLight, s.Appearance.Theme);
        Assert.False(s.General.EnableNvidiaProvider);
        Assert.True(File.Exists(Path.ChangeExtension(_path, ".v1.json")));
        Assert.Contains("\"SchemaVersion\": 2", await File.ReadAllTextAsync(_path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Theme_and_overlay_settings_persist()
    {
        var svc = new NovaHardware.Infrastructure.Settings.JsonSettingsService(_path, NullLogger<NovaHardware.Infrastructure.Settings.JsonSettingsService>.Instance);
        var settings = new AppSettings
        {
            Appearance = new() { Theme = NovaHardware.Core.Appearance.ThemeChoice.NovaOcean, AccentColor = "#2DD4BF", AnimationsEnabled = false, Density = NovaHardware.Core.Appearance.InterfaceDensity.Comfortable },
            Overlay = new() { Left = 10, Top = 20, ClickThrough = true, Metrics = [OverlayMetric.Fps, OverlayMetric.GpuHotspot], Hotkey = "Alt+F9" },
        };
        await svc.SaveAsync(settings, TestContext.Current.CancellationToken);
        var loaded = await new NovaHardware.Infrastructure.Settings.JsonSettingsService(_path, NullLogger<NovaHardware.Infrastructure.Settings.JsonSettingsService>.Instance).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(NovaHardware.Core.Appearance.ThemeChoice.NovaOcean, loaded.Appearance.Theme);
        Assert.Equal("#2DD4BF", loaded.Appearance.AccentColor);
        Assert.False(loaded.Appearance.AnimationsEnabled);
        Assert.Equal((10d, 20d, true), (loaded.Overlay.Left!.Value, loaded.Overlay.Top!.Value, loaded.Overlay.ClickThrough));
        Assert.Equal([OverlayMetric.Fps, OverlayMetric.GpuHotspot], loaded.Overlay.Metrics);
        Assert.Equal("Alt+F9", loaded.Overlay.Hotkey);
    }

    public void Dispose()
    {
        foreach (var f in new[] { _path, Path.ChangeExtension(_path, ".v1.json") }) if (File.Exists(f)) File.Delete(f);
    }
}
