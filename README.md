# HWUltimate

**Your Hardware. Every Detail. One Place.**

HWUltimate is a Windows 10/11 (x64) desktop application for hardware monitoring, system identification
and hardware reporting. Built with C# / .NET 10 LTS and WPF.

## Features (v0.3)

> Renamed from "Nova Hardware". On first start, settings, sensor preferences, reports and history are copied from `%LOCALAPPDATA%\NovaHardware` to `%LOCALAPPDATA%\HWUltimate` (the old folder is left untouched), and an old launch-with-Windows entry is replaced.
> The Mini Overlay (with PresentMon FPS) and the Alerts page were removed after v0.2.0; their settings sections are ignored.

Navigation: **Sensors** (start page) · **Hardware** · **Reports** · **Settings**.

| Area | What it does |
|---|---|
| **Sensors** | Classic hardware-monitor tree: device → sensor group → sensor, columns Sensor · Value · Min · Max (Average and technical columns optional). Every core reading sits in its type group (all core temperatures under Temperatures). Super I/O / EC chips are listed under their motherboard. **Secondary sensors** — never reported, empty fan headers, unconnected voltage / temperature inputs, virtual or disconnected network adapters, cumulative traffic counters — are hidden by default; *Show all sensors* reveals them. Hide sensors, whole devices or groups permanently (they follow id changes such as drives renumbered at boot); the *Hidden* list shows them again. Favourites, pins, custom order, search, filters, sorting. |
| **Detection** | *Rescan hardware* repeats detection without restarting. Automatic passes shortly after start repeat detection when it looks incomplete (driver still loading at logon, bus held by another monitoring tool); unavailable providers are retried periodically. A banner explains when the **PawnIO** driver (needed by LibreHardwareMonitor for motherboard, fan, voltage and some CPU sensors) is missing. System memory is named from SMBIOS (e.g. "32 GB DDR5-6000 (2 × 16 GB)"). |
| **Physical device identity** | One device per piece of hardware. LibreHardwareMonitor, NVAPI and NVML views of the same GPU merge by PCI location, PnP instance path or unambiguous PCI ids, never by name alone. Integrated and discrete GPUs, and two identical cards, stay separate. Duplicate readings are kept once and record the other providers that also reported them. |
| **Sensor classification** | Limits, thresholds and resolutions exposed as "temperatures" (e.g. NVMe warning / critical temperature, thermal sensor limits) are technical metadata. They are never shown as live readings. |
| **Hardware** | Device tree (computer → category → device) and one Feature · Description · Source table with section bands. A **System summary** page lists processor, graphics, motherboard, memory modules, drives, displays, network and OS. Every value shows whether it is an official specification (catalog), detected, live or not available. Search, copy, export to HTML / JSON / text. |
| **Silicon quality** | Processors: the factory per-core ranking (Intel favored cores / AMD preferred cores) Windows reads from ACPI CPPC — best cores, full order, levels; hybrid CPUs per core type. No percentage "quality score" is shown: Intel, AMD, NVIDIA and Windows expose none, and HWUltimate never estimates one. |
| **Reports** | Sections: system summary, inventory, CPU, GPU, memory, storage, current readings. Formats: HTML (HWUltimate template), CSV, JSON, text. The live in-app preview shows exactly what will be exported. Files are read back and validated before success is reported. |
| **Settings** | Appearance (HWUltimate Dark / Light / Midnight / Ocean / Follow Windows, accent, status colours, animations, density, text size — applied live), Sensors, General (language, launch with Windows, start minimized, logging, providers, data folder). Settings are validated before saving. |

### Data integrity rules
- Sensor values are **never fabricated or interpolated**; a sensor without a value shows **N/A**. 
- Values keep the provider's original unit; the only conversions are exact and labelled (NVAPI kHz → MHz, NVML mW → W).
- Values outside a broad physical range are kept unchanged but flagged **Suspect**.
- A silicon / ASIC quality *score* is shown as *Not Available*: no supported API exposes it and HWUltimate never estimates it. Only the firmware-reported per-core ranking is shown.

## Architecture

```
HWUltimate.Domain           Unified models (SensorReading, HardwareDevice, DeviceInfo, stress, reports)
HWUltimate.Core             Interfaces + platform-neutral services (polling, statistics, history recorder,
                              stress engine, report export)
HWUltimate.Hardware         LibreHardwareMonitor provider
HWUltimate.Hardware.Nvidia  NVAPI provider (nvapi_QueryInterface, unmanaged function pointers)
HWUltimate.Hardware.Amd     ADLX runtime loader/provider
HWUltimate.Infrastructure   WMI, SMBIOS (GetSystemFirmwareTable + parser), SetupAPI, inventory merge, settings, Serilog
HWUltimate.Storage          SQLite history repository — retained for existing history.db files, not used by the v0.2 app
HWUltimate.Benchmark        Stress / stability engine — extracted for the future HWULTIMATE BENCHMARK product, not used by the app
HWUltimate.UI               HWUltimate design system (tokens, themes, controls: HwuIconButton, HwuSearchBox, HwuStatusIndicator,
                              HwuChevron, HwuDialog; styles for dropdown, toggle, checkbox, slider, scrollbar,
                              menus, tooltips, data grid, navigation), icons, localisation (resx: en, tr)
HWUltimate.App              WPF shell, MVVM view models (CommunityToolkit.Mvvm), DI composition root
HWUltimate.Tests            xUnit v3 tests (227)
tools/HwuPerf                Sensor-pipeline micro-benchmark (synthetic data, development only)
```

