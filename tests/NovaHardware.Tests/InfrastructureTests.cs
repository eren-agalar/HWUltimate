using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NovaHardware.Core.Settings;
using NovaHardware.Domain.Hardware;
using NovaHardware.Infrastructure.Discovery;
using NovaHardware.Infrastructure.Settings;
using NovaHardware.Infrastructure.Smbios;

namespace NovaHardware.Tests;

public class SmbiosParserTests
{
    /// <summary>Builds one SMBIOS structure: header + formatted area (padded to <paramref name="length"/>) + string-set.</summary>
    private static byte[] Structure(byte type, int length, Action<byte[]> fill, params string[] strings)
    {
        var formatted = new byte[length];
        formatted[0] = type;
        formatted[1] = (byte)length;
        fill(formatted);
        var tail = strings.Length == 0 ? [0, 0] : Encoding.ASCII.GetBytes(string.Concat(strings.Select(s => s + "\0")) + "\0");
        return [.. formatted, .. tail];
    }

    private static byte[] Raw(params byte[][] structures)
    {
        var table = structures.SelectMany(s => s).Concat(Structure(127, 4, _ => { })).ToArray();
        var header = new byte[] { 0, 3, 4, 0 }.Concat(BitConverter.GetBytes((uint)table.Length));
        return header.Concat(table).ToArray();
    }

    private static readonly byte[] Data = Raw(
        Structure(0, 0x18, f => { f[4] = 1; f[5] = 2; f[8] = 3; f[9] = 0xFF; }, "American Megatrends", "F12", "03/15/2025"),
        Structure(1, 0x1B, f => { f[4] = 1; f[5] = 2; f[7] = 3; for (var i = 0; i < 16; i++) f[8 + i] = (byte)(i + 1); }, "Acme", "Model X", "To Be Filled By O.E.M."),
        Structure(2, 0x08, f => { f[4] = 1; f[5] = 2; }, "BoardCo", "Z990 PRO"),
        Structure(4, 0x30, f =>
        {
            f[4] = 1; f[7] = 2; f[0x10] = 3;
            BitConverter.GetBytes((ushort)100).CopyTo(f, 0x12);
            BitConverter.GetBytes((ushort)5700).CopyTo(f, 0x14);
            BitConverter.GetBytes((ushort)4500).CopyTo(f, 0x16);
            f[0x23] = 16; f[0x25] = 32;
        }, "AM5", "Advanced Micro Devices, Inc.", "AMD Ryzen 9 9950X 16-Core Processor"),
        Structure(17, 0x28, f =>
        {
            BitConverter.GetBytes((ushort)0x7FFF).CopyTo(f, 0x0C);
            BitConverter.GetBytes(32768u).CopyTo(f, 0x1C);
            f[0x0E] = 0x09; f[0x10] = 1; f[0x11] = 2; f[0x12] = 0x22;
            BitConverter.GetBytes((ushort)6000).CopyTo(f, 0x15);
            f[0x17] = 3; f[0x18] = 4; f[0x1A] = 5;
            BitConverter.GetBytes((ushort)6000).CopyTo(f, 0x20);
            BitConverter.GetBytes((ushort)1350).CopyTo(f, 0x26);
        }, "DIMM_A1", "BANK 0", "G.Skill", "00000000", "F5-6000J3038F16G"),
        Structure(17, 0x28, f => { /* empty slot: size 0 */ }, "DIMM_A2"));

    [Fact]
    public void Parses_core_structures()
    {
        var d = SmbiosParser.Parse(Data);
        Assert.Equal(new Version(3, 4), d.Version);
        Assert.Equal("American Megatrends", d.Bios!.Vendor);
        Assert.Equal("F12", d.Bios.Version);
        Assert.Equal(16384, d.Bios.RomSizeKb);
        Assert.Equal("Model X", d.System!.Product);
        Assert.Null(d.System.SerialNumber); // OEM placeholder filtered
        Assert.NotNull(d.System.Uuid);
        Assert.Equal("Z990 PRO", d.Baseboard!.Product);

        var cpu = Assert.Single(d.Processors);
        Assert.Equal("AM5", cpu.Socket);
        Assert.Equal(16, cpu.Cores);
        Assert.Equal(32, cpu.Threads);
        Assert.Equal(5700, cpu.MaxSpeedMhz);

        var mem = Assert.Single(d.MemoryDevices); // empty slot skipped
        Assert.Equal(32768, mem.SizeMegabytes);
        Assert.Equal("DDR5", mem.MemoryType);
        Assert.Equal("DIMM", mem.FormFactor);
        Assert.Equal(6000, mem.ConfiguredSpeedMts);
        Assert.Equal(1350, mem.ConfiguredVoltageMv);
        Assert.Null(mem.SerialNumber);
    }

