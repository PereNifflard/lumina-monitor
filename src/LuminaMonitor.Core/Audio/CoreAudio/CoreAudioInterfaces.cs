using System.Runtime.InteropServices;

namespace LuminaMonitor.Core.Audio.CoreAudio;

// The Core Audio COM interfaces — the MMDevice API and the WASAPI render
// client — declared by hand in the style of Media\MediaFoundation.
//
// Only what plays sound is here. The capture client, the session manager and
// the peak meter were declared for an audio bridge that no longer exists (see
// docs/AUDIO.md §9): nothing reads a microphone in this project, and nothing
// will, because the phone advertises no incoming audio at all (§5).
//
// Sources. The Windows SDK is not installed here, so the headers
// (mmdeviceapi.h, audioclient.h, functiondiscoverykeys_devpkey.h) were not read
// on this machine. The IIDs,
// CLSIDs and property keys are the ones Microsoft's documentation publishes
// for each interface (learn.microsoft.com, "Core Audio APIs" reference, the
// "Requirements" block of every interface page names its header); the method
// order is the header's, which is the order of the documentation's method
// table only when that table is sorted by declaration — so it was taken from
// the interface definitions themselves, not the alphabetical lists. Each
// IID was checked by the experiment: a cast is a QueryInterface, and a wrong
// IID fails with E_NOINTERFACE on the first use rather than misbehaving.
//
// Slots never called are still declared, as Unused, so that every slot below
// keeps its position. Every method keeps its HRESULT.

/// <summary>EDataFlow. Only the render half is ever asked for here.</summary>
internal static class DataFlow
{
    public const int Render = 0;
}

/// <summary>
/// ERole. <see cref="Console"/> is the one this project asks for, and the choice
/// matters: Windows keeps a second default for calls, and on this project's own
/// test PC that one is a virtual cable feeding something else. "The default
/// output" means the one the music comes out of.
/// </summary>
internal static class Role
{
    public const int Console = 0;
    public const int Multimedia = 1;
    public const int Communications = 2;
}

/// <summary>DEVICE_STATE_XXX, as returned by <c>IMMDevice.GetState</c>.</summary>
internal static class DeviceStates
{
    public const uint Active = 0x1;
    public const uint Disabled = 0x2;
    public const uint NotPresent = 0x4;
    public const uint Unplugged = 0x8;
    public const uint All = 0xF;
}

/// <summary>PROPERTYKEY.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;

    public PropertyKey(string formatId, uint propertyId)
    {
        FormatId = new Guid(formatId);
        PropertyId = propertyId;
    }
}

/// <summary>The GUIDs and property keys, spelled out.</summary>
internal static class CoreAudioGuids
{
    /// <summary>CLSID_MMDeviceEnumerator (mmdeviceapi.h).</summary>
    public static Guid ClsidDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    public static Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    /// <summary>PKEY_Device_FriendlyName: for example "Headphones (USB Audio Interface)", the endpoint's full name.</summary>
    public static PropertyKey DeviceFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    /// <summary>PKEY_DeviceInterface_FriendlyName: the name of the device the endpoint belongs to.</summary>
    public static PropertyKey InterfaceFriendlyName = new("026e516e-b814-414b-83cd-856d6fef4822", 2);
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection? devices);
    /// <summary>E_NOTFOUND (0x80070490) when there is no endpoint of that kind.</summary>
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    [PreserveSig] int Unused06();   // RegisterEndpointNotificationCallback
    [PreserveSig] int Unused07();   // UnregisterEndpointNotificationCallback
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice? device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    /// <summary>STGM_READ = 0.</summary>
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore? properties);
    /// <summary>The endpoint id, a string the caller frees with CoTaskMemFree.</summary>
    [PreserveSig] int GetId(out IntPtr id);
    [PreserveSig] int GetState(out uint state);
}

/// <summary>IPropertyStore (propsys.h). The value is a PROPVARIANT the caller clears.</summary>
[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, IntPtr value);
}

