using System.Runtime.InteropServices;
using LuminaMonitor.Core.Audio.CoreAudio;

namespace LuminaMonitor.Core.Audio;

/// <summary>Which way an endpoint carries sound.</summary>
public enum AudioFlow
{
    /// <summary>A render endpoint: speakers, headphones, the phone's hands-free "speaker".</summary>
    Output,
    /// <summary>A capture endpoint: a microphone, the phone's hands-free "microphone".</summary>
    Input,
}

/// <summary>DEVICE_STATE_XXX of an endpoint.</summary>
[Flags]
public enum AudioEndpointState
{
    Active = 0x1,
    Disabled = 0x2,
    NotPresent = 0x4,
    Unplugged = 0x8,
}

/// <summary>What a Bluetooth endpoint is for, when it belongs to a paired phone.</summary>
public enum BluetoothAudioRole
{
    /// <summary>Not a Bluetooth endpoint.</summary>
    None,
    /// <summary>The A2DP sink: the phone's media sound arriving on this PC.</summary>
    Media,
    /// <summary>
    /// The hands-free profile (HFP, Windows in the "car kit" role): an input
    /// carrying the far end of a call, an output carrying this PC's voice to the phone.
    /// </summary>
    HandsFree,
    /// <summary>A Bluetooth endpoint of another kind (a headset, speakers).</summary>
    Other,
}

/// <summary>One Windows audio endpoint, as the sound settings list it.</summary>
/// <param name="Id">The MMDevice endpoint id, stable across reboots (what a setting remembers).</param>
/// <param name="Name">The full name Windows shows, for example "Headphones (USB Audio Interface)".</param>
/// <param name="DeviceName">The device it belongs to, for example "USB Audio Interface" or "Jane's iPhone".</param>
/// <param name="Flow">Output (render) or input (capture).</param>
/// <param name="State">Active, disabled, absent or unplugged.</param>
/// <param name="IsDefault">The default endpoint of its flow for multimedia.</param>
/// <param name="IsDefaultForCommunications">The default endpoint of its flow for calls.</param>
/// <param name="BluetoothRole">What it is for, when it belongs to a Bluetooth device.</param>
public sealed record AudioEndpoint(
    string Id,
    string Name,
    string DeviceName,
    AudioFlow Flow,
    AudioEndpointState State,
    bool IsDefault,
    bool IsDefaultForCommunications,
    BluetoothAudioRole BluetoothRole)
{
    public bool IsActive => (State & AudioEndpointState.Active) != 0;
}

/// <summary>One audio session on an output endpoint: who is playing there, and how loud right now.</summary>
/// <param name="ProcessId">The process that owns the session (0 for the system sounds).</param>
/// <param name="ProcessName">Its executable name, when it can be read.</param>
/// <param name="DisplayName">The session's display name, often empty.</param>
/// <param name="Identifier">The session identifier string (names the executable and the endpoint).</param>
/// <param name="IsActive">AudioSessionStateActive: a stream is running in it.</param>
/// <param name="Peak">Its peak meter, 0 to 1, over the last metering period.</param>
public sealed record AudioSessionInfo(
    uint ProcessId,
    string ProcessName,
    string DisplayName,
    string Identifier,
    bool IsActive,
    float Peak);

