using System.Runtime.InteropServices;
using LuminaMonitor.Core.Audio.CoreAudio;

namespace LuminaMonitor.Core.Audio;

/// <summary>DEVICE_STATE_XXX of an endpoint.</summary>
[Flags]
public enum AudioEndpointState
{
    Active = 0x1,
    Disabled = 0x2,
    NotPresent = 0x4,
    Unplugged = 0x8,
}

/// <summary>One Windows output, as the sound settings list it.</summary>
/// <param name="Id">The MMDevice endpoint id, stable across reboots — what a setting remembers.</param>
/// <param name="Name">The full name Windows shows, for example "Headphones (USB Audio Interface)".</param>
/// <param name="DeviceName">The device it belongs to, for example "USB Audio Interface".</param>
/// <param name="State">Active, disabled, absent or unplugged.</param>
/// <param name="IsDefault">Windows's default output — the Console role, the one a media player gets.</param>
public sealed record AudioEndpoint(
    string Id,
    string Name,
    string DeviceName,
    AudioEndpointState State,
    bool IsDefault)
{
    public bool IsActive => (State & AudioEndpointState.Active) != 0;
}

/// <summary>
/// The Windows outputs, read through the MMDevice API: what the Audio panel
/// offers as "play on".
/// </summary>
/// <remarks>
/// Outputs only. There is no capture side here and there will not be one: the
/// phone advertises no incoming audio over CoreDevice (docs/AUDIO.md §5), so a
/// microphone list would be a list of things nothing can be done with.
///
/// <para><b>Which default.</b> <c>eConsole</c>, never
/// <c>eCommunications</c>. Windows keeps two defaults and they are routinely
/// different machines' worth apart — on this project's own test PC the
/// communications default is a virtual cable feeding something else. A person who
/// asks for "the default output" means the one their music comes out of, which is
/// the console role.</para>
///
/// <para>Every method creates its enumerator and releases it before returning,
/// and can be called from any thread-pool thread: the COM objects here are
/// free-threaded (the implicit MTA, see <see cref="CoreAudioNative.EnsureMta"/>).
/// Do not call from the window's STA thread: <see cref="Task.Run(Action)"/>
/// first.</para>
/// </remarks>
public static class AudioEndpoints
{
    /// <summary>
    /// Every output endpoint; the inactive ones — disabled, unplugged, absent —
    /// only when asked for.
    /// </summary>
    public static IReadOnlyList<AudioEndpoint> List(bool includeInactive = false)
    {
        CoreAudioNative.EnsureMta();
        var enumerator = CoreAudioNative.CreateEnumerator();
        try
        {
            string? preferred = DefaultId(enumerator);
            uint mask = includeInactive ? DeviceStates.All : DeviceStates.Active;
            CoreAudioNative.Check(enumerator.EnumAudioEndpoints(DataFlow.Render, mask, out var collection),
                "EnumAudioEndpoints");
            var result = new List<AudioEndpoint>();
            try
            {
                CoreAudioNative.Check(collection!.GetCount(out uint count), "IMMDeviceCollection.GetCount");
                for (uint index = 0; index < count; index++)
                {
                    if (collection.Item(index, out var device) < 0 || device is null)
                        continue;
                    try { result.Add(Describe(device, preferred)); }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection!);
            }
            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Windows's default output, or null when the machine has none.</summary>
    public static AudioEndpoint? Default() => List().FirstOrDefault(endpoint => endpoint.IsDefault);

    /// <summary>
    /// The format the audio engine mixes in on one endpoint, for the probe's
    /// listing; null when the endpoint cannot be opened.
    /// </summary>
    public static string? MixFormat(string endpointId)
    {
        CoreAudioNative.EnsureMta();
        var enumerator = CoreAudioNative.CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(endpointId, out var device) < 0 || device is null)
                return null;
            try
            {
                Guid iid = CoreAudioGuids.IidAudioClient;
                if (device.Activate(ref iid, CoreAudioNative.ClsCtxAll, IntPtr.Zero, out object? client) < 0
                    || client is null)
                    return null;
                try
                {
                    if (((IAudioClient)client).GetMixFormat(out IntPtr format) < 0)
                        return null;
                    try { return AudioFormat.Read(format).ToString(); }
                    finally { Marshal.FreeCoTaskMem(format); }
                }
                finally
                {
                    Marshal.ReleaseComObject(client);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Opens one endpoint by id, or the default output when the id is null or
    /// gone, and says which one it got.
    /// </summary>
    /// <remarks>
    /// A stored identifier that no longer resolves is not treated as an error:
    /// headphones get unplugged, and a person who chose them once should get sound
    /// out of whatever is there rather than silence and a message. The caller logs
    /// the substitution — see <see cref="WasapiOutput"/>.
    /// </remarks>
    internal static IMMDevice Open(IMMDeviceEnumerator enumerator, string? endpointId, out string name,
        out bool substituted)
    {
        IMMDevice? device = null;
        substituted = false;
        if (endpointId is { Length: > 0 } && enumerator.GetDevice(endpointId, out device) < 0)
            device = null;
        if (device is null)
        {
            substituted = endpointId is { Length: > 0 };
            CoreAudioNative.Check(
                enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out device),
                "GetDefaultAudioEndpoint(rendu, console)");
        }
        name = FriendlyName(device!);
        return device!;
    }

    private static AudioEndpoint Describe(IMMDevice device, string? preferred)
    {
        CoreAudioNative.Check(device.GetId(out IntPtr idText), "IMMDevice.GetId");
        string id = CoreAudioNative.TakeString(idText);
        device.GetState(out uint state);

        string name = id, deviceName = "";
        if (device.OpenPropertyStore(0, out var store) >= 0 && store is not null)
        {
            try
            {
                name = CoreAudioNative.ReadString(store, CoreAudioGuids.DeviceFriendlyName) ?? id;
                deviceName = CoreAudioNative.ReadString(store, CoreAudioGuids.InterfaceFriendlyName) ?? "";
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        return new AudioEndpoint(id, name, deviceName, (AudioEndpointState)state, id == preferred);
    }

    private static string FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) < 0 || store is null)
            return "?";
        try { return CoreAudioNative.ReadString(store, CoreAudioGuids.DeviceFriendlyName) ?? "?"; }
        finally { Marshal.ReleaseComObject(store); }
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator)
    {
        if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var device) < 0 || device is null)
            return null;
        try
        {
            return device.GetId(out IntPtr id) < 0 ? null : CoreAudioNative.TakeString(id);
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }
}
