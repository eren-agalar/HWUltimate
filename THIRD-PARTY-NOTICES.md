# Third-party components

| Component | Licence | How it is used | Bundled? |
|---|---|---|---|
| LibreHardwareMonitorLib 0.9.6 | MPL-2.0 | Sensor provider (unmodified NuGet package) | Yes (DLL) |
| CommunityToolkit.Mvvm | MIT | MVVM source generators | Yes |
| Microsoft.Extensions.* / System.Management | MIT | DI, logging abstractions, WMI | Yes |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Logging | Apache-2.0 | Logging | Yes |
| NVIDIA NVAPI / NVML | NVIDIA driver runtime | Loaded from the installed driver (`nvapi64.dll`, `nvml.dll`) | No |
| AMD ADLX | AMD driver runtime | Loaded from the installed driver (`amdadlx64.dll`) | No |
| PawnIO (namazso) | see pawnio.eu | Low-level driver LibreHardwareMonitor uses for Super I/O, SMBus, MSR and SMU access | **No.** Users install it from the official site; HWUltimate only detects it and links there. |

MPL-2.0 is file-level copyleft. HWUltimate ships LibreHardwareMonitorLib unmodified as a separate DLL; its source is available from the upstream project.
