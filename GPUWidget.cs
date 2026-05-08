using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

/// <summary>
/// Borderless GPU 3D utilisation + temperature widget — Task Manager style.
/// Drag anywhere to move  |  drag edges/corners to resize  |  right-click to close.
/// Layout (position + size) is persisted in an NTFS Alternate Data Stream on the .exe.
/// </summary>
public class GPUWidget : Form
{
    // ── colours ───────────────────────────────────────────────────────────────
    private static readonly Color C_BG   = Color.FromArgb(23,  23,  23);
    private static readonly Color C_GRID = Color.FromArgb(38,  38,  38);
    private static readonly Color C_LINE = Color.FromArgb(224, 64,  251);
    private static readonly Color C_FILL = Color.FromArgb(50,  0,   60);
    private static readonly Color C_TEXT = Color.FromArgb(176, 176, 176);
    private static readonly Color C_BORD = Color.FromArgb(55,  55,  55);

    // ── constants ─────────────────────────────────────────────────────────────
    private const int HISTORY  = 300;
    private const int INTERVAL = 1000;
    private const int BORDER   = 6;
    private const int PAD      = 4;
    private const int HDR      = 22;

    // ── circular history buffer ───────────────────────────────────────────────
    private readonly float[] _buf  = new float[HISTORY];
    private int               _head = 0;
    private float             _current;
    private int               _tempC = -1;   // -1 = unavailable

    // ── pre-allocated paint arrays ────────────────────────────────────────────
    private readonly PointF[] _linePts = new PointF[HISTORY];
    private readonly PointF[] _polyPts = new PointF[HISTORY + 2];

    // ── cached GDI resources ──────────────────────────────────────────────────
    private readonly Pen        _gridPen;
    private readonly Pen        _linePen;
    private readonly Pen        _bordPen;
    private readonly SolidBrush _fillBrush;
    private readonly SolidBrush _textBrush;
    private readonly SolidBrush _bgBrush;
    private readonly Font       _font;

    // ── cached grid bitmap ────────────────────────────────────────────────────
    private Bitmap _gridCache;
    private Size   _gridCacheSize;

    // ── GPU load counters ─────────────────────────────────────────────────────
    private PerformanceCounter[] _counters = new PerformanceCounter[0];
    private int _refreshTick;

    // ── timer ─────────────────────────────────────────────────────────────────
    private Timer _timer;

    // ── context menu ──────────────────────────────────────────────────────────
    private ContextMenuStrip _menu;

    // ── WndProc constants ─────────────────────────────────────────────────────
    private const int WM_NCHITTEST   = 0x0084;
    private const int WM_NCRBUTTONUP = 0x00A5;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int HTCAPTION      = 2;
    private const int HTLEFT         = 10;
    private const int HTRIGHT        = 11;
    private const int HTTOP          = 12;
    private const int HTTOPLEFT      = 13;
    private const int HTTOPRIGHT     = 14;
    private const int HTBOTTOM       = 15;
    private const int HTBOTTOMLEFT   = 16;
    private const int HTBOTTOMRIGHT  = 17;

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    // ── constructor ───────────────────────────────────────────────────────────
    public GPUWidget()
    {
        _gridPen   = new Pen(C_GRID, 1f);
        _linePen   = new Pen(C_LINE, 1f);
        _bordPen   = new Pen(C_BORD, 1f);
        _fillBrush = new SolidBrush(C_FILL);
        _textBrush = new SolidBrush(C_TEXT);
        _bgBrush   = new SolidBrush(C_BG);
        _font      = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer, true);

        FormBorderStyle = FormBorderStyle.None;
        Size            = new Size(320, 115);
        MinimumSize     = new Size(200, 80);
        BackColor       = C_BG;
        StartPosition   = FormStartPosition.Manual;
        Location        = new Point(60, 60);

        _menu = new ContextMenuStrip();
        _menu.BackColor = Color.FromArgb(30, 30, 30);
        _menu.ForeColor = C_TEXT;
        _menu.Items.Add("Close", null, delegate { Close(); });

        InitCounters();

        Paint  += OnPaint;
        Resize += delegate { _gridCache = null; Invalidate(); };

        LoadLayout();   // restore last position/size from ADS

        _timer = new Timer();
        _timer.Interval = INTERVAL;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // ── GPU load monitoring ───────────────────────────────────────────────────

