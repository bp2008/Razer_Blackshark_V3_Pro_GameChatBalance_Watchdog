using System.Drawing.Drawing2D;

namespace BalanceWatchdog;

/// <summary>Everything the overlay shows. Nulls mean "unknown".</summary>
public sealed record OverlayState(
    byte? Balance,
    byte? Sidetone, bool SidetoneOn, int SidetoneScale,
    float? Volume, bool VolumeMuted,
    float? Mic, bool MicMuted, bool MicHwMuted,
    byte? Battery, bool Charging,
    int LinkDrops, bool Linked);

/// <summary>
/// Click-through, never-focused, topmost OSD in the centre of the primary display: a Synapse-style
/// game/chat balance slider, smaller volume / mic / sidetone bars, and a battery + link footer.
/// </summary>
sealed class BalanceOverlay : Form
{
    const int HoldMs = 2500, FadeMs = 400;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    DateTime _shownAt;
    OverlayState? _state;
    string _highlight = "", _note = "";

    static readonly Color Green = Color.FromArgb(68, 214, 44);
    static readonly Color DarkGreen = Color.FromArgb(32, 72, 24);
    static readonly Color Red = Color.FromArgb(255, 80, 80);
    static readonly Color Amber = Color.FromArgb(255, 190, 60);

    public BalanceOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 24);
        DoubleBuffered = true;
        _timer.Tick += (_, _) => Animate();
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Raised for WM_QUERYENDSESSION (0x11) / WM_ENDSESSION (0x16). This hidden top-level window receives
    /// them when Windows logs off / shuts down, or when Restart Manager (installers, updaters) closes the app.
    /// </summary>
    public event Action<int, IntPtr, IntPtr>? SessionMessage;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is 0x11 or 0x16) SessionMessage?.Invoke(m.Msg, m.WParam, m.LParam);
        base.WndProc(ref m);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public bool IsShowing => Visible;

    /// <summary>Show (or refresh) the overlay. highlight: "balance", "volume", "mic", "sidetone" or "".</summary>
    public void ShowState(OverlayState st, string highlight = "", string note = "")
    {
        _state = st;
        _highlight = highlight;
        _note = note;
        float s = DeviceDpi / 96f;
        var size = new Size((int)(520 * s), (int)(262 * s));
        var sb = Screen.PrimaryScreen!.Bounds;   // centre of the primary display
        Bounds = new Rectangle(sb.Left + (sb.Width - size.Width) / 2, sb.Top + (sb.Height - size.Height) / 2, size.Width, size.Height);
        using (var path = Rounded(new Rectangle(Point.Empty, size), (int)(12 * s))) Region = new Region(path);
        _shownAt = DateTime.Now;
        Opacity = 0.94;
        if (!Visible) Show();
        Invalidate();
        _timer.Start();
    }

    /// <summary>Repaint with new data if currently visible, without extending the display time.</summary>
    public void UpdateIfShowing(OverlayState st)
    {
        if (!Visible) return;
        _state = st;
        Invalidate();
    }

    void Animate()
    {
        double t = (DateTime.Now - _shownAt).TotalMilliseconds;
        if (t < HoldMs) return;
        if (t >= HoldMs + FadeMs) { _timer.Stop(); Hide(); return; }
        Opacity = 0.94 * (1 - (t - HoldMs) / FadeMs);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_state is not { } st) return;
        float s = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int w = ClientSize.Width;

        using var title = new Font("Segoe UI", 13f);
        using var label = new Font("Segoe UI", 10f);
        using var small = new Font("Segoe UI", 9f);
        using var bubbleFont = new Font("Segoe UI Semibold", 10f);
        using var dim = new SolidBrush(Color.FromArgb(160, 160, 160));
        using var white = new SolidBrush(Color.FromArgb(235, 235, 235));
        using var dark = new SolidBrush(Color.FromArgb(17, 17, 17));

        // ---- Game/chat balance (large) ----
        byte? raw = st.Balance;
        bool valid = raw is byte b0 && b0 <= RazerHeadset.RawMax;
        var accent = raw is null || valid ? Green : Red;
        using var acc = new SolidBrush(accent);
        g.DrawString("GAME/CHAT BALANCE", title, _highlight is "balance" or "" ? acc : dim, 26 * s, 16 * s);
        string value = raw is not byte r ? "—"
            : !valid ? $"INVALID ({r})"
            : r == RazerHeadset.RawCenter ? "CENTRED"
            : r < RazerHeadset.RawCenter ? $"GAME +{RazerHeadset.RawCenter - r}" : $"CHAT +{r - RazerHeadset.RawCenter}";
        if (_note.Length > 0 && _highlight is "balance" or "") value = $"{value} · {_note}";
        var vs = g.MeasureString(value, label);
        g.DrawString(value, label, valid ? dim : acc, w - 26 * s - vs.Width, 20 * s);

        float x0 = 30 * s, x1 = w - 30 * s, y = 82 * s, mid = (x0 + x1) / 2;
        using (var track = new Pen(DarkGreen, 7 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, x0, y, x1, y);
        using (var tick = new Pen(Color.FromArgb(200, 200, 200), 1.5f * s))
            g.DrawLine(tick, mid, y + 22 * s, mid, y + 34 * s);
        if (valid && raw is byte rv)
        {
            float pos = x0 + (x1 - x0) * (RazerHeadset.RawMax - rv) / RazerHeadset.RawMax;
            using (var pin = new GraphicsPath())
            {
                float pw = 18 * s, ph = 20 * s, top = y - 12 * s;
                pin.AddArc(pos - pw / 2, top, 6 * s, 6 * s, 180, 90);
                pin.AddArc(pos + pw / 2 - 6 * s, top, 6 * s, 6 * s, 270, 90);
                pin.AddLine(pos + pw / 2, top + ph * 0.6f, pos, top + ph);
                pin.AddLine(pos, top + ph, pos - pw / 2, top + ph * 0.6f);
                pin.CloseFigure();
                g.FillPath(acc, pin);
            }
            int step = RazerHeadset.RawCenter - rv;
            string bt = step == 0 ? "0" : step > 0 ? $"+{step}" : step.ToString();
            var bsz = g.MeasureString(bt, bubbleFont);
            float bw = Math.Max(bsz.Width + 10 * s, 28 * s);
            var br = new RectangleF(pos - bw / 2, y - 46 * s, bw, 24 * s);
            using (var bp = Rounded(Rectangle.Round(br), (int)(3 * s))) g.FillPath(acc, bp);
            g.DrawString(bt, bubbleFont, dark, br.X + (br.Width - bsz.Width) / 2, br.Y + (br.Height - bsz.Height) / 2);
        }
        g.DrawString("MORE CHAT", label, dim, x0 - 4 * s, y + 24 * s);
        var rs = g.MeasureString("MORE GAME", label);
        g.DrawString("MORE GAME", label, dim, x1 + 4 * s - rs.Width, y + 24 * s);

        // ---- divider ----
        using (var div = new Pen(Color.FromArgb(55, 55, 55), 1 * s)) g.DrawLine(div, 26 * s, 142 * s, w - 26 * s, 142 * s);

        // ---- smaller bars ----
        float rowY = 160 * s;
        Row("VOLUME", st.Volume, st.VolumeMuted ? "MUTED" : null, st.Volume is float v ? $"{Math.Round(v * 100)}%" : "—", "volume");
        string? micFlag = st.MicHwMuted ? "MUTED (HEADSET)" : st.MicMuted ? "MUTED" : null;
        Row("MIC", st.Mic, micFlag, st.Mic is float m ? $"{Math.Round(m * 100)}%" : "—", "mic");
        float? stFrac = st.Sidetone is byte sl ? Math.Min(1f, sl / (float)Math.Max(1, st.SidetoneScale)) : null;
        bool stInvalid = st.Sidetone is byte sv && sv > WatchdogApp.SidetoneHardMax;
        Row("SIDETONE", stFrac, !st.SidetoneOn && st.Sidetone != null ? "OFF" : stInvalid ? "INVALID" : null,
            st.Sidetone is byte sx ? $"{sx}" : "—", "sidetone", stInvalid);

        void Row(string name, float? frac, string? flag, string text, string key, bool bad = false)
        {
            bool hi = _highlight == key;
            using var lb = new SolidBrush(hi ? Green : Color.FromArgb(160, 160, 160));
            g.DrawString(name, small, lb, 26 * s, rowY - 9 * s);
            float bx0 = 120 * s, bx1 = w - 150 * s, by = rowY + 1 * s;
            using (var t = new Pen(Color.FromArgb(48, 48, 48), 5 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(t, bx0, by, bx1, by);
            if (frac is float f && flag == null)
            {
                using var fill = new Pen(hi ? Green : Color.FromArgb(46, 150, 30), 5 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(fill, bx0, by, bx0 + (bx1 - bx0) * Math.Clamp(f, 0.001f, 1f), by);
            }
            string right = flag != null ? $"{text}  {flag}" : text;
            using var tb = new SolidBrush(bad ? Red : flag != null ? Amber : hi ? Color.White : Color.FromArgb(200, 200, 200));
            var ts = g.MeasureString(right, small);
            g.DrawString(right, small, tb, w - 26 * s - ts.Width, rowY - 9 * s);
            rowY += 26 * s;
        }

        // ---- footer: battery + link ----
        float fy = 236 * s;
        string batt = st.Battery is byte bp2 ? $"BATTERY {bp2}%{(st.Charging ? " · CHARGING" : "")}" : "BATTERY —";
        using var battBrush = new SolidBrush(st.Battery is byte bl && bl <= 15 && !st.Charging ? Amber : Color.FromArgb(160, 160, 160));
        g.DrawString(batt, small, battBrush, 26 * s, fy - 9 * s);
        string link = !st.Linked ? "HEADSET NOT CONNECTED" : st.LinkDrops == 0 ? "LINK STABLE (1 H)" : $"LINK DROPS (1 H): {st.LinkDrops}";
        using var linkBrush = new SolidBrush(!st.Linked || st.LinkDrops > 0 ? Amber : Color.FromArgb(160, 160, 160));
        var ls = g.MeasureString(link, small);
        g.DrawString(link, small, linkBrush, w - 26 * s - ls.Width, fy - 9 * s);
    }

    static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
