# Third-party components

| Component | Licence | How it is used | Bundled? |
|---|---|---|---|
| LibreHardwareMonitorLib 0.9.6 | MPL-2.0 | Sensor provider (unmodified NuGet package) | Yes (DLL) |
| CommunityToolkit.Mvvm | MIT | MVVM source generators | Yes |
| Microsoft.Extensions.* / System.Management | MIT | DI, logging abstractions, WMI | Yes |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Logging | Apache-2.0 | Logging | Yes |
| NVIDIA NVAPI / NVML | NVIDIA driver runtime | Loaded from the installed driver (`nvapi64.dll`, `nvml.dll`) | No |
| AMD ADLX | AMD driver runtime | Loaded from the installed driver (`amdadlx64.dll`) | No |
| PresentMon (GameTechDev / Intel) | MIT | Optional external process for FPS / frame time | **No.** Users install it or set its path; nothing is redistributed. If a future build bundles it, its MIT licence text must ship alongside. |

MPL-2.0 is file-level copyleft. Nova ships LibreHardwareMonitorLib unmodified as a separate DLL; its source is available from the upstream project.