    private void InitCounters()
    {
        foreach (PerformanceCounter c in _counters)
            try { c.Dispose(); } catch { }

        System.Collections.Generic.List<PerformanceCounter> list =
            new System.Collections.Generic.List<PerformanceCounter>();
        try
        {
            PerformanceCounterCategory cat = new PerformanceCounterCategory("GPU Engine");
            foreach (string inst in cat.GetInstanceNames())
            {
                if (!inst.Contains("engtype_3D")) continue;
                try
                {
                    PerformanceCounter pc =
                        new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                    pc.NextValue();
                    list.Add(pc);
                }
                catch { }
            }
        }
        catch { }

        _counters    = list.ToArray();
        _refreshTick = 0;
    }

    private float ReadGPU()
    {
        if (++_refreshTick >= 30) InitCounters();
        if (_counters.Length == 0) return 0f;

        float total = 0f;
        bool  bad   = false;
        foreach (PerformanceCounter c in _counters)
            try   { total += c.NextValue(); }
            catch { bad = true; }

        if (bad) InitCounters();
        return Math.Min(100f, total);
    }

    // ── timer tick ────────────────────────────────────────────────────────────

    private void OnTick(object sender, EventArgs e)
    {
        _current    = ReadGPU();
        _tempC      = GpuTemp.Read();
        _buf[_head] = _current;
        _head       = (_head + 1) % HISTORY;
        Invalidate();
    }

    // ── painting ──────────────────────────────────────────────────────────────

    private void RebuildGridCache(Rectangle R)
    {
        if (_gridCache != null) _gridCache.Dispose();
        _gridCache     = new Bitmap(R.Width, R.Height);
        _gridCacheSize = R.Size;
        using (Graphics g = Graphics.FromImage(_gridCache))
        {
            g.Clear(C_BG);
            for (int i = 1; i < 4; i++)
            {
                g.DrawLine(_gridPen, R.Width * i / 4, 0,       R.Width * i / 4, R.Height);
                g.DrawLine(_gridPen, 0,               R.Height * i / 4, R.Width, R.Height * i / 4);
            }
        }
    }

    private void OnPaint(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int W = ClientSize.Width;
        int H = ClientSize.Height;

        Rectangle R = new Rectangle(PAD, HDR, W - PAD * 2, H - HDR - PAD);

        // grid (cached bitmap)
        if (_gridCache == null || _gridCacheSize != R.Size)
            RebuildGridCache(R);
        g.DrawImage(_gridCache, R.Left, R.Top);

        // header background
        g.FillRectangle(_bgBrush, 0, 0, W, HDR);

        // left label: temperature
        string tempStr = _tempC >= 0 ? "TEMP " + _tempC + "C" : "TEMP --";
        g.DrawString(tempStr, _font, _textBrush, PAD, 3);

        // right label: "GPU 3D  xx%"
        string rightStr = "GPU 3D  " + ((int)_current).ToString() + "%";
        SizeF  rightSz  = g.MeasureString(rightStr, _font);
        g.DrawString(rightStr, _font, _textBrush, W - rightSz.Width - PAD, 3);

        // graph
        if (R.Width < 4 || R.Height < 4) return;

        for (int i = 0; i < HISTORY; i++)
        {
            float v     = _buf[(_head + i) % HISTORY];
            _linePts[i] = new PointF(
                R.Left + (float)i / (HISTORY - 1) * R.Width,
                R.Bottom - v / 100f * R.Height);
        }

        _polyPts[0]           = new PointF(_linePts[0].X,           R.Bottom);
        _polyPts[HISTORY + 1] = new PointF(_linePts[HISTORY - 1].X, R.Bottom);
        for (int i = 0; i < HISTORY; i++) _polyPts[i + 1] = _linePts[i];

        g.FillPolygon(_fillBrush, _polyPts);
        g.DrawLines(_linePen,     _linePts);

        // border
        g.DrawRectangle(_bordPen, 0, 0, W - 1, H - 1);
    }