    [Fact]
    public void Converts_to_devices()
    {
        var devices = SmbiosDiscoveryService.ToDevices(SmbiosParser.Parse(Data));
        Assert.Contains(devices, d => d.Category == HardwareCategory.Motherboard && d.Name == "BoardCo Z990 PRO");
        var mem = Assert.Single(devices, d => d.Category == HardwareCategory.Memory);
        Assert.Equal("G.Skill F5-6000J3038F16G (DIMM_A1)", mem.Name);
        Assert.Contains(mem.Properties, p => p.Name == "Capacity" && p.Value == "32 GB");
    }

    [Fact]
    public void Rejects_truncated_buffer() => Assert.Throws<FormatException>(() => SmbiosParser.Parse(new byte[3]));

    [Fact]
    public void Stops_safely_on_corrupt_length()
    {
        var raw = Raw(Structure(0, 0x18, f => f[4] = 1, "Vendor"));
        raw[8 + 1] = 0xFF; // structure claims to run past the buffer
        var d = SmbiosParser.Parse(raw);
        Assert.Null(d.Bios);
    }

    [Theory]
    [InlineData(0x8000 | 2048, 2)] // KB granularity
    [InlineData(8192, 8192)]
    public void Memory_size_encoding(int raw, long expectedMb)
    {
        var f = new byte[0x28];
        BitConverter.GetBytes((ushort)raw).CopyTo(f, 0x0C);
        Assert.Equal(expectedMb, SmbiosParser.MemorySizeMb(f));
    }
}

public class InventoryMergeTests
{
    [Fact]
    public void Merges_same_device_from_multiple_sources_without_overwriting()
    {
        var merged = HardwareInventoryService.Merge(
        [
            new DeviceInfo { Id = "w", Name = "AMD Ryzen 9", Category = HardwareCategory.Cpu, Source = "WMI", Properties = [new("Cores", "16")] },
            new DeviceInfo { Id = "s", Name = "AMD  Ryzen 9 ", Category = HardwareCategory.Cpu, Source = "SMBIOS", Manufacturer = "AMD", Properties = [new("Cores", "99"), new("Socket", "AM5")] },
            new DeviceInfo { Id = "g", Name = "GPU", Category = HardwareCategory.Gpu, Source = "WMI" },
        ]);

        Assert.Equal(2, merged.Count);
        var cpu = merged[0];
        Assert.Equal("WMI, SMBIOS", cpu.Source);
        Assert.Equal("AMD", cpu.Manufacturer);
        Assert.Equal("16", cpu.Properties.Single(p => p.Name == "Cores").Value);
        Assert.Contains(cpu.Properties, p => p.Name == "Socket");
    }
}

public sealed class SettingsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nova-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Round_trips_and_raises_changed()
    {
        var svc = new JsonSettingsService(_path, NullLogger<JsonSettingsService>.Instance);
        AppSettings? changed = null;
        svc.Changed += (_, s) => changed = s;
        await svc.SaveAsync(new AppSettings { PollingIntervalMs = 500, Language = "tr-TR", Theme = ThemeMode.Light }, TestContext.Current.CancellationToken);

        var loaded = await new JsonSettingsService(_path, NullLogger<JsonSettingsService>.Instance).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(500, loaded.PollingIntervalMs);
        Assert.Equal("tr-TR", loaded.Language);
        Assert.Equal(ThemeMode.Light, loaded.Theme);
        Assert.NotNull(changed);
    }

    [Fact]
    public async Task Corrupt_file_falls_back_to_defaults()
    {
        await File.WriteAllTextAsync(_path, "{ not json", TestContext.Current.CancellationToken);
        var loaded = await new JsonSettingsService(_path, NullLogger<JsonSettingsService>.Instance).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new AppSettings().PollingIntervalMs, loaded.PollingIntervalMs);
        Assert.True(File.Exists(_path + ".bak"));
    }

    public void Dispose()
    {
        foreach (var f in new[] { _path, _path + ".bak" })
        {
            if (File.Exists(f)) File.Delete(f);
        }
    }
}
