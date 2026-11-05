using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace BalanceWatchdog;

/// <summary>
/// Tray app. Signals, cheapest first:
///  1. Live HID listener: sees the headset's replies to Synapse's commands and the headset's own
///     "balance changed" / "connected" events the instant they happen (blocking read, no polling).
///  2. Synapse's log (tailed every 3 s; Synapse itself flushes ~10 s late) — attribution for the log file.
///  3. A direct HID read every N seconds (default 60) — safety net, works even if Synapse is closed.
/// Valid changes made on the headset or in Synapse become the new target; anything else
/// (e.g. Synapse's bogus 255 after a power-on) is logged, notified and reverted.
/// </summary>
sealed class WatchdogApp : ApplicationContext
{
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BalanceWatchdog");
    static readonly string StateFile = Path.Combine(DataDir, "target.txt");
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "BlackSharkBalanceWatchdog";

    readonly RazerHeadset _hs = new();
    readonly SynapseLogTailer _tailer = new();
    readonly HidListener _listener = new();
    readonly BalanceOverlay _overlay = new();
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _pollTimer = new(), _tailTimer = new(), _soonTimer = new(), _readTimer = new();
    readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    readonly ToolStripMenuItem _autoFixItem, _startupItem, _overlayItem;

    byte _target = RazerHeadset.RawCenter;
    bool _autoFix = true, _showOverlay = true, _checking, _checkAgain, _everLinked;
    bool? _linked;
    DateTime _linkedAt = DateTime.MinValue;
    byte? _lastRaw;                    // last value seen from any source (may be a transient 255)
    byte? _lastShownRaw;               // last valid value shown on the overlay
    DateTime _lastSynapseWrite = DateTime.MinValue;   // a "Set balance" ack we didn't cause
    DateTime _ownWriteUntil = DateTime.MinValue;
    DateTime _lastSynapseInvalid = DateTime.MinValue;
    string _lastSynapseInvalidText = "";
    DateTime _lastSynapseActivity = DateTime.MinValue;
    byte? _pendingRaw;
    DateTime _pendingSince;
    readonly Queue<DateTime> _recentFixes = new();
    DateTime _autoFixPausedUntil = DateTime.MinValue;
    string _lastHidError = "";