    // ── WndProc: native drag, resize, right-click ─────────────────────────────

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCRBUTTONUP || m.Msg == WM_CONTEXTMENU)
        {
            int   lp = m.LParam.ToInt32();
            Point pt = (lp == -1)
                ? new Point(Left + Width / 2, Top + Height / 2)
                : new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            _menu.Show(pt);
            return;
        }

        if (m.Msg == WM_NCHITTEST)
        {
            int   lp = m.LParam.ToInt32();
            Point p  = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));

            bool onL = p.X < BORDER,           onR = p.X > Width  - BORDER;
            bool onT = p.Y < BORDER,           onB = p.Y > Height - BORDER;

            if (onT && onL) { m.Result = (IntPtr)HTTOPLEFT;     return; }
            if (onT && onR) { m.Result = (IntPtr)HTTOPRIGHT;    return; }
            if (onB && onL) { m.Result = (IntPtr)HTBOTTOMLEFT;  return; }
            if (onB && onR) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
            if (onL)        { m.Result = (IntPtr)HTLEFT;        return; }
            if (onR)        { m.Result = (IntPtr)HTRIGHT;       return; }
            if (onT)        { m.Result = (IntPtr)HTTOP;         return; }
            if (onB)        { m.Result = (IntPtr)HTBOTTOM;      return; }

            m.Result = (IntPtr)HTCAPTION;
            return;
        }

        base.WndProc(ref m);
    }

    // ── layout persistence ────────────────────────────────────────────────────
    // Stored in GPUWidget.pos, a plain text file next to the executable.
    // Not a registry entry, not an .ini file — travels with the exe.

    private static string PosPath()
    {
        return Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath), "GPUWidget.pos");
    }

    private void LoadLayout()
    {
        try
        {
            string[] p = File.ReadAllText(PosPath()).Split(',');
            if (p.Length != 4) return;

            int x = int.Parse(p[0].Trim());
            int y = int.Parse(p[1].Trim());
            int w = Math.Max(MinimumSize.Width,  int.Parse(p[2].Trim()));
            int h = Math.Max(MinimumSize.Height, int.Parse(p[3].Trim()));

            // Guard: at least 40 px visible on a currently connected screen
            Rectangle saved = new Rectangle(x, y, w, h);
            bool onScreen = false;
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle overlap = Rectangle.Intersect(s.WorkingArea, saved);
                if (overlap.Width >= 40 && overlap.Height >= 40) { onScreen = true; break; }
            }
            if (!onScreen) return;

            Location = new Point(x, y);
            Size     = new Size(w, h);
        }
        catch { }   // file absent on first run — silently use defaults
    }

    private void SaveLayout()
    {
        try
        {
            File.WriteAllText(PosPath(), Left + "," + Top + "," + Width + "," + Height);
        }
        catch { }
    }

    // ── cleanup ───────────────────────────────────────────────────────────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SaveLayout();
        if (_timer != null) { _timer.Stop(); _timer.Dispose(); _timer = null; }
        foreach (PerformanceCounter c in _counters)
            try { c.Dispose(); } catch { }
        _gridPen.Dispose();   _linePen.Dispose();   _bordPen.Dispose();
        _fillBrush.Dispose(); _textBrush.Dispose(); _bgBrush.Dispose();
        _font.Dispose();
        if (_gridCache != null) _gridCache.Dispose();
        base.OnFormClosing(e);
    }

    // ── entry point ───────────────────────────────────────────────────────────

    [STAThread]
    public static void Main()
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new GPUWidget());
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// GPU Temperature — tries NVIDIA (NVAPI) then AMD (ADL) via LoadLibrary.
// Returns -1 if neither driver is present; widget shows "TEMP --" in that case.
// No extra DLL references required — both APIs are part of the GPU driver.
// ═════════════════════════════════════════════════════════════════════════════

