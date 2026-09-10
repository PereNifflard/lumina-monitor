using System.Runtime.InteropServices;
using LuminaMonitor.Core.Audio.WinRt;

namespace LuminaMonitor.Core.Audio;

/// <summary>A paired device Windows can receive Bluetooth media sound (A2DP) from.</summary>
/// <param name="Id">The device interface id to hand to <see cref="BluetoothAudio.StartListeningAsync"/>.</param>
/// <param name="Name">The name Windows gives it, the phone's own name.</param>
/// <param name="IsEnabled">Whether the interface is enabled (the selector only returns enabled ones).</param>
public sealed record BluetoothPhone(string Id, string Name, bool IsEnabled);

/// <summary>
/// The phone's media sound on this PC, over Bluetooth: Windows as an A2DP
/// receiver, which it has been able to be since Windows 10 2004 — but only for
/// as long as an application holds a <c>Windows.Media.Audio.AudioPlaybackConnection</c>
/// open to that phone.
/// </summary>
/// <remarks>
/// <para>That condition is the whole diagnosis of "nothing comes through over
/// Bluetooth": pairing creates the <c>&lt;phone&gt; A2DP SNK</c> device, but no
/// sound flows until some application runs <c>GetDeviceSelector</c> →
/// <c>TryCreateFromId</c> → <c>StartAsync</c> → <c>OpenAsync</c> and keeps the
/// object alive. Nothing on a stock Windows does. Windows then plays the
/// sound itself, on the default output; this project never sees a sample.
/// See docs/BLUETOOTH_AUDIO.md.</para>
///
/// <para>Everything runs on thread-pool threads (the implicit MTA), whichever
/// thread calls: the methods are safe to await from the window.</para>
/// </remarks>
public static class BluetoothAudio
{
    internal const string ConnectionClass = "Windows.Media.Audio.AudioPlaybackConnection";
    internal const string DeviceInformationClass = "Windows.Devices.Enumeration.DeviceInformation";

    /// <summary>How long FindAllAsync may take.</summary>
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// True when this Windows has the class at all (Windows 10 2004 or later).
    /// Cheap: one activation factory, released at once.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            int hr = WinRtNative.TryGetFactory<IAudioPlaybackConnectionStatics>(ConnectionClass, out var statics);
            if (statics is not null) Marshal.ReleaseComObject(statics);
            return hr >= 0;
        }
    }

    /// <summary>
    /// The paired devices Windows can receive media sound from — for an
    /// iPhone paired to this PC, one entry named after the phone.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Windows older than 10 2004.</exception>
    public static Task<IReadOnlyList<BluetoothPhone>> ListPhonesAsync(CancellationToken cancel = default) =>
        Task.Run(() => ListCoreAsync(cancel), cancel);

    /// <summary>
    /// Opens the connection to one phone and returns once it is open, refused
    /// or timed out (<see cref="PhoneAudioLink.OpenTimeout"/>). The sound flows
    /// for as long as the returned object is alive; <see cref="PhoneAudioLink.Dispose"/>
    /// cuts it.
    /// </summary>
    /// <remarks>
    /// A refusal is not an exception: the link comes back in
    /// <see cref="PhoneAudioState.Refused"/> with its <see cref="PhoneAudioLink.Refusal"/>
    /// and text, still listening — the phone may still pick this PC from its
    /// own Bluetooth menu, and <see cref="PhoneAudioLink.ReopenAsync"/> tries again.
    /// </remarks>
    /// <param name="deviceId">A <see cref="BluetoothPhone.Id"/>.</param>
    /// <param name="name">The name to show in texts; the id's owner, when known.</param>
    /// <param name="cancel">Stops waiting (and cancels the pending open).</param>
    public static Task<PhoneAudioLink> StartListeningAsync(string deviceId, string? name = null,
        CancellationToken cancel = default) =>
        Task.Run(() => PhoneAudioLink.CreateAsync(deviceId, name ?? "", cancel), cancel);

    /// <summary>
    /// Opens Windows's per-application sound page (<c>ms-settings:apps-volume</c>),
    /// where the output of each application — this one included — can be chosen.
    /// </summary>
    public static void OpenSoundSettings()
    {
        using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ms-settings:apps-volume",
            UseShellExecute = true,
        });
    }

    /// <summary>The AQS selector Windows builds for A2DP sources (probe display).</summary>
    internal static string DeviceSelector()
    {
        var statics = Statics();
        try
        {
            WinRtNative.Check(statics.GetDeviceSelector(out IntPtr selector), "AudioPlaybackConnection.GetDeviceSelector");
            return WinRtNative.TakeString(selector);
        }
        finally
        {
            Marshal.ReleaseComObject(statics);
        }
    }

    /// <summary>The statics of AudioPlaybackConnection, or the reason there are none.</summary>
    internal static IAudioPlaybackConnectionStatics Statics()
    {
        int hr = WinRtNative.TryGetFactory<IAudioPlaybackConnectionStatics>(ConnectionClass, out var statics);
        if (hr == WinRtNative.ClassNotRegistered)
            throw new PlatformNotSupportedException(CoreTexts.Current.BluetoothAudioNeedsWindows2004);
        WinRtNative.Check(hr, "RoGetActivationFactory(AudioPlaybackConnection)");
        return statics!;
    }

    private static async Task<IReadOnlyList<BluetoothPhone>> ListCoreAsync(CancellationToken cancel)
    {
        string selector = DeviceSelector();
        WinRtNative.Check(WinRtNative.TryGetFactory<IDeviceInformationStatics>(DeviceInformationClass, out var devices),
            "RoGetActivationFactory(DeviceInformation)");
        var found = new List<BluetoothPhone>();
        IntPtr filter = WinRtNative.CreateString(selector);
        IAsyncOperationOfDeviceInformationCollection? operation = null;
        IDeviceInformationVectorView? vector = null;
        try
        {
            WinRtNative.Check(devices!.FindAllAsync(filter, out operation), "DeviceInformation.FindAllAsync");
            if (!await WinRtNative.WaitAsync(operation!, FindTimeout, "DeviceInformation.FindAllAsync", cancel).ConfigureAwait(false))
                throw new TimeoutException("DeviceInformation.FindAllAsync");
            WinRtNative.Check(operation!.GetResults(out vector), "FindAllAsync.GetResults");
            WinRtNative.Check(vector!.GetSize(out uint size), "IVectorView.Size");
            for (uint i = 0; i < size; i++)
            {
                if (vector.GetAt(i, out var item) < 0 || item is null) continue;
                try
                {
                    item.GetId(out IntPtr id);
                    item.GetName(out IntPtr name);
                    item.GetIsEnabled(out byte enabled);
                    found.Add(new BluetoothPhone(WinRtNative.TakeString(id), WinRtNative.TakeString(name), enabled != 0));
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            WinRtNative.DeleteString(filter);
            if (vector is not null) Marshal.ReleaseComObject(vector);
            if (operation is not null)
            {
                WinRtNative.CloseOperation(operation);
                Marshal.ReleaseComObject(operation);
            }
            Marshal.ReleaseComObject(devices!);
        }
        return found;
    }
}
