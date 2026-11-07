# HWUltimate v0.1 → v0.2 audit

Baseline: tag `v0.1.0` (commit f3d8338). Work branch: `release/v0.2`.

## Architecture (v0.1)

| Layer | Project | Responsibility |
|---|---|---|
| Domain | HWUltimate.Domain | Sensor / device / inventory / alert / report models |
| Core | HWUltimate.Core | Interfaces, central `SensorPollingService`, statistics, explorer tree builder, alerts, history recorder, stress engine, report rendering, CPUID / PCI decoding |
| Providers | HWUltimate.Hardware (+ .Nvidia, .Amd) | LibreHardwareMonitor, Windows memory manager, NVAPI + NVML, ADLX loader |
| Infrastructure | HWUltimate.Infrastructure | WMI, SMBIOS, SetupAPI / PnP, CPU / GPU / motherboard discovery, settings and preference stores, Serilog |
| Storage | HWUltimate.Storage | SQLite sensor history |
| UI | HWUltimate.UI | Theme dictionaries, styles, converters, icons, localisation |
| App | HWUltimate.App | WPF shell, MVVM view models, DI composition root |

A single polling loop (`SensorPollingService`) feeds every consumer. The UI marshals coalesced snapshots onto the dispatcher.

## Findings

### Obsolete pages (removed in v0.2)
- Dashboard (`DashboardView` / `DashboardViewModel`, LiveCharts)
- History (`HistoryView` / `HistoryViewModel`, plus `HistoryRecorder`, which writes all sensors to SQLite every 2 s)
- Stability Test (`StabilityView` / `StabilityViewModel`; CPU / RAM workloads only, GPU backend missing)

### Controls that do not follow the theme
- `ComboBox` has no template: white popup and chrome in dark mode (Settings, Reports, Sensors toolbar, Alerts grid).
- `ContextMenu` / `MenuItem`, `ScrollBar`, `Slider` and the `CheckBox` box use default Windows styling.
- Error message boxes use `MessageBox` (system styling).

### Duplicate hardware
- The same NVIDIA GPU appears twice in the sensor tree: once as `lhm:/gpu-nvidia/0` (LibreHardwareMonitor) and once as `nvapi:gpu0` (NVAPI / NVML). Devices carry no physical identity.
- The inventory merges by device name, so two identical physical GPUs collapse into one entry.
- LibreHardwareMonitor and the Windows memory manager each produce a separate "memory" device.

### Sensor classification
- Configuration values and thresholds (e.g. NVMe "Warning / Critical Composite Temperature", "Thermal Sensor Low / High Limit", "Temperature Sensor Resolution") are typed as temperatures and shown in live Temperatures groups.
- They also feed `MaxProcessorTemperature` and alert evaluation.

### Alerts
- One threshold per rule. "High" and "critical" are separate rules, so one event raises two unrelated alerts.
- There is no cooldown. A sensor oscillating around the hysteresis band re-alerts repeatedly.
- The message text does not distinguish warning, critical, device-reported and user thresholds.
- Thresholds are identical for every vendor and device.

### Reports
- The preview stays blank until "Preview" is clicked, and always renders plain text.
- Report sections still include stability tests. There are no per-component sections (CPU / GPU / memory / storage).
- Export success is reported without validating the written file.

## Baseline measurements (Linux container, Release, .NET 10.0.114)

`dotnet run -c Release --project tools/HwuPerf` — 12 synthetic devices × 40 sensors = 480 sensors, three runs:

| Operation | Time / op | Allocated / op |
|---|---|---|
| `SensorPollingService.PollOnce` | 394–405 µs | 306–316 KB |
| `SensorExplorerBuilder.Build` | 247–268 µs | 200 KB |
| `Build` with search filter | 143–165 µs | 130 KB |

Publish output (`win-x64`, framework-dependent): 54,975,939 bytes in 61 files; zip 17,913,284 bytes.

On-Windows CPU and memory usage of the WPF process could not be measured in this environment.

## After the upgrade (same environment, same synthetic load)

| Operation | v0.1 | v0.2 | Change |
|---|---|---|---|
| `SensorPollingService.PollOnce` | 394–405 µs, 306–316 KB | 342–352 µs, 332–335 KB | ~13 % faster; ~5 % more allocation (physical-device merge, sensor kinds, duplicate suppression) |
| `SensorExplorerBuilder.Build` | 247–268 µs, 200 KB | 246–249 µs, 229 KB | same time; ~14 % more allocation (device / provider / status filters, pins) |
| `Build` with search | 143–165 µs, 130 KB | 150–174 µs, 154 KB | similar time; more allocation |

The explorer rebuilds its tree only when the sensor set, filters or preferences change. When sorted by name or in custom order, value updates are applied to existing rows in place, with no rebuild.

Publish output (`win-x64`, framework-dependent, without PDBs):

| | v0.1 | v0.2 |
|---|---|---|
| Size | 54,975,939 bytes | 32,384,703 bytes (−41 %) |
| Files | 61 | 34 |

The reduction comes from dropping LiveCharts / SkiaSharp (the Dashboard, History and Stability pages) and SQLite (history recording) from the application.

On-Windows CPU and RAM use of the running app, were **not measured**: the WPF app cannot run in this Linux build environment.

## Data migration

- `settings.json` (v0.1, flat) is converted automatically to the v0.2 sectioned schema on first start. The original is kept as `settings.v1.json`. v0.1 single-threshold alert rules are not carried over; the component-aware rules replace them.
- `sensor-explorer.json`: the v0.1 value `"Detailed"` maps to the v0.2 Advanced mode. All other preferences (hidden, favourites, order, columns) carry over unchanged, keyed by the same sensor ids.
- `history.db` is **left untouched** on disk. v0.2 neither reads nor writes it. The SQLite repository (`HWUltimate.Storage`) stays in the solution, outside the application, so a future version can export or import that data. Users can delete the file themselves.

## Post-v0.2.0 change: Mini Overlay and Alerts removed

At the user's request the Mini Overlay (overlay window, global shortcut, PresentMon FPS / frame-time source) and the Alerts feature (component-aware rules, threshold resolver, toasts, navigation badge, report alert history) were removed. Navigation is now Sensors · Hardware · Reports · Settings. The Sensors explorer keeps its data-quality status (Suspect / Stale); value-threshold colouring, which came from the alert rules, is gone. `Overlay` and `Alerts` sections in an existing `settings.json` are ignored on load and dropped on the next save; every other setting is kept (covered by `SettingsV2Tests`). Sections above that describe the overlay or alerts are the historical v0.2.0 audit.
