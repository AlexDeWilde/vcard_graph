# GPU Widget

A lightweight, borderless desktop overlay that displays real-time GPU 3D engine utilisation as a scrolling graph — matching the visual style of the Windows Task Manager performance tab.

---

## Files

| File            | Purpose                                                       |
| --------------- | ------------------------------------------------------------- |
| `GPUWidget.cs`  | Full C# source — single file, single class                    |
| `GPUWidget.exe` | Compiled executable — self-contained, copy anywhere           |
| `GPUWidget.cfg` | Split-chart configuration — zone lengths and split ratio      |
| `GPUWidget.pos` | Last saved position/size — created on first close, plain text |
| `compile.bat`   | Rebuild script — no Visual Studio needed                      |
| `README.md`     | This file                                                     |

---

## Usage

**Run:** Double-click `GPUWidget.exe`. No installation, no dependencies beyond Windows itself.

**Move:** Click and drag anywhere on the widget body.

**Resize:** Drag any edge or corner. Minimum size: 200 x 80 px. Resizing changes pixel density, not the time length displayed.

**Settings:** Right-click > Settings to adjust the split ratio and zone lengths.

**Close:** Right-click > Close.

The widget starts at position (60, 60) on screen and opens at 320 x 115 px by default.

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

## Split chart

The chart is divided into two zones with distinct visual styles:

- **Right zone (purple)** — high-resolution recent data that scrolls smoothly left, one sample per second.
- **Left zone (orange)** — compressed historical data rendered with time-aligned bins for a stable, static appearance.

As new samples arrive, data scrolls left through the right zone. When it crosses the split boundary it is compressed into the left zone at lower time resolution. A subtle vertical divider marks the boundary.

The left zone uses time-aligned binning: bin boundaries are anchored to absolute tick counts rather than sliding with each new sample. This prevents bar heights from oscillating as data shifts in, giving the left zone a steady, static look.

The total visible history is the sum of the two zone lengths. For example, with `length_left=1800` and `length_right=600`, the widget shows 40 minutes of history.

| Property         | Value                                                |
| ---------------- | ---------------------------------------------------- |
| Sample rate      | 1 sample per second                                  |
| History buffer   | 7200 samples (2 hours max)                           |
| Graph direction  | Left = oldest, right = newest (same as Task Manager) |
| Value range      | 0 -- 100%                                            |
| Update on resize | Pixel density adapts; time lengths preserved         |

---

## Configuration — `GPUWidget.cfg`

Place `GPUWidget.cfg` next to the executable. The widget reads it at startup; edit the file and restart to apply changes, or use right-click > Settings to adjust at runtime. If the file is absent, defaults are written on first run.

```ini
; GPUWidget split-chart configuration
; Edit here or use right-click > Settings

; Left zone width as % of chart (right = 100 - left)
split_left=20

; Seconds of history shown in each zone (fixed on resize)
length_left=310
length_right=250
```

| Key            | Default | Meaning                                              |
| -------------- | ------- | ---------------------------------------------------- |
| `split_left`   | `20`    | Width of the left zone as % of total chart width     |
| `length_left`  | `310`   | Seconds of history shown in the left (orange) zone   |
| `length_right` | `250`   | Seconds of history shown in the right (purple) zone  |

The right zone percentage is always `100 - split_left`. Lines starting with `;` are comments. Keys are case-insensitive.

### Settings dialog

Right-click > Settings opens a dialog with three editable fields:

- **Split Left %** — left zone width as percentage (right % updates automatically)
- **Length Left (s)** — seconds of history in the left zone
- **Length Right (s)** — seconds of history in the right zone

Changes are applied immediately and saved to `GPUWidget.cfg`.

---

## Visual design

Matches the Windows 11 Task Manager dark-theme GPU graph, with an orange left zone for visual distinction.

| Element            | Colour (RGB)   | Hex       |
| ------------------ | -------------- | --------- |
| Background         | 23, 23, 23     | `#171717` |
| Grid lines         | 38, 38, 38     | `#262626` |
| Right graph line   | 224, 64, 251   | `#e040fb` |
| Right graph fill   | 50, 0, 60      | `#32003c` |
| Left graph line    | 240, 150, 30   | `#f0961e` |
| Left graph fill    | 55, 30, 5      | `#371e05` |
| Text               | 176, 176, 176  | `#b0b0b0` |
| Window border      | 55, 55, 55     | `#373737` |
| Split divider      | 60, 60, 60     | `#3c3c3c` |