internal static class GpuTemp
{
    [DllImport("kernel32.dll")] static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr h, string fn);

    // ── NVAPI delegates ───────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NvQueryIface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   NvInitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   NvEnumFn(IntPtr[] gpus, out int n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   NvThermFn(IntPtr gpu, uint idx, ref NvTherm s);

    // NV_GPU_THERMAL_SETTINGS_V1 — 68 bytes (0x44), version = 0x00010044
    [StructLayout(LayoutKind.Sequential)]
    struct NvSensor { public int ctrl, minT, maxT, curT, target; }  // 20 bytes each

    [StructLayout(LayoutKind.Sequential)]
    struct NvTherm
    {
        public uint ver;                    //  4 bytes  (set to sizeof|0x10000)
        public uint count;                  //  4 bytes
        public NvSensor s0, s1, s2;         // 60 bytes  (3 × 20)
    }                                       // = 68 bytes total

    // ── ADL delegates ─────────────────────────────────────────────────────────
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr AdlMallocFn(int n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   AdlCreateFn(AdlMallocFn cb, int connected);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   AdlNumAdapFn(out int n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   AdlTempFn(int adapter, int ctrl, ref AdlTemp t);

    // ADLTemperature — 8 bytes; iTemperature is in millidegrees Celsius
    [StructLayout(LayoutKind.Sequential)]
    struct AdlTemp { public int size, temp; }

    // ── state ─────────────────────────────────────────────────────────────────
    static bool     _inited;
    static bool     _nvOk;
    static IntPtr[] _nvGpus  = new IntPtr[64];
    static int      _nvCount;
    static NvThermFn _nvTherm;
    static bool     _adlOk;
    static int      _adlAdapters;
    static AdlTempFn _adlTherm;
    // Keep the malloc delegate alive so the GC doesn't collect it while ADL holds it
    static AdlMallocFn _adlMalloc = n => Marshal.AllocHGlobal(n);

    // ── helpers ───────────────────────────────────────────────────────────────

    static T Fn<T>(IntPtr mod, string name) where T : class
    {
        IntPtr p = GetProcAddress(mod, name);
        return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    static T NvFn<T>(NvQueryIface qi, uint id) where T : class
    {
        IntPtr p = qi(id);
        return p == IntPtr.Zero ? null : (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
    }

    // ── NVAPI init ────────────────────────────────────────────────────────────

    static void InitNv()
    {
        IntPtr mod = LoadLibrary(IntPtr.Size == 8 ? "nvapi64.dll" : "nvapi.dll");
        if (mod == IntPtr.Zero) return;

        IntPtr qiPtr = GetProcAddress(mod, "nvapi_QueryInterface");
        if (qiPtr == IntPtr.Zero) return;

        NvQueryIface qi = (NvQueryIface)Marshal.GetDelegateForFunctionPointer(qiPtr, typeof(NvQueryIface));

        NvInitFn init = NvFn<NvInitFn>(qi, 0x0150E828);
        if (init == null || init() != 0) return;

        NvEnumFn enumGpus = NvFn<NvEnumFn>(qi, 0xE5AC921F);
        if (enumGpus == null || enumGpus(_nvGpus, out _nvCount) != 0 || _nvCount == 0) return;

        _nvTherm = NvFn<NvThermFn>(qi, 0xE3640A56);
        _nvOk    = _nvTherm != null;
    }

    // ── ADL init ──────────────────────────────────────────────────────────────

    static void InitAdl()
    {
        IntPtr mod = LoadLibrary(IntPtr.Size == 8 ? "atiadlxx.dll" : "atiadlxy.dll");
        if (mod == IntPtr.Zero) return;

        AdlCreateFn create = Fn<AdlCreateFn>(mod, "ADL_Main_Control_Create");
        if (create == null || create(_adlMalloc, 1) != 0) return;

        AdlNumAdapFn numAdapters = Fn<AdlNumAdapFn>(mod, "ADL_Adapter_NumberOfAdapters_Get");
        if (numAdapters == null || numAdapters(out _adlAdapters) != 0 || _adlAdapters == 0) return;

        _adlTherm = Fn<AdlTempFn>(mod, "ADL_Overdrive5_Temperature_Get");
        _adlOk    = _adlTherm != null;
    }

    // ── public API ────────────────────────────────────────────────────────────

    static void EnsureInit()
    {
        if (_inited) return;
        _inited = true;
        try { InitNv(); } catch { }
        if (!_nvOk) try { InitAdl(); } catch { }
    }

    public static int Read()
    {
        EnsureInit();

        // ── NVIDIA ────────────────────────────────────────────────────────────
        if (_nvOk)
        {
            try
            {
                NvTherm s = new NvTherm();
                s.ver = (uint)(Marshal.SizeOf(s) | (1 << 16));  // 68 | 0x10000 = 0x10044

                if (_nvTherm(_nvGpus[0], 15, ref s) == 0 && s.count > 0)
                {
                    // target == 1 (NVAPI_THERMAL_TARGET_GPU) = GPU core sensor
                    if (s.count > 0 && s.s0.target == 1) return s.s0.curT;
                    if (s.count > 1 && s.s1.target == 1) return s.s1.curT;
                    if (s.count > 2 && s.s2.target == 1) return s.s2.curT;
                    return s.s0.curT;   // fallback: first sensor regardless of target
                }
            }
            catch { _nvOk = false; }
        }

        // ── AMD ───────────────────────────────────────────────────────────────
        if (_adlOk)
        {
            try
            {
                for (int i = 0; i < _adlAdapters; i++)
                {
                    AdlTemp t = new AdlTemp();
                    t.size = Marshal.SizeOf(t);   // must be set to 8
                    if (_adlTherm(i, 0, ref t) == 0 && t.temp > 0)
                        return t.temp > 300 ? t.temp / 1000 : t.temp;
                }
            }
            catch { _adlOk = false; }
        }

        return -1;  // GPU type not recognised, or driver not present
    }
}
