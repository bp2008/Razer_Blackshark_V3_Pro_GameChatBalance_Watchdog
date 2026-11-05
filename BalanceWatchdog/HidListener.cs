namespace BalanceWatchdog;

/// <summary>
/// Keeps a read handle open on the dongle's vendor HID interface. Windows hands every input report
/// to every open handle, so this sees the headset's replies to Synapse's commands (e.g. "Set Game
/// Chat Balance") and its unsolicited events (e.g. headset connected) the instant they happen,
/// with no polling: the read simply blocks until the device sends something.
/// </summary>
public sealed class HidListener : IDisposable
{
    readonly CancellationTokenSource _cts = new();
    Task? _loop;

    /// <summary>Raised on a thread-pool thread for every 64-byte input report.</summary>
    public event Action<byte[]>? Report;
    /// <summary>Raised when the device can't be opened / disappears (retries automatically).</summary>
    public event Action<string>? Problem;

    public void Start() => _loop = Task.Run(RunAsync);

    async Task RunAsync()
    {
        string lastProblem = "";
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                string? path = RazerHeadset.FindVendorInterfacePath();
                if (path == null) throw new IOException("dongle not found");
                using var fs = RazerHeadset.OpenAsync(path);
                lastProblem = "";
                var buf = new byte[64];
                while (!_cts.IsCancellationRequested)
                {
                    int n = await fs.ReadAsync(buf, _cts.Token);
                    if (n == 64) Report?.Invoke((byte[])buf.Clone());
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (ex.Message != lastProblem) { lastProblem = ex.Message; Problem?.Invoke(ex.Message); }
                try { await Task.Delay(3000, _cts.Token); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(1000); } catch { }
    }
}
