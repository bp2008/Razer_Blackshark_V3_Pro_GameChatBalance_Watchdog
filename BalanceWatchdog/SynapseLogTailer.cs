using System.Text;
using System.Text.RegularExpressions;

namespace BalanceWatchdog;

public enum SynapseEventKind { UserSet, ProfileReplay, ReadFromHeadset, WroteToHeadset, LinkStatus }

/// <summary>
/// Setting is "balance" (Value = Synapse balanceMode, 20 - raw) or "sidetone" (Value = level), or "" for link status.
/// </summary>
public sealed record SynapseEvent(SynapseEventKind Kind, string Setting, string Timestamp, int Value, string Text)
{
    /// <summary>True when the value is out of range (the bug signature: Synapse stored the headset's 255 "not ready").</summary>
    public bool Invalid => Setting switch
    {
        "balance" => Value < 0 || Value > 20,
        "sidetone" => Value < 0 || Value > WatchdogApp.SidetoneHardMax,
        _ => false,
    };
}

/// <summary>
/// Cheaply follows Razer Synapse's device-module logs and extracts only balance/sidetone traffic.
/// Synapse logs every read/write, which is exactly the forensic trail we want.
/// </summary>
public sealed partial class SynapseLogTailer
{
    public static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Razer", "RazerAppEngine", "User Data", "Logs");

    readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    string _lastKey = "";
    public event Action<SynapseEvent>? EventSeen;

    public SynapseLogTailer(bool fromStart = false)
    {
        // Normally start at the end of every existing file: only report new activity.
        if (!fromStart) foreach (var f in Candidates()) _offsets[f.FullName] = f.Length;
    }

