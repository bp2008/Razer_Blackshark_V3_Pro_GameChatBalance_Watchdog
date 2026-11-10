using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace BalanceWatchdog;

/// <summary>
/// Tray app. Signals, cheapest first:
///  1. Live HID listener: sees the headset's replies to Synapse's commands and the headset's own
///     events (balance roller, battery, mic mute, connect/disconnect) the instant they happen.
///  2. Windows Core Audio notifications for the headset's volume and mic level.
///  3. Synapse's log (tailed every 3 s; Synapse flushes ~10 s late): attribution for the log file.
///  4. A direct HID read every N seconds (default 60): safety net, works even if Synapse is closed.
/// Guarded settings (game/chat balance, sidetone level): valid changes made on the headset or in
/// Synapse become the new target; invalid values (Synapse storing the headset's 255 "not ready"
/// reply after a reconnect) and unexplained changes are logged, notified and reverted.
/// </summary>
sealed class WatchdogApp : ApplicationContext
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BalanceWatchdog");
    /// <summary>Sidetone levels above this are certainly garbage (Synapse writes 255).</summary>
    public const int SidetoneHardMax = 100;
    /// <summary>Synapse's sidetone slider for this headset appears to go 0..15; the overlay bar uses this unless it sees more.</summary>
    const int SidetoneScaleDefault = 15;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "BlackSharkBalanceWatchdog";

    // HID command bytes (see RazerHeadset / README)
    const byte CmdBalance = 92, CmdSetBalance = 220, CmdSidetoneOn = 24, CmdSidetone = 25, CmdSetSidetone = 153,
               CmdBattery = 33, CmdCharging = 42, CmdMicStatus = 85, CmdLink = 32;

    readonly RazerHeadset _hs = new();
    readonly SynapseLogTailer _tailer = new();
    readonly HidListener _listener = new();
    readonly BalanceOverlay _overlay = new();
    readonly AudioEndpoints _audio;
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _pollTimer = new(), _tailTimer = new(), _soonTimer = new(), _readTimer = new();
    readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    readonly ToolStripMenuItem _autoFixItem, _startupItem, _overlayItem;

    readonly GuardedSetting _balance = new()
    {
        Name = "balance", Title = "Game/Chat balance", GetCmd = CmdBalance, SetCmd = CmdSetBalance, DefaultTarget = RazerHeadset.RawCenter,
        IsValid = r => r <= RazerHeadset.RawMax, Describe = DescribeBalance,
    };
    readonly GuardedSetting _sidetone = new()
    {
        Name = "sidetone", Title = "Mic sidetone", GetCmd = CmdSidetone, SetCmd = CmdSetSidetone, DefaultTarget = 0,
        IsValid = r => r <= SidetoneHardMax, Describe = r => r <= SidetoneHardMax ? $"level {r}" : $"invalid {r}",
        IsSuspicious = r => r >= SidetoneScaleDefault,   // firmware caps Synapse's 255 at 15
    };
    GuardedSetting[] Settings => [_balance, _sidetone];
    readonly HashSet<GuardedSetting> _toRead = [];

    bool _autoFix = true, _showOverlay = true, _checking, _checkAgain, _everLinked;
    bool? _linked;
    DateTime _linkedAt = DateTime.MinValue;
    DateTime _lastSynapseActivity = DateTime.MinValue;
    bool _sidetoneOn = true, _charging, _micHwMuted;
    byte? _battery;
    int _sidetoneScale = SidetoneScaleDefault;
    readonly Queue<DateTime> _linkDrops = new();
    string _lastHidError = "";

    public WatchdogApp(int pollSeconds)
    {
        Directory.CreateDirectory(DataDir);
        foreach (var s in Settings) s.LoadTarget();
        _ = _overlay.Handle; // create the window handle now so BeginInvoke works from background threads
        _audio = new AudioEndpoints(_overlay);

        _autoFixItem = new ToolStripMenuItem("Auto-fix", null, (_, _) => { _autoFix = !_autoFix; _autoFixItem!.Checked = _autoFix; Log($"Auto-fix {(_autoFix ? "enabled" : "disabled")} by user"); }) { Checked = true };
        _overlayItem = new ToolStripMenuItem("Show overlay on change", null, (_, _) => { _showOverlay = !_showOverlay; _overlayItem!.Checked = _showOverlay; }) { Checked = true };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = IsStartupEnabled() };
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _statusItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Show overlay", null, (_, _) => _overlay.ShowState(State())),
            new ToolStripMenuItem("Check now", null, (_, _) => RequestCheck("manual")),
            new ToolStripMenuItem("Re-centre balance now", null, async (_, _) => { SetTarget(_balance, RazerHeadset.RawCenter, "user chose Re-centre"); await FixAsync(_balance, "manual re-centre", force: true); }),
            _autoFixItem,
            _overlayItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open log", null, (_, _) => Open(LogPath)),
            new ToolStripMenuItem("Open log folder", null, (_, _) => Open(DataDir)),
            _startupItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        });
        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "BlackShark watchdog" };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) _overlay.ShowState(State()); };
        _tray.DoubleClick += (_, _) => Open(LogPath);
        SetIcon(Color.Gray);

        _tailer.EventSeen += OnSynapseEvent;
        _tailTimer.Interval = 3000;
        _tailTimer.Tick += (_, _) => _tailer.Poll();
        _pollTimer.Interval = pollSeconds * 1000;
        _pollTimer.Tick += (_, _) => RequestCheck(null);
        _soonTimer.Tick += (_, _) => { _soonTimer.Stop(); RequestCheck(null); };
        _readTimer.Interval = 150;
        _readTimer.Tick += async (_, _) =>
        {
            _readTimer.Stop();
            foreach (var s in _toRead.ToArray()) { _toRead.Remove(s); await _hs.GetAsync(s.GetCmd); } // replies arrive via the listener
        };
        _audio.Changed += kind => { UpdateStatus(); if (_showOverlay) _overlay.ShowState(State(), kind); };
        SystemEvents.PowerModeChanged += OnPower;

        _listener.Report += r => _overlay.BeginInvoke(() => OnHidReport(r));
        _listener.Problem += p => _overlay.BeginInvoke(() => HidError($"listener: {p}"));

        // ---- lifetime diagnostics: how did the previous run end, and how does this one end? ----
        ReportPreviousRun();
        Heartbeat("alive");
        _heartbeatTimer.Interval = 5 * 60 * 1000;
        _heartbeatTimer.Tick += (_, _) => Heartbeat("alive");
        _heartbeatTimer.Start();
        _overlay.SessionMessage += (msg, w, l) =>
        {
            long lp = l.ToInt64();
            string why = (lp & 0x1) != 0 ? "an application/installer (Restart Manager, ENDSESSION_CLOSEAPP)"
                       : (lp & 0x80000000L) != 0 ? "user logoff"
                       : (lp & 0x40000000L) != 0 ? "a critical shutdown" : "Windows shutdown/restart";
            if (msg == 0x11) Log($"Windows asked to close this app (WM_QUERYENDSESSION) on behalf of {why}");
            else if (w != IntPtr.Zero) { Log($"Closing: session ending (WM_ENDSESSION) on behalf of {why}"); Heartbeat($"exited: session end ({why})"); }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (!_exitLogged) { _exitLogged = true; Log("Process exiting"); Heartbeat("exited: process exit"); } };
        // If Restart Manager closes us (e.g. during an update), ask Windows to start us again afterwards.
        // Crash/hang restarts are excluded: Program handles crashes itself.
        try { RegisterApplicationRestart("--ensure", 1 /*RESTART_NO_CRASH*/ | 2 /*RESTART_NO_HANG*/); } catch { }

        Log($"Started v{Application.ProductVersion.Split('+')[0]} (live HID listener + poll every {pollSeconds}s; targets: balance {DescribeBalance(_balance.Target)}, sidetone {(_sidetone.HasTarget ? _sidetone.Target.ToString() : "from first reading")}; auto-fix on)");
        _audio.Refresh();
        _listener.Start();
        _tailTimer.Start();
        _pollTimer.Start();
        RequestCheck("startup");
    }

    // ---------------------------------------------------------------- lifetime diagnostics
    readonly System.Windows.Forms.Timer _heartbeatTimer = new();
    bool _exitLogged;
    internal static string HeartbeatFile => Path.Combine(DataDir, "heartbeat.txt");

    /// <summary>True if the last run ended because the user chose Exit (so --ensure should leave it closed).</summary>
    internal static bool LastExitWasUser()
    {
        try { return File.Exists(HeartbeatFile) && File.ReadAllText(HeartbeatFile).StartsWith("exited: user chose Exit"); }
        catch { return false; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int RegisterApplicationRestart(string commandLine, int flags);

    internal static void Heartbeat(string state)
    {
        try { File.WriteAllText(HeartbeatFile, $"{state}|{DateTime.Now:yyyy-MM-dd HH:mm:ss}|pid {Environment.ProcessId}"); } catch { }
    }

    static void ReportPreviousRun()
    {
        try
        {
            if (!File.Exists(HeartbeatFile)) return;
            var parts = File.ReadAllText(HeartbeatFile).Split('|');
            if (parts.Length < 2) return;
            if (parts[0] == "alive")
                Log($"PREVIOUS RUN ENDED UNEXPECTEDLY ({(parts.Length > 2 ? parts[2] : "")}): no exit was recorded. It was last seen alive at {parts[1]} " +
                    "(heartbeat every 5 min), so it was killed or died without a trace between then and the next 5-minute mark.");
            else
                Log($"Previous run ended normally: {parts[0]} at {parts[1]}");
        }
        catch { }
    }

    // ---------------------------------------------------------------- live HID traffic
    enum Source { Headset, Read, Poll }

    void OnHidReport(byte[] r)
    {
        if (r[0] != 2 || r[2] != 0x60 || r[1] != 2) return;
        byte cmd = r[10], sub = r[11], v = r[13];
        bool evt = sub == 0x02, reply = sub == 0x01;
        if (!evt && !reply) return;
        switch (cmd)
        {
            case CmdBalance: OnValueSeen(_balance, v, evt ? Source.Headset : Source.Read); break;
            case CmdSidetone: OnValueSeen(_sidetone, v, evt ? Source.Headset : Source.Read); break;
            case CmdSetBalance when reply: OnSetAck(_balance); break;
            case CmdSetSidetone when reply: OnSetAck(_sidetone); break;
            case CmdSidetoneOn when v <= 1: _sidetoneOn = v == 1; _overlay.UpdateIfShowing(State()); break;
            case CmdBattery when v <= 100:
                if (_battery != v) { if (_battery == null || Math.Abs(_battery.Value - v) >= 5 || v <= 15) Log($"Battery {v}%"); _battery = v; UpdateStatus(); _overlay.UpdateIfShowing(State()); }
                break;
            case CmdCharging when v <= 1:
                if (_charging != (v == 1)) { _charging = v == 1; Log(_charging ? "Charging" : "Not charging"); UpdateStatus(); _overlay.UpdateIfShowing(State()); }
                break;
            case CmdMicStatus when v <= 1:
                if (_micHwMuted != (v == 1)) { _micHwMuted = v == 1; Log($"Headset mic {(v == 1 ? "muted" : "unmuted")}"); if (_showOverlay) _overlay.ShowState(State(), "mic"); }
                break;
            case CmdLink when evt:
                SetLinked(v == 1, v == 1 ? "headset connected (event)" : $"headset disconnected (event, status {v})");
                break;
        }
    }

    void OnSetAck(GuardedSetting s)
    {
        if (DateTime.Now <= s.OwnWriteUntil) return;
        s.LastSynapseWrite = DateTime.Now;
        _toRead.Add(s);
        _readTimer.Stop(); _readTimer.Start();
    }

    void OnValueSeen(GuardedSetting s, byte raw, Source src)
    {
        bool valid = s.IsValid(raw);
        bool changed = raw != s.LastRaw;
        if (changed)
        {
            string how = src switch { Source.Headset => "changed on headset", Source.Read => "read", _ => "poll" };
            Log($"Headset {s.Name} = {s.Describe(raw)} (raw {raw}, {how}){(raw == s.Target ? " = target" : "")}");
            s.LastRaw = raw;
            if (s == _sidetone && valid && raw > _sidetoneScale) _sidetoneScale = SidetoneHardMax;
            UpdateStatus();
            if (valid && _showOverlay && _linked == true) _overlay.ShowState(State(), s.Name);
        }
        if (raw == s.Target) { s.PendingRaw = null; SetIcon(Color.LimeGreen); return; }
        if (_linked != true || !s.HasTarget || (!changed && src != Source.Headset)) return;

        if (valid && src == Source.Headset) { SetTarget(s, raw, "changed on the headset"); return; }
        bool synapseJustWrote = DateTime.Now - s.LastSynapseWrite < TimeSpan.FromSeconds(2);
        bool afterConnect = DateTime.Now - _linkedAt < TimeSpan.FromSeconds(30);
        if (valid && synapseJustWrote && !afterConnect && !s.IsSuspicious(raw)) { SetTarget(s, raw, "changed in Synapse"); return; }

        // Out of range, a Synapse write right after a reconnect, or unexplained: CheckAsync decides
        // (it waits out Synapse's burst of writes and gives Synapse's log time to explain the change).
        CheckSoon(valid ? 2000 : 3500, debounce: true);
    }

    // ---------------------------------------------------------------- Synapse log events (attribution)
    void OnSynapseEvent(SynapseEvent e)
    {
        string flag = e.Invalid ? "  <-- INVALID" : "";
        string val = e.Kind == SynapseEventKind.LinkStatus ? "" : e.Setting == "balance" ? $" => balanceMode {e.Value}" : "";
        Log($"[Synapse {e.Timestamp}] {e.Text}{val}{flag}");
        var s = e.Setting == "balance" ? _balance : e.Setting == "sidetone" ? _sidetone : null;
        if (s == null) return;

        if (e.Kind == SynapseEventKind.UserSet && !e.Invalid)
            SetTarget(s, (byte)(s == _balance ? 20 - e.Value : e.Value), "set in the Synapse UI (from Synapse log)");

        if (e.Invalid)
        {
            if (DateTime.Now - s.LastSynapseInvalid > TimeSpan.FromSeconds(30))
                Log($"WARNING: Synapse bug signature on {s.Name}: it read/stored an out-of-range value (the headset answers 255 = 'not ready' while it is still linking, and Synapse saves and writes that back).");
            s.LastSynapseInvalid = DateTime.Now;
            s.LastSynapseInvalidText = $"{e.Text} at {e.Timestamp}";
        }
        _lastSynapseActivity = DateTime.Now;
        CheckSoon(4000, debounce: true);
    }

    void OnPower(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.StatusChange) return;
        _overlay.BeginInvoke(() =>
        {
            Log($"System power event: {e.Mode}");
            if (e.Mode == PowerModes.Resume) { _audio.Refresh(); CheckSoon(10000); }
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

        // Status values (replies are handled by the listener; ignore failures here).
        await _hs.GetAsync(CmdBattery);
        await _hs.GetAsync(CmdCharging);
        await _hs.GetAsync(CmdSidetoneOn);
        await _hs.GetAsync(CmdMicStatus);

        foreach (var s in Settings) await CheckSettingAsync(s);
        UpdateStatus();
    }

    async Task CheckSettingAsync(GuardedSetting s)
    {
        if (DateTime.Now - s.LastSynapseWrite < TimeSpan.FromSeconds(3)) { CheckSoon(3500, debounce: true); return; } // Synapse mid-burst

        var data = await _hs.GetAsync(s.GetCmd);
        if (data is not { Length: > 0 }) { HidError($"no response to {s.Name} query"); return; }
        _lastHidError = "";
        byte raw = data[0];
        OnValueSeenQuiet(s, raw);
        if (!s.HasTarget && s.IsValid(raw) && DateTime.Now - _linkedAt > TimeSpan.FromSeconds(30))
            SetTarget(s, raw, "first reading (no saved target yet)");
        if (raw == s.Target) { s.PendingRaw = null; SetIcon(Color.LimeGreen); return; }
        if (!s.HasTarget) { CheckSoon(31000, debounce: true); return; }

        // Catch up on Synapse's log: a UI change may update the target instead of being reverted.
        _tailer.Poll();
        if (raw == s.Target) { s.PendingRaw = null; return; }

        // A valid value with no explanation yet: Synapse flushes its log ~10 s late, so give it 20 s
        // to show a UI change (which updates the target) before reverting. Suspicious values (e.g. the
        // capped sidetone 15) are reverted right away; if the log later shows the user chose them,
        // the target updates and the next check puts them back.
        if (s.IsValid(raw) && !s.IsSuspicious(raw))
        {
            if (s.PendingRaw != raw) { s.PendingRaw = raw; s.PendingSince = DateTime.Now; }
            if (DateTime.Now - s.PendingSince < TimeSpan.FromSeconds(20)) { CheckSoon(21000, debounce: true); return; }
        }
        s.PendingRaw = null;

        string cause = DateTime.Now - s.LastSynapseInvalid < TimeSpan.FromSeconds(60)
            ? $"Synapse wrote an invalid value ({s.LastSynapseInvalidText})"
            : !s.IsValid(raw) ? "headset holds an invalid value (Synapse log entry not seen yet)"
            : DateTime.Now - _linkedAt < TimeSpan.FromSeconds(60) ? "changed by Synapse right after the headset reconnected"
            : "changed with no matching headset event or Synapse UI change";
        Log($"MISMATCH: {s.Name} raw {raw}, target {s.Target}. Likely cause: {cause}");
        await FixAsync(s, cause, force: false);
    }

    /// <summary>Records a value from our own poll (the listener normally reports it too).</summary>
    void OnValueSeenQuiet(GuardedSetting s, byte raw)
    {
        if (raw == s.LastRaw) return;
        Log($"Headset {s.Name} = {s.Describe(raw)} (raw {raw}, poll)");
        s.LastRaw = raw;
        UpdateStatus();
        if (s.IsValid(raw) && _showOverlay) _overlay.ShowState(State(), s.Name);
    }

    async Task FixAsync(GuardedSetting s, string cause, bool force)
    {
        var before = s.LastRaw;
        if (!force && (!_autoFix || DateTime.Now < s.PausedUntil))
        {
            SetIcon(Color.Red);
            Notify($"{s.Title} changed", $"Headset {s.Name} is {(before is byte b ? s.Describe(b) : "?")} (target {s.Describe(s.Target)}). Auto-fix is off.\n{cause}", ToolTipIcon.Warning);
            return;
        }
        while (s.RecentFixes.Count > 0 && DateTime.Now - s.RecentFixes.Peek() > TimeSpan.FromMinutes(2)) s.RecentFixes.Dequeue();
        if (!force && s.RecentFixes.Count >= 5)
        {
            s.PausedUntil = DateTime.Now.AddMinutes(5);
            Log($"Something keeps changing {s.Name} back (5 fixes in 2 min); pausing auto-fix for it for 5 minutes.");
            Notify($"{s.Title} keeps changing", "Something keeps overriding it. Auto-fix paused 5 min; see log.", ToolTipIcon.Error);
            SetIcon(Color.Red);
            return;
        }
        s.RecentFixes.Enqueue(DateTime.Now);

        s.OwnWriteUntil = DateTime.Now.AddSeconds(1.5);
        bool ok = await _hs.SetByteAsync(s.SetCmd, s.Target);
        await Task.Delay(300);
        var after = (await _hs.GetAsync(s.GetCmd)) is { Length: > 0 } d ? d[0] : (byte?)null;
        bool good = ok && after == s.Target;
        Log($"FIX: {s.Name} wrote raw {s.Target} -> {(ok ? "ack" : "no ack")}, read back {after?.ToString() ?? "nothing"} => {(good ? "OK" : "FAILED")}");
        if (after is byte a) s.LastRaw = a;
        UpdateStatus();
        SetIcon(good ? Color.LimeGreen : Color.Red);
        if (_showOverlay) _overlay.ShowState(State(), s.Name, good ? "restored" : "restore failed");
        string synapseHint = cause.StartsWith("Synapse") ? "\nSynapse may show the wrong value until it next reads the headset." : "";
        if (!force)
            Notify(good ? $"{s.Title} restored" : $"Could not restore {s.Name}",
                $"{(good ? $"Set back to {s.Describe(s.Target)}" : "Failed to set")} (was {(before is byte bb ? s.Describe(bb) : "?")}). {cause}{synapseHint}",
                good ? ToolTipIcon.Info : ToolTipIcon.Error);
    }

    void SetLinked(bool? linked, string text)
    {
        if (linked == _linked) return;
        // The first "connected" seen at app startup is not a fresh power-on: don't apply the
        // post-connect grace/replay rules to it.
        bool freshConnect = _everLinked || _linked == false;
        if (_linked == true && linked != true)
        {
            _linkDrops.Enqueue(DateTime.Now);
            while (_linkDrops.Count > 0 && DateTime.Now - _linkDrops.Peek() > TimeSpan.FromHours(1)) _linkDrops.Dequeue();
        }
        _linked = linked;
        Log($"Link: {text}{(linked == false && _linkDrops.Count > 1 ? $" ({_linkDrops.Count} drops in the last hour)" : "")}");
        if (linked == true)
        {
            _everLinked = true;
            _linkedAt = freshConnect ? DateTime.Now : DateTime.MinValue;
            if (freshConnect) { CheckSoon(6000); _audio.Refresh(); }
        }
        else foreach (var s in Settings) s.LastRaw = null;
        SetIcon(linked == true ? Color.LimeGreen : Color.Gray);
        UpdateStatus();
        _overlay.UpdateIfShowing(State());
    }

    void HidError(string msg)
    {
        if (msg == _lastHidError) return;
        _lastHidError = msg;
        Log($"HID: {msg}");
    }

    void SetTarget(GuardedSetting s, byte raw, string why)
    {
        byte old = s.Target;
        if (!s.SetTarget(raw)) return;
        Log($"Target {s.Name} changed {s.Describe(old)} -> {s.Describe(raw)} ({why})");
        UpdateStatus();
    }

    // ---------------------------------------------------------------- UI helpers
    OverlayState State()
    {
        while (_linkDrops.Count > 0 && DateTime.Now - _linkDrops.Peek() > TimeSpan.FromHours(1)) _linkDrops.Dequeue();
        return new OverlayState(
            Balance: _balance.LastRaw ?? (_linked == true ? null : _balance.Target),
            Sidetone: _sidetone.LastRaw ?? (_linked == true ? null : _sidetone.Target), SidetoneOn: _sidetoneOn, SidetoneScale: _sidetoneScale,
            Volume: _audio.Volume?.Scalar, VolumeMuted: _audio.Volume?.Muted ?? false,
            Mic: _audio.Mic?.Scalar, MicMuted: _audio.Mic?.Muted ?? false, MicHwMuted: _micHwMuted,
            Battery: _battery, Charging: _charging,
            LinkDrops: _linkDrops.Count, Linked: _linked == true);
    }

    static string DescribeBalance(byte raw) =>
        raw > RazerHeadset.RawMax ? $"invalid {raw}"
        : raw == RazerHeadset.RawCenter ? "centre"
        : raw < RazerHeadset.RawCenter ? $"game +{RazerHeadset.RawCenter - raw}" : $"chat +{raw - RazerHeadset.RawCenter}";

    void UpdateStatus()
    {
        string s = _linked switch
        {
            null => "Dongle not found",
            false => "Headset off / not linked",
            true => $"Balance {(_balance.LastRaw is byte r ? DescribeBalance(r) : "?")} · sidetone {(_sidetone.LastRaw?.ToString() ?? "?")}"
                    + (_battery is byte b ? $" · {b}%{(_charging ? "+" : "")}" : ""),
        };
        _statusItem.Text = s;
        _tray.Text = s.Length > 63 ? s[..63] : s;
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
        _exitLogged = true;
        Heartbeat("exited: user chose Exit");
        Program.UserExit = true;
        _heartbeatTimer.Stop();
        SystemEvents.PowerModeChanged -= OnPower;
        _pollTimer.Stop(); _tailTimer.Stop(); _soonTimer.Stop(); _readTimer.Stop();
        _listener.Dispose();
        _audio.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Dispose();
        base.ExitThreadCore();
    }
}
