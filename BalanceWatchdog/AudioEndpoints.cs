using System.Runtime.InteropServices;

namespace BalanceWatchdog;

/// <summary>
/// Windows Core Audio volume/mute of the headset's endpoints, with change notifications
/// (no polling). Synapse's "Volume" slider is the Game playback endpoint's Windows volume and its
/// mic volume is the Chat capture endpoint's, so these are the same numbers Synapse shows.
/// </summary>
sealed class AudioEndpoints : IDisposable
{
    public sealed record Level(float Scalar, bool Muted);

    public Level? Volume { get; private set; }   // "Speakers (BlackShark V3 Pro - Game)"
    public Level? Mic { get; private set; }      // "Microphone (BlackShark V3 Pro - Chat)"

    /// <summary>Raised on the UI thread: "volume" or "mic".</summary>
    public event Action<string>? Changed;

    readonly Control _ui;
    Watch? _vol, _mic;

    public AudioEndpoints(Control uiInvoker) { _ui = uiInvoker; }

    /// <summary>(Re)binds to the headset endpoints. Cheap; call when the dongle/headset (re)appears.</summary>
    public void Refresh()
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
            _vol = Rebind(_vol, en, EDataFlow.Render, "Game", "volume", l => Volume = l);
            _mic = Rebind(_mic, en, EDataFlow.Capture, "Chat", "mic", l => Mic = l);
        }
        catch (Exception ex) { WatchdogApp.Log($"Audio endpoints: {ex.Message}"); }
    }

    Watch? Rebind(Watch? old, IMMDeviceEnumerator en, EDataFlow flow, string suffix, string kind, Action<Level> store)
    {
        string? id = FindEndpoint(en, flow, suffix);
        if (id != null && old?.Id == id) return old;
        old?.Dispose();
        if (id == null) return null;
        en.GetDevice(id, out var dev);
        var iid = typeof(IAudioEndpointVolume).GUID;
        dev.Activate(ref iid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out object o);
        var w = new Watch(id, (IAudioEndpointVolume)o, (scalar, muted) =>
        {
            store(new Level(scalar, muted));
            _ui.BeginInvoke(() => Changed?.Invoke(kind));
        });
        w.Endpoint.GetMasterVolumeLevelScalar(out float s);
        w.Endpoint.GetMute(out bool m);
        store(new Level(s, m));
        return w;
    }

    static string? FindEndpoint(IMMDeviceEnumerator en, EDataFlow flow, string suffix)
    {
        en.EnumAudioEndpoints(flow, 1 /*DEVICE_STATE_ACTIVE*/, out var col);
        col.GetCount(out int n);
        for (int i = 0; i < n; i++)
        {
            col.Item(i, out var dev);
            dev.OpenPropertyStore(0, out var ps);
            var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 }; // FriendlyName
            ps.GetValue(ref key, out var pv);
            string? name = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null;
            PropVariantClear(ref pv);
            if (name != null && name.Contains("BlackShark V3 Pro", StringComparison.OrdinalIgnoreCase) && name.Contains(suffix, StringComparison.OrdinalIgnoreCase))
            {
                dev.GetId(out string id);
                return id;
            }
        }
        return null;
    }

    public void Dispose() { _vol?.Dispose(); _mic?.Dispose(); }

    [ComVisible(true)]
    internal sealed class Watch : IAudioEndpointVolumeCallback, IDisposable
    {
        public readonly string Id;
        public readonly IAudioEndpointVolume Endpoint;
        readonly Action<float, bool> _onChange;
        public Watch(string id, IAudioEndpointVolume ep, Action<float, bool> onChange)
        {
            Id = id; Endpoint = ep; _onChange = onChange;
            Endpoint.RegisterControlChangeNotify(this);
        }
        public int OnNotify(IntPtr pNotify)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID context; BOOL bMuted; float fMasterVolume; ...
            bool muted = Marshal.ReadInt32(pNotify, 16) != 0;
            float vol = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(pNotify, 20));
            _onChange(vol, muted);
            return 0;
        }
        public void Dispose() { try { Endpoint.UnregisterControlChangeNotify(this); } catch { } }
    }

    // ---------------------------------------------------------------- COM interop
    enum EDataFlow { Render = 0, Capture = 1 }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort vt, r1, r2, r3; public IntPtr p, p2; }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr pNotify);
    }
}