[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    /// <summary>shareMode 0 = AUDCLNT_SHAREMODE_SHARED; durations in 100 ns units; format is a WAVEFORMATEX*.</summary>
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity,
        IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int Unused07();   // IsFormatSupported
    /// <summary>The engine's own format, a WAVEFORMATEX* the caller frees with CoTaskMemFree.</summary>
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    /// <summary>flags: AUDCLNT_BUFFERFLAGS_SILENT = 2.</summary>
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

/// <summary>The flat entry points and the constants of this module.</summary>
internal static class CoreAudioNative
{
    public const uint ClsCtxAll = 0x17;   // CLSCTX_INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER

    /// <summary>
    /// AUDCLNT_E_DEVICE_INVALIDATED: the endpoint has gone — headphones
    /// unplugged, a virtual cable stopped. What <see cref="WasapiOutput"/>
    /// answers by opening the default output instead.
    /// </summary>
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    public const uint StreamFlagsEventCallback = 0x00040000;           // AUDCLNT_STREAMFLAGS_EVENTCALLBACK
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;          // AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;       // AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY
    public const uint BufferFlagsSilent = 0x2;                          // AUDCLNT_BUFFERFLAGS_SILENT

    public const ushort VtLpwstr = 31;
    public const ushort VtUi4 = 19;
    public const ushort VtClsid = 72;

    private static readonly object MtaGate = new();
    private static IntPtr _mtaCookie;

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object instance);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int PropVariantClear(IntPtr value);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoIncrementMTAUsage(out IntPtr cookie);

    /// <summary>Throws with the HRESULT in hexadecimal.</summary>
    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} : HRESULT 0x{hr:X8}", hr);
    }

    /// <summary>
    /// Keeps the process's multithreaded apartment alive, once. A thread-pool
    /// thread that has not initialized COM itself belongs to the implicit MTA
    /// only once something has called this for the process; without it, the
    /// calls below can answer CO_E_NOTINITIALIZED.
    /// </summary>
    public static void EnsureMta()
    {
        lock (MtaGate)
        {
            if (_mtaCookie != IntPtr.Zero) return;
            Check(CoIncrementMTAUsage(out _mtaCookie), "CoIncrementMTAUsage");
        }
    }

    /// <summary>A CoTaskMem string handed over by the callee, read and freed.</summary>
    public static string TakeString(IntPtr text)
    {
        if (text == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(text) ?? ""; }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    /// <summary>The device enumerator, created fresh: it is cheap, and a fresh one never holds a stale list.</summary>
    public static IMMDeviceEnumerator CreateEnumerator()
    {
        Guid clsid = CoreAudioGuids.ClsidDeviceEnumerator;
        Guid iid = typeof(IMMDeviceEnumerator).GUID;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, CoreAudioNative.ClsCtxAll, ref iid, out object instance),
            "CoCreateInstance(MMDeviceEnumerator)");
        return (IMMDeviceEnumerator)instance;
    }

    /// <summary>One property of a store, as a string (strings, numbers and GUIDs; anything else is null).</summary>
    public static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        // A PROPVARIANT is 24 bytes on x64 (vt, three reserved words, a
        // 16-byte union); 32 leaves room on either architecture.
        IntPtr value = Marshal.AllocCoTaskMem(32);
        try
        {
            for (int i = 0; i < 32; i++) Marshal.WriteByte(value, i, 0);
            if (store.GetValue(ref key, value) < 0) return null;
            try
            {
                ushort vt = (ushort)Marshal.ReadInt16(value);
                IntPtr payload = value + 8;
                return vt switch
                {
                    VtLpwstr => Marshal.PtrToStringUni(Marshal.ReadIntPtr(payload)),
                    VtUi4 => ((uint)Marshal.ReadInt32(payload)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    VtClsid => Marshal.PtrToStructure<Guid>(Marshal.ReadIntPtr(payload)).ToString(),
                    _ => null,
                };
            }
            finally
            {
                PropVariantClear(value);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }
}
