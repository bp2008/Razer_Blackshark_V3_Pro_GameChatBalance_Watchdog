using System.Drawing.Drawing2D;

namespace BalanceWatchdog;

/// <summary>
/// Click-through, never-focused, topmost OSD (like the Windows volume flyout) that shows the
/// game/chat balance for a few seconds after it changes.
/// </summary>
sealed class BalanceOverlay : Form
{
    const int HoldMs = 2500, FadeMs = 400;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    DateTime _shownAt;
    byte _raw = RazerHeadset.RawCenter;
    string _note = "";

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

    /// <summary>Show (or refresh) the overlay. raw: 0..20 (0 = all game, 20 = all chat), anything else = invalid.</summary>
    public void ShowBalance(byte raw, string note = "")
    {
        _raw = raw;
        _note = note;
        float s = DeviceDpi / 96f;
        var size = new Size((int)(520 * s), (int)(150 * s));
        var sb = Screen.PrimaryScreen!.Bounds;   // centre of the primary display
        Bounds = new Rectangle(sb.Left + (sb.Width - size.Width) / 2, sb.Top + (sb.Height - size.Height) / 2, size.Width, size.Height);
        using (var path = Rounded(new Rectangle(Point.Empty, size), (int)(12 * s))) Region = new Region(path);
        _shownAt = DateTime.Now;
        Opacity = 0.94;
        if (!Visible) Show();
        Invalidate();
        _timer.Start();
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
        float s = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        bool valid = _raw <= RazerHeadset.RawMax;
        var green = Color.FromArgb(68, 214, 44);
        var accent = valid ? green : Color.FromArgb(255, 80, 80);
        int w = ClientSize.Width;

        using var title = new Font("Segoe UI", 13f);
        using var label = new Font("Segoe UI", 10f);
        using var bubbleFont = new Font("Segoe UI Semibold", 10f);
        using var dim = new SolidBrush(Color.FromArgb(160, 160, 160));
        using var acc = new SolidBrush(accent);
        using var dark = new SolidBrush(Color.FromArgb(17, 17, 17));

        // Title row: "GAME/CHAT BALANCE" in green like Synapse, value / note on the right.
        g.DrawString("GAME/CHAT BALANCE", title, acc, 26 * s, 16 * s);
        string value = !valid ? $"INVALID ({_raw})"
            : _raw == RazerHeadset.RawCenter ? "CENTRED"
            : _raw < RazerHeadset.RawCenter ? $"GAME +{RazerHeadset.RawCenter - _raw}" : $"CHAT +{_raw - RazerHeadset.RawCenter}";
        if (_note.Length > 0) value = $"{value} · {_note}";
        var vs = g.MeasureString(value, label);
        g.DrawString(value, label, valid ? dim : acc, w - 26 * s - vs.Width, 20 * s);

        // Track: chat on the left, game on the right (same orientation as Synapse), centre tick below.
        float x0 = 30 * s, x1 = w - 30 * s, y = 82 * s, mid = (x0 + x1) / 2;
        using (var track = new Pen(Color.FromArgb(32, 72, 24), 7 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(track, x0, y, x1, y);
        using (var tick = new Pen(Color.FromArgb(200, 200, 200), 1.5f * s))
            g.DrawLine(tick, mid, y + 22 * s, mid, y + 34 * s);

        if (valid)
        {
            float pos = x0 + (x1 - x0) * (RazerHeadset.RawMax - _raw) / RazerHeadset.RawMax;
            // Synapse-style thumb: a rounded "pin" pointing down at the track.
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
            // Value bubble above the thumb (balance step, -10..+10 from centre; + = more game).
            int step = RazerHeadset.RawCenter - _raw;
            string bt = step == 0 ? "0" : step > 0 ? $"+{step}" : step.ToString();
            var bsz = g.MeasureString(bt, bubbleFont);
            var br = new RectangleF(pos - Math.Max(bsz.Width + 10 * s, 28 * s) / 2, y - 46 * s, Math.Max(bsz.Width + 10 * s, 28 * s), 24 * s);
            using (var bp = Rounded(Rectangle.Round(br), (int)(3 * s))) g.FillPath(acc, bp);
            g.DrawString(bt, bubbleFont, dark, br.X + (br.Width - bsz.Width) / 2, br.Y + (br.Height - bsz.Height) / 2);
        }

        g.DrawString("MORE CHAT", label, dim, x0 - 4 * s, y + 24 * s);
        var rs = g.MeasureString("MORE GAME", label);
        g.DrawString("MORE GAME", label, dim, x1 + 4 * s - rs.Width, y + 24 * s);
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