/// <summary>
/// The Windows audio endpoints, read through the MMDevice API: what a window
/// offers as "play on" and "record from", and the measurements that prove a
/// sound is really going somewhere.
/// </summary>
/// <remarks>
/// Every method creates its enumerator and releases it before returning, and
/// can be called from any thread-pool thread: COM objects here are free-threaded
/// (the implicit MTA, see <c>CoreAudioNative.EnsureMta</c>). Do not call from the
/// window's STA thread: <see cref="Task.Run(Action)"/> first.
/// </remarks>
public static class AudioEndpoints
{
    /// <summary>
    /// Every endpoint of a flow — both flows when <paramref name="flow"/> is null.
    /// Inactive ones (disabled, unplugged, absent) only when asked: they are
    /// how the phone's hands-free endpoints look outside a call.
    /// </summary>
    public static IReadOnlyList<AudioEndpoint> List(AudioFlow? flow = null, bool includeInactive = false)
    {
        CoreAudioNative.EnsureMta();
        var enumerator = CoreAudioNative.CreateEnumerator();
        try
        {
            string? defaultOutput = DefaultId(enumerator, DataFlow.Render, Role.Multimedia);
            string? defaultInput = DefaultId(enumerator, DataFlow.Capture, Role.Multimedia);
            string? commsOutput = DefaultId(enumerator, DataFlow.Render, Role.Communications);
            string? commsInput = DefaultId(enumerator, DataFlow.Capture, Role.Communications);

            int dataFlow = flow switch
            {
                AudioFlow.Output => DataFlow.Render,
                AudioFlow.Input => DataFlow.Capture,
                _ => DataFlow.All,
            };
            uint mask = includeInactive ? DeviceStates.All : DeviceStates.Active;
            CoreAudioNative.Check(enumerator.EnumAudioEndpoints(dataFlow, mask, out var collection), "EnumAudioEndpoints");
            var result = new List<AudioEndpoint>();
            try
            {
                CoreAudioNative.Check(collection!.GetCount(out uint count), "IMMDeviceCollection.GetCount");
                for (uint i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) < 0 || device is null) continue;
                    try
                    {
                        result.Add(Describe(device, defaultOutput, defaultInput, commsOutput, commsInput));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
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

    /// <summary>The default endpoint of a flow for multimedia, or null when there is none.</summary>
    public static AudioEndpoint? Default(AudioFlow flow) =>
        List(flow).FirstOrDefault(endpoint => endpoint.IsDefault);

    /// <summary>
    /// The peak level an endpoint is carrying right now, 0 to 1 — the objective
    /// sign that sound flows through it. Null when the endpoint cannot be read
    /// (gone, or inactive).
    /// </summary>
    public static float? Peak(string endpointId)
    {
        CoreAudioNative.EnsureMta();
        var enumerator = CoreAudioNative.CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(endpointId, out var device) < 0 || device is null) return null;
            try
            {
                Guid iid = CoreAudioGuids.IidAudioMeterInformation;
                if (device.Activate(ref iid, CoreAudioNative.ClsCtxAll, IntPtr.Zero, out object? meter) < 0 || meter is null)
                    return null;
                try
                {
                    return ((IAudioMeterInformation)meter).GetPeakValue(out float peak) < 0 ? null : peak;
                }
                finally
                {
                    Marshal.ReleaseComObject(meter);
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
    /// The audio sessions of an output endpoint — which processes play there,
    /// active or not, with each one's own peak level. This is how the volume
    /// mixer knows who makes a sound.
    /// </summary>
    public static IReadOnlyList<AudioSessionInfo> Sessions(string endpointId)
    {
        CoreAudioNative.EnsureMta();
        var result = new List<AudioSessionInfo>();
        var enumerator = CoreAudioNative.CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(endpointId, out var device) < 0 || device is null) return result;
            try
            {
                Guid iid = CoreAudioGuids.IidAudioSessionManager2;
                if (device.Activate(ref iid, CoreAudioNative.ClsCtxAll, IntPtr.Zero, out object? managerObject) < 0
                    || managerObject is null)
                    return result;
                var manager = (IAudioSessionManager2)managerObject;
                try
                {
                    if (manager.GetSessionEnumerator(out var sessions) < 0 || sessions is null) return result;
                    try
                    {
                        sessions.GetCount(out int count);
                        for (int i = 0; i < count; i++)
                        {
                            if (sessions.GetSession(i, out var control) < 0 || control is null) continue;
                            try
                            {
                                result.Add(DescribeSession(control));
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(control);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sessions);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(manager);
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
        return result;
    }

    /// <summary>
    /// Every property an endpoint's store holds, as "key = value" lines — the
    /// probe's way of finding which ones tell a phone's endpoint apart.
    /// </summary>
    internal static IReadOnlyList<string> DumpProperties(string endpointId)
    {
        CoreAudioNative.EnsureMta();
        var lines = new List<string>();
        var enumerator = CoreAudioNative.CreateEnumerator();
        try
        {
            if (enumerator.GetDevice(endpointId, out var device) < 0 || device is null) return lines;
            try
            {
                if (device.OpenPropertyStore(0, out var store) < 0 || store is null) return lines;
                try
                {
                    store.GetCount(out uint count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (store.GetAt(i, out var key) < 0) continue;
                        string? value = CoreAudioNative.ReadString(store, key);
                        if (value is not null) lines.Add($"{{{key.FormatId}}},{key.PropertyId} = {value}");
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(store);
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
        return lines;
    }

    private static AudioEndpoint Describe(IMMDevice device, string? defaultOutput, string? defaultInput,
        string? commsOutput, string? commsInput)
    {
        CoreAudioNative.Check(device.GetId(out IntPtr idText), "IMMDevice.GetId");
        string id = CoreAudioNative.TakeString(idText);
        device.GetState(out uint state);
        var flow = ((IMMEndpoint)device).GetDataFlow(out int dataFlow) >= 0 && dataFlow == DataFlow.Capture
            ? AudioFlow.Input
            : AudioFlow.Output;

        string name = id, deviceName = "", enumeratorName = "", instance = "";
        if (device.OpenPropertyStore(0, out var store) >= 0 && store is not null)
        {
            try
            {
                name = CoreAudioNative.ReadString(store, CoreAudioGuids.DeviceFriendlyName) ?? id;
                deviceName = CoreAudioNative.ReadString(store, CoreAudioGuids.InterfaceFriendlyName) ?? "";
                enumeratorName = CoreAudioNative.ReadString(store, CoreAudioGuids.DeviceEnumeratorName) ?? "";
                instance = CoreAudioNative.ReadString(store, CoreAudioGuids.DeviceInstanceOfEndpoint) ?? "";
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }

        bool isDefault = id == (flow == AudioFlow.Output ? defaultOutput : defaultInput);
        bool isComms = id == (flow == AudioFlow.Output ? commsOutput : commsInput);
        return new AudioEndpoint(id, name, deviceName, flow, (AudioEndpointState)state, isDefault, isComms,
            RoleOf(enumeratorName, instance, deviceName));
    }

    /// <summary>
    /// Tells the phone's endpoints apart by the device they are built on, as
    /// the endpoint's own store names it: Windows's hands-free driver
    /// (BthHFAud) enumerates under <c>BTHHFENUM</c>; the A2DP sink (BthA2dp)
    /// under <c>BTHENUM</c> with the Audio Source service UUID 0000110A in its
    /// instance id. The device name is the fallback, for a store that lacks both.
    /// </summary>
    internal static BluetoothAudioRole RoleOf(string enumeratorName, string instance, string deviceName)
    {
        if (enumeratorName.Equals("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
            || instance.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("Hands-Free HF", StringComparison.OrdinalIgnoreCase))
            return BluetoothAudioRole.HandsFree;
        if (instance.Contains("{0000110A-", StringComparison.OrdinalIgnoreCase)
            || deviceName.Contains("A2DP SNK", StringComparison.OrdinalIgnoreCase))
            return BluetoothAudioRole.Media;
        if (enumeratorName.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)
            || instance.Contains("}.BTH", StringComparison.OrdinalIgnoreCase))
            return BluetoothAudioRole.Other;
        return BluetoothAudioRole.None;
    }

    private static AudioSessionInfo DescribeSession(IAudioSessionControl2 control)
    {
        control.GetState(out int state);
        control.GetProcessId(out uint pid);
        control.GetDisplayName(out IntPtr displayText);
        control.GetSessionIdentifier(out IntPtr identifierText);
        string display = CoreAudioNative.TakeString(displayText);
        string identifier = CoreAudioNative.TakeString(identifierText);
        float peak = 0;
        if (control is IAudioMeterInformation meter && meter.GetPeakValue(out float value) >= 0) peak = value;

        string processName = "";
        if (pid != 0)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                processName = process.ProcessName;
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        return new AudioSessionInfo(pid, processName, display, identifier, state == 1, peak);
    }

    private static string? DefaultId(IMMDeviceEnumerator enumerator, int dataFlow, int role)
    {
        if (enumerator.GetDefaultAudioEndpoint(dataFlow, role, out var device) < 0 || device is null) return null;
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
