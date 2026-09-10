using LuminaMonitor.Core.Ddi;
using LuminaMonitor.Core.Hid;
using LuminaMonitor.Core.Media;
using LuminaMonitor.Core.RemoteXpc;
using LuminaMonitor.Core.Tunnel;
using LuminaMonitor.Core.Usb;
using HidReports = LuminaMonitor.Core.Hid.Hid;
using XpcService = LuminaMonitor.Core.RemoteXpc.RemoteXpc;

namespace LuminaMonitor.Core;

/// <summary>
/// One phone, from the USB socket to a finger on its screen.
/// </summary>
/// <remarks>
/// The whole ladder in one object: the multiplexer, the lockdown TLS session,
/// the CoreDevice tunnel with its own IPv6 and TCP, the RSD directory, the
/// developer image and the media stream that opens the HID gate. Each rung
/// publishes <see cref="StateChanged"/>; the only thing the caller has to do
/// between rungs is listen to <see cref="UnlockRequired"/> and ask the person
/// to unlock the phone.
///
/// <para>It also keeps itself alive. A stream that goes quiet is answered by a
/// key frame request, then by a new media session, then by the soft reset — the
/// developer image taken down and put back up, which restarts the daemons the
/// image carries and is the only thing found to revive a display service that
/// has stopped answering. Three of those without success and the session gives
/// up and says so through <see cref="RestartRequired"/>: past that point only
/// the person restarting the phone helps.</para>
/// </remarks>
public sealed class DeviceSession : IAsyncDisposable
{
    private const int LockdownPort = 62078;

    /// <summary>Deaf openings of the display service in a row before the image is cycled.</summary>
    private const int DeafOpeningsBeforeReset = 2;

    /// <summary>Soft resets in a row without a working mirror before we stop trying.</summary>
    private const int MaxResets = 3;

    private const int UnlockRetryMs = 3000;
    private const int UnlockAttempts = 200;      // 200 x 3 s = 10 min

    private readonly DdiSource _ddi;
    private readonly ILog? _log;

    private UsbmuxClient? _mux;
    private UsbmuxClient? _lockdownPipe;
    private UsbmuxClient? _servicePipe;
    private CdTunnel? _tunnel;
    private TunnelNet? _net;
    private Rsd? _rsd;
    private MediaSession? _media;
    private InputInjector? _input;

    private int _deafOpenings;
    private int _resets;
    private bool _recovering;

    public DeviceSession(DdiSource ddi, ILog? log = null)
    {
        _ddi = ddi;
        _log = log;
    }

    public SessionState State { get; private set; } = SessionState.Detached;

    public DeviceInfo? Device { get; private set; }

    public event Action<SessionState>? StateChanged;

    /// <summary>The phone is locked: ask the person to unlock it, the climb resumes alone.</summary>
    public event Action<string>? UnlockRequired;

    /// <summary>Nothing this side can do revives the mirror: the phone itself has to go down.</summary>
    public event Action<string>? RestartRequired;

    /// <summary>Every RTP datagram of the video stream, relayed from the media session.</summary>
    public event Action<ReadOnlyMemory<byte>>? RtpPacket;

    /// <summary>Valid once <see cref="State"/> is <see cref="SessionState.MediaUp"/>.</summary>
    public InputInjector Input => _input ?? throw new LuminaException("Session non connectee : appeler ConnectAsync d'abord.");

    /// <summary>How many soft resets this session has needed, for the window's counters.</summary>
    public int Resets => _resets;

    /// <summary>
    /// Which codec banks the media offer advertises. Bank 123 alone by
    /// default, which is H.264: the phone prefers HEVC when both are offered,
    /// and HEVC needs a Store extension Windows does not ship.
    /// </summary>
    internal VideoCodecs VideoCodecs { get; set; } = VideoCodecs.AvcOnly;

    /// <summary>
    /// Every lever of the video negotiation and of the receiver feedback.
    /// </summary>
    /// <remarks>
    /// Left at <see cref="StreamTuning.Default"/> the offer is Xcode's own, byte
    /// for byte, and no feedback loop runs; anything else is an experiment whose
    /// ground is written up in <see cref="StreamTuning"/>. Carried through every
    /// restart, soft reset included, because a stream rebuilt with different
    /// parameters would silently invalidate whatever the run was measuring.
    /// </remarks>
    internal StreamTuning VideoTuning { get; set; } = StreamTuning.Default;

