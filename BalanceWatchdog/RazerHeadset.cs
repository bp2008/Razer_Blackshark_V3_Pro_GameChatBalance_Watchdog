using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BalanceWatchdog;

/// <summary>
/// Talks to the BlackShark V3 Pro dongle over its vendor HID interface using the same
/// 64-byte report format Razer Synapse uses (reverse-read from Synapse's own JS):
///   [0]=report id 2, [2]=transaction 0x60, [6]=len(cmd)+len(data)+1,
///   [9]=0x80 to forward to the headset (0x00 = command for the dongle itself),
///   [10..12]=command, [13..]=data, [62]=XOR of bytes 0..61.
/// Responses echo the header; [1]=status (2=OK, 1/3=busy/retry), data starts at [13].
/// </summary>
public sealed class RazerHeadset
{
    public const ushort VendorId = 0x1532;
    public const ushort DonglePid = 0x0577;

    // Command triplets: {command, sub, dataLength}
    static readonly byte[] CmdGetBalance = [92, 0, 0];     // "Get Game Chat Balance Value"
    static readonly byte[] CmdSetBalance = [220, 0, 1];    // "Set Game Chat Balance Value"
    static readonly byte[] CmdGetLink = [32, 0, 0];        // "Get wireless Connection Status" (dongle)
    static readonly byte[] CmdGetRoller = [106, 0, 0];     // "Get Roller Function"

    public const byte RawCenter = 10;   // raw 0..20, Synapse shows balanceMode = 20 - raw
    public const byte RawMax = 20;

    readonly SemaphoreSlim _lock = new(1, 1);
    string? _path;

    public string? DevicePath => _path;

    /// <summary>Returns 1 when the headset is connected to the dongle, null if the dongle is absent/unresponsive.</summary>
    public Task<byte?> GetLinkStatusAsync() => QueryByteAsync(CmdGetLink, null, toHeadset: false);
    public Task<byte?> GetBalanceRawAsync() => QueryByteAsync(CmdGetBalance, null, toHeadset: true);
    public Task<byte?> GetRollerFunctionAsync() => QueryByteAsync(CmdGetRoller, null, toHeadset: true);

    public async Task<bool> SetBalanceRawAsync(byte raw)
    {
        if (raw > RawMax) throw new ArgumentOutOfRangeException(nameof(raw));
        var r = await QueryAsync(CmdSetBalance, [raw], toHeadset: true);
        return r != null;
    }

    /// <summary>Test aid: writes 255 exactly like Synapse's power-on bug does.</summary>
    public async Task<bool> SimulateSynapseBugAsync() => await QueryAsync(CmdSetBalance, [255], toHeadset: true) != null;

    async Task<byte?> QueryByteAsync(byte[] cmd, byte[]? data, bool toHeadset)
    {
        var r = await QueryAsync(cmd, data, toHeadset);
        return r is { Length: > 0 } ? r[0] : null;
    }

    async Task<byte[]?> QueryAsync(byte[] cmd, byte[]? data, bool toHeadset)
    {
        await _lock.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                _path ??= FindVendorInterface();
                if (_path == null) return null;
                try
                {
                    var r = await TransactAsync(_path, cmd, data, toHeadset);
                    if (r != null) return r;
                }
                catch (IOException) { _path = null; } // device re-enumerated; look it up again
                catch (UnauthorizedAccessException) { _path = null; }
            }
            return null;
        }
        finally { _lock.Release(); }
    }

    public static string? FindVendorInterfacePath() => FindVendorInterface();

    /// <summary>Opens the interface for overlapped read/write with an empty input queue.</summary>
    public static FileStream OpenAsync(string path)
    {
        var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING,
            Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { int err = Marshal.GetLastWin32Error(); h.Dispose(); throw new IOException($"open failed ({err})"); }
        Native.HidD_FlushQueue(h);
        return new FileStream(h, FileAccess.ReadWrite, 1, isAsync: true);
    }

    static async Task<byte[]?> TransactAsync(string path, byte[] cmd, byte[]? data, bool toHeadset)
    {
        using var fs = OpenAsync(path);

        var report = BuildReport(cmd, data, toHeadset);
        for (int send = 0; send < 3; send++)
        {
            await fs.WriteAsync(report);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));
            var buf = new byte[64];
            try
            {
                while (true)
                {
                    int n = await fs.ReadAsync(buf, cts.Token);
                    if (n < 14 || buf[0] != 2 || buf[2] != 0x60 || buf[10] != cmd[0] || buf[11] != 1)
                        continue; // someone else's response (Synapse polls the same device)
                    byte status = buf[1];
                    if (status == 2)
                    {
                        int len = Math.Max(1, (int)buf[12]);
                        return buf.AsSpan(13, Math.Min(len, 64 - 13)).ToArray();
                    }
                    break; // busy / failed: resend
                }
            }
            catch (OperationCanceledException) { }
            await Task.Delay(60);
        }
        return null;
    }

    static byte[] BuildReport(byte[] cmd, byte[]? data, bool toHeadset)
    {
        var r = new byte[64];
        int dl = data?.Length ?? 0;
        r[0] = 2;
        r[2] = 0x60;
        r[6] = (byte)(cmd.Length + dl + 1);
        r[9] = toHeadset ? (byte)0x80 : (byte)0;
        cmd.CopyTo(r, 10);
        data?.CopyTo(r, 10 + cmd.Length);
        byte x = 0;
        for (int i = 0; i < 62; i++) x ^= r[i];
        r[62] = x;
        return r;
    }

    /// <summary>Finds the dongle's HID collection that carries 64-byte vendor reports.</summary>
    static string? FindVendorInterface()
    {
        var hidGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        if (Native.CM_Get_Device_Interface_List_SizeW(out int size, ref hidGuid, null, 0) != 0 || size <= 1) return null;
        var buf = new char[size];
        if (Native.CM_Get_Device_Interface_ListW(ref hidGuid, null, buf, size, 0) != 0) return null;
        string needle = $"vid_{VendorId:x4}&pid_{DonglePid:x4}";
        foreach (var p in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!p.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            using var h = Native.CreateFile(p, 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (!Native.HidD_GetPreparsedData(h, out var pp)) continue;
            var caps = new byte[64];
            Native.HidP_GetCaps(pp, caps);
            Native.HidD_FreePreparsedData(pp);
            ushort usagePage = BitConverter.ToUInt16(caps, 2);
            ushort inLen = BitConverter.ToUInt16(caps, 4), outLen = BitConverter.ToUInt16(caps, 6);
            if (usagePage >= 0xFF00 && inLen == 64 && outLen == 64) return p;
        }
        return null;
    }

    static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_OVERLAPPED = 0x40000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr template);
        [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
        [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
        [DllImport("hid.dll")] public static extern bool HidD_FlushQueue(SafeFileHandle h);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_List_SizeW(out int len, ref Guid g, string? devId, int flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        public static extern int CM_Get_Device_Interface_ListW(ref Guid g, string? devId, char[] buf, int len, int flags);
    }
}
