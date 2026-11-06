# NOVA HARDWARE

**Your Hardware. Every Detail. One Place.**

Nova Hardware is a Windows 10/11 (x64) desktop application for hardware monitoring, system identification,
historical sensor analytics, stability testing and hardware reporting. Built with C# / .NET 10 LTS and WPF.

## Features (v0.2)

Navigation: **Sensors** (start page) · **Hardware** · **Mini Overlay** · **Alerts** · **Reports** · **Settings**.

| Area | What it does |
|---|---|
| **Sensors** | Compact tree: Category → physical device → sensor group → sensor. Columns: Sensor, Current, Min, Max, Average, plus optional technical columns. Expand / collapse all or single nodes. Hide, favourite, pin and reorder sensors. Search; filter by device, hardware type, sensor category, provider and status; sort by name, value, maximum, temperature, power or load. Compact (default) and Advanced modes; Advanced adds sensor ids, providers and a technical-metadata group. Refresh interval is user-selectable; all preferences persist by stable sensor id. |
| **Physical device identity** | One device per piece of hardware. LibreHardwareMonitor, NVAPI and NVML views of the same GPU merge by PCI location, PnP instance path or unambiguous PCI ids, never by name alone. Integrated and discrete GPUs, and two identical cards, stay separate. Duplicate readings are kept once and record the other providers that also reported them. |
| **Sensor classification** | Limits, thresholds and resolutions exposed as "temperatures" (e.g. NVMe warning / critical temperature, thermal sensor limits) are technical metadata. They are never shown as live readings and never trigger alerts. |
| **Hardware** | Sectioned information per device. Every value shows its source and whether it is an official specification (catalog), detected from the system, or not available. Live readings are listed separately. Search, copy (value / property / device / all), export to HTML / JSON / text. |
| **Mini Overlay** | Lightweight, always-on-top, borderless, non-activating window. Presets (Minimal, Gaming, Full hardware, Custom), metric order, opacity, scale, font, colours, graphs, horizontal / vertical layout, drag to position (remembered), click-through, global shortcut, auto-hide, startup behaviour. Values come from the shared sensor engine; the overlay never polls hardware itself. FPS / frame time come from PresentMon when installed (see below); otherwise they show N/A. |
| **Alerts** | Component-aware rules (CPU, GPU, GPU hotspot, GPU memory, drive temperature, memory load, thermal throttling, fan failure). Separate warning and critical levels. Thresholds come from the device's own limits where reported (TjMax, NVML slowdown, NVMe WCTEMP / CCTEMP — never swapped), otherwise from vendor / device-class defaults, or from user values. Sustain, hysteresis and cooldown per rule. Messages distinguish warning, critical, device-reported and user thresholds. |
| **Reports** | Sections: system summary, inventory, CPU, GPU, memory, storage, current readings, alert history. Formats: HTML (Nova template), CSV, JSON, text. The live in-app preview shows exactly what will be exported. Files are read back and validated before success is reported. |
| **Settings** | Appearance (Nova Dark / Light / Midnight / Ocean / Follow Windows, accent, status colours, animations, density, text size — applied live), Sensors, Overlay, Alerts, General (language, launch with Windows, start minimized, logging, providers, data folder). Settings are validated before saving. |

### Data integrity rules
- Sensor values are **never fabricated or interpolated**; a sensor without a value shows **N/A**. FPS is never estimated.
- Values keep the provider's original unit; the only conversions are exact and labelled (NVAPI kHz → MHz, NVML mW → W).
- Values outside a broad physical range are kept unchanged but flagged **Suspect**, and excluded from alerts.
- ASIC / silicon quality is shown as *Not Available*: no supported API exposes it and Nova never estimates it.

## Architecture

```
NovaHardware.Domain           Unified models (SensorReading, HardwareDevice, DeviceInfo, alerts, stress, reports)
NovaHardware.Core             Interfaces + platform-neutral services (polling, statistics, alerts, history recorder,
                              stress engine, report export)
NovaHardware.Hardware         LibreHardwareMonitor provider
NovaHardware.Hardware.Nvidia  NVAPI provider (nvapi_QueryInterface, unmanaged function pointers)
NovaHardware.Hardware.Amd     ADLX runtime loader/provider
NovaHardware.Infrastructure   WMI, SMBIOS (GetSystemFirmwareTable + parser), SetupAPI, inventory merge, settings, Serilog
NovaHardware.Storage          SQLite history repository — retained for existing history.db files, not used by the v0.2 app
NovaHardware.Benchmark        Stress / stability engine — extracted for the future NOVA BENCHMARK product, not used by the app
NovaHardware.UI               Nova design system (tokens, themes, controls: NovaIconButton, NovaSearchBox, NovaStatusIndicator,
                              NovaChevron, NovaDialog, Sparkline; styles for dropdown, toggle, checkbox, slider, scrollbar,
                              menus, tooltips, data grid, navigation), icons, localisation (resx: en, tr)
NovaHardware.App              WPF shell, MVVM view models (CommunityToolkit.Mvvm), overlay window, DI composition root
NovaHardware.Tests            xUnit v3 tests (223)
tools/NovaPerf                Sensor-pipeline micro-benchmark (synthetic data, development only)
```

The UI only depends on `NovaHardware.Core` interfaces (`IHardwareProvider`, `ISensorProvider`, `IDeviceDiscoveryService`,
`ISensorPollingService`, `IHardwareInventoryService`, `IHistoricalDataService`, `IAlertService`, `IReportExportService`,
`IStressTestService`). Concrete implementations are wired up only in `App/Services/Bootstrapper.cs`.