    /// <summary>Whether the media session decodes the stream; off, the packets are only counted.</summary>
    public bool DecodeVideo { get; set; }

    /// <summary>Every decoded picture; the pixels belong to the decoder until the handler returns.</summary>
    public event Action<VideoFrame>? FrameDecoded;

    /// <summary>The media stream itself, for the probe: its answer and its counters.</summary>
    internal MediaSession? Media => _media;

    /// <summary>The tunnel's network, for the probe: pinging while the input path is loaded.</summary>
    internal TunnelNet? Net => _net;

    /// <summary>What the video path has counted, or null while there is no stream.</summary>
    public MediaStats? MediaStatistics => _media?.Stats;

    /// <summary>Forgets the video path's high-water marks, so the next window measures itself.</summary>
    public void ResetMediaPeaks() => _media?.ResetPeaks();

    /// <summary>
    /// Closes the tunnel pump's measuring window and hands it over; null while
    /// there is no tunnel.
    /// </summary>
    /// <remarks>
    /// Taking is what clears it, so exactly one caller may ask, once per line.
    /// Two readers would each see half a window and neither would know it.
    /// </remarks>
    public TunnelStats? TunnelStatistics => _net?.TakeStats();

    /// <summary>
    /// Builds a fresh HID channel under the existing injector, and says whether
    /// it worked.
    /// </summary>
    /// <remarks>
    /// The narrowest repair the session has. A HID channel can die on its own —
    /// a RST from the daemon, a write that never drains — while the tunnel, the
    /// directory and the video stream are all perfectly alive, and taking the
    /// whole ladder down for it costs the person ten seconds and a black
    /// picture. The service is in the directory the session already loaded, so
    /// reopening it is one connection.
    ///
    /// <para>When even that fails there is nothing narrower left, so the session
    /// enters <see cref="SessionState.Faulted"/> and the window rebuilds it from
    /// the cable up, exactly as it does for any other lost session.</para>
    /// </remarks>
    public async Task<bool> RebuildInputAsync()
    {
        if (_rsd is null || _input is null)
            return false;
        try
        {
            await _input.ReopenAsync();
            Info("Canal HID reouvert : l'entree repart sur une connexion neuve.");
            return true;
        }
        catch (Exception exception)
        {
            Warn($"Reouverture du canal HID impossible : {exception.Message}");
            Enter(SessionState.Faulted);
            return false;
        }
    }

    /// <summary>
    /// Climbs the ladder, and cycles the developer image rather than failing
    /// when the display service turns out to be deaf twice in a row.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellation = default)
    {
        while (true)
        {
            try
            {
                await ClimbAsync(cancellation);
                return;
            }
            catch (Exception exception)
            {
                if (DisplayServiceDeaf(exception) && _deafOpenings >= DeafOpeningsBeforeReset)
                {
                    _deafOpenings = 0;
                    await ReleaseAsync();
                    if (await SoftResetAsync(cancellation))
                        continue;
                }
                Enter(SessionState.Faulted);
                await ReleaseAsync();
                if (exception is LuminaException) throw;
                throw new LuminaException(exception.Message, exception);
            }
        }
    }

    public async Task DisconnectAsync()
    {
        await ReleaseAsync();
        Enter(SessionState.Detached);
    }

    public async ValueTask DisposeAsync() => await ReleaseAsync();

    private async Task ClimbAsync(CancellationToken cancellation) =>
        await ClimbFromAsync(await AttachAsync(cancellation), cancellation);

    /// <summary>The three things every rung above the multiplexer needs.</summary>
    private readonly record struct Attachment(long DeviceId, string Udid, PairRecord Record);

