namespace BalanceWatchdog;

/// <summary>
/// A headset setting that Synapse can corrupt after a reconnect (it stores the headset's
/// 255 "not ready" reply and writes it back). Tracks the user's chosen value (persisted)
/// and the bookkeeping the watchdog needs to decide "adopt" vs "revert".
/// </summary>
sealed class GuardedSetting
{
    public required string Name { get; init; }          // "balance", "sidetone"
    public required string Title { get; init; }         // for notifications
    public required byte GetCmd { get; init; }          // reply cmd byte; unsolicited event = same cmd, sub 2
    public required byte SetCmd { get; init; }          // reply to a set = this cmd, sub 1
    public required Func<byte, bool> IsValid { get; init; }
    /// <summary>
    /// Valid-looking values that can also be the bug's footprint (e.g. the headset caps a 255 sidetone
    /// write at its maximum, 15). Synapse writes producing these are only accepted with a Synapse UI log entry.
    /// </summary>
    public Func<byte, bool> IsSuspicious { get; init; } = _ => false;
    public required Func<byte, string> Describe { get; init; }
    public required byte DefaultTarget { get; init; }

    public byte Target { get; private set; }
    /// <summary>False until a target was saved or chosen; the first valid reading then becomes the target.</summary>
    public bool HasTarget { get; private set; }
    public byte? LastRaw;
    public byte? PendingRaw;
    public DateTime PendingSince;
    public DateTime LastSynapseWrite = DateTime.MinValue;   // a "set" ack we didn't cause
    public DateTime OwnWriteUntil = DateTime.MinValue;
    public DateTime LastSynapseInvalid = DateTime.MinValue;
    public string LastSynapseInvalidText = "";
    public readonly Queue<DateTime> RecentFixes = new();
    public DateTime PausedUntil = DateTime.MinValue;

    string StateFile => Path.Combine(WatchdogApp.DataDir, Name == "balance" ? "target.txt" : $"target-{Name}.txt");

    public void LoadTarget()
    {
        Target = DefaultTarget;
        try
        {
            if (File.Exists(StateFile) && byte.TryParse(File.ReadAllText(StateFile).Trim(), out var t) && IsValid(t)) { Target = t; HasTarget = true; }
        }
        catch { }
    }

    /// <summary>Returns true if the target actually changed (or was set for the first time).</summary>
    public bool SetTarget(byte raw)
    {
        if (!IsValid(raw) || (raw == Target && HasTarget)) return false;
        Target = raw;
        HasTarget = true;
        PendingRaw = null;
        try { File.WriteAllText(StateFile, raw.ToString()); } catch { }
        return true;
    }
}
