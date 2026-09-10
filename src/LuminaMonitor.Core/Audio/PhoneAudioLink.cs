using System.Runtime.InteropServices;
using LuminaMonitor.Core.Audio.WinRt;

namespace LuminaMonitor.Core.Audio;

/// <summary>Where a <see cref="PhoneAudioLink"/> stands.</summary>
public enum PhoneAudioState
{
    /// <summary>The open request is in flight.</summary>
    Opening,
    /// <summary>Connected: whatever the phone plays comes out of this PC.</summary>
    Open,
    /// <summary>
    /// Listening but not connected — the connection dropped (phone out of
    /// range, Bluetooth turned off, another output chosen on the phone). The
    /// phone can pick this PC again from its own audio menu.
    /// </summary>
    Waiting,
    /// <summary>The open request failed; <see cref="PhoneAudioLink.Refusal"/> says why. Still listening.</summary>
    Refused,
    /// <summary>Disposed: nothing flows any more.</summary>
    Closed,
}

/// <summary>Why an open request failed.</summary>
public enum PhoneAudioRefusal
{
    None,
    /// <summary>AudioPlaybackConnectionOpenResultStatus.RequestTimedOut: the phone did not answer (out of range, Bluetooth off).</summary>
    TimedOut,
    /// <summary>AudioPlaybackConnectionOpenResultStatus.DeniedBySystem: Windows refused (see <see cref="PhoneAudioLink.ErrorCode"/>).</summary>
    DeniedBySystem,
    /// <summary>AudioPlaybackConnectionOpenResultStatus.UnknownFailure.</summary>
    UnknownFailure,
    /// <summary>The id is not (or no longer) an A2DP source Windows knows: unpaired, or the profile disabled.</summary>
    NotAvailable,
    /// <summary>An interop call failed; <see cref="PhoneAudioLink.ErrorCode"/> holds its HRESULT.</summary>
    Error,
}

/// <summary>
/// One open <c>AudioPlaybackConnection</c>: the phone's sound plays on this
/// PC for as long as this object lives. <see cref="Dispose"/> cuts it.
/// </summary>
/// <remarks>
/// <para>The state is read by polling <c>IAudioPlaybackConnection.State</c>
/// every half second on a thread-pool thread, and <see cref="StateChanged"/> is
/// raised from that thread when it moves — marshal to the window's thread
/// before touching controls. Polling rather than the StateChanged event of
/// the class: a WinRT event is a managed delegate Windows calls back into,
/// with an IID of its own to compute and marshal, for a state that changes a
/// few times per session.</para>
///
/// <para>Where the sound goes is Windows's decision, not this object's: see
/// docs/BLUETOOTH_AUDIO.md, "Choosing the output".</para>
/// </remarks>
public sealed class PhoneAudioLink : IDisposable
{
    /// <summary>How long an open request may take before it is given up.</summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// ERROR_GEN_FAILURE as an HRESULT: the ExtendedError of an open request
    /// the phone never answered (Bluetooth off, or out of range).
    /// </summary>
    public const int PhoneDidNotAnswer = unchecked((int)0x8007001F);

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private const int PollMs = 500;

    private readonly object _gate = new();
    private IAudioPlaybackConnection? _connection;
    private Timer? _poll;
    private PhoneAudioState _state;

    private PhoneAudioLink(string deviceId, string name)
    {
        DeviceId = deviceId;
        Name = name;
        _state = PhoneAudioState.Opening;
    }

    /// <summary>The device interface id the link was opened on.</summary>
    public string DeviceId { get; }

    /// <summary>The phone's name, as given to <see cref="BluetoothAudio.StartListeningAsync"/>.</summary>
    public string Name { get; }

    /// <summary>The state as last read (at most half a second old).</summary>
    public PhoneAudioState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Why the last open request failed; <see cref="PhoneAudioRefusal.None"/> otherwise.</summary>
    public PhoneAudioRefusal Refusal { get; private set; }

    /// <summary>The HRESULT behind a refusal (ExtendedError, or the failing call's), when there is one.</summary>
    public int? ErrorCode { get; private set; }

    /// <summary>When the connection last became open, in local time.</summary>
    public DateTime? OpenedAt { get; private set; }