    /// <summary>
    /// The multiplexer, the first device it lists, and the pairing record
    /// Apple's own app already negotiated.
    /// </summary>
    private async Task<Attachment> AttachAsync(CancellationToken cancellation)
    {
        _mux = await UsbmuxClient.ConnectAsync();
        var listing = await _mux.RequestAsync("ListDevices");
        if (listing.GetValueOrDefault("DeviceList") is not List<object> devices || devices.Count == 0)
            throw new LuminaException("Aucun appareil attache.");
        var first = (Dictionary<string, object>)devices[0];
        long deviceId = (long)first["DeviceID"];
        var properties = first.GetValueOrDefault("Properties") as Dictionary<string, object>;
        string udid = properties?.GetValueOrDefault("SerialNumber")?.ToString()
            ?? throw new LuminaException("Appareil sans UDID.");
        Info($"Appareil usbmux #{deviceId}, UDID {DeviceInfo.Mask(udid)}.");

        var pairReply = await _mux.RequestAsync("ReadPairRecord",
            new Dictionary<string, object> { ["PairRecordID"] = udid });
        if (pairReply.GetValueOrDefault("PairRecordData") is not byte[] pairBytes)
            throw new LuminaException("Appareil non appaire : ouvrir l'app Appareils Apple et repondre « Se fier a cet ordinateur ».");
        var pairDict = Plist.Read(pairBytes) as Dictionary<string, object>
            ?? throw new LuminaException("Enregistrement d'appairage illisible.");
        var record = PairRecord.Parse(pairDict);
        Info($"Enregistrement d'appairage charge (HostID {pairDict["HostID"]}).");
        Enter(SessionState.Attached);
        return new Attachment(deviceId, udid, record);
    }

    private async Task ClimbFromAsync(Attachment attached, CancellationToken cancellation)
    {
        var (deviceId, udid, record) = attached;

        // 1) The TLS session, and who the phone says it is.
        _lockdownPipe = await UsbmuxClient.ConnectAsync();
        var lockdown = new LockdownClient(await _lockdownPipe.ConnectToDeviceAsync(deviceId, LockdownPort));
        string? refusal = await lockdown.StartSessionAsync(record);
        if (refusal is not null)
            throw new LuminaException($"StartSession refuse : {refusal}");
        Device = new DeviceInfo(udid,
            await ValueAsync(lockdown, "DeviceName"),
            await ValueAsync(lockdown, "ProductType"),
            await ValueAsync(lockdown, "ProductVersion"),
            await ValueAsync(lockdown, "BuildVersion"));
        Info($"Session TLS ouverte : {Device.Name ?? "?"}, {Device.ProductType ?? "?"} {Device.ProductVersion ?? "?"} ({Device.BuildVersion ?? "?"}).");
        Enter(SessionState.Paired);

        // 2) Developer Mode, read where the switch itself lives.
        var amfi = await lockdown.GetValueRawAsync("DeveloperModeStatus", "com.apple.security.mac.amfi");
        object? developerMode = amfi.GetValueOrDefault("Value");
        if (developerMode is not true)
            throw new LuminaException($"Mode developpeur inactif (DeveloperModeStatus = {developerMode ?? amfi.GetValueOrDefault("Error") ?? "?"})"
                + " — Reglages > Confidentialite et securite > Mode developpeur.");
        Info("Mode developpeur actif.");

        // 3) The developer image, before the tunnel: the RSD directory only
        //    lists the HID services once the image is mounted.
        var manager = new DdiManager(deviceId, record);
        manager.UnlockRequired += message => UnlockRequired?.Invoke(message);
        var status = await manager.EnsureMountedAsync(_ddi, new LogProgress(_log), cancellation);
        if (status is not (DdiStatus.AlreadyMounted or DdiStatus.Mounted))
            throw new LuminaException($"Image developpeur indisponible : {status}.");
        Info(status == DdiStatus.AlreadyMounted ? "Image developpeur deja montee." : "*** IMAGE DEVELOPPEUR MONTEE ***");
        Enter(SessionState.DdiMounted);

        // 4) The tunnel, our IPv6/TCP over it, and the phone's service directory.
        var (port, ssl) = await lockdown.StartServiceAsync(CdTunnel.ServiceName);
        _servicePipe = await UsbmuxClient.ConnectAsync();
        Stream service = await _servicePipe.ConnectToDeviceAsync(deviceId, port);
        if (ssl) service = await PlistService.WrapTlsAsync(service, record.HostCertificate);
        _tunnel = new CdTunnel(service);
        var handshake = await _tunnel.EstablishAsync();
        var pipe = _servicePipe!;
        _net = new TunnelNet(_tunnel, handshake)
        {
            Log = line => Info(line),
            SocketAvailable = () => pipe.Available,
        };
        _net.Start();
        _rsd = new Rsd(_net);
        await _rsd.LoadAsync(handshake.ServerRsdPort);
        Info($"Annuaire : {_rsd.Services.Count} services.");
        if (!_rsd.Services.ContainsKey(HidReports.ServiceName))
            throw new LuminaException("Service HID absent de l'annuaire : l'image developpeur n'est pas montee.");
        Enter(SessionState.TunnelUp);

        // 5) The media stream that opens the HID gate, then the HID channel.
        var media = new MediaSession(_net, _rsd)
        {
            Codecs = VideoCodecs,
            DecodeVideo = DecodeVideo,
            Tuning = VideoTuning,
        };
        media.RtpPacket += packet => RtpPacket?.Invoke(packet);
        media.FrameDecoded += frame => FrameDecoded?.Invoke(frame);
        media.StreamStalled += action => OnStallAsync(media, action);
        _media = media;
        try
        {
            await media.StartAsync(new LogProgress(_log), line => Info("    display " + line));
            _deafOpenings = 0;
        }
        catch (Exception exception) when (DisplayServiceDeaf(exception))
        {
            _deafOpenings++;
            Warn($"Service d'affichage muet ({_deafOpenings}e fois de suite) : {exception.Message}");
            throw;
        }
        var hid = await _rsd.OpenAsync(HidReports.ServiceName, writePatience: InputInjector.ChannelPatience);
        _input = new InputInjector(_rsd, hid);
        var surfaces = await _input.ListSurfacesAsync();
        Info($"Surfaces HID : {Xpc.Dump(surfaces).Trim().Replace("\n", " | ")}");
        Enter(SessionState.MediaUp);
        media.StartWatch();
    }