Grid: 3 vertical + 3 horizontal lines (4x4 divisions), no axis labels.

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

`NV_GPU_THERMAL_SETTINGS_V1` is 68 bytes; `version` field = `sizeof | (1 << 16)` = `0x00010044`. The call uses `sensorIndex = 15` (NVAPI_THERMAL_TARGET_ALL) and returns up to 3 sensor readings. The one with `target == 1` (NVAPI_THERMAL_TARGET_GPU) is the GPU core.

**AMD — ADL (`atiadlxx.dll` / `atiadlxy.dll`)**
Functions resolved via `GetProcAddress`. Call sequence: `ADL_Main_Control_Create` > `ADL_Adapter_NumberOfAdapters_Get` > `ADL_Overdrive5_Temperature_Get`. `ADLTemperature.iTemperature` is in millidegrees Celsius (divide by 1000); a heuristic `> 300` guard handles drivers that return whole degrees instead.

Neither `nvapi64.dll` nor `atiadlxx.dll` is shipped with the widget — they are part of the respective GPU drivers already on the machine.

## Architecture

Single C# file, single class `GPUWidget : Form`. No NuGet packages, no project file.

### Rendering pipeline

1. **Timer tick** (every 1 s on the UI thread): reads GPU counter, writes to circular buffer, calls `Invalidate()`.
2. **OnPaint**: blits cached grid bitmap > draws header text > draws left zone (orange, time-aligned bins) > draws right zone (purple, per-pixel scrolling) > draws split divider > draws border.

### Key design decisions

**No per-frame heap allocations.**
`PointF[]` arrays for line and fill polygons are allocated per zone and reused every frame. The history uses a circular buffer (`float[7200]` + integer head pointer) — adding a sample is O(1) with no shifting or copying.

**GDI resources created once.**
All `Pen` and `SolidBrush` objects are created in the constructor and reused for the lifetime of the widget. No GDI object is created or destroyed during a paint call.

**Grid cached as a Bitmap.**
The 6 grid lines are drawn once into a `Bitmap` when the widget first paints or after a resize. Each subsequent frame does a single `Graphics.DrawImage` blit instead of 6 `DrawLine` calls.

**Time-aligned binning for left zone.**
Each left-zone pixel represents a bin of N samples. Bin boundaries are aligned to absolute `_tickCount` values, not to the sliding buffer head. This means bin contents only change every N seconds (when a full new bin completes), giving the left zone a stable, static appearance instead of oscillating bar heights.

**Separate left/right rendering.**
Each zone has its own pre-allocated `PointF[]` arrays, colours, and drawing logic. The right zone renders per-pixel with smooth scrolling; the left zone renders averaged bins with time alignment.

**Native window chrome via WM_NCHITTEST.**
The form has `FormBorderStyle.None`. Resize and drag are handled by returning appropriate HT* codes from `WM_NCHITTEST` — Windows handles all mouse tracking and cursor changes natively.

**Right-click via WM_NCRBUTTONUP.**
Because the entire client area returns `HTCAPTION` from `WM_NCHITTEST`, Windows classifies right-clicks as non-client events and sends `WM_NCRBUTTONUP` rather than `WM_RBUTTONUP`. The context menu is triggered directly from `WM_NCRBUTTONUP` in `WndProc`.

**Paint flags.**
`ControlStyles.OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint` — suppresses `WM_ERASEBKGND` (no background flicker), owns all painting, uses GDI+ double-buffer managed by the framework.

**Layout persistence via `GPUWidget.pos`.**
On close, the window's position and size are written to `GPUWidget.pos`, a plain text file placed next to the executable (`x,y,width,height`). On next launch `LoadLayout()` reads it back. The saved bounds are validated against all currently connected screens; if fewer than 40 x 40 px would be visible the save is discarded and defaults are used.

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

Output: a single `.exe`, ~23 KB. Requires .NET Framework 4.x at runtime (present on all Windows 10/11 machines by default).

The source targets **C# 5** (the language version supported by the .NET Framework 4 `csc.exe`) — no `?.`, no `using var`, no string interpolation, no named tuples.

---

## Known limitations

- The `GPU Engine` performance counter category requires a WDDM driver. Older or software-only GPUs may not expose it; the widget shows 0% in that case.
- On systems with multiple physical GPUs, all `engtype_3D` instances across all GPUs are summed into a single value, matching Task Manager's default aggregate view.
- The compiled `.exe` targets `anycpu` but the `.NET Framework 4` runtime it depends on is Windows-only.