    /// <summary>The state in one sentence, in the interface language.</summary>
    public string Description
    {
        get
        {
            var texts = CoreTexts.Current;
            return State switch
            {
                PhoneAudioState.Opening => texts.PhoneAudioOpening(Name),
                PhoneAudioState.Open => texts.PhoneAudioOpen(Name),
                PhoneAudioState.Waiting => texts.PhoneAudioWaiting(Name),
                PhoneAudioState.Closed => texts.PhoneAudioClosed,
                _ => Refusal switch
                {
                    PhoneAudioRefusal.TimedOut => texts.PhoneAudioTimedOut(Name),
                    // What this PC answers when the phone does not respond to the
                    // Bluetooth page (measured three times, 5.3 to 12.9 s): the
                    // status says "unknown", the cause is the phone's radio.
                    PhoneAudioRefusal.UnknownFailure when ErrorCode == PhoneDidNotAnswer => texts.PhoneAudioTimedOut(Name),
                    PhoneAudioRefusal.DeniedBySystem => texts.PhoneAudioDenied(ErrorCode),
                    PhoneAudioRefusal.NotAvailable => texts.PhoneAudioNotAvailable,
                    PhoneAudioRefusal.UnknownFailure => texts.PhoneAudioUnknownFailure(ErrorCode),
                    _ => texts.PhoneAudioError(ErrorCode),
                },
            };
        }
    }

    /// <summary>Raised on a thread-pool thread whenever <see cref="State"/> moves.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Asks the phone again — after a refusal, or when the connection dropped.
    /// Returns once open, refused or timed out.
    /// </summary>
    public Task<PhoneAudioState> ReopenAsync(CancellationToken cancel = default) =>
        Task.Run(async () =>
        {
            await OpenCoreAsync(cancel).ConfigureAwait(false);
            return State;
        }, cancel);

    /// <summary>Closes the connection: the phone falls back to its own speaker (or its previous output).</summary>
    public void Dispose()
    {
        IAudioPlaybackConnection? connection;
        lock (_gate)
        {
            if (_state == PhoneAudioState.Closed && _connection is null) return;
            connection = _connection;
            _connection = null;
            _poll?.Dispose();
            _poll = null;
        }
        if (connection is not null)
        {
            // On a thread-pool thread: the object was made in the MTA, and the
            // caller may well be the window's STA thread.
            Task.Run(() =>
            {
                try
                {
                    if (connection is IClosable closable) closable.Close();
                }
                catch (COMException) { }
                finally
                {
                    Marshal.ReleaseComObject(connection);
                }
            }).Wait(TimeSpan.FromSeconds(5));
        }
        SetState(PhoneAudioState.Closed);
    }

    internal static async Task<PhoneAudioLink> CreateAsync(string deviceId, string name, CancellationToken cancel)
    {
        var link = new PhoneAudioLink(deviceId, name);
        try
        {
            return await link.StartAsync(cancel).ConfigureAwait(false);
        }
        catch
        {
            // Cancelled, or an unexpected failure: nothing may stay listening behind the caller's back.
            link.Dispose();
            throw;
        }
    }

    /// <summary>TryCreateFromId, StartAsync, OpenAsync, then the state poll.</summary>
    private async Task<PhoneAudioLink> StartAsync(CancellationToken cancel)
    {
        IAudioPlaybackConnection connection;
        var statics = BluetoothAudio.Statics();
        IntPtr id = WinRtNative.CreateString(DeviceId);
        try
        {
            int hr = statics.TryCreateFromId(id, out var created);
            if (hr < 0 || created is null)
            {
                Refuse(PhoneAudioRefusal.NotAvailable, hr < 0 ? hr : null);
                SetState(PhoneAudioState.Refused);
                return this;
            }
            connection = created;
            lock (_gate) _connection = created;
        }
        finally
        {
            WinRtNative.DeleteString(id);
            Marshal.ReleaseComObject(statics);
        }

        try
        {
            // StartAsync: Windows starts listening for this phone — from here on
            // the phone can pick this PC in its own audio menu.
            WinRtNative.Check(connection.StartAsync(out var start), "AudioPlaybackConnection.StartAsync");
            try
            {
                if (!await WinRtNative.WaitAsync(start!, StartTimeout, "AudioPlaybackConnection.StartAsync", cancel).ConfigureAwait(false))
                    throw new TimeoutException("AudioPlaybackConnection.StartAsync");
                WinRtNative.Check(start!.GetResults(), "StartAsync.GetResults");
            }
            finally
            {
                WinRtNative.CloseOperation(start);
                if (start is not null) Marshal.ReleaseComObject(start);
            }
        }
        catch (COMException exception)
        {
            Refuse(PhoneAudioRefusal.Error, exception.HResult);
            SetState(PhoneAudioState.Refused);
            return this;
        }
        catch (TimeoutException)
        {
            Refuse(PhoneAudioRefusal.TimedOut, null);
            SetState(PhoneAudioState.Refused);
            return this;
        }

        await OpenCoreAsync(cancel).ConfigureAwait(false);
        lock (_gate)
        {
            if (_connection is not null)
                _poll = new Timer(_ => Poll(), null, PollMs, PollMs);
        }
        return this;
    }

