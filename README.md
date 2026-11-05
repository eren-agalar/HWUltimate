# NOVA HARDWARE

**Your Hardware. Every Detail. One Place.**

Nova Hardware is a Windows 10/11 (x64) desktop application for hardware monitoring, system identification,
historical sensor analytics, stability testing and hardware reporting. Built with C# / .NET 10 LTS and WPF.

## Features

| Area | What it does |
|---|---|
| **Dashboard** | Live CPU / GPU / memory load, temperature, power and clock; 2-minute utilisation and temperature charts; storage temperatures; fans; provider health. |
| **Sensors** | Every sensor from every provider, grouped by device, with current / min / max / average, unit, data quality and source. Search and min/max reset. |
| **Hardware** | System summary plus a device inventory merged from WMI, the raw SMBIOS table and SetupAPI (CPU, GPU incl. real VRAM size, DIMMs, disks incl. NVMe/SSD/HDD and health, network, monitors (EDID), audio, USB/HID, battery). |
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

## Known limitations (current milestone)
- **GPU stress test**: the engine has a pluggable `IGpuStressBackend`, but no compute backend (for example D3D11 compute) ships yet. The option is shown as unavailable.
- **AMD ADLX**: the runtime is detected and initialised, but metric interfaces are not bound yet. AMD GPU sensors come from LibreHardwareMonitor in the meantime.
- The RAM test runs in user mode and cannot test memory used by Windows or other processes.
- The UI was compiled and checked statically on a non-Windows CI host. Run it on Windows to verify layout and visuals.

## Third-party
LibreHardwareMonitorLib (MPL-2.0), LiveCharts2 (MIT), CommunityToolkit.Mvvm (MIT), Serilog (Apache-2.0),
Microsoft.Data.Sqlite (MIT). NVIDIA NVAPI and AMD ADLX are used through the runtimes installed with the vendor drivers.
No proprietary code, branding or assets from other products are included.
