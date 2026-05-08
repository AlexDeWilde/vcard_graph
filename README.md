# GPU Widget

A lightweight, borderless desktop overlay that displays real-time GPU 3D engine utilisation as a scrolling graph — matching the visual style of the Windows Task Manager performance tab.

---

## Files

| File            | Purpose                                                       |
| --------------- | ------------------------------------------------------------- |
| `GPUWidget.cs`  | Full C# source — single file, single class                    |
| `GPUWidget.exe` | Compiled executable — self-contained, copy anywhere           |
| `GPUWidget.pos` | Last saved position/size — created on first close, plain text |
| `compile.bat`   | Rebuild script — no Visual Studio needed                      |
| `README.md`     | This file                                                     |

---

## Usage

**Run:** Double-click `GPUWidget.exe`. No installation, no dependencies beyond Windows itself.

**Move:** Click and drag anywhere on the widget body.

**Resize:** Drag any edge or corner. Minimum size: 160 × 80 px. No minimum maximum.

**Close:** Right-click → Close.

The widget starts at position (60, 60) on screen and opens at 320 × 115 px by default.

---

## What it monitors

The widget reads the Windows Performance Counter:

```
\GPU Engine(*engtype_3D)\Utilization Percentage
```

This is the same data source used by Windows Task Manager's GPU performance tab. It measures the utilisation of the **3D engine** on your GPU — the engine responsible for rendering, games, DirectX/OpenGL workloads.

On systems with multiple GPU engines (e.g. NVIDIA cards expose separate 3D, Copy, Video Encode, Video Decode engines), only `engtype_3D` instances are selected and their values are **summed** to give a single total.

The counter category is `GPU Engine`, provided by the WDDM (Windows Display Driver Model) driver. It is available on all modern Windows 10/11 systems with a WDDM-compatible GPU. If the category is absent (e.g. a headless or very old driver), the widget shows 0% silently.

---

## Data & graph

| Property         | Value                                                |
| ---------------- | ---------------------------------------------------- |
| Sample rate      | 1 sample per second                                  |
| History length   | 300 samples (5 minutes)                              |
| Graph direction  | Left = oldest, right = newest (same as Task Manager) |
| Value range      | 0 – 100%                                             |
| Update on resize | Grid redrawn; history and scale preserved            |

The graph scrolls left as new samples arrive. The oldest sample drops off the left edge at the 5-minute mark.

---

## Visual design

Matches the Windows 11 Task Manager dark-theme GPU graph.

| Element       | Colour (RGB)  | Hex       |
| ------------- | ------------- | --------- |
| Background    | 23, 23, 23    | `#171717` |
| Grid lines    | 38, 38, 38    | `#262626` |
| Graph line    | 224, 64, 251  | `#e040fb` |
| Graph fill    | 50, 0, 60     | `#32003c` |
| Text          | 176, 176, 176 | `#b0b0b0` |
| Window border | 55, 55, 55    | `#373737` |

Grid: 3 vertical + 3 horizontal lines (4×4 divisions), no axis labels.

Header row (Segoe UI 9pt):

- **Left:** `TEMP 85C` — GPU core temperature in Celsius. Shows `TEMP --` when unavailable.
- **Right:** `GPU 3D  3%` — engine label and current utilisation percentage.

---

## GPU temperature

Temperature is read by the static `GpuTemp` class, which tries two driver APIs in order and returns `-1` (shown as `TEMP --`) if neither is present.

**NVIDIA — NVAPI (`nvapi64.dll` / `nvapi.dll`)**
`nvapi_QueryInterface` is the single exported entry point; all other functions are resolved by passing a 32-bit ID to it. IDs used:

| Function                       | ID           |
| ------------------------------ | ------------ |
| `NvAPI_Initialize`             | `0x0150E828` |
| `NvAPI_EnumPhysicalGPUs`       | `0xE5AC921F` |
| `NvAPI_GPU_GetThermalSettings` | `0xE3640A56` |

`NV_GPU_THERMAL_SETTINGS_V1` is 68 bytes; `version` field = `sizeof \| (1 << 16)` = `0x00010044`. The call uses `sensorIndex = 15` (NVAPI_THERMAL_TARGET_ALL) and returns up to 3 sensor readings. The one with `target == 1` (NVAPI_THERMAL_TARGET_GPU) is the GPU core.

**AMD — ADL (`atiadlxx.dll` / `atiadlxy.dll`)**
Functions resolved via `GetProcAddress`. Call sequence: `ADL_Main_Control_Create` → `ADL_Adapter_NumberOfAdapters_Get` → `ADL_Overdrive5_Temperature_Get`. `ADLTemperature.iTemperature` is in millidegrees Celsius (divide by 1000); a heuristic `> 300` guard handles drivers that return whole degrees instead.

Neither `nvapi64.dll` nor `atiadlxx.dll` is shipped with the widget — they are part of the respective GPU drivers already on the machine.

## Architecture

Single C# file, single class `GPUWidget : Form`. No NuGet packages, no project file.

### Rendering pipeline