    /// <summary>OpenAsync, waited for; the result lands in the state and refusal.</summary>
    private async Task OpenCoreAsync(CancellationToken cancel)
    {
        IAudioPlaybackConnection? connection;
        lock (_gate) connection = _connection;
        if (connection is null) return;

        SetState(PhoneAudioState.Opening);
        IAsyncOperationOfOpenResult? operation = null;
        IAudioPlaybackConnectionOpenResult? result = null;
        try
        {
            WinRtNative.Check(connection.OpenAsync(out operation), "AudioPlaybackConnection.OpenAsync");
            if (!await WinRtNative.WaitAsync(operation!, OpenTimeout, "AudioPlaybackConnection.OpenAsync", cancel).ConfigureAwait(false))
            {
                Refuse(PhoneAudioRefusal.TimedOut, null);
                SetState(PhoneAudioState.Refused);
                return;
            }
            WinRtNative.Check(operation!.GetResults(out result), "OpenAsync.GetResults");
            result!.GetStatus(out int status);
            result.GetExtendedError(out int extended);
            if (status == 0)
            {
                Refuse(PhoneAudioRefusal.None, null);
                OpenedAt = DateTime.Now;
                SetState(PhoneAudioState.Open);
                return;
            }
            Refuse(status switch
            {
                1 => PhoneAudioRefusal.TimedOut,
                2 => PhoneAudioRefusal.DeniedBySystem,
                _ => PhoneAudioRefusal.UnknownFailure,
            }, extended != 0 ? extended : null);
            SetState(PhoneAudioState.Refused);
        }
        catch (COMException exception)
        {
            Refuse(PhoneAudioRefusal.Error, exception.HResult);
            SetState(PhoneAudioState.Refused);
        }
        catch (InvalidComObjectException)
        {
            // Disposed while the request was in flight: the link is closed, nothing to report.
        }
        finally
        {
            if (result is not null) Marshal.ReleaseComObject(result);
            if (operation is not null)
            {
                WinRtNative.CloseOperation(operation);
                Marshal.ReleaseComObject(operation);
            }
        }
    }

    /// <summary>One poll of IAudioPlaybackConnection.State.</summary>
    private void Poll()
    {
        IAudioPlaybackConnection? connection;
        PhoneAudioState current;
        lock (_gate)
        {
            connection = _connection;
            current = _state;
        }
        if (connection is null || current == PhoneAudioState.Opening) return;
        int opened;
        try
        {
            if (connection.GetState(out opened) < 0) return;
        }
        catch (InvalidComObjectException)
        {
            return;   // released by Dispose between the read above and here
        }

        if (opened == 1 && current != PhoneAudioState.Open)
        {
            // The phone connected on its own (picked this PC in its menu).
            Refuse(PhoneAudioRefusal.None, null);
            OpenedAt = DateTime.Now;
            SetState(PhoneAudioState.Open);
        }
        else if (opened == 0 && current == PhoneAudioState.Open)
        {
            SetState(PhoneAudioState.Waiting);
        }
    }

    private void Refuse(PhoneAudioRefusal refusal, int? errorCode)
    {
        Refusal = refusal;
        ErrorCode = errorCode;
    }

    private void SetState(PhoneAudioState state)
    {
        bool changed;
        lock (_gate)
        {
            // Once closed, stays closed: a late poll or open result must not revive it.
            if (_state == PhoneAudioState.Closed) return;
            changed = _state != state;
            _state = state;
        }
        if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