    // --- Keeping the mirror alive -------------------------------------------------

    /// <summary>
    /// The display service is not answering at all.
    /// </summary>
    /// <remarks>
    /// Two shapes, one cause. The daemon that has frozen accepts the TCP
    /// connection and then says nothing, so the HTTP/2 opening times out — "pas
    /// de SETTINGS du telephone en 3 s". The daemon that has gone refuses or
    /// drops the connection outright, which arrives as an
    /// <see cref="IOException"/>. Either way the image's daemons need
    /// restarting, and only unmounting the image does that.
    /// </remarks>
    private static bool DisplayServiceDeaf(Exception exception) =>
        exception is TimeoutException or IOException ||
        (exception is LuminaException && exception.InnerException is TimeoutException or IOException);

    /// <summary>
    /// What to do about a stream that went quiet, one rung at a time.
    /// </summary>
    /// <remarks>
    /// Called from the media session's own watch task, which awaits it: this is
    /// the only thing running while it runs, so each rung is finished before the
    /// next is considered.
    /// </remarks>
    private async Task OnStallAsync(MediaSession media, StallAction action)
    {
        if (!ReferenceEquals(media, _media))
            return;                                     // a watch left over from a session already replaced

        switch (action)
        {
            case StallAction.RequestKeyFrame:
                Warn($"Aucun paquet RTP depuis {StreamWatchdog.StallSeconds:0} s : demande d'image cle.");
                await media.RequestKeyFrameAsync();
                break;

            case StallAction.RestartStream:
                Warn($"Toujours rien {StreamWatchdog.KeyFrameGraceSeconds:0} s apres l'image cle : relance de la session media.");
                if (await media.RestartAsync() is StallAction.SoftReset)
                    goto case StallAction.SoftReset;
                break;

            case StallAction.SoftReset:
                Warn($"{StreamWatchdog.RestartFailuresBeforeReset} relances de flux sans succes : reset doux.");
                await RecoverAsync();
                break;

            case StallAction.Recovered:
                Info("Flux video retabli.");
                _resets = 0;
                _deafOpenings = 0;
                break;
        }
    }

