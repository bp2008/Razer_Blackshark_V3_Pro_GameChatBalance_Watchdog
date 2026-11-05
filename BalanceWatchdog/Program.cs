using System.Runtime.InteropServices;

namespace BalanceWatchdog;

static class Program
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    /// <summary>Set when the user chooses Exit, so a normal shutdown isn't treated as a crash.</summary>
    public static bool UserExit;

    [STAThread]
    static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].TrimStart('-', '/').ToLowerInvariant() : "";
        if (cmd is "status" or "set" or "scan" or "listen" or "simulate-bug" or "help" or "?")
        {
            if (!Console.IsOutputRedirected) AttachConsole(-1);
            return RunCli(cmd, args).GetAwaiter().GetResult();
        }

        int interval = 60, restarts = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            string a = args[i].TrimStart('-', '/').ToLowerInvariant();
            if (a == "interval") int.TryParse(args[i + 1], out interval);
            if (a == "restarted") int.TryParse(args[i + 1], out restarts);
        }

        // Single instance. After a crash-restart, wait for the old process to release the mutex.
        using var single = new Mutex(false, @"Local\BlackSharkBalanceWatchdog");
        bool owned;
        try { owned = single.WaitOne(restarts > 0 ? 15000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            if (restarts == 0) MessageBox.Show("Balance Watchdog is already running (see the tray).", "Balance Watchdog");
            return 1;
        }

        // Crash recovery: log the exception and relaunch (up to 5 times per process chain).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashAndRestart(e.Exception, interval, restarts, single);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashAndRestart(e.ExceptionObject as Exception, interval, restarts, single);
        TaskScheduler.UnobservedTaskException += (_, e) => { WatchdogApp.Log($"Unobserved task exception (ignored): {e.Exception.GetBaseException().Message}"); e.SetObserved(); };

        if (restarts > 0) WatchdogApp.Log($"Restarted after crash (#{restarts})");
        ApplicationConfiguration.Initialize();
        Application.Run(new WatchdogApp(Math.Clamp(interval, 5, 3600)));
        return 0;
    }

    static int _crashing;
    static void CrashAndRestart(Exception? ex, int interval, int restarts, Mutex single)
    {
        if (Interlocked.Exchange(ref _crashing, 1) == 1) return;
        WatchdogApp.Log($"CRASH: {ex}");
        if (!UserExit && restarts < 5)
        {
            try { single.ReleaseMutex(); } catch { }
            try
            {
                System.Diagnostics.Process.Start(Environment.ProcessPath!, ["--interval", interval.ToString(), "--restarted", (restarts + 1).ToString()]);
            }
            catch (Exception e2) { WatchdogApp.Log($"Restart failed: {e2.Message}"); }
        }
        else WatchdogApp.Log("Not restarting (too many crashes or user exit).");
        Environment.Exit(3);
    }

    static async Task<int> RunCli(string cmd, string[] args)
    {
        if (cmd == "scan")
        {
            var tailer = new SynapseLogTailer(fromStart: true);
            tailer.EventSeen += e =>
            {
                string val = e.Kind == SynapseEventKind.LinkStatus ? "" : $" => balanceMode {e.Value}";
                Console.WriteLine($"{e.Timestamp}  {e.Text}{val}{(e.Invalid ? "   <-- INVALID" : "")}");
            };
            tailer.ScanAll();
            return 0;
        }
        if (cmd == "listen")
        {
            int secs = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 30;
            using var listener = new HidListener();
            listener.Report += r => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {BitConverter.ToString(r, 0, 16)}");
            listener.Problem += p => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  ({p})");
            listener.Start();
            await Task.Delay(secs * 1000);
            return 0;
        }
        var hs = new RazerHeadset();
        if (cmd == "status")
        {
            var link = await hs.GetLinkStatusAsync();
            Console.WriteLine($"Device path : {hs.DevicePath ?? "(BlackShark V3 Pro dongle not found)"}");
            Console.WriteLine($"Link status : {Describe(link, v => v == 1 ? "connected" : "not connected")}");
            var bal = await hs.GetBalanceRawAsync();
            Console.WriteLine($"Balance raw : {Describe(bal, v => v == RazerHeadset.RawCenter ? "centre" : v > RazerHeadset.RawMax ? "INVALID" : "off-centre")}");
            var roller = await hs.GetRollerFunctionAsync();
            Console.WriteLine($"Roller func : {Describe(roller, _ => "")}");
            return 0;
        }
        if (cmd == "simulate-bug")
        {
            bool ok = await hs.SimulateSynapseBugAsync();
            Console.WriteLine($"Wrote raw 255 (Synapse bug value): {(ok ? "acknowledged" : "FAILED")}; read back: {Describe(await hs.GetBalanceRawAsync(), _ => "")}");
            return 0;
        }
        if (cmd == "set" && args.Length > 1 && byte.TryParse(args[1], out byte raw) && raw <= RazerHeadset.RawMax)
        {
            bool ok = await hs.SetBalanceRawAsync(raw);
            var now = await hs.GetBalanceRawAsync();
            Console.WriteLine($"Set raw {raw}: {(ok ? "acknowledged" : "FAILED")}; read back: {Describe(now, _ => "")}");
            return ok && now == raw ? 0 : 2;
        }
        Console.WriteLine("""
            BalanceWatchdog                 run in the tray: live watch, overlay, log, notify, auto-revert
            BalanceWatchdog --interval 60   tray mode, safety-net poll every N seconds (default 60)
            BalanceWatchdog status          print link state and the raw balance byte (0..20, 10 = centre)
            BalanceWatchdog set 10          write a raw balance value (0..20; 0 = all game, 20 = all chat)
            BalanceWatchdog scan            print the balance history found in Synapse's existing logs
            BalanceWatchdog listen 30       dump the headset's HID traffic for N seconds
            """);
        return 0;
    }

    static string Describe(byte? v, Func<byte, string> label) =>
        v is byte b ? $"{b} {label(b)}".TrimEnd() : "(no response)";
}
