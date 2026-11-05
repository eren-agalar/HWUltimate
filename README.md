# NOVA HARDWARE

**Your Hardware. Every Detail. One Place.**

Nova Hardware is a Windows 10/11 (x64) desktop application for hardware monitoring, system identification,
historical sensor analytics, stability testing and hardware reporting. Built with C# / .NET 10 LTS and WPF.

## Features

| Area | What it does |
|---|---|
| **Dashboard** | Live CPU / GPU / memory load, temperature, power and clock; 2-minute utilisation and temperature charts; storage temperatures; fans; provider health. |
| **Sensor Explorer** | Virtualised tree: Category → Device → Sensor group → Sensor (CPU per-core, thermal throttling, GPU hotspot / memory temperatures / VRAM / performance limiters, storage activity / health, …). Shows current / min / max / average values. Columns can be shown or hidden. Expand or collapse everything or a single node. Sensors can be hidden, marked as favourite (with their own ordered group), searched, filtered by hardware or sensor type, sorted, and reordered manually. Min/max can be reset for all sensors or one. Compact and detailed modes. Refresh interval is user-selectable. All preferences persist, keyed by stable sensor ids. |
| **Hardware information center** | Device list with icons, and per device the Overview / Identification / Technical Specifications / Performance Capabilities / Firmware & Drivers / Advanced Details sections, plus live sensors. Every value shows its source API. Unsupported properties are listed explicitly as *Not Available*, with the reason. Search covers all properties. You can copy a single value, a property, a device report or everything, and export to TXT / JSON / HTML. |
| **History** | SQLite-backed sensor history with 15 min – 7 day ranges, min/avg/max aggregation and range statistics. Configurable sampling and retention. |
| **Stability test** | Self-verifying CPU floating-point, integer and SIMD workloads and a pattern-based RAM test. Any wrong result counts as an error. A thermal limit (default 95 °C) aborts the test automatically. |
| **Alerts** | Threshold rules per sensor or per sensor type and device category, with sustain time and hysteresis. Shown as in-app notifications. |
| **Reports** | HTML, JSON, plain text and CSV exports. You pick which sections to include. |
| **Settings** | Polling interval, history, language (English / Türkçe, switches live), dark / light theme, vendor providers. |

### Data integrity rules
- Sensor values are **never fabricated or interpolated**. A sensor with no value shows **N/A**.
- Values keep the provider's original unit. The only conversion is NVAPI kHz to MHz, which is exact and labelled.
- Values outside a broad physical range are kept unchanged but flagged **Suspect**. Suspect values are excluded from alerts.
- If a provider fails, its last real values are shown as **Stale** for up to 10 s and then removed.
- Min / max / average come only from real samples in the current session.

## Architecture

```
NovaHardware.Domain           Unified models (SensorReading, HardwareDevice, DeviceInfo, alerts, stress, reports)
NovaHardware.Core             Interfaces + platform-neutral services (polling, statistics, alerts, history recorder,
                              stress engine, report export)
NovaHardware.Hardware         LibreHardwareMonitor provider
NovaHardware.Hardware.Nvidia  NVAPI provider (nvapi_QueryInterface, unmanaged function pointers)
NovaHardware.Hardware.Amd     ADLX runtime loader/provider
NovaHardware.Infrastructure   WMI, SMBIOS (GetSystemFirmwareTable + parser), SetupAPI, inventory merge, settings, Serilog
NovaHardware.Storage          SQLite history repository (WAL, WITHOUT ROWID clustered samples)
NovaHardware.UI               Themes, styles, converters, localisation (resx: en, tr)
NovaHardware.App              WPF shell, MVVM view models (CommunityToolkit.Mvvm), LiveCharts2 views, DI composition root
NovaHardware.Tests            xUnit v3 tests (64)
```

The UI only depends on `NovaHardware.Core` interfaces (`IHardwareProvider`, `ISensorProvider`, `IDeviceDiscoveryService`,
`ISensorPollingService`, `IHardwareInventoryService`, `IHistoricalDataService`, `IAlertService`, `IReportExportService`,
`IStressTestService`). Concrete implementations are wired up only in `App/Services/Bootstrapper.cs`.

## Build

Requires the .NET 10 SDK.

```bash
dotnet build NovaHardware.sln
```

```bash
dotnet test --project tests/NovaHardware.Tests
```

```bash
dotnet publish src/NovaHardware.App -c Release -r win-x64 --self-contained false -o publish
```

The app requests `highestAvailable` elevation. Administrators get full sensor access (Super I/O, voltages, some CPU
sensors). Standard users get reduced sensors and a banner offering to restart elevated.

User data is stored in `%LOCALAPPDATA%\NovaHardware` (`settings.json`, `history.db`, `logs\`, `reports\`).

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

## Known limitations (current milestone)
- **ASIC / silicon quality** is shown as *Not Available*: NVAPI, NVML, ADLX and Windows expose no such metric, and Nova never estimates it.
- **SPD contents and memory timings** are *Not Available*: they need SMBus access, which no supported Windows API provides. SMBIOS is a firmware summary, not SPD.
- **Memory channels and ECC operating status** are *Not Available*, because they cannot be determined reliably.
- **VRAM type and memory bandwidth** are *Not Available*, because no public API reports the memory type.
- **CPU TDP** is *Not Available*, because it is not exposed by CPUID, SMBIOS or WMI.
- **Supported GPU display outputs** are *Not Available*. Only connected monitors and their connection types are shown.
- **GPU chip codename, die size, transistors and process** come only from Nova's built-in catalog of public vendor figures for about 30 common GPUs. They are labelled as externally reported.
- **Raw SMART attribute tables and NVMe identify data** are not parsed directly. Windows reliability counters and the sensor provider's values are shown instead.
- Two physically identical devices with the same name (e.g. two identical GPUs) are merged into one inventory entry.
- **GPU stress test**: the engine has a pluggable `IGpuStressBackend`, but no compute backend (for example D3D11 compute) ships yet. The option is shown as unavailable.
- **AMD ADLX**: the runtime is detected and initialised, but metric interfaces are not bound yet. AMD GPU sensors come from LibreHardwareMonitor in the meantime.
- The RAM test runs in user mode and cannot test memory used by Windows or other processes.
- The UI was compiled and checked statically on a non-Windows CI host. Run it on Windows to verify layout and visuals.

## Third-party
LibreHardwareMonitorLib (MPL-2.0), LiveCharts2 (MIT), CommunityToolkit.Mvvm (MIT), Serilog (Apache-2.0),
Microsoft.Data.Sqlite (MIT). NVIDIA NVAPI and AMD ADLX are used through the runtimes installed with the vendor drivers.
No proprietary code, branding or assets from other products are included.