    public WatchdogApp(int pollSeconds)
    {
        Directory.CreateDirectory(DataDir);
        if (File.Exists(StateFile) && byte.TryParse(File.ReadAllText(StateFile).Trim(), out var t) && t <= RazerHeadset.RawMax) _target = t;
        _ = _overlay.Handle; // create the window handle now so BeginInvoke works from background threads

        _autoFixItem = new ToolStripMenuItem("Auto-fix", null, (_, _) => { _autoFix = !_autoFix; _autoFixItem!.Checked = _autoFix; Log($"Auto-fix {(_autoFix ? "enabled" : "disabled")} by user"); }) { Checked = true };
        _overlayItem = new ToolStripMenuItem("Show balance on change", null, (_, _) => { _showOverlay = !_showOverlay; _overlayItem!.Checked = _showOverlay; }) { Checked = true };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = IsStartupEnabled() };
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Check now", null, (_, _) => RequestCheck("manual")),
            new ToolStripMenuItem("Re-centre now", null, async (_, _) => { SetTarget(RazerHeadset.RawCenter, "user chose Re-centre"); await FixAsync("manual re-centre", force: true); }),
            _autoFixItem,
            _overlayItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open log", null, (_, _) => Open(LogPath)),
            new ToolStripMenuItem("Open log folder", null, (_, _) => Open(DataDir)),
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        });
        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "BlackShark balance watchdog" };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && _lastShownRaw is byte r) _overlay.ShowBalance(r, $"target {Describe(_target)}"); };
        _tray.DoubleClick += (_, _) => Open(LogPath);
        SetIcon(Color.Gray);

        _tailer.EventSeen += OnSynapseEvent;
        _tailTimer.Interval = 3000;
        _tailTimer.Tick += (_, _) => _tailer.Poll();
        _pollTimer.Interval = pollSeconds * 1000;
        _pollTimer.Tick += (_, _) => RequestCheck(null);
        _soonTimer.Tick += (_, _) => { _soonTimer.Stop(); RequestCheck(null); };
        _readTimer.Interval = 150;
        _readTimer.Tick += async (_, _) => { _readTimer.Stop(); await _hs.GetBalanceRawAsync(); }; // reply arrives via the listener
        SystemEvents.PowerModeChanged += OnPower;

        _listener.Report += r => _overlay.BeginInvoke(() => OnHidReport(r));
        _listener.Problem += p => _overlay.BeginInvoke(() => HidError($"listener: {p}"));

        Log($"Started (live HID listener + poll every {pollSeconds}s, target raw {_target}, auto-fix on). Synapse logs: {SynapseLogTailer.LogDir}");
        _listener.Start();
        _tailTimer.Start();
        _pollTimer.Start();
        RequestCheck("startup");
    }

    // ---------------------------------------------------------------- live HID traffic
    enum Source { Headset, Read, Poll }

    void OnHidReport(byte[] r)
    {
        if (r[0] != 2 || r[2] != 0x60) return;
        byte cmd = r[10], sub = r[11];
        switch (cmd)
        {
            case 0x5C when sub == 0x02:                 // unsolicited: balance changed on the headset
                OnBalanceSeen(r[13], Source.Headset);
                break;
            case 0x5C when sub == 0x01 && r[1] == 2:    // reply to a "Get balance" (Synapse's or ours)
                OnBalanceSeen(r[13], Source.Read);
                break;
            case 0xDC when sub == 0x01:                 // reply to a "Set balance"
                if (DateTime.Now > _ownWriteUntil) { _lastSynapseWrite = DateTime.Now; _readTimer.Stop(); _readTimer.Start(); }
                break;
            case 0x20 when sub == 0x02:                 // unsolicited: headset link changed
                SetLinked(r[13] == 1, r[13] == 1 ? "headset connected (event)" : $"headset disconnected (event, status {r[13]})");
                break;
        }
    }

    void OnBalanceSeen(byte raw, Source src)
    {
        bool valid = raw <= RazerHeadset.RawMax;
        bool changed = raw != _lastRaw;
        if (changed)
        {
            string how = src switch { Source.Headset => "changed on headset", Source.Read => "read", _ => "poll" };
            Log($"Headset balance raw = {raw} [{Describe(raw)}] ({how}){(raw == _target ? " = target" : "")}");
            _lastRaw = raw;
            UpdateStatus();
        }
        if (valid && raw != _lastShownRaw)
        {
            _lastShownRaw = raw;
            if (_showOverlay) _overlay.ShowBalance(raw);
        }
        if (raw == _target) { _pendingRaw = null; SetIcon(Color.LimeGreen); return; }
        if (_linked != true || (!changed && src != Source.Headset)) return;

        if (valid && src == Source.Headset) { SetTarget(raw, "changed on the headset"); return; }
        bool synapseJustWrote = DateTime.Now - _lastSynapseWrite < TimeSpan.FromSeconds(2);
        bool afterConnect = DateTime.Now - _linkedAt < TimeSpan.FromSeconds(30);
        if (valid && synapseJustWrote && !afterConnect) { SetTarget(raw, "changed in Synapse"); return; }

        // Out of range, or an unexplained change: let CheckAsync decide (it waits out Synapse bursts).
        CheckSoon(valid ? 2000 : 3500, debounce: true);
    }

    // ---------------------------------------------------------------- Synapse log events (attribution)
    void OnSynapseEvent(SynapseEvent e)
    {
        string flag = e.Invalid ? "  <-- INVALID (valid range 0..20)" : "";
        string val = e.Kind == SynapseEventKind.LinkStatus ? "" : $" => balanceMode {e.Value}";
        Log($"[Synapse {e.Timestamp}] {e.Text}{val}{flag}");

        if (e.Kind == SynapseEventKind.UserSetBalance && !e.Invalid)
            SetTarget((byte)(20 - e.Value), "user moved the Synapse slider (from Synapse log)");

        if (e.Invalid)
        {
            if (DateTime.Now - _lastSynapseInvalid > TimeSpan.FromSeconds(30))
                Log("WARNING: Synapse bug signature: it read/stored an out-of-range balance (the headset answers 255 = 'not ready' while it is still linking, and Synapse saves that as balanceMode 20-255 = -235, then writes it back).");
            _lastSynapseInvalid = DateTime.Now;
            _lastSynapseInvalidText = $"{e.Text} at {e.Timestamp}";
        }
        if (e.Kind != SynapseEventKind.LinkStatus) { _lastSynapseActivity = DateTime.Now; CheckSoon(4000, debounce: true); }
    }

    void OnPower(object? s, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) return;
        _overlay.BeginInvoke(() =>
        {
            Log($"System power event: {e.Mode}");
            if (e.Mode == PowerModes.Resume) CheckSoon(10000);
        });
    }

    // ---------------------------------------------------------------- checks and fixes
    void CheckSoon(int ms, bool debounce = false)
    {
        if (!debounce && _soonTimer.Enabled && _soonTimer.Interval <= ms) return;
        _soonTimer.Stop();
        _soonTimer.Interval = ms;
        _soonTimer.Start();
    }

    async void RequestCheck(string? reason)
    {
        if (_checking) { _checkAgain = true; return; }
        _checking = true;
        try
        {
            do { _checkAgain = false; await CheckAsync(reason); reason = null; } while (_checkAgain);
        }
        catch (Exception ex) { Log($"Check error: {ex.GetType().Name}: {ex.Message}"); }
        finally { _checking = false; }
    }

    async Task CheckAsync(string? reason)
    {
        if (reason != null) Log($"Check requested ({reason})");
        var link = await _hs.GetLinkStatusAsync();
        if (link == null) { SetLinked(null, "dongle not found / not responding"); return; }
        if (link != 1) { SetLinked(false, $"headset not connected (dongle status {link})"); return; }
        SetLinked(true, "headset connected");

        if (DateTime.Now - _linkedAt < TimeSpan.FromSeconds(5)) { CheckSoon(6000); return; } // still initialising
        if (DateTime.Now - _lastSynapseWrite < TimeSpan.FromSeconds(3)) { CheckSoon(3500, debounce: true); return; } // Synapse mid-burst

        var raw = await _hs.GetBalanceRawAsync();
        if (raw == null) { HidError("no response to balance query"); return; }
        _lastHidError = "";
        OnBalanceSeenQuiet(raw.Value);

        if (raw == _target) { _pendingRaw = null; SetIcon(Color.LimeGreen); return; }

        // Catch up on Synapse's log: a UI slider move may update the target instead of being reverted.
        _tailer.Poll();
        if (raw == _target) { _pendingRaw = null; UpdateStatus(); SetIcon(Color.LimeGreen); return; }

        // An in-range value with no explanation yet: Synapse flushes its log ~10 s late, so give it 15 s.
        if (raw <= RazerHeadset.RawMax)
        {
            if (_pendingRaw != raw) { _pendingRaw = raw; _pendingSince = DateTime.Now; }
            if (DateTime.Now - _pendingSince < TimeSpan.FromSeconds(15)) { CheckSoon(16000, debounce: true); return; }
        }
        _pendingRaw = null;

        string cause = DateTime.Now - _lastSynapseInvalid < TimeSpan.FromSeconds(60)
            ? $"Synapse wrote an invalid value ({_lastSynapseInvalidText})"
            : raw > RazerHeadset.RawMax ? "headset holds an invalid value (Synapse log entry not seen yet)"
            : "changed with no matching headset event or Synapse slider move";
        Log($"MISMATCH: headset raw {raw}, target {_target}. Likely cause: {cause}");
        await FixAsync(cause, force: false);
    }

    /// <summary>Records a value from our own poll (the listener normally reports it too).</summary>
    void OnBalanceSeenQuiet(byte raw)
    {
        if (raw != _lastRaw) { Log($"Headset balance raw = {raw} [{Describe(raw)}] (poll)"); _lastRaw = raw; UpdateStatus(); }
        if (raw <= RazerHeadset.RawMax && raw != _lastShownRaw) { _lastShownRaw = raw; if (_showOverlay) _overlay.ShowBalance(raw); }
    }

    async Task FixAsync(string cause, bool force)
    {
        var before = _lastRaw;
        if (!force && (!_autoFix || DateTime.Now < _autoFixPausedUntil))
        {
            SetIcon(Color.Red);
            Notify("Game/Chat balance moved", $"Headset balance is raw {before} (target {_target}). Auto-fix is off.\n{cause}", ToolTipIcon.Warning);
            return;
        }
        while (_recentFixes.Count > 0 && DateTime.Now - _recentFixes.Peek() > TimeSpan.FromMinutes(2)) _recentFixes.Dequeue();
        if (!force && _recentFixes.Count >= 5)
        {
            _autoFixPausedUntil = DateTime.Now.AddMinutes(5);
            Log("Something keeps changing the balance back (5 fixes in 2 min); pausing auto-fix for 5 minutes.");
            Notify("Balance keeps changing", "Something keeps overriding the balance. Auto-fix paused 5 min; see log.", ToolTipIcon.Error);
            SetIcon(Color.Red);
            return;
        }
        _recentFixes.Enqueue(DateTime.Now);

        _ownWriteUntil = DateTime.Now.AddSeconds(1.5);
        bool ok = await _hs.SetBalanceRawAsync(_target);
        await Task.Delay(300);
        var after = await _hs.GetBalanceRawAsync();
        bool good = ok && after == _target;
        Log($"FIX: wrote raw {_target} -> {(ok ? "ack" : "no ack")}, read back {after?.ToString() ?? "nothing"} => {(good ? "OK" : "FAILED")}");
        if (after is byte a) OnBalanceSeenQuiet(a);
        UpdateStatus();
        SetIcon(good ? Color.LimeGreen : Color.Red);
        if (_showOverlay && after is byte shown && shown <= RazerHeadset.RawMax) _overlay.ShowBalance(shown, good ? "restored" : "restore failed");
        string synapseHint = cause.StartsWith("Synapse") ? "\nSynapse's slider may show the wrong position until it next reads the headset." : "";
        if (!force)
            Notify(good ? "Game/Chat balance restored" : "Could not restore balance",
                $"{(good ? $"Set back to {Describe(_target)}" : "Failed to set")} (was raw {before?.ToString() ?? "?"}). {cause}{synapseHint}",
                good ? ToolTipIcon.Info : ToolTipIcon.Error);
    }

    void SetLinked(bool? linked, string text)
    {
        if (linked == _linked) return;
        // The first "connected" seen at app startup is not a fresh power-on: don't apply the
        // post-connect grace/replay rules to it.
        bool freshConnect = _everLinked || _linked == false;
        _linked = linked;
        Log($"Link: {text}");
        if (linked == true)
        {
            _everLinked = true;
            _linkedAt = freshConnect ? DateTime.Now : DateTime.MinValue;
            if (freshConnect) CheckSoon(6000);
        }
        else _lastRaw = null;
        SetIcon(linked == true ? Color.LimeGreen : Color.Gray);
        UpdateStatus();
    }

    void HidError(string msg)
    {
        if (msg == _lastHidError) return;
        _lastHidError = msg;
        Log($"HID: {msg}");
    }

    void SetTarget(byte raw, string why)
    {
        if (raw == _target) return;
        Log($"Target changed {Describe(_target)} -> {Describe(raw)} (raw {raw}; {why})");
        _target = raw;
        _pendingRaw = null;
        try { File.WriteAllText(StateFile, raw.ToString()); } catch { }
        UpdateStatus();
        SetIcon(_lastRaw == raw ? Color.LimeGreen : _iconColor);
    }

    // ---------------------------------------------------------------- UI helpers
    static string Describe(byte raw) =>
        raw > RazerHeadset.RawMax ? $"invalid {raw}"
        : raw == RazerHeadset.RawCenter ? "centre"
        : raw < RazerHeadset.RawCenter ? $"game +{RazerHeadset.RawCenter - raw}" : $"chat +{raw - RazerHeadset.RawCenter}";

    void UpdateStatus()
    {
        string s = _linked switch
        {
            null => "Dongle not found",
            false => "Headset off / not linked",
            true => _lastRaw is byte r ? $"Balance {Describe(r)} (target {Describe(_target)})" : "Headset connected",
        };
        _statusItem.Text = s;
        string tip = "Balance watchdog: " + s;
        _tray.Text = tip.Length > 63 ? s[..Math.Min(63, s.Length)] : tip;
    }

    void Notify(string title, string text, ToolTipIcon icon)
    {
        Log($"NOTIFY: {title} | {text.Replace('\n', ' ')}");
        _tray.ShowBalloonTip(8000, title, text, icon);
    }

    Color _iconColor = Color.Empty;
    void SetIcon(Color c)
    {
        if (c == _iconColor) return;
        _iconColor = c;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(c);
            g.FillEllipse(b, 2, 2, 28, 28);
            using var p = new Pen(Color.Black, 3);
            g.DrawLine(p, 9, 16, 23, 16);   // balance bar
            g.DrawLine(p, 16, 9, 16, 23);   // centre tick
        }
        var old = _tray.Icon;
        _tray.Icon = Icon.FromHandle(bmp.GetHicon());
        old?.Dispose();
    }

    public static string LogPath => Path.Combine(DataDir, $"balance-watchdog-{DateTime.Now:yyyy-MM}.log");

    public static void Log(string msg)
    {
        try { Directory.CreateDirectory(DataDir); File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {msg}{Environment.NewLine}"); } catch { }
    }

    static void Open(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    static bool IsStartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    void ToggleStartup()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)!;
        if (IsStartupEnabled()) { k.DeleteValue(RunName, false); Log("Removed from Windows startup"); }
        else { k.SetValue(RunName, $"\"{Environment.ProcessPath}\""); Log($"Added to Windows startup: {Environment.ProcessPath}"); }
        _startupItem.Checked = IsStartupEnabled();
    }

    protected override void ExitThreadCore()
    {
        Log("Exiting (user)");
        Program.UserExit = true;
        SystemEvents.PowerModeChanged -= OnPower;
        _pollTimer.Stop(); _tailTimer.Stop(); _soonTimer.Stop(); _readTimer.Stop();
        _listener.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        base.ExitThreadCore();
    }
}
