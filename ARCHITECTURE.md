# Architecture

Visual reference for the GPU Widget. All diagrams use [Mermaid](https://mermaid.js.org/) syntax.

---

## 1. Runtime Loop

How one second of widget operation flows from timer tick to painted frame.

```mermaid
flowchart TD
    T["Timer.Tick (1 s)"] --> RG["ReadGPU()\nSum all engtype_3D counters"]
    T --> RT["GpuTemp.Read()\nNVAPI or ADL"]
    RG --> BUF["Circular buffer\nfloat[7200] + head pointer"]
    RT --> TEMP["_tempC"]
    BUF --> INV["Invalidate()"]
    TEMP --> INV
    INV --> PAINT["OnPaint"]

    PAINT --> GRID["DrawImage — cached grid bitmap"]
    PAINT --> HDR["DrawString — TEMP + GPU %"]
    PAINT --> LEFT["Left zone (orange)\nTime-aligned bins\nAveraged, stable"]
    PAINT --> RIGHT["Right zone (purple)\nPer-pixel smooth scrolling"]
    PAINT --> BORDER["DrawRectangle — window border"]
```

---

## 2. Class Structure

All types in GPUWidget.cs and their relationships.

```mermaid
classDiagram
    class GPUWidget {
        -float[] _buf  (7200 circular)
        -int _head, _tickCount
        -float _current
        -int _tempC
        -PerformanceCounter[] _counters
        -PointF[] _lineLeft, _lineRight
        -Bitmap _gridCache
        +GPUWidget()
        -OnTick()
        -ReadGPU() float
        -OnPaint()
        -RebuildGridCache()
        -InitCounters()
        -ShowSettings()
        -LoadConfig() / SaveConfig()
        -LoadLayout() / SaveLayout()
        #WndProc(ref Message)
    }
    GPUWidget --|> Form

    class ConfigDialog {
        +float SplitLeftPct
        +float LengthLeft
        +float LengthRight
        -OnOk()
        -UpdateDerived()
    }
    ConfigDialog --|> Form
    GPUWidget ..> ConfigDialog : opens

    class GpuTemp {
        -bool _nvOk, _adlOk
        -InitNv()$
        -InitAdl()$
        +Read()$ int
    }
    GPUWidget ..> GpuTemp : reads temperature
```

---

## 3. Data Sources

Where the widget gets GPU utilisation and temperature, and how each is resolved.

```mermaid
flowchart LR
    subgraph "Windows Performance Counters"
        PC["GPU Engine\n(*engtype_3D)\nUtilization Percentage"]
    end

    subgraph "GPU Temperature (native DLL)"
        NV["nvapi64.dll\nNvAPI_GPU_GetThermalSettings\ntarget=GPU core"]
        AMD["atiadlxx.dll\nADL_Overdrive5_Temperature_Get\niTemperature / 1000"]
    end

    PC -->|"sum all 3D instances"| UTIL["_current  (0-100%)"]
    NV -->|"tried first"| TEMPC["_tempC  (celsius)"]
    AMD -->|"fallback"| TEMPC

    UTIL --> DISPLAY["Header: GPU 3D  42%"]
    TEMPC --> DISPLAY2["Header: TEMP 68C"]
```

---

## 4. Window Chrome via WndProc

How borderless drag, resize, and right-click are handled without a title bar.

```mermaid
stateDiagram-v2
    [*] --> WM_NCHITTEST : mouse event
    WM_NCHITTEST --> HTCAPTION : interior (drag to move)
    WM_NCHITTEST --> HTLEFT : left edge
    WM_NCHITTEST --> HTRIGHT : right edge
    WM_NCHITTEST --> HTTOP : top edge
    WM_NCHITTEST --> HTBOTTOM : bottom edge
    WM_NCHITTEST --> HTTOPLEFT : top-left corner
    WM_NCHITTEST --> HTTOPRIGHT : top-right corner
    WM_NCHITTEST --> HTBOTTOMLEFT : bottom-left corner
    WM_NCHITTEST --> HTBOTTOMRIGHT : bottom-right corner

    HTCAPTION --> WM_NCRBUTTONUP : right-click
    WM_NCRBUTTONUP --> ContextMenu : Settings / Close
```

---

## 5. File Map

```mermaid
graph TD
    subgraph repo["vcard_graph/"]
        CS["GPUWidget.cs\nSingle source file:\nGPUWidget · ConfigDialog · GpuTemp"]
        EXE["GPUWidget.exe\nCompiled output (~23 KB)"]
        CFG["GPUWidget.cfg\nsplit_left · length_left · length_right"]
        POS["GPUWidget.pos\nx,y,width,height\n(created on first close)"]
        BAT["compile.bat\ncsc.exe /t:winexe /optimize+"]
        README["README.md"]
        ARCH["ARCHITECTURE.md"]
    end

    CS -->|"compile.bat"| EXE
    EXE -->|"reads at startup"| CFG
    EXE -->|"reads/writes"| POS
```
