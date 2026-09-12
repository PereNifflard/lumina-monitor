namespace LuminaMonitor.Core.Usb;

/// <summary>
/// Apple's <c>com.apple.mobile.notification_proxy</c>: a relay for the Darwin
/// notifications the phone posts to itself, opened over the trusted lockdown
/// session so the screen-lock ones come through.
/// </summary>
/// <remarks>
/// The point is a single question the video stream cannot answer: is the phone
/// locked? The picture's frame rate says whether the screen is <em>dark</em>,
/// not whether it is <em>locked</em> — a lit lock screen with the passcode
/// prompt looks exactly like the home screen from the rate alone, and a screen
/// woken by a hand on the phone drops nothing we can see. So the state is read
/// from the phone instead: <c>com.apple.springboard.lockcomplete</c> fires when
/// it locks, and <c>com.apple.springboard.lockstate</c> fires on every change,
/// lock and unlock both. Told apart by their order — a lock is a lockstate
/// followed at once by a lockcomplete, an unlock is a lockstate alone — they
/// give a clean locked/unlocked the passcode banner can follow.
///
/// <para>The framing is every lockdown service's: a four-byte length and an XML
/// plist (<see cref="PlistService"/>), here carrying <c>Command</c> messages
/// rather than <c>Request</c> ones. Observe requests are written before the read
/// pump starts, so the socket is never read and written at once.</para>
/// </remarks>
internal sealed class NotificationProxy : IDisposable
{
    public const string ServiceName = "com.apple.mobile.notification_proxy";

    /// <summary>Posted when the phone finishes locking.</summary>
    public const string LockComplete = "com.apple.springboard.lockcomplete";

    /// <summary>Posted whenever the lock state changes, in either direction.</summary>
    public const string LockState = "com.apple.springboard.lockstate";

    private readonly UsbmuxClient _pipe;
    private readonly Stream _stream;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;

    /// <summary>A notification arrived; the argument is its name, on the pump thread.</summary>
    public event Action<string>? Received;

    private NotificationProxy(UsbmuxClient pipe, Stream stream)
    {
        _pipe = pipe;
        _stream = stream;
    }

    /// <summary>
    /// Starts the service through lockdown and connects to it, on its own usbmux
    /// pipe so it lives beside the tunnel rather than through it.
    /// </summary>
    public static async Task<NotificationProxy> OpenAsync(long deviceId, LockdownClient lockdown, PairRecord record)
    {
        var (port, ssl) = await lockdown.StartServiceAsync(ServiceName);
        var pipe = await UsbmuxClient.ConnectAsync();
        Stream stream = await pipe.ConnectToDeviceAsync(deviceId, port);
        if (ssl)
            stream = await PlistService.WrapTlsAsync(stream, record.HostCertificate);
        return new NotificationProxy(pipe, stream);
    }

    /// <summary>Asks the phone to relay one notification by name. Call before <see cref="Start"/>.</summary>
    public Task ObserveAsync(string name) =>
        PlistService.WriteAsync(_stream, new Dictionary<string, object>
        {
            ["Command"] = "ObserveNotification",
            ["Name"] = name,
        });

    /// <summary>Begins reading relayed notifications, raising <see cref="Received"/> for each.</summary>
    public void Start() => _pump = Task.Run(PumpAsync);

    private async Task PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var message = await PlistService.ReadAsync(_stream);
                if (message.TryGetValue("Command", out var command) && command as string == "RelayNotification"
                    && message.TryGetValue("Name", out var name) && name is string relayed)
                    Received?.Invoke(relayed);
            }
        }
        catch (Exception)
        {
            // The channel closed — the session is going down, or the phone hung
            // up. Nothing to recover: the banner falls back to the frame rate.
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _stream.Dispose(); } catch (Exception) { }
        _pipe.Dispose();
        _cts.Dispose();
    }
}