## Build, run and publish

Requires the .NET 10 SDK on Windows 10/11 x64 (the app needs the .NET 10 Desktop Runtime to run).

```bash
dotnet build NovaHardware.sln -c Release
```

```bash
dotnet test --project tests/NovaHardware.Tests -c Release
```

```bash
dotnet run --project src/NovaHardware.App -c Release
```

```bash
dotnet publish src/NovaHardware.App -c Release -r win-x64 --self-contained false -o publish
```

For a build that does not need the .NET runtime installed, add `--self-contained true` (larger output).

The app requests `highestAvailable` elevation. Administrators get full sensor access and FPS via PresentMon.
Standard users get reduced sensors and see a banner offering to restart elevated.

User data lives in `%LOCALAPPDATA%\NovaHardware`: `settings.json`, `sensor-explorer.json`, `logs\`, `reports\`, and the v0.1 `history.db`, which v0.2 leaves untouched.

### FPS / frame time (optional)
Install [PresentMon](https://github.com/GameTechDev/PresentMon/releases) (MIT licence; not bundled with Nova). Nova finds it in one of these places:
- the path set under Mini Overlay → FPS
- `tools\PresentMon\PresentMon*.exe` next to `NovaHardware.exe`
- `%ProgramFiles%\Intel\PresentMon`

It runs only while the overlay is visible and an FPS metric is selected. It is tied to Nova through a Windows job object, so it never outlives Nova. FPS is reported for the foreground application; desktop compositor frame rates are labelled as desktop, not as a game.

## Localisation
UI strings live in `tools/strings.py`, the single source for English and Turkish.
Run `python3 tools/strings.py` to regenerate `Strings.resx` / `Strings.tr.resx`.
To add a language, add a column and a culture to `Loc.Languages`.

## Hardware information sources
| Area | Sources |
|---|---|
| CPU | CPUID (vendor, brand, signature, ISA extensions, cache descriptors, VT-x / AMD-V, hypervisor, leaf 0x16 clocks), GetLogicalProcessorInformationEx (cores, threads, P/E split, cache instances), registry (microcode revision), WMI (socket, firmware VT, SLAT) |
| GPU | PnP / PCI ids, bus / device / function and PCIe current / max link (SetupDiGetDeviceProperty), display-driver registry (VRAM size, VBIOS string, chip / DAC type), DXGI (memory, adapters), D3D12 / D3D11 feature-level probe, Vulkan (API / driver version), NVML (architecture, VBIOS, bus width, clocks, power limits, PCIe generation, thermal thresholds, CUDA), NVAPI (base / boost clocks), Nova chip catalog for officially published die figures (labelled as externally reported) |
| Memory | SMBIOS type 16 / 17 (ECC capability, slots, max capacity, data / total width, rank, rated vs configured speed, voltage), Windows memory manager (commit, virtual memory, cache, pools) |
| Motherboard | SMBIOS types 0 / 1 / 2, chipset LPC / ISA bridge and host bridge (PCI class 06-01 / 06-00), Super I/O and EC from the sensor provider |
| Storage | MSFT_PhysicalDisk, MSFT_StorageReliabilityCounter (temperature, wear, power-on hours, error counters, latency), live sensors (e.g. data read / written) |
| Network | WMI + System.Net.NetworkInformation (status, negotiated speed, IPv4 / IPv6, gateway, DNS, MTU) |
| Display | WmiMonitorID / WmiMonitorConnectionParams (EDID identity, connection type) |

## Known limitations (v0.2)
- **Not run on Windows here.** The UI and native calls (DXGI, D3D12 / D3D11, Vulkan, NVML, SetupAPI, RegisterHotKey, PresentMon) were compiled and covered by tests. The application has not been launched and viewed on Windows in this environment.
- **PresentMon command-line options** target the PresentMon 2.x console (`--output_stdout --stop_existing_session --session_name --no_console_stats`). The CSV header is parsed dynamically, but other PresentMon versions may need different options.
- **Overlay over exclusive-fullscreen games**: a normal top-most window cannot draw over exclusive fullscreen. It works over borderless / windowed games. There is no in-game injection, by design.
- **Unavailable properties**:
  - ASIC quality, SPD contents and memory timings
  - memory channels and ECC operating status
  - VRAM type and memory bandwidth, CPU TDP
  - supported GPU outputs (only connected monitors are listed)
  - raw SMART tables
- **GPU chip codename / die / process / transistors** come only from Nova's catalog of about 30 common GPUs and are labelled as an official specification (catalog).
- **AMD ADLX** is detected and initialised but not bound for metrics. AMD GPU sensors come from LibreHardwareMonitor.
- **Inventory merge**: two identical GPUs merge correctly only when Windows reports their PCI location. Keyless entries with an ambiguous name are kept separate rather than guessed.
- **Not measured**: on-Windows CPU / RAM use and the overlay's effect on game FPS (see `docs/AUDIT-v0.2.md`).

## Third-party
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

LibreHardwareMonitorLib (MPL-2.0), LiveCharts2 (MIT), CommunityToolkit.Mvvm (MIT), Serilog (Apache-2.0),
Microsoft.Data.Sqlite (MIT). NVIDIA NVAPI and AMD ADLX are used through the runtimes installed with the vendor drivers.
No proprietary code, branding or assets from other products are included.