    /// <summary>Parses every existing Synapse log, oldest first (for the "scan" history command).</summary>
    public void ScanAll()
    {
        if (!Directory.Exists(LogDir)) return;
        foreach (var f in new DirectoryInfo(LogDir).EnumerateFiles("products_*_mw*.log").OrderBy(f => f.LastWriteTimeUtc))
        {
            try
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null) ParseLine(line);
            }
            catch (IOException) { }
        }
    }

    IEnumerable<FileInfo> Candidates()
    {
        if (!Directory.Exists(LogDir)) return [];
        var cutoff = DateTime.UtcNow.AddHours(-2);
        return new DirectoryInfo(LogDir).EnumerateFiles("products_*_mw*.log")
            .Where(f => f.LastWriteTimeUtc > cutoff || _offsets.Count == 0);
    }

    /// <summary>Call periodically (every couple of seconds). Reads only newly appended bytes.</summary>
    public void Poll()
    {
        foreach (var f in Candidates())
        {
            try
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long len = fs.Length;
                if (!_offsets.TryGetValue(f.FullName, out long off)) off = 0; // new rotated file: read from start
                if (len < off) off = 0;
                if (len == off) continue;
                // Only scan the tail end (Synapse can write MBs during reconnect bursts).
                if (len - off > 8 * 1024 * 1024) off = len - 8 * 1024 * 1024;
                fs.Seek(off, SeekOrigin.Begin);
                var bytes = new byte[len - off];
                int read = fs.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
                // Only consume complete lines.
                int lastNl = Array.LastIndexOf(bytes, (byte)'\n', read - 1);
                if (lastNl < 0) continue;
                _offsets[f.FullName] = off + lastNl + 1;
                foreach (var line in Encoding.UTF8.GetString(bytes, 0, lastNl).Split('\n'))
                    ParseLine(line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    void ParseLine(string line)
    {
        if (line.Length < 30 || !line.Contains("alance") && !line.Contains("idetone") && !line.Contains("SIDE_TONE") && !line.Contains("ConnectionStatus r->")) return;
        string ts = line.Length > 25 && line[0] == '[' ? line.Substring(1, 23) : "";
        SynapseEvent? ev = null;
        Match m;
        int V(int g = 1) => int.Parse(m.Groups[g].Value);
        if ((m = UiSet().Match(line)).Success)
            ev = new(SynapseEventKind.UserSet, "balance", ts, V(), "user moved the balance slider in Synapse");
        else if ((m = Replay().Match(line)).Success)
            ev = new(SynapseEventKind.ProfileReplay, "balance", ts, V(), $"re-applied stored balance (version {m.Groups[2].Value})");
        else if ((m = GetResult().Match(line)).Success)
            ev = new(SynapseEventKind.ReadFromHeadset, "balance", ts, 20 - V(), $"read balance raw {V()} from headset");
        else if ((m = SetResult().Match(line)).Success)
            ev = new(SynapseEventKind.WroteToHeadset, "balance", ts, 20 - V(), $"wrote balance raw {V()} to headset");
        else if ((m = UiSidetone().Match(line)).Success)
            ev = new(SynapseEventKind.UserSet, "sidetone", ts, V(2), $"user set sidetone {V(2)} in Synapse ({(m.Groups[1].Value == "true" ? "on" : "off")})");
        else if ((m = GetSidetone().Match(line)).Success)
            ev = new(SynapseEventKind.ReadFromHeadset, "sidetone", ts, V(), $"read sidetone {V()} from headset");
        else if ((m = SetSidetone().Match(line)).Success)
            ev = new(SynapseEventKind.WroteToHeadset, "sidetone", ts, V(), $"wrote sidetone {V()} to headset");
        else if ((m = Link().Match(line)).Success)
            ev = new(SynapseEventKind.LinkStatus, "", ts, V(), $"dongle reports headset {m.Groups[2].Value}");
        if (ev == null) return;

        // Synapse logs most events twice; and link status is polled constantly. Only report changes / new events.
        string key = ev.Kind == SynapseEventKind.LinkStatus ? $"L{ev.Value}" : $"{ev.Kind}{ev.Setting}{ev.Value}{ev.Timestamp[..Math.Min(19, ev.Timestamp.Length)]}";
        if (ev.Kind == SynapseEventKind.LinkStatus && _lastLink == key) return;
        if (ev.Kind == SynapseEventKind.LinkStatus) _lastLink = key;
        else if (key == _lastKey) return;
        else _lastKey = key;
        EventSeen?.Invoke(ev);
    }
    string _lastLink = "";

    [GeneratedRegex(@"FROM UI \{""type"":""ON_SET_GAME_CHAT_BALANCE_VALUE"",""payload"":\{""balanceMode"":(-?\d+)")]
    private static partial Regex UiSet();
    [GeneratedRegex(@"""from"":""actionFromLocalStorage"",""payload"":\{""balanceMode"":(-?\d+),""version"":(\d+)")]
    private static partial Regex Replay();
    [GeneratedRegex(@"getGameChatBalanceValue r-> .*?""data"":\[(\d+)\]")]
    private static partial Regex GetResult();
    // Dongle (Valkyrie) logs the raw byte it sent as jsonData.index
    [GeneratedRegex(@"setGameChatBalanceValue r-> .*?""jsonData"":\{""index"":(-?\d+)")]
    private static partial Regex SetResult();
    [GeneratedRegex(@"FROM UI \{""type"":""ON_SET_MIC_SIDE_TONE"".*?""micSideTone"":\{""isEnabled"":(true|false),""value"":(-?\d+)")]
    private static partial Regex UiSidetone();
    [GeneratedRegex(@"getSidetoneVolume r-> .*?""data"":\[(\d+)\]")]
    private static partial Regex GetSidetone();
    [GeneratedRegex(@"setSidetoneVolume r-> .*?""jsonData"":\{""level"":(-?\d+)")]
    private static partial Regex SetSidetone();
    [GeneratedRegex(@"getWirelessConnectionStatus r->.*?""jsonData"":\{""status"":(\d+),""statusEnum"":""(\w+)""")]
    private static partial Regex Link();
}
