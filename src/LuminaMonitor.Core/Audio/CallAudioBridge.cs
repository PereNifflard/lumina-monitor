namespace LuminaMonitor.Core.Audio;

/// <summary>
/// The two endpoints Windows's hands-free driver (BthHFAud, "&lt;phone&gt;
/// Hands-Free HF Audio") gives a connected phone: Windows plays the part of a
/// car kit.
/// </summary>
/// <param name="FromPhone">The input (capture) endpoint: the far end of the call, the voice to hear.</param>
/// <param name="ToPhone">The output (render) endpoint: what is played into it is what the far end hears.</param>
public sealed record PhoneCallEndpoints(AudioEndpoint FromPhone, AudioEndpoint ToPhone);

/// <summary>
/// A call through the PC: the chosen microphone into the phone's hands-free
/// link, and the phone's side of the call out of the chosen output. Off
/// unless started; <see cref="Dispose"/> stops it.
/// </summary>
/// <remarks>
/// <para><b>Read before wiring this to a button.</b> Opening the hands-free
/// endpoints is what makes the phone hand its audio to the PC: Windows asks the
/// phone for the Bluetooth voice channel (SCO), and iOS moves the call audio —
/// its microphone included — to this PC. During a call the person chose to
/// route to the PC, that is the point. Outside a call it is the "the phone's
/// microphone is dead" symptom already met: the phone's own apps hear nothing
/// for as long as the channel is held. So: start only on an explicit request
/// during a call, never at startup, never on a timer, and dispose as soon as
/// the call ends — the bridge stops by itself when Windows tears the link down
/// (<see cref="Stopped"/>).</para>
///
/// <para>Verified on this PC: the two WASAPI pumps (format conversion,
/// event-driven capture, bounded latency) between ordinary endpoints. Not yet
/// verified: the phone's hands-free endpoints themselves, which only exist while
/// the phone is connected over Bluetooth — see docs/BLUETOOTH_AUDIO.md for the
/// test protocol.</para>
/// </remarks>
public sealed class CallAudioBridge : IDisposable
{
    private readonly AudioPump _toPhone;
    private readonly AudioPump _fromPhone;
    private int _stopped;

    private CallAudioBridge(AudioPump toPhone, AudioPump fromPhone)
    {
        _toPhone = toPhone;
        _fromPhone = fromPhone;
    }

    /// <summary>
    /// The phone's hands-free endpoints, when Windows has them active (phone
    /// connected over Bluetooth); null otherwise — then there is nothing to bridge.
    /// </summary>
    public static PhoneCallEndpoints? FindPhoneEndpoints()
    {
        var handsFree = AudioEndpoints.List()
            .Where(e => e.BluetoothRole == BluetoothAudioRole.HandsFree)
            .ToList();
        var from = handsFree.FirstOrDefault(e => e.Flow == AudioFlow.Input);
        var to = handsFree.FirstOrDefault(e => e.Flow == AudioFlow.Output);
        return from is not null && to is not null ? new PhoneCallEndpoints(from, to) : null;
    }

    /// <summary>
    /// Starts both directions. Throws (<see cref="System.Runtime.InteropServices.COMException"/>)
    /// if an endpoint refuses to open; nothing is left running then.
    /// </summary>
    /// <param name="microphoneId">The PC microphone to send to the phone (an input <see cref="AudioEndpoint.Id"/>).</param>
    /// <param name="outputId">Where to play the phone's side of the call (an output <see cref="AudioEndpoint.Id"/>).</param>
    /// <param name="phone">From <see cref="FindPhoneEndpoints"/>.</param>
    public static CallAudioBridge Start(string microphoneId, string outputId, PhoneCallEndpoints phone) =>
        Start(microphoneId, outputId, phone.FromPhone.Id, phone.ToPhone.Id, 1f);

    /// <summary>The general form, also used by the probe's self-test (gain 0: nothing audible).</summary>
    internal static CallAudioBridge Start(string microphoneId, string outputId, string fromPhoneId, string toPhoneId, float gain)
    {
        WinRt.WinRtNative.EnsureMta();
        var toPhone = new AudioPump("micro -> telephone", microphoneId, toPhoneId, gain);
        var fromPhone = new AudioPump("telephone -> sortie", fromPhoneId, outputId, gain);
        try
        {
            toPhone.Start();
            fromPhone.Start();
        }
        catch
        {
            toPhone.Dispose();
            fromPhone.Dispose();
            throw;
        }
        var bridge = new CallAudioBridge(toPhone, fromPhone);
        toPhone.Died += bridge.OnPumpDied;
        fromPhone.Died += bridge.OnPumpDied;
        return bridge;
    }

    /// <summary>Both directions are moving sound.</summary>
    public bool IsRunning => _toPhone.IsRunning && _fromPhone.IsRunning;

    /// <summary>Frames moved (and dropped) microphone → phone, then phone → output.</summary>
    public (long Moved, long Dropped) ToPhoneFrames => (_toPhone.FramesRendered, _toPhone.FramesDropped);
    public (long Moved, long Dropped) FromPhoneFrames => (_fromPhone.FramesRendered, _fromPhone.FramesDropped);

    /// <summary>Why the bridge stopped by itself, in the interface language; null while running or after Dispose.</summary>
    public string? StopReason { get; private set; }

    /// <summary>Raised (on a pump thread) when the bridge stops by itself — usually the call ended.</summary>
    public event EventHandler? Stopped;

    /// <summary>Stops both directions and gives the endpoints back.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _stopped, 1);
        _toPhone.Dispose();
        _fromPhone.Dispose();
    }

    private void OnPumpDied(AudioPump pump)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        int code = pump.Failure ?? 0;
        StopReason = code == CoreAudio.CoreAudioNative.DeviceInvalidated
            ? CoreTexts.Current.CallBridgeEndpointGone
            : CoreTexts.Current.CallBridgeFailed(code);
        // The other direction is stopped from a thread of its own: this one is
        // a pump thread, and a pump cannot join itself.
        var other = ReferenceEquals(pump, _toPhone) ? _fromPhone : _toPhone;
        Task.Run(() =>
        {
            other.Dispose();
            pump.Dispose();
            Stopped?.Invoke(this, EventArgs.Empty);
        });
    }
}
