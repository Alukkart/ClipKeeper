using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DeviceGuard
{
    // Windows audio device list (Core Audio) and notifications when devices appear or disappear.
    // IDs and names match what OBS shows in WASAPI sources.
    class AudioDevices : IMMNotificationClient, IDisposable
    {
        const int DEVICE_STATE_ACTIVE = 1;
        const ushort VT_LPWSTR = 31;

        IMMDeviceEnumerator en;
        bool registered;
        public event Action Changed;

        public AudioDevices(bool notify)
        {
            en = (IMMDeviceEnumerator)(new MMDeviceEnumeratorCom());
            if (notify) registered = en.RegisterEndpointNotificationCallback(this) == 0;
        }

        public List<DevItem> List(bool capture)
        {
            var res = new List<DevItem>();
            IMMDeviceCollection col;
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(capture ? EDataFlow.Capture : EDataFlow.Render,
                                                              DEVICE_STATE_ACTIVE, out col));
            try
            {
                int n;
                Marshal.ThrowExceptionForHR(col.GetCount(out n));
                for (int i = 0; i < n; i++)
                {
                    IMMDevice dev;
                    if (col.Item(i, out dev) != 0 || dev == null) continue;
                    try
                    {
                        string id;
                        if (dev.GetId(out id) != 0) continue;
                        string name = FriendlyName(dev);
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name)) res.Add(new DevItem(name, id));
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
            }
            finally { Marshal.ReleaseComObject(col); }
            return res;
        }

        static string FriendlyName(IMMDevice dev)
        {
            IPropertyStore ps;
            if (dev.OpenPropertyStore(0 /* STGM_READ */, out ps) != 0 || ps == null) return null;
            try
            {
                var key = new PropertyKey { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                PropVariant pv;
                if (ps.GetValue(ref key, out pv) != 0) return null;
                try { return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.ptr) : null; }
                finally { PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(ps); }
        }

        // ── device level meter (the same as the green bar in Windows Sound settings) ──
        readonly Dictionary<string, IAudioMeterInformation> meters = new Dictionary<string, IAudioMeterInformation>();

        // peak 0..1 or -1 if the device is unavailable
        public float Peak(string id)
        {
            IAudioMeterInformation m;
            if (!meters.TryGetValue(id, out m))
            {
                IMMDevice dev;
                if (en.GetDevice(id, out dev) != 0 || dev == null) return -1;
                try
                {
                    var iid = typeof(IAudioMeterInformation).GUID;
                    object o;
                    if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out o) != 0 || o == null) return -1;
                    m = (IAudioMeterInformation)o;
                    meters[id] = m;
                }
                finally { Marshal.ReleaseComObject(dev); }
            }
            float p;
            if (m.GetPeakValue(out p) != 0)
            {
                meters.Remove(id);
                Marshal.ReleaseComObject(m);
                return -1;
            }
            return p;
        }

        public void ResetMeters()
        {
            foreach (var m in meters.Values) try { Marshal.ReleaseComObject(m); } catch { }
            meters.Clear();
        }

        void Fire()
        {
            var h = Changed;
            if (h != null) try { h(); } catch { }
        }

        // ── IMMNotificationClient: only wake the check, nothing heavy in callbacks ──
        public int OnDeviceStateChanged(string id, int state) { Fire(); return 0; }
        public int OnDeviceAdded(string id) { Fire(); return 0; }
        public int OnDeviceRemoved(string id) { Fire(); return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, int role, string id) { return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) { return 0; }

        public void Dispose()
        {
            if (en == null) return;
            ResetMeters();
            try { if (registered) en.UnregisterEndpointNotificationCallback(this); } catch { }
            try { Marshal.ReleaseComObject(en); } catch { }
            en = null;
        }

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant pv);
    }

    // ── Core Audio COM declarations ─────────────────────────────────────────
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    enum EDataFlow { Render = 0, Capture = 1, All = 2 }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr ptr;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
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
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object iface);
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
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out int count);
        [PreserveSig] int GetChannelsPeakValues(int count, [MarshalAs(UnmanagedType.LPArray)] float[] peaks);
        [PreserveSig] int QueryHardwareSupport(out int mask);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }
}