1. **Timer tick** (every 1 s on the UI thread): reads GPU counter, writes to circular buffer, calls `Invalidate()`.
2. **OnPaint**: blits cached grid bitmap → draws header text → draws filled polygon + line from pre-allocated arrays → draws border.

### Key design decisions

**No per-frame heap allocations.**
`PointF[]` arrays for the line (300 points) and fill polygon (302 points) are allocated once at startup and reused every frame. The history uses a circular buffer (`float[300]` + integer head pointer) — adding a sample is O(1) with no shifting or copying.

**GDI resources created once.**
All `Pen` and `SolidBrush` objects are created in the constructor and reused for the lifetime of the widget. No GDI object is created or destroyed during a paint call.

**Grid cached as a Bitmap.**
The 6 grid lines are drawn once into a `Bitmap` when the widget first paints or after a resize. Each subsequent frame does a single `Graphics.DrawImage` blit instead of 6 `DrawLine` calls.

**Native window chrome via WM_NCHITTEST.**
The form has `FormBorderStyle.None`. Resize and drag are handled by returning appropriate HT* codes from `WM_NCHITTEST` — Windows then handles all mouse tracking and cursor changes natively. No manual `MouseMove` drag tracking.

**Right-click via WM_NCRBUTTONUP.**
Because the entire client area returns `HTCAPTION` from `WM_NCHITTEST`, Windows classifies right-clicks as non-client events and sends `WM_NCRBUTTONUP` rather than `WM_RBUTTONUP`. `WM_CONTEXTMENU` is not generated for caption hits. The context menu is therefore triggered directly from `WM_NCRBUTTONUP` in `WndProc`.

**Paint flags.**
`ControlStyles.OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint` — suppresses `WM_ERASEBKGND` (no background flicker), owns all painting, uses GDI+ double-buffer managed by the framework.

**Layout persistence via `GPUWidget.pos`.**
On close, the window's position and size are written to `GPUWidget.pos`, a plain text file placed next to the executable (`x,y,width,height`). On next launch `LoadLayout()` reads it back. The saved bounds are validated against all currently connected screens; if fewer than 40 × 40 px would be visible (e.g. a monitor was disconnected) the save is discarded and defaults are used. The file is not a registry entry, not an `.ini` file, and travels with the exe when the folder is moved or copied. If the file is absent or unreadable the widget silently falls back to its default position and size. Note: the original NTFS Alternate Data Stream approach was abandoned because security/AV tools commonly block or strip ADS writes on `.exe` files.

**Counter refresh.**
The list of `GPU Engine` instances is re-enumerated every 30 seconds. Instances appear and disappear as GPU-using processes start and stop; refreshing prevents stale counter handles from silently returning 0.

---

## Compilation

Requires nothing beyond what ships with Windows — the .NET Framework 4 compiler is at:

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
```

Run `compile.bat` from the project folder, or manually:

```bat
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" ^
    /nologo /out:GPUWidget.exe /t:winexe /platform:anycpu /optimize+ ^
    /reference:System.Windows.Forms.dll ^
    /reference:System.Drawing.dll ^
    GPUWidget.cs
```

Output: a single `.exe`, ~10 KB. Requires .NET Framework 4.x at runtime (present on all Windows 10/11 machines by default).

The source targets **C# 5** (the language version supported by the .NET Framework 4 `csc.exe`) — no `?.`, no `using var`, no string interpolation, no named tuples.

---

## Possible extensions

A few things that could be added without restructuring the code:

**Multiple metrics** — add a second `PerformanceCounterCategory` read (e.g. `engtype_VideoDecode`, `engtype_Copy`, CPU `% Processor Time`) and overlay additional coloured lines or separate panels.

**Always-on-top toggle** — add a menu item that toggles `this.TopMost`.

**Opacity / click-through** — `this.Opacity = 0.85` for a semi-transparent overlay. Adding `WS_EX_TRANSPARENT` to `CreateParams` makes it click-through.

**Remember position** — save window bounds to a `.ini` or registry key in `OnFormClosing`; restore in constructor.

**Startup with Windows** — add a `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry entry pointing to the `.exe` path.

**Configurable history length** — expose `HISTORY` as a runtime setting; longer history (e.g. 3600 for 1 hour) only costs an additional ~3 KB of RAM per float array.

**Other engine types** — replace `engtype_3D` with `engtype_VideoDecode`, `engtype_VideoEncode`, or `engtype_Copy` to monitor other GPU workloads. Or add a right-click submenu to switch between them at runtime.

**Smooth interpolation** — pass `Graphics.SmoothingMode = SmoothingMode.AntiAlias` before `DrawLines` for a smoother curve at the cost of slightly more CPU per frame.

---

## Known limitations

- The `GPU Engine` performance counter category requires a WDDM driver. Older or software-only GPUs may not expose it; the widget shows 0% in that case.
- On systems with multiple physical GPUs, all `engtype_3D` instances across all GPUs are summed into a single value, matching Task Manager's default aggregate view.
- The compiled `.exe` targets `anycpu` but the `.NET Framework 4` runtime it depends on is Windows-only.