The UI only depends on `HWUltimate.Core` interfaces (`IHardwareProvider`, `ISensorProvider`, `IDeviceDiscoveryService`,
`ISensorPollingService`, `IHardwareInventoryService`, `IHistoricalDataService`, `IReportExportService`,
`IStressTestService`). Concrete implementations are wired up only in `App/Services/Bootstrapper.cs`.

## Build, run and publish

Requires the .NET 10 SDK on Windows 10/11 x64 (the app needs the .NET 10 Desktop Runtime to run).

```bash
dotnet build HWUltimate.sln -c Release
```

```bash
dotnet test --project tests/HWUltimate.Tests -c Release
```

```bash
dotnet run --project src/HWUltimate.App -c Release
```

```bash
dotnet publish src/HWUltimate.App -c Release -r win-x64 --self-contained false -o publish
```

For a build that does not need the .NET runtime installed, add `--self-contained true` (larger output).

The app requests `highestAvailable` elevation. Administrators get full sensor access.
Standard users get reduced sensors and see a banner offering to restart elevated.

User data lives in `%LOCALAPPDATA%\HWUltimate`: `settings.json`, `sensor-explorer.json`, `logs\`, `reports\`, and the v0.1 `history.db`, which v0.2 leaves untouched.

## Localisation
UI strings live in `tools/strings.py`, the single source for English and Turkish.
Run `python3 tools/strings.py` to regenerate `Strings.resx` / `Strings.tr.resx`.
To add a language, add a column and a culture to `Loc.Languages`.

## Hardware information sources
| Area | Sources |
|---|---|
| CPU | CPUID (vendor, brand, signature, ISA extensions, cache descriptors, VT-x / AMD-V, hypervisor, leaf 0x16 clocks), GetLogicalProcessorInformationEx (cores, threads, P/E split, cache instances), registry (microcode revision), WMI (socket, firmware VT, SLAT) |
| GPU | PnP / PCI ids, bus / device / function and PCIe current / max link (SetupDiGetDeviceProperty), display-driver registry (VRAM size, VBIOS string, chip / DAC type), DXGI (memory, adapters), D3D12 / D3D11 feature-level probe, Vulkan (API / driver version), NVML (architecture, VBIOS, bus width, clocks, power limits, PCIe generation, thermal thresholds, CUDA), NVAPI (base / boost clocks), HWUltimate chip catalog for officially published die figures (labelled as externally reported) |
| Memory | SMBIOS type 16 / 17 (ECC capability, slots, max capacity, data / total width, rank, rated vs configured speed, voltage), Windows memory manager (commit, virtual memory, cache, pools) |
| Motherboard | SMBIOS types 0 / 1 / 2, chipset LPC / ISA bridge and host bridge (PCI class 06-01 / 06-00), Super I/O and EC from the sensor provider |
| Storage | MSFT_PhysicalDisk, MSFT_StorageReliabilityCounter (temperature, wear, power-on hours, error counters, latency), live sensors (e.g. data read / written) |
| Network | WMI + System.Net.NetworkInformation (status, negotiated speed, IPv4 / IPv6, gateway, DNS, MTU) |
| Display | WmiMonitorID / WmiMonitorConnectionParams (EDID identity, connection type) |

## Known limitations
- **Not run on Windows here.** The UI and native calls (DXGI, D3D12 / D3D11, Vulkan, NVML, SetupAPI) were compiled and covered by tests. The application has not been launched and viewed on Windows in this environment.
- **Unavailable properties**:
  - ASIC quality, SPD contents and memory timings
  - memory channels and ECC operating status
  - VRAM type and memory bandwidth, CPU TDP
  - supported GPU outputs (only connected monitors are listed)
  - raw SMART tables
- **GPU chip codename / die / process / transistors** come only from HWUltimate's catalog of about 30 common GPUs and are labelled as an official specification (catalog).
- **PawnIO** is not bundled (it is a separately installed, signed driver). Without it LibreHardwareMonitor cannot read Super I/O, SMBus, MSR or SMU data.
- **AMD Ryzen per-core temperatures**: AMD exposes Tctl / Tdie and per-CCD temperatures; LibreHardwareMonitor does not provide per-core temperatures on Ryzen, and HWUltimate does not derive them. Intel processors report every core.
- **GPU voltage / frequency curve and ASIC quality** are not read: only undocumented, generation-specific interfaces exist.
- **AMD ADLX** is detected and initialised but not bound for metrics. AMD GPU sensors come from LibreHardwareMonitor.
- **Inventory merge**: two identical GPUs merge correctly only when Windows reports their PCI location. Keyless entries with an ambiguous name are kept separate rather than guessed.
- **Not measured**: on-Windows CPU / RAM use (see `docs/AUDIT-v0.2.md`).

## Third-party
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

LibreHardwareMonitorLib (MPL-2.0), LiveCharts2 (MIT), CommunityToolkit.Mvvm (MIT), Serilog (Apache-2.0),
Microsoft.Data.Sqlite (MIT). NVIDIA NVAPI and AMD ADLX are used through the runtimes installed with the vendor drivers.
No proprietary code, branding or assets from other products are included.