    /// <summary>The soft reset from inside a live session: cycle the image, then climb again.</summary>
    private async Task RecoverAsync()
    {
        if (_recovering)
            return;
        _recovering = true;
        try
        {
            if (!await SoftResetAsync(CancellationToken.None))
            {
                Enter(SessionState.Faulted);
                await ReleaseAsync();
                return;
            }
            await ClimbAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            Warn($"Relance du miroir echouee : {exception.Message}");
            Enter(SessionState.Faulted);
            await ReleaseAsync();
        }
        finally
        {
            _recovering = false;
        }
    }

    /// <summary>
    /// Takes every developer image down, so the climb that follows puts ours
    /// back up and restarts the daemons it carries.
    /// </summary>
    /// <remarks>
    /// The proven remedy for a display service that has stopped answering, and
    /// the only one: unplugging the cable does not do it, because the image
    /// stays mounted across it. Unmounting is refused while the screen is
    /// locked, exactly like the upload — so the person is asked, and the reset
    /// waits for them, every three seconds for at most ten minutes.
    /// </remarks>
    private async Task<bool> SoftResetAsync(CancellationToken cancellation)
    {
        if (_resets >= MaxResets)
        {
            Warn($"{MaxResets} relances du miroir sans succes : il n'y a plus rien a tenter d'ici.");
            RestartRequired?.Invoke("Redemarre l'iPhone.");
            return false;
        }
        _resets++;
        Enter(SessionState.Resetting);
        Info($"Reset doux {_resets}/{MaxResets} : demontage de l'image developpeur.");
        await ReleaseAsync();

        var progress = new LogProgress(_log);
        try
        {
            var attached = await AttachAsync(cancellation);
            var manager = new DdiManager(attached.DeviceId, attached.Record);
            for (int attempt = 0; ; attempt++)
            {
                var (unmounted, refusal) = await manager.UnmountAllAsync(progress);
                if (refusal is null)
                {
                    Info($"{unmounted} image(s) demontee(s) — les demons de l'image vont redemarrer au remontage.");
                    return true;
                }
                if (!refusal.Contains("DeviceLocked", StringComparison.Ordinal))
                {
                    Warn($"Demontage refuse : {refusal}");
                    return false;
                }
                if (attempt == 0)
                {
                    Info("iPhone VERROUILLE : le reset attend le deverrouillage (10 min max).");
                    UnlockRequired?.Invoke("Deverrouille l'iPhone pour relancer le miroir");
                }
                else if (attempt % 20 == 0) Info($"  toujours verrouille ({attempt * 3} s)…");
                if (attempt >= UnlockAttempts)
                {
                    Warn("Toujours verrouille apres 10 min : reset abandonne.");
                    return false;
                }
                await Task.Delay(UnlockRetryMs, cancellation);
            }
        }
        catch (Exception exception)
        {
            Warn($"Reset doux impossible : {exception.Message}");
            return false;
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    private async Task ReleaseAsync()
    {
        if (_media is not null) { try { await _media.StopAsync(); } catch (Exception) { } _media = null; }
        if (_input is not null)
        {
            var input = _input;
            _input = null;
            try { Info($"Canaux HID fermes en {await input.CloseAsync():F0} ms."); }
            catch (Exception) { }
        }
        _rsd = null;
        if (_net is not null) { try { await _net.DisposeAsync(); } catch (Exception) { } _net = null; }
        _tunnel = null;
        _servicePipe?.Dispose();
        _servicePipe = null;
        _lockdownPipe?.Dispose();
        _lockdownPipe = null;
        _mux?.Dispose();
        _mux = null;
    }

    private static async Task<string?> ValueAsync(LockdownClient lockdown, string key)
    {
        var reply = await lockdown.GetValueRawAsync(key);
        return reply.GetValueOrDefault("Value")?.ToString();
    }

    private void Enter(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private void Info(string message) => _log?.Info(message);

    private void Warn(string message) => _log?.Warn(message);

    /// <summary>
    /// A synchronous bridge from the bricks' IProgress to the session's log:
    /// the lines must land in the order they were produced, which
    /// <see cref="Progress{T}"/> does not promise.
    /// </summary>
    private sealed class LogProgress : IProgress<string>
    {
        private readonly ILog? _log;

        public LogProgress(ILog? log) => _log = log;

        public void Report(string value) => _log?.Info(value);
    }
}
