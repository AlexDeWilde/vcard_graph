using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class GPUWidget : Form
{
    // ── colours ───────────────────────────────────────────────────────────────
    private static readonly Color C_BG     = Color.FromArgb(23,  23,  23);
    private static readonly Color C_GRID   = Color.FromArgb(38,  38,  38);
    private static readonly Color C_LINE   = Color.FromArgb(224, 64,  251);
    private static readonly Color C_FILL   = Color.FromArgb(50,  0,   60);
    private static readonly Color C_LINE_L = Color.FromArgb(240, 150, 30);
    private static readonly Color C_FILL_L = Color.FromArgb(55,  30,  5);
    private static readonly Color C_TEXT   = Color.FromArgb(176, 176, 176);
    private static readonly Color C_BORD   = Color.FromArgb(55,  55,  55);
    private static readonly Color C_SPLIT  = Color.FromArgb(60,  60,  60);

    // ── constants ─────────────────────────────────────────────────────────────
    private const int MAX_HISTORY = 7200;
    private const int INTERVAL    = 1000;
    private const int BORDER      = 6;
    private const int PAD         = 4;
    private const int HDR         = 22;

    // ── split-chart config (persisted) ────────────────────────────────────────
    private float _splitLeftPct = 20f;
    private float _lengthLeft   = 310f;
    private float _lengthRight  = 250f;

    // ── circular history buffer ───────────────────────────────────────────────
    private readonly float[] _buf  = new float[MAX_HISTORY];
    private int               _head = 0;
    private int               _tickCount;
    private float             _current;
    private int               _tempC = -1;

    // ── per-zone paint arrays (allocated on resize / split change) ────────────
    private PointF[] _lineLeft;
    private PointF[] _polyLeft;
    private PointF[] _lineRight;
    private PointF[] _polyRight;
    private int      _cachedLeftPx;
    private int      _cachedRightPx;

    // ── cached GDI resources ──────────────────────────────────────────────────
    private readonly Pen        _gridPen;
    private readonly Pen        _linePen;
    private readonly Pen        _lineLeftPen;
    private readonly Pen        _bordPen;
    private readonly Pen        _splitPen;
    private readonly SolidBrush _fillBrush;
    private readonly SolidBrush _fillLeftBrush;
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
        _gridPen       = new Pen(C_GRID, 1f);
        _linePen       = new Pen(C_LINE, 1f);
        _lineLeftPen   = new Pen(C_LINE_L, 1f);
        _bordPen       = new Pen(C_BORD, 1f);
        _splitPen      = new Pen(C_SPLIT, 1f);
        _fillBrush     = new SolidBrush(C_FILL);
        _fillLeftBrush = new SolidBrush(C_FILL_L);
        _textBrush     = new SolidBrush(C_TEXT);
        _bgBrush       = new SolidBrush(C_BG);
        _font          = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        LoadConfig();

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
        _menu.Items.Add("Settings", null, delegate { ShowSettings(); });
        _menu.Items.Add("Close",    null, delegate { Close(); });

        InitCounters();

        Paint  += OnPaint;
        Resize += delegate { _gridCache = null; Invalidate(); };

        LoadLayout();

        if (!File.Exists(CfgPath()))
            SaveConfig();

        _timer = new Timer();
        _timer.Interval = INTERVAL;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // ── config ────────────────────────────────────────────────────────────────

    private static string CfgPath()
    {
        return Path.Combine(
            Path.GetDirectoryName(Application.ExecutablePath), "GPUWidget.cfg");
    }

    private void LoadConfig()
    {
        try
        {
            string[] lines = File.ReadAllLines(CfgPath());
            NumberStyles ns = NumberStyles.Float;
            CultureInfo  ci = CultureInfo.InvariantCulture;

            bool hasLenL = false, hasLenR = false;
            float speedL = 5f, speedR = 1f;

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();
                float v;
                switch (key)
                {
                    case "split":
                        string[] parts = val.Split('/');
                        if (parts.Length == 2)
                        {
                            float l, r;
                            if (float.TryParse(parts[0].Trim(), ns, ci, out l) &&
                                float.TryParse(parts[1].Trim(), ns, ci, out r) &&
                                l > 0 && r > 0)
                                _splitLeftPct = l / (l + r) * 100f;
                        }
                        break;
                    case "split_left":
                        if (float.TryParse(val, ns, ci, out v) && v > 0 && v < 100)
                            _splitLeftPct = v;
                        break;
                    case "speed_left":
                        if (float.TryParse(val, ns, ci, out v) && v > 0)
                            speedL = v;
                        break;
                    case "speed_right":
                        if (float.TryParse(val, ns, ci, out v) && v > 0)
                            speedR = v;
                        break;
                    case "length_left":
                        if (float.TryParse(val, ns, ci, out v) && v > 0)
                        { _lengthLeft = v; hasLenL = true; }
                        break;
                    case "length_right":
                        if (float.TryParse(val, ns, ci, out v) && v > 0)
                        { _lengthRight = v; hasLenR = true; }
                        break;
                }
            }

            if (!hasLenL)
            {
                int cw = 320 - PAD * 2;
                _lengthLeft = Math.Max(1, (int)(cw * _splitLeftPct / 100f)) * speedL;
            }
            if (!hasLenR)
            {
                int cw = 320 - PAD * 2;
                int lp = Math.Max(1, (int)(cw * _splitLeftPct / 100f));
                _lengthRight = (cw - lp) * speedR;
            }
        }
        catch { }
    }

    private void SaveConfig()
    {
        try
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("; GPUWidget split-chart configuration");
            sb.AppendLine("; Edit here or use right-click > Settings");
            sb.AppendLine();
            sb.AppendLine("; Left zone width as % of chart (right = 100 - left)");
            sb.AppendLine("split_left=" + _splitLeftPct.ToString(ci));
            sb.AppendLine();
            sb.AppendLine("; Seconds of history shown in each zone");
            sb.AppendLine("length_left=" + _lengthLeft.ToString(ci));
            sb.AppendLine("length_right=" + _lengthRight.ToString(ci));
            File.WriteAllText(CfgPath(), sb.ToString());
        }
        catch { }
    }

    private void ShowSettings()
    {
        ConfigDialog dlg = new ConfigDialog();
        dlg.SplitLeftPct = _splitLeftPct;
        dlg.LengthLeft   = _lengthLeft;
        dlg.LengthRight  = _lengthRight;

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _splitLeftPct = dlg.SplitLeftPct;
            _lengthLeft   = dlg.LengthLeft;
            _lengthRight  = dlg.LengthRight;
            SaveConfig();
            _gridCache = null;
            Invalidate();
        }
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
        _head       = (_head + 1) % MAX_HISTORY;
        _tickCount++;
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
            int splitX = (int)(R.Width * _splitLeftPct / 100f);
            g.DrawLine(_splitPen, splitX, 0, splitX, R.Height);
        }
    }

    private void OnPaint(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int W = ClientSize.Width;
        int H = ClientSize.Height;

        Rectangle R = new Rectangle(PAD, HDR, W - PAD * 2, H - HDR - PAD);

        if (_gridCache == null || _gridCacheSize != R.Size)
            RebuildGridCache(R);
        g.DrawImage(_gridCache, R.Left, R.Top);

        g.FillRectangle(_bgBrush, 0, 0, W, HDR);

        string tempStr = _tempC >= 0 ? "TEMP " + _tempC + "C" : "TEMP --";
        g.DrawString(tempStr, _font, _textBrush, PAD, 3);

        string rightStr = "GPU 3D  " + ((int)_current).ToString() + "%";
        SizeF  rightSz  = g.MeasureString(rightStr, _font);
        g.DrawString(rightStr, _font, _textBrush, W - rightSz.Width - PAD, 3);

        if (R.Width < 4 || R.Height < 4) return;

        // ── split zones ───────────────────────────────────────────────────────
        int leftPx  = Math.Max(1, (int)(R.Width * _splitLeftPct / 100f));
        int rightPx = R.Width - leftPx;

        int leftSamples  = Math.Max(1, (int)_lengthLeft);
        int rightSamples = Math.Max(1, (int)_lengthRight);
        int totalSamples = leftSamples + rightSamples;

        if (totalSamples > MAX_HISTORY)
        {
            leftSamples  = MAX_HISTORY - rightSamples;
            if (leftSamples < 1) leftSamples = 1;
            totalSamples = leftSamples + rightSamples;
        }

        // reallocate per-zone arrays when dimensions change
        if (_cachedLeftPx != leftPx || _lineLeft == null)
        {
            _cachedLeftPx  = leftPx;
            _lineLeft  = new PointF[Math.Max(2, leftPx)];
            _polyLeft  = new PointF[Math.Max(2, leftPx) + 2];
        }
        if (_cachedRightPx != rightPx || _lineRight == null)
        {
            _cachedRightPx = rightPx;
            _lineRight = new PointF[Math.Max(2, rightPx)];
            _polyRight = new PointF[Math.Max(2, rightPx) + 2];
        }

        // ── left zone — time-aligned bins (static between shifts) ─────────────
        int binSize = Math.Max(1, leftSamples / leftPx);
        int newestLeftTick   = _tickCount - rightSamples - 1;
        int newestCompleteBin = (newestLeftTick + 1) / binSize - 1;

        for (int px = 0; px < leftPx; px++)
        {
            int binK   = newestCompleteBin - leftPx + 1 + px;
            int tStart = binK * binSize;
            int tEnd   = tStart + binSize;

            float sum = 0f;
            int   cnt = 0;
            for (int t = tStart; t < tEnd; t++)
            {
                if (t < 0 || t >= _tickCount) continue;
                int bufIdx = ((t % MAX_HISTORY) + MAX_HISTORY) % MAX_HISTORY;
                sum += _buf[bufIdx];
                cnt++;
            }
            float v = cnt > 0 ? sum / cnt : 0f;
            _lineLeft[px] = new PointF(
                R.Left + px,
                R.Bottom - v / 100f * R.Height);
        }

        // ── right zone — smooth per-pixel scrolling ───────────────────────────
        float samplesPerPxR = (float)rightSamples / rightPx;
        for (int px = 0; px < rightPx; px++)
        {
            int sStart = (int)(px * samplesPerPxR);
            int sEnd   = (int)((px + 1) * samplesPerPxR);
            if (sEnd <= sStart) sEnd = sStart + 1;
            if (sEnd > rightSamples) sEnd = rightSamples;

            float sum = 0f;
            int   cnt = 0;
            for (int s = sStart; s < sEnd; s++)
            {
                int bufIdx = ((_head - rightSamples + s) % MAX_HISTORY + MAX_HISTORY) % MAX_HISTORY;
                sum += _buf[bufIdx];
                cnt++;
            }
            float v = cnt > 0 ? sum / cnt : 0f;
            _lineRight[px] = new PointF(
                R.Left + leftPx + px,
                R.Bottom - v / 100f * R.Height);
        }

        // ── draw left zone (orange) ───────────────────────────────────────────
        if (leftPx >= 2)
        {
            _polyLeft[0] = new PointF(_lineLeft[0].X, R.Bottom);
            for (int i = 0; i < leftPx; i++) _polyLeft[i + 1] = _lineLeft[i];
            _polyLeft[leftPx + 1] = new PointF(_lineLeft[leftPx - 1].X, R.Bottom);
            g.FillPolygon(_fillLeftBrush, _polyLeft);
            g.DrawLines(_lineLeftPen, _lineLeft);
        }

        // ── draw right zone (purple) ──────────────────────────────────────────
        if (rightPx >= 2)
        {
            _polyRight[0] = new PointF(_lineRight[0].X, R.Bottom);
            for (int i = 0; i < rightPx; i++) _polyRight[i + 1] = _lineRight[i];
            _polyRight[rightPx + 1] = new PointF(_lineRight[rightPx - 1].X, R.Bottom);
            g.FillPolygon(_fillBrush, _polyRight);
            g.DrawLines(_linePen, _lineRight);
        }

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
        catch { }
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
        _gridPen.Dispose();   _linePen.Dispose();   _lineLeftPen.Dispose();
        _bordPen.Dispose();   _splitPen.Dispose();
        _fillBrush.Dispose(); _fillLeftBrush.Dispose();
        _textBrush.Dispose(); _bgBrush.Dispose();
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
// Settings dialog — split % and length per zone, nothing else.
// ═════════════════════════════════════════════════════════════════════════════

internal class ConfigDialog : Form
{
    private TextBox _txtSplitLeft;
    private Label   _lblSplitRight;
    private TextBox _txtLengthLeft;
    private TextBox _txtLengthRight;

    public float SplitLeftPct { get; set; }
    public float LengthLeft   { get; set; }
    public float LengthRight  { get; set; }

    public ConfigDialog()
    {
        Text            = "GPU Widget Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.FromArgb(30, 30, 30);
        ForeColor       = Color.FromArgb(176, 176, 176);
        Font            = new Font("Segoe UI", 9f);
        ShowInTaskbar   = false;
        ClientSize      = new Size(300, 230);

        int y      = 12;
        int lblX   = 20;
        int indX   = 32;
        int valX   = 150;
        int unitX  = 238;
        int inputW = 80;
        int rowH   = 28;

        // ── Split ─────────────────────────────────────────────────────────────
        AddSection("Split", lblX, ref y);
        AddLabel("Left:", indX, y);
        _txtSplitLeft = AddTextBox(valX, y, inputW);
        AddLabel("%", unitX, y);
        y += rowH;
        AddLabel("Right:", indX, y);
        _lblSplitRight = AddReadOnly(valX, y);
        AddLabel("%", unitX, y);
        y += rowH + 6;

        // ── Left Zone ─────────────────────────────────────────────────────────
        AddSection("Left Zone", lblX, ref y);
        AddLabel("Length:", indX, y);
        _txtLengthLeft = AddTextBox(valX, y, inputW);
        AddLabel("sec", unitX, y);
        y += rowH + 6;

        // ── Right Zone ────────────────────────────────────────────────────────
        AddSection("Right Zone", lblX, ref y);
        AddLabel("Length:", indX, y);
        _txtLengthRight = AddTextBox(valX, y, inputW);
        AddLabel("sec", unitX, y);
        y += rowH + 14;

        // ── Buttons ───────────────────────────────────────────────────────────
        Button btnOk = MakeButton("OK", ClientSize.Width / 2 - 90, y);
        btnOk.Click += OnOk;
        Controls.Add(btnOk);

        Button btnCancel = MakeButton("Cancel", ClientSize.Width / 2 + 10, y);
        btnCancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(btnCancel);

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        _txtSplitLeft.TextChanged += delegate { UpdateDerived(); };
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        CultureInfo ci = CultureInfo.InvariantCulture;
        _txtSplitLeft.Text   = SplitLeftPct.ToString("0.##", ci);
        _txtLengthLeft.Text  = LengthLeft.ToString("0.##", ci);
        _txtLengthRight.Text = LengthRight.ToString("0.##", ci);
        UpdateDerived();
    }

    private void UpdateDerived()
    {
        float splitL;
        if (float.TryParse(_txtSplitLeft.Text,
                NumberStyles.Float, CultureInfo.InvariantCulture, out splitL) &&
            splitL > 0 && splitL < 100)
            _lblSplitRight.Text = (100f - splitL).ToString("0.##", CultureInfo.InvariantCulture) + " %";
        else
            _lblSplitRight.Text = "-- %";
    }

    private void OnOk(object sender, EventArgs e)
    {
        NumberStyles ns = NumberStyles.Float;
        CultureInfo  ci = CultureInfo.InvariantCulture;
        float sl, ll, lr;

        if (!float.TryParse(_txtSplitLeft.Text,   ns, ci, out sl) || sl <= 0 || sl >= 100 ||
            !float.TryParse(_txtLengthLeft.Text,   ns, ci, out ll) || ll <= 0 ||
            !float.TryParse(_txtLengthRight.Text,  ns, ci, out lr) || lr <= 0)
        {
            MessageBox.Show(this,
                "Split must be between 0 and 100.\nLengths must be positive numbers.",
                "Invalid Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SplitLeftPct = sl;
        LengthLeft   = ll;
        LengthRight  = lr;
        DialogResult = DialogResult.OK;
        Close();
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private void AddSection(string text, int x, ref int y)
    {
        Label lbl    = new Label();
        lbl.Text     = text;
        lbl.AutoSize = true;
        lbl.Location = new Point(x, y + 2);
        lbl.Font     = new Font(Font, FontStyle.Bold);
        Controls.Add(lbl);
        y += 22;
    }

    private void AddLabel(string text, int x, int y)
    {
        Label lbl    = new Label();
        lbl.Text     = text;
        lbl.AutoSize = true;
        lbl.Location = new Point(x, y + 3);
        Controls.Add(lbl);
    }

    private Label AddReadOnly(int x, int y)
    {
        Label lbl     = new Label();
        lbl.Text      = "--";
        lbl.AutoSize  = true;
        lbl.Location  = new Point(x, y + 3);
        lbl.ForeColor = Color.FromArgb(130, 130, 130);
        Controls.Add(lbl);
        return lbl;
    }

    private TextBox AddTextBox(int x, int y, int w)
    {
        TextBox txt     = new TextBox();
        txt.Size        = new Size(w, 22);
        txt.Location    = new Point(x, y);
        txt.BackColor   = Color.FromArgb(45, 45, 45);
        txt.ForeColor   = Color.FromArgb(220, 220, 220);
        txt.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(txt);
        return txt;
    }

    private Button MakeButton(string text, int x, int y)
    {
        Button btn    = new Button();
        btn.Text      = text;
        btn.Size      = new Size(80, 28);
        btn.Location  = new Point(x, y);
        btn.FlatStyle = FlatStyle.Flat;
        btn.BackColor = Color.FromArgb(50, 50, 50);
        btn.ForeColor = Color.FromArgb(176, 176, 176);
        btn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
        return btn;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// GPU Temperature — tries NVIDIA (NVAPI) then AMD (ADL) via LoadLibrary.
// ═════════════════════════════════════════════════════════════════════════════

internal static class GpuTemp
{
    [DllImport("kernel32.dll")] static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GetProcAddress(IntPtr h, string fn);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr NvQueryIface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   NvInitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   NvEnumFn(IntPtr[] gpus, out int n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   NvThermFn(IntPtr gpu, uint idx, ref NvTherm s);

    [StructLayout(LayoutKind.Sequential)]
    struct NvSensor { public int ctrl, minT, maxT, curT, target; }

    [StructLayout(LayoutKind.Sequential)]
    struct NvTherm
    {
        public uint ver;
        public uint count;
        public NvSensor s0, s1, s2;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr AdlMallocFn(int n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   AdlCreateFn(AdlMallocFn cb, int connected);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   AdlNumAdapFn(out int n);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int   AdlTempFn(int adapter, int ctrl, ref AdlTemp t);

    [StructLayout(LayoutKind.Sequential)]
    struct AdlTemp { public int size, temp; }

    static bool     _inited;
    static bool     _nvOk;
    static IntPtr[] _nvGpus  = new IntPtr[64];
    static int      _nvCount;
    static NvThermFn _nvTherm;
    static bool     _adlOk;
    static int      _adlAdapters;
    static AdlTempFn _adlTherm;
    static AdlMallocFn _adlMalloc = n => Marshal.AllocHGlobal(n);

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

        if (_nvOk)
        {
            try
            {
                NvTherm s = new NvTherm();
                s.ver = (uint)(Marshal.SizeOf(s) | (1 << 16));
                if (_nvTherm(_nvGpus[0], 15, ref s) == 0 && s.count > 0)
                {
                    if (s.count > 0 && s.s0.target == 1) return s.s0.curT;
                    if (s.count > 1 && s.s1.target == 1) return s.s1.curT;
                    if (s.count > 2 && s.s2.target == 1) return s.s2.curT;
                    return s.s0.curT;
                }
            }
            catch { _nvOk = false; }
        }

        if (_adlOk)
        {
            try
            {
                for (int i = 0; i < _adlAdapters; i++)
                {
                    AdlTemp t = new AdlTemp();
                    t.size = Marshal.SizeOf(t);
                    if (_adlTherm(i, 0, ref t) == 0 && t.temp > 0)
                        return t.temp > 300 ? t.temp / 1000 : t.temp;
                }
            }
            catch { _adlOk = false; }
        }

        return -1;
    }
}
