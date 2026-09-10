using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LuminaMonitor.Core;
using LuminaMonitor.Core.Ddi;
using LuminaMonitor.Core.Hid;
using LuminaMonitor.Core.Media;
using IoPath = System.IO.Path;

namespace LuminaMonitor.App;

/// <summary>
/// The window: a drawn iPhone with the real one's picture inside it, and a
/// mouse and keyboard that reach through the cable.
/// </summary>
/// <remarks>
/// Everything below the picture is <see cref="DeviceSession"/>: one object that
/// climbs from the USB multiplexer to the HID surfaces, publishes where it has
/// got to, and hands over decoded pictures and an input injector. There is no
/// socket, no port and no token in this file — the previous version of this
/// window talked to a Raspberry Pi over TCP and Bluetooth, and every trace of
/// that is gone.
///
/// <para>What is kept from it, deliberately: the chassis and its geometry, the
/// two-buffer frame path, the deferred tap, and the keyboard translation by
/// scan code. Those were paid for in measurements, not in guesses.</para>
/// </remarks>
public partial class MainWindow : Window
{
    private readonly Settings _settings = Settings.Load();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Stopwatch _sendClock = Stopwatch.StartNew();
    private readonly object _pendingGate = new();
    private readonly HashSet<int> _heldKeys = [];

    /// <summary>The journal of the process; on for every run, not only diagnostics.</summary>
    private readonly Journal _journal = Journal.Shared;

    // --- Session ---------------------------------------------------------------

    private DeviceSession? _session;
    private DeviceWatcher? _watcher;

    /// <summary>The injector, non-null exactly while the session is up.</summary>
    private InputInjector? _input;

    private SessionState _state = SessionState.Detached;
    private string? _ddiFolder;
    private bool _connecting;
    private bool _extracting;
    private double _lastAttemptSeconds = double.NegativeInfinity;


    // --- Picture ---------------------------------------------------------------

    /// <summary>
    /// The bitmap the picture is shown from, or null before the first picture
    /// arrives. Made, filled and read on the interface thread, and nowhere else.
    /// </summary>
    /// <remarks>
    /// A <see cref="WriteableBitmap"/> is filled between <c>Lock</c> and
    /// <c>Unlock</c> by the thread that made it, which is a rule and not a
    /// suggestion — but it is also the only presentation path this project has
    /// ever measured working: 59 pictures a second at two to five milliseconds
    /// from arrival to screen.
    ///
    /// <para>A pair of <c>InteropBitmap</c> over shared sections was tried in
    /// its place, to move the conversion off this thread. It reported the same
    /// rates and showed a frozen picture: alternating <c>Image.Source</c>
    /// between two bitmaps realises each one afresh, and the <c>Invalidate</c>
    /// that follows the assignment has nothing realised yet to dirty. The
    /// counters cannot see that, because they count the assignment. Hence the
    /// return here, and hence <see cref="_presentLatencyMs"/> being measured
    /// after <c>Unlock</c> rather than after a flag.</para>
    /// </remarks>
    private WriteableBitmap? _surface;

    /// <summary>The geometry of <see cref="_surface"/>, readable off the interface thread.</summary>
    private int _surfaceWidth, _surfaceHeight;

    // --- Staging ---------------------------------------------------------------
    // The decode thread owns _pending and the interface thread owns _present;
    // they are swapped, never shared, and the swap is the only thing the lock
    // covers. NV12 rather than BGRA because it is half the bytes, and the
    // conversion has to happen on the thread that fills the back buffer anyway.

    private byte[] _pending = [];
    private byte[] _present = [];
    private int _pendingWidth, _pendingHeight, _pendingStride;
    private long _pendingArrivalTicks;
    private bool _hasPending;

    private long _framesReceived, _framesPresented;
    private long _lastFrameTicks;
    private long _lastReceived, _lastPresented;
    private double _lastSampleSeconds;
    private double _fps, _presentedFps;
    private double _latencyMs;

    // --- Frame path timings, all in milliseconds, all "the last one seen" ------
    private double _convertMs, _maxConvertMs, _acceptMs, _presentLatencyMs, _maxPresentLatencyMs;
    private long _framesSuperseded;
    private long _lastPackets, _lastDecoded;
    private long _statsReceived, _statsPresented;
    private bool _hadStream;
    private bool _wasMirroring;

    /// <summary>Which way up the phone is, answered from the pixels.</summary>
    private DeviceGeometry.Orientation _orientation = DeviceGeometry.Orientation.Portrait;
    private int _orientationPending;

    // --- Pointer ---------------------------------------------------------------

    private bool _engaged;
    private double _pointerX, _pointerY;
    private bool _pointerKnown;

    // The mouse handler sends and records; the render tick only draws. See
    // OnMouseMove.
    private bool _mouseDirty;
    private bool _pointerInside;

    // The geometry the mouse handler needs, kept ready so it never asks the
    // layout for it: one property read per render tick instead of two thousand
    // a second.
    private double _scale = 1;
    private int _pictureWidth, _pictureHeight;
    private double _slop = TapSlopWindowPx;

    // A left press the phone has not been told about yet. See OnMouseDown for
    // why the press is held back, and FlushPendingPress for the two ways it
    // stops being pending.
    private bool _pressPending;
    private double _pressStartMs;
    private double _pressX, _pressY;

    /// <summary>A finger is down on the phone: moves and a release are owed.</summary>
    private bool _touching;

    private long _mouseEvents, _lastMouseEvents;
    private long _lastReportsSent, _lastMovesDropped;

    /// <summary>The injector's counters as of the last journal line, for its rates.</summary>
    /// <remarks>
    /// Separate from the metrics pane's pair above: the pane samples once a
    /// second whether or not anything is written down, and the journal writes
    /// every ten in an ordinary run. Sharing one counter would make each of
    /// them report the other's window.
    /// </remarks>
    private long _lineReports, _lineDropped;

    // --- What the mouse handler and the render tick cost, measured ---------------
    // The two numbers that decide whether a hand on a fast mouse can starve the
    // picture: how long one mouse event is held on the interface thread, and how
    // far apart the render ticks actually landed.

    private long _moveTicks, _moveCount, _moveMaxTicks;
    private long _lastRenderTicks;
    private readonly double[] _renderGaps = new double[1024];
    private int _renderGapCount;
    private double _renderGapSum, _renderGapMax;

    /// <summary>Border width in window units, kept so a colour change can reshade the buttons.</summary>
    private double _border;
    private Color _buttonJoint, _buttonChamfer, _buttonFace;

    /// <summary>Runs the housekeeping that must not stop when the rendering does.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _upkeep = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };

    /// <summary>
    /// Refreshes the counters and the state text, four times a second.
    /// </summary>
    /// <remarks>
    /// Not from the render tick and never from the mouse handler. Text is
    /// layout, layout is the interface thread, and the interface thread is
    /// exactly what a fast hand floods: a metrics pane rebuilt per event would
    /// put two thousand measurements a second in front of the picture. Four a
    /// second is faster than an eye reads a number and costs nothing.
    /// </remarks>
    private readonly System.Windows.Threading.DispatcherTimer _display = new()
    {
        Interval = TimeSpan.FromMilliseconds(250),
    };

    // --- Replay -----------------------------------------------------------------

    /// <summary>
    /// The capture played instead of a phone, or null for an ordinary run.
    /// </summary>
    /// <remarks>
    /// <c>--replay &lt;fichier.rtp&gt;</c> puts the recorded stream through the
    /// same depacketizer, the same decoder and the same presentation as the
    /// cable does, so the picture path can be measured against a saturated
    /// interface without a phone on the desk — which at two in the morning is
    /// the difference between a measurement and a guess. Input stays live: the
    /// handlers do all their work and hand the position to a sink that counts it
    /// instead of an injector that sends it.
    /// </remarks>
    private ReplaySource? _replay;
    private readonly string? _replayPath;
    private readonly int _replaySeconds;

    // The stand-in for the injector's one-slot pending position: the same lock,
    // the same two integers, the same last-one-wins, and a counter instead of a
    // wire. Without it the mouse path measured under --replay would be missing
    // exactly the call the real one ends with.
    private readonly object _sinkGate = new();
    private int _sinkX, _sinkY;
    private bool _sinkHasMove;
    private long _sinkMoves, _sinkDropped, _sinkReports;

    // --- Diagnostic run --------------------------------------------------------

    private readonly int _diagnosticSeconds;
    private double _diagnosticStarted;
    private bool _diagnosticClosing;
    private long _errors;

    /// <summary>When the last counters line was written, diagnostic run or not.</summary>
    private double _lastStatsSeconds;

    /// <summary>How often the counters reach the journal in an ordinary run.</summary>
    private const double StatsSeconds = 10.0;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    public MainWindow()
    {
        InitializeComponent();

        _diagnosticSeconds = DiagnosticSecondsFromCommandLine();
        _replayPath = ReplayPathFromCommandLine();
        _replaySeconds = ReplaySecondsFromCommandLine();

        Loaded += OnLoaded;
        Closed += OnClosed;
        Deactivated += (_, _) => Disengage("Fenêtre quittée.");
        CompositionTarget.Rendering += OnRendering;

        _upkeep.Tick += OnUpkeep;
        _upkeep.Start();

        _display.Tick += (_, _) => UpdateMetrics();
        _display.Start();
    }

    /// <summary>Reads <c>--diagnostic &lt;secondes&gt;</c>; zero means an ordinary run.</summary>
    private static int DiagnosticSecondsFromCommandLine()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 1; i < arguments.Length - 1; i++)
            if (arguments[i] == "--diagnostic" && int.TryParse(arguments[i + 1], out int seconds))
                return Math.Clamp(seconds, 1, 3600);
        return 0;
    }

    /// <summary>
    /// Reads <c>--replay &lt;fichier.rtp&gt; [secondes]</c>.
    /// </summary>
    /// <remarks>
    /// The optional duration is the run's own stop, for a replay asked to end
    /// without <c>--diagnostic</c>; with both, the shorter one wins, which is
    /// what makes <c>--replay f.rtp 20 --diagnostic 20</c> mean one thing.
    /// </remarks>
    private static string? ReplayPathFromCommandLine()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 1; i < arguments.Length - 1; i++)
            if (arguments[i] == "--replay")
                return arguments[i + 1];
        return null;
    }

    private static int ReplaySecondsFromCommandLine()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        for (int i = 1; i < arguments.Length - 2; i++)
            if (arguments[i] == "--replay" && int.TryParse(arguments[i + 2], out int seconds))
                return Math.Clamp(seconds, 1, 3600);
        return 0;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyChassis();
        DarkenTitleBar();
        PlaceWindow();

        Stage.Focus();
        ShowIdleHint();
        UpdateState();

        _diagnosticStarted = _clock.Elapsed.TotalSeconds;
        _lastStatsSeconds = _clock.Elapsed.TotalSeconds;
        if (_diagnosticSeconds > 0)
        {
            _journal.Write($"--diagnostic {_diagnosticSeconds} s, journal {_journal.Path}");
            Report($"Diagnostic : {_diagnosticSeconds} s, journal dans {_journal.Path}");
        }

        // Said before anything else: a settings file that could not be read has
        // just been moved aside, and it is the only record of where the
        // developer image was unpacked.
        if (Settings.RescuedTo is string rescued)
            Report($"Réglages illisibles — l'ancien fichier est conservé sous {rescued}.");

        // A replay run never touches the cable: no multiplexer, no developer
        // image, no session. Everything above the depacketizer is the same code.
        if (_replayPath is not null)
        {
            StartReplay(_replayPath);
            return;
        }

        // The multiplexer tells us when the cable moves; the once-a-second beat
        // covers everything it does not say, including the very first climb.
        _watcher = new DeviceWatcher();
        _watcher.Attached += _ => Dispatcher.InvokeAsync(() =>
        {
            Report("iPhone branché.");
            _failures = 0;
            _lastAttemptSeconds = double.NegativeInfinity;
            EnsureSession();
        });
        _watcher.Detached += _ => Dispatcher.InvokeAsync(() => Detached());
        _watcher.Trouble += message => Dispatcher.InvokeAsync(() =>
            Note($"Multiplexeur : {message}"));
        _watcher.Start();

        _ = StartUpAsync();
    }

    /// <summary>
    /// Opens the capture and plays it into the picture path, in a loop.
    /// </summary>
    /// <remarks>
    /// The window is told nothing else: <see cref="AcceptFrame"/> is the same
    /// handler the live session's decoder calls, so a picture that arrives here
    /// is staged, converted and shown by exactly the code the cable exercises.
    /// </remarks>
    private void StartReplay(string path)
    {
        try
        {
            var replay = new ReplaySource(path) { Log = line => _journal.Write("rejeu : " + line) };
            if (replay.Records == 0)
            {
                Fault($"Capture vide : {path}");
                return;
            }
            replay.FrameDecoded += AcceptFrame;
            _replay = replay;
            replay.Start();
            _journal.Write($"--replay {path} : {replay.Records} datagramme(s), boucle de {replay.SpanSeconds:F1} s");
            Report($"Rejeu : {IoPath.GetFileName(path)}  ·  {replay.Records} datagrammes, boucle de {replay.SpanSeconds:F1} s");
        }
        catch (Exception exception)
        {
            Fault($"Rejeu impossible : {exception.Message}");
        }
    }

    /// <summary>
    /// The injector's door, when there is no injector: counts and forgets.
    /// </summary>
    /// <remarks>
    /// Deliberately the same shape as <c>InputInjector.QueueMove</c> — one lock,
    /// one slot, last one wins — so the mouse handler measured under a replay
    /// pays what it pays in front of a real phone, to within a few nanoseconds.
    /// </remarks>
    private void SinkMove(double xNorm, double yNorm)
    {
        int x = (int)Math.Clamp(xNorm * 65535.0, 0, 65535);
        int y = (int)Math.Clamp(yNorm * 65535.0, 0, 65535);
        lock (_sinkGate)
        {
            if (_sinkHasMove) _sinkDropped++;
            _sinkX = x;
            _sinkY = y;
            _sinkHasMove = true;
            _sinkMoves++;
        }
    }

    /// <summary>Anything discrete a replay run cannot send: counted, not queued.</summary>
    private void SinkReport()
    {
        lock (_sinkGate)
        {
            _sinkHasMove = false;
            _sinkReports++;
        }
    }

    // --- The developer image ----------------------------------------------------

    /// <summary>
    /// Finds the developer image, asking for it only when there is no other way.
    /// </summary>
    /// <remarks>
    /// Three places, in order: the folder the settings remember, a copy sitting
    /// in the repository beside the executable, and finally a person with an
    /// Xcode archive. The middle one matters more than it looks — it is what
    /// lets an unattended diagnostic run climb the whole ladder without a dialog
    /// waiting for a click nobody is there to give.
    /// </remarks>
    private async Task StartUpAsync()
    {
        if (Remembered() is string remembered)
        {
            _ddiFolder = remembered;
        }
        else if (BesideTheRepository() is string nearby)
        {
            _ddiFolder = nearby;
            _settings.DdiFolder = nearby;
            _settings.Save();
            Note($"Image développeur trouvée dans le dépôt : {nearby}");
        }
        else if (_diagnosticSeconds > 0)
        {
            Fault("Aucune image développeur et personne pour en choisir une : diagnostic sans session.");
            return;
        }
        else
        {
            await ExtractDdiAsync();
        }

        EnsureSession();

        string? Remembered() =>
            _settings.DdiFolder.Length > 0 && LooksLikeDdi(_settings.DdiFolder)
                ? _settings.DdiFolder
                : null;
    }

    /// <summary>A folder is a usable copy of Restore/ when the manifest is in it.</summary>
    private static bool LooksLikeDdi(string folder) =>
        Directory.Exists(folder) && File.Exists(IoPath.Combine(folder, "BuildManifest.plist"));

    /// <summary>
    /// Walks up from the executable looking for a <c>ddi*</c> folder.
    /// </summary>
    /// <remarks>
    /// The build output sits four levels below the repository root, and the
    /// images extracted during the protocol work are at the root. Walking up
    /// rather than naming a path keeps the machine this was written on out of
    /// the source.
    /// </remarks>
    private static string? BesideTheRepository()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; up < 8 && folder is not null; up++, folder = folder.Parent)
        {
            var candidates = folder.GetDirectories("ddi*")
                .Select(d => d.FullName)
                .Where(LooksLikeDdi)
                .OrderByDescending(name => name)
                .ToList();
            if (candidates.Count > 0)
                return candidates[0];
        }
        return null;
    }

    /// <summary>
    /// Asks for an Xcode archive and unpacks the developer image out of it.
    /// </summary>
    /// <remarks>
    /// Once per iOS build, and about two minutes of it: the chain runs from the
    /// package's payload through a cpio, a second disk image and a filesystem to
    /// a faithful copy of <c>Restore/</c>. It happens on a worker thread with the
    /// status bar showing what it is reading, because a frozen window for two
    /// minutes reads as a crash.
    /// </remarks>
    private async Task ExtractDdiAsync()
    {
        if (_extracting)
            return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Image développeur : archive Xcode (.xip), Device Support (.dmg) ou paquet (.pkg)",
            Filter = "Archives Apple (*.xip;*.dmg;*.pkg)|*.xip;*.dmg;*.pkg|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true,
        };
        if (_settings.LastArchive.Length > 0 &&
            IoPath.GetDirectoryName(_settings.LastArchive) is string last && Directory.Exists(last))
            dialog.InitialDirectory = last;

        Report("Aucune image développeur : choisis une archive Xcode ou un Device Support.");
        if (dialog.ShowDialog(this) != true)
        {
            Report("Sans image développeur, le téléphone ne peut pas être piloté. Relance pour en choisir une.");
            return;
        }

        _extracting = true;
        string source = dialog.FileName;
        string staging = IoPath.Combine(Settings.Folder, "ddi", "extraction");
        try
        {
            // What a previous attempt left behind is not part of this one: the
            // folder is renamed wholesale at the end, and half of an older
            // image riding along would be indistinguishable from a good one.
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);

            Report($"Extraction de {IoPath.GetFileName(source)} — quelques minutes…");
            var progress = new Progress<string>(line => Note(line));
            var result = await Task.Run(() => DdiExtractor.Extract(source, staging, progress));

            if (result.Source is null || !result.ManifestPresent)
            {
                Fault($"{IoPath.GetFileName(source)} ne contient pas d'image développeur"
                    + " — attendu une archive Xcode 27, un composant Device Support ou XcodeSystemResources.pkg.");
                return;
            }

            // Named after the build it carries, so several iOS versions can live
            // side by side and the folder says which is which.
            string folder = staging;
            if (result.ProductBuildVersion is string build && build.Length > 0)
            {
                folder = IoPath.Combine(Settings.Folder, "ddi", build);
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                Directory.Move(staging, folder);
            }

            _ddiFolder = folder;
            _settings.DdiFolder = folder;
            _settings.LastArchive = source;
            _settings.Save();
            Report($"Image développeur prête ({result.ProductBuildVersion ?? "build inconnue"}).");
        }
        catch (Exception exception)
        {
            Fault($"Extraction impossible : {exception.Message}");
        }
        finally
        {
            _extracting = false;
        }
    }

    // --- Session ----------------------------------------------------------------

    /// <summary>How long the first failed climb waits before the next one.</summary>
    private const double RetrySeconds = 5.0;

    /// <summary>
    /// The longest wait between climbs, whatever keeps failing.
    /// </summary>
    /// <remarks>
    /// Measured on the phone, 8 September: the display service refuses a new
    /// media stream for a minute or more after the previous one, and — this is
    /// the part that matters — <b>a refused attempt renews the refusal</b>. A
    /// flat five-second retry therefore never lets the phone recover: fifteen
    /// attempts in a row were refused, while the console probe, which asks once
    /// and then waits, was served in between. So the wait doubles at each
    /// failure up to this, and is reset by a success or by a fresh cable.
    /// </remarks>
    private const double RetryCeilingSeconds = 30.0;

    private int _failures;

    private double RetryDelay => Math.Min(RetryCeilingSeconds, RetrySeconds * Math.Pow(2, Math.Max(0, _failures - 1)));

    /// <summary>
    /// Climbs the ladder whenever there is no session and a phone to climb to.
    /// </summary>
    /// <remarks>
    /// Called from three places — the first load, an attach event, and the beat
    /// — so there is only one path to get right. It is also the whole of the
    /// recovery story: a fault drops the session, and the next beat past the
    /// retry delay starts again, for as long as the window is open.
    /// </remarks>
    private void EnsureSession()
    {
        if (_connecting || _extracting || _ddiFolder is null)
            return;

        // A session that exists is climbing, mirroring, or putting itself back
        // together after a soft reset. The last of those is the reason this is
        // no longer a test on the injector: the injector is null for the whole
        // of a reset, and a second session opened on top of the first would ask
        // the phone for a second media stream — which is what froze its display
        // service in the first place.
        if (_session is not null)
        {
            if (_session.State != SessionState.Faulted)
                return;
            var dead = _session;
            _session = null;
            _input = null;
            _ = DropAsync(dead);
        }

        if (_clock.Elapsed.TotalSeconds - _lastAttemptSeconds < RetryDelay)
            return;

        _lastAttemptSeconds = _clock.Elapsed.TotalSeconds;
        _connecting = true;
        Report("Connexion à l'iPhone…");
        _ = Task.Run(ClimbAsync);
    }

    private async Task ClimbAsync()
    {
        var session = new DeviceSession(new DdiSource(_ddiFolder!), new SessionLog(this))
        {
            DecodeVideo = true,
        };
        session.StateChanged += state => Dispatcher.InvokeAsync(() => AdoptState(state));
        session.UnlockRequired += message => Dispatcher.InvokeAsync(() => ShowUnlockBanner(message));
        session.RestartRequired += message => Dispatcher.InvokeAsync(() =>
        {
            ShowUnlockBanner(message);
            Fault(message);
        });
        session.FrameDecoded += AcceptFrame;
        _session = session;

        try
        {
            await session.ConnectAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                // The injector itself was adopted with the MediaUp state, along
                // with the fault handler it needs: the pump sends on its own
                // thread, so a report that fails has no caller left to tell.
                _lastReportsSent = 0;
                _lastMovesDropped = 0;
                _failures = 0;
                HideUnlockBanner();
                Report(_diagnosticSeconds > 0
                    ? "Miroir ouvert."
                    : "Miroir ouvert · clic dans l'image pour piloter · Ctrl+Alt pour rendre la souris");
                _ = DimAsync();
            });
        }
        catch (Exception exception)
        {
            await DropAsync(session);
            await Dispatcher.InvokeAsync(() =>
            {
                _failures++;
                // Apple's multiplexer is worth a banner and a button: it is the
                // one failure the person can act on without touching the phone.
                if (exception is LuminaException { AppleMultiplexer: true })
                    ShowMultiplexerBanner(exception.Message);
                Fault($"{exception.Message} — nouvelle tentative dans {RetryDelay:0} s.");
            });
        }
        finally
        {
            _connecting = false;
        }
    }

    /// <summary>Lets a session go, whatever state it stopped in.</summary>
    private async Task DropAsync(DeviceSession? session)
    {
        if (session is null)
            return;
        if (ReferenceEquals(session, _session))
        {
            _session = null;
            _input = null;
        }
        try { await session.DisposeAsync(); }
        catch (Exception) { /* it is already broken; that is why we are here */ }
    }

    private void Detached()
    {
        Report("iPhone débranché.");
        _touching = false;
        _pressPending = false;
        _heldKeys.Clear();
        var session = _session;
        _session = null;
        _input = null;
        _state = SessionState.Detached;
        UpdateState();
        _ = DropAsync(session);
    }

    /// <summary>
    /// Takes a rung the session has reached, and the injector that came with it.
    /// </summary>
    /// <remarks>
    /// The injector is re-read here rather than only after
    /// <see cref="DeviceSession.ConnectAsync"/> returns, because a session can
    /// now reach <see cref="SessionState.MediaUp"/> a second time on its own: a
    /// soft reset climbs the whole ladder again and hands out a new injector,
    /// and the window would otherwise go on posting touches down a channel that
    /// is closed.
    /// </remarks>
    private void AdoptState(SessionState state)
    {
        _state = state;
        if (state == SessionState.MediaUp && _session is not null)
        {
            _input = _session.Input;
            _input.Faulted = exception =>
                Dispatcher.InvokeAsync(() => NoteInputFault(exception));
        }
        else if (state is SessionState.Resetting or SessionState.Faulted or SessionState.Detached)
        {
            _input = null;
            _touching = false;
            _pressPending = false;
            _heldKeys.Clear();
        }

        Report(state switch
        {
            SessionState.Detached => "Débranché.",
            SessionState.Attached => "Branché.",
            SessionState.Paired => "Appairé.",
            SessionState.DdiMounted => "Image développeur montée.",
            SessionState.TunnelUp => "Tunnel ouvert.",
            SessionState.MediaUp => "Miroir en cours.",
            SessionState.Resetting => "Relance du miroir…",
            _ => "Erreur.",
        });
        if (state != SessionState.Faulted)
            HideUnlockBanner();
        _journal.Write($"etat {state}");
    }

    private void ShowUnlockBanner(string message)
    {
        UnlockBannerText.Text = message;
        AppleDevicesButton.Visibility = Visibility.Collapsed;
        UnlockBanner.Visibility = Visibility.Visible;
        Report(message);
    }

    /// <summary>
    /// The same banner, plus the one button that fixes what it says.
    /// </summary>
    /// <remarks>
    /// Apple's multiplexer is the only piece of software on this PC that is not
    /// ours, and the only fault whose remedy is a program to open rather than
    /// something to do to the phone. The window opens it; it never stops it.
    /// </remarks>
    private void ShowMultiplexerBanner(string message)
    {
        ShowUnlockBanner(message);
        AppleDevicesButton.Visibility = Visibility.Visible;
    }

    private void HideUnlockBanner()
    {
        UnlockBanner.Visibility = Visibility.Collapsed;
        AppleDevicesButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>The Store app's identity, which is how a packaged app is opened.</summary>
    private const string AppleDevicesShellPath = @"shell:AppsFolder\AppleInc.AppleDevices_nzyj5cx40ttqa!App";

    private void OnAppleDevicesClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            // Through the shell, not by naming an executable: the app is
            // packaged, so there is no path to run — and nothing here goes
            // near the process that may already be wedged.
            Process.Start(new ProcessStartInfo("explorer.exe", AppleDevicesShellPath) { UseShellExecute = true });
            Report("Appareils Apple demandé — la connexion repart d'elle-même.");
        }
        catch (Exception exception)
        {
            Fault($"Ouverture d'Appareils Apple impossible : {exception.Message}");
        }
    }

    /// <summary>
    /// Where Core says what it is doing.
    /// </summary>
    /// <remarks>
    /// Every rung of the climb reports through this, from the thread that
    /// climbed it — which is never the UI thread, hence the hop. The lines are
    /// worth having even in an ordinary run: the status bar shows the last one,
    /// and the diagnostic journal keeps them all.
    /// </remarks>
    private sealed class SessionLog : ILog
    {
        private readonly MainWindow _window;

        public SessionLog(MainWindow window) => _window = window;

        public void Info(string message) => _window.Dispatcher.InvokeAsync(() => _window.Note(message));

        public void Warn(string message) => _window.Dispatcher.InvokeAsync(() => _window.Note(message));
    }

    // --- Input ------------------------------------------------------------------

    /// <summary>
    /// Puts one composed gesture at the end of the injector's sequence.
    /// </summary>
    /// <remarks>
    /// Touch reports are a sequence, not a set: a move that overtakes its own
    /// press lands on nothing, and a release that overtakes a move leaves a
    /// finger stuck on the screen. That order used to be kept here, by chaining
    /// every call onto one task — which kept the order and built a queue, since
    /// a hand on a mouse produces positions far faster than the wire carries
    /// them. The order is now the injector's business, and its pump has one
    /// pending position rather than a queue; this is only the door in.
    /// </remarks>
    private void Post(string what, Func<InputInjector, Task> work)
    {
        var input = _input;
        if (input is null)
            return;

        input.Queue(async () =>
        {
            try
            {
                await work(input);
            }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() => Fault($"{what} : {exception.Message}"));
            }
        });
    }

    /// <summary>How long after a rebuilt HID channel another one may be asked for.</summary>
    /// <remarks>
    /// A channel that comes back broken would otherwise be reopened on every
    /// report of the burst that follows, and the person would be watching the
    /// window reconnect to itself instead of getting an error.
    /// </remarks>
    private const double InputRebuildSeconds = 5.0;

    private bool _rebuildingInput;
    private double _lastInputRebuild = double.NegativeInfinity;

    /// <summary>
    /// A report the injector could not put on the wire.
    /// </summary>
    /// <remarks>
    /// The pump has already given up on it; what is decided here is whether the
    /// channel underneath is worth keeping. It is not: a write that has stopped
    /// returning holds the channel's write lock for as long as the socket does,
    /// so every report behind it would wait too — the several-second delay
    /// between a click and the phone. One rebuild per burst, and the session
    /// itself faults if the rebuild fails.
    /// </remarks>
    private void NoteInputFault(Exception exception)
    {
        Fault($"entrée : {exception.Message}");

        double now = _clock.Elapsed.TotalSeconds;
        if (_rebuildingInput || _session is not { } session ||
            now - _lastInputRebuild < InputRebuildSeconds)
            return;

        _rebuildingInput = true;
        _lastInputRebuild = now;

        // The finger the window thought was down was on the old channel: no
        // release is owed on the new one, and claiming otherwise would send one
        // from nowhere.
        _touching = false;
        _pressPending = false;
        _heldKeys.Clear();
        Report("Canal d'entrée bloqué : réouverture…");
        _ = RebuildInputAsync(session);
    }

    private async Task RebuildInputAsync(DeviceSession session)
    {
        bool rebuilt = await session.RebuildInputAsync();
        await Dispatcher.InvokeAsync(() =>
        {
            _rebuildingInput = false;
            if (rebuilt)
                Report("Canal d'entrée rétabli.");
            else
                Fault("Canal d'entrée perdu : la session est reconstruite.");
        });
    }

    /// <summary>Where the finger should be; the injector's pump decides when to say so.</summary>
    private void Move(double xNorm, double yNorm)
    {
        if (_input is { } input)
            input.QueueMove(xNorm, yNorm);
        else if (_replay is not null)
            SinkMove(xNorm, yNorm);
    }

    /// <summary>Is there anywhere for a gesture to go — a phone, or a replay's sink?</summary>
    private bool Drivable => _input is not null || _replay is not null;

    /// <summary>Picture pixels to the 0..1 the injector speaks.</summary>
    private (double X, double Y) Normalise(double pictureX, double pictureY)
    {
        int width = _surfaceWidth;
        int height = _surfaceHeight;
        return (Math.Clamp(pictureX / Math.Max(1, width), 0, 1),
                Math.Clamp(pictureY / Math.Max(1, height), 0, 1));
    }

    // --- Video ------------------------------------------------------------------

    /// <summary>
    /// Called on the decode thread. Copies the picture into a buffer of the
    /// window's own and returns; nothing else happens here.
    /// </summary>
    /// <remarks>
    /// <see cref="VideoFrame.Nv12"/> belongs to the decoder and is written over
    /// by the next picture, so anything that outlives this call has to be
    /// copied — which is also why the copy cannot be skipped in favour of
    /// keeping the frame. Two megabytes and a half at 1328x2896, once per
    /// picture, on the thread that already has them warm.
    ///
    /// <para>A picture that arrives while one is still unshown replaces it, and
    /// that is the right loss: dropping here costs exactly one picture, unlike
    /// dropping an access unit, which costs every frame to the next key
    /// frame.</para>
    ///
    /// <para>What is deliberately <i>not</i> here is the conversion. It used to
    /// be, straight into a shared section — and the picture froze while the
    /// counters said 50 i/s; see <see cref="_surface"/>. It converts on the
    /// interface thread now, at the tick, into the back buffer of the one
    /// bitmap on screen.</para>
    /// </remarks>
    private void AcceptFrame(VideoFrame frame)
    {
        Interlocked.Increment(ref _framesReceived);
        Interlocked.Exchange(ref _lastFrameTicks, _clock.ElapsedTicks);

        long accepted = Stopwatch.GetTimestamp();
        int width = frame.Width, height = frame.Height, stride = frame.Stride;
        _latencyMs = frame.LatencyMs;

        // Which way up the phone is has to be read off the picture, and only
        // while the answer is still in doubt: once settled it costs nothing.
        if (_orientationPending > 0)
        {
            _orientationPending--;
            var seen = DeviceGeometry.DetectNv12(frame.Nv12, width, height, stride);
            if (seen != DeviceGeometry.Orientation.Unknown || _orientationPending == 0)
            {
                _orientationPending = 0;
                Dispatcher.InvokeAsync(() => AdoptOrientation(seen));
            }
        }

        int needed = frame.Nv12Length;
        lock (_pendingGate)
        {
            // Grown, never shrunk: a rotation is the only thing that changes the
            // size and the buffer is back to the larger of the two next time.
            if (_pending.Length < needed)
                _pending = new byte[needed];
            frame.Nv12.AsSpan(0, needed).CopyTo(_pending);
            _pendingWidth = width;
            _pendingHeight = height;
            _pendingStride = stride;
            _pendingArrivalTicks = frame.ArrivalTicks;
            if (_hasPending)
                _framesSuperseded++;         // this picture replaces one never shown
            _hasPending = true;
        }

        _acceptMs = Ms(Stopwatch.GetTimestamp() - accepted);
    }

    /// <summary>Interface thread: builds the bitmap and hangs it on the screen.</summary>
    private void MakeSurface(int width, int height)
    {
        _surface = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
        _surfaceWidth = width;
        _surfaceHeight = height;
        Screen.Source = _surface;

        Hint.Visibility = Visibility.Collapsed;
        _pointerKnown = false;

        // A new geometry is a rotation, or a new session. Either way the
        // orientation is no longer known; twenty frames is a third of a second
        // to settle it, and a dark screen simply runs out and leaves it Unknown.
        _orientation = height >= width
            ? DeviceGeometry.Orientation.Portrait
            : DeviceGeometry.Orientation.Unknown;
        _orientationPending = height >= width ? 0 : 20;

        FitDevice();
        ShowIdleHint();
        UpdateState();
        UpdatePointerGeometry();
        _journal.Write($"image {width}x{height}, bitmap recree");
    }

    /// <summary>Stopwatch ticks as milliseconds.</summary>
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// How long it has been since the last render tick.
    /// </summary>
    /// <remarks>
    /// The one number that says whether the interface thread is being kept from
    /// the picture. A screen refreshing at sixty hertz calls this every 16.7 ms;
    /// anything above that is a tick the dispatcher could not get to, and under a
    /// flood of mouse events that is exactly what a growing delay looks like from
    /// the inside. The samples are kept for the second, so the tail can be
    /// reported rather than an average that hides it.
    /// </remarks>
    private void NoteRenderTick()
    {
        long now = Stopwatch.GetTimestamp();
        long previous = _lastRenderTicks;
        _lastRenderTicks = now;
        if (previous == 0)
            return;

        double gap = Ms(now - previous);
        _renderGapSum += gap;
        if (gap > _renderGapMax)
            _renderGapMax = gap;
        if (_renderGapCount < _renderGaps.Length)
            _renderGaps[_renderGapCount] = gap;
        _renderGapCount++;
    }

    /// <summary>The tick gaps of the window just closed: mean, peak and the tail.</summary>
    private (double Mean, double Max, double P99) RenderGaps()
    {
        int kept = Math.Min(_renderGapCount, _renderGaps.Length);
        double mean = _renderGapCount > 0 ? _renderGapSum / _renderGapCount : 0;
        double p99 = 0;
        if (kept > 0)
        {
            var sorted = _renderGaps[..kept];
            Array.Sort(sorted);
            p99 = sorted[Math.Min(kept - 1, (int)Math.Ceiling(kept * 0.99) - 1)];
        }
        return (mean, _renderGapMax, p99);
    }

    /// <summary>What one mouse event cost the interface thread, in microseconds.</summary>
    private (double Mean, double Max) MouseCost()
    {
        long count = Interlocked.Read(ref _moveCount);
        double mean = count > 0 ? Ms(Interlocked.Read(ref _moveTicks)) * 1000.0 / count : 0;
        return (mean, Ms(Interlocked.Read(ref _moveMaxTicks)) * 1000.0);
    }

    /// <summary>
    /// The render tick, reduced to what only it can do.
    /// </summary>
    /// <remarks>
    /// Three things and no fourth: show the picture the decode thread has
    /// already converted, move the pointer overlay, and let a press that has
    /// outlived the defer window become a hold. The counters left with the
    /// four-hertz timer, and the pixels left with <see cref="AcceptFrame"/>.
    /// </remarks>
    private void OnRendering(object? sender, EventArgs e)
    {
        NoteRenderTick();
        UpdatePointerGeometry();
        PresentReadyFrame();

        // The overlay for however many mouse events arrived since the last
        // frame, drawn once: the phone was told about them as they came.
        if (_mouseDirty)
        {
            _mouseDirty = false;
            RefreshPointer();
        }

        // A press still pending past the defer window is a hold, not a tap.
        // Checked here rather than on a timer of its own: after a UI stall the
        // dispatcher runs this tick and the queued mouse-up back to back, so a
        // click the stall sat on still goes out as press-then-release.
        if (_pressPending &&
            _sendClock.Elapsed.TotalMilliseconds - _pressStartMs > TapDeferMs)
            FlushPendingPress();
    }

    /// <summary>
    /// Keeps the numbers the mouse handler needs where it can read them.
    /// </summary>
    /// <remarks>
    /// <c>Screen.ActualWidth</c> is a layout answer, and asking for it two
    /// thousand times a second is asking the layout system two thousand
    /// questions a second. Asked once per render tick instead; a scale up to one
    /// frame old is a pointer up to one frame off during a window resize, which
    /// nobody has ever noticed.
    /// </remarks>
    private void UpdatePointerGeometry()
    {
        if (_surface is null || _surfaceWidth == 0 || Screen.ActualWidth <= 0)
            return;
        _pictureWidth = _surfaceWidth;
        _pictureHeight = _surfaceHeight;
        _scale = Screen.ActualWidth / _surfaceWidth;
        _slop = TapSlopWindowPx / Math.Max(_scale, 0.01);
    }

    /// <summary>
    /// The once-a-second beat that keeps running when the render tick does not.
    /// </summary>
    /// <remarks>
    /// <c>CompositionTarget.Rendering</c> is tied to something being drawn, and a
    /// minimised window draws nothing — which is precisely when a session that
    /// fell over has to be rebuilt without anyone watching.
    /// </remarks>
    private void OnUpkeep(object? sender, EventArgs e)
    {
        EnsureSession();
        WatchStream();
        UpdateState();
        WriteStatsLine();
        WatchDiagnostic();

        // Backstop for the expiry check in OnRendering, which stops with the
        // rendering: a hold begun over a frozen picture would otherwise stay
        // pending until the button came up, and come out as a tap.
        if (_pressPending &&
            _sendClock.Elapsed.TotalMilliseconds - _pressStartMs > TapDeferMs)
            FlushPendingPress();
    }

    /// <summary>Is a picture actually arriving right now?</summary>
    private bool Mirroring =>
        _surface is not null &&
        (_clock.ElapsedTicks - Interlocked.Read(ref _lastFrameTicks)) < 5 * Stopwatch.Frequency;

    /// <summary>
    /// Converts the staged picture into the bitmap's back buffer and shows it.
    /// </summary>
    /// <remarks>
    /// The buffers are swapped under the lock and the conversion happens
    /// outside it, so the decode thread is never held for the length of a
    /// picture. Holding it was measured once, on the Raspberry Pi bridge this
    /// window descends from, and cost half a second of stale video sitting in a
    /// socket.
    ///
    /// <para>Last one wins: whatever the decoder deposited since the previous
    /// tick is what gets converted, and the pictures in between are never
    /// touched. That is why the conversion being on this thread costs the delay
    /// of one picture rather than a backlog — a slow tick converts fewer
    /// pictures, never later ones.</para>
    /// </remarks>
    private void PresentReadyFrame()
    {
        int width, height, stride;
        long arrivalTicks;

        lock (_pendingGate)
        {
            if (!_hasPending)
                return;
            _hasPending = false;
            (_pending, _present) = (_present, _pending);
            width = _pendingWidth;
            height = _pendingHeight;
            stride = _pendingStride;
            arrivalTicks = _pendingArrivalTicks;
        }

        if (_surface is null || _surfaceWidth != width || _surfaceHeight != height)
            MakeSurface(width, height);
        if (_surface is not { } surface)
            return;

        long start = Stopwatch.GetTimestamp();
        surface.Lock();
        try
        {
            Nv12ToBgra.Convert(_present, width, height, stride,
                surface.BackBuffer, surface.BackBufferStride);
            surface.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            surface.Unlock();
        }
        long done = Stopwatch.GetTimestamp();

        _convertMs = Ms(done - start);
        if (_convertMs > _maxConvertMs)
            _maxConvertMs = _convertMs;
        _presentLatencyMs = Ms(done - arrivalTicks);
        if (_presentLatencyMs > _maxPresentLatencyMs)
            _maxPresentLatencyMs = _presentLatencyMs;
        _framesPresented++;
    }

    private void UpdateMetrics()
    {
        double now = _clock.Elapsed.TotalSeconds;
        if (now - _lastSampleSeconds < 1.0)
            return;

        double span = now - _lastSampleSeconds;
        long received = Interlocked.Read(ref _framesReceived);
        _fps = (received - _lastReceived) / span;
        _presentedFps = (_framesPresented - _lastPresented) / span;
        double mouseHz = (_mouseEvents - _lastMouseEvents) / span;

        // What the injector really put on the wire, and what it threw away to
        // do it: a superseded position is the healthy loss, a queue is not.
        var input = _input?.Stats ?? default;
        double sendHz = (input.ReportsSent - _lastReportsSent) / span;
        double dropHz = (input.MovesDropped - _lastMovesDropped) / span;
        _lastReceived = received;
        _lastPresented = _framesPresented;
        _lastMouseEvents = _mouseEvents;
        _lastReportsSent = input.ReportsSent;
        _lastMovesDropped = input.MovesDropped;
        _lastSampleSeconds = now;

        string mode = _engaged ? "PILOTAGE" : "libre";
        MetricsText.Text = _surface is not null
            ? $"{mode}   {_surfaceWidth}x{_surfaceHeight}->{Screen.ActualWidth,4:0}   " +
              $"{_fps,4:0} i/s reçues   {_presentedFps,4:0} i/s affichées   décodage {_latencyMs,5:0.0} ms   " +
              $"souris {mouseHz,4:0}/s -> {sendHz,3:0}/s (-{dropHz,4:0}/s, file {input.QueueDepth})   " +
              $"état {_state}   erreurs {_errors}"
            : $"{mode}   aucune image   souris {mouseHz,4:0}/s   état {_state}";

        if (_diagnosticSeconds > 0)
        {
            _lastStatsSeconds = now;
            _statsReceived = received;
            _statsPresented = _framesPresented;
            WriteDiagnosticLine(now - _diagnosticStarted, span, _fps, _presentedFps);
        }
    }

    /// <summary>
    /// The counters, in the journal, every ten seconds of a mirror that is up.
    /// </summary>
    /// <remarks>
    /// Driven from the once-a-second beat rather than the render tick, because
    /// the render tick stops with the drawing and a minimised window is exactly
    /// where a stream dies unwatched. Ten seconds is the compromise: often
    /// enough to see a rate collapse before the picture does, rare enough that a
    /// day of mirroring is a few hundred kilobytes.
    /// </remarks>
    private void WriteStatsLine()
    {
        if (_diagnosticSeconds > 0 || (_state != SessionState.MediaUp && _replay is null))
            return;
        double now = _clock.Elapsed.TotalSeconds;
        double span = now - _lastStatsSeconds;
        if (span < StatsSeconds)
            return;

        // Counters of its own rather than the metrics pane's: that pane is fed
        // by the render tick, which is not running when this matters most.
        long received = Interlocked.Read(ref _framesReceived);
        double receivedFps = (received - _statsReceived) / span;
        double presentedFps = (_framesPresented - _statsPresented) / span;
        _statsReceived = received;
        _statsPresented = _framesPresented;
        _lastStatsSeconds = now;
        WriteDiagnosticLine(now, span, receivedFps, presentedFps);
    }

    /// <summary>
    /// One line a second, and everything needed to place the delay on it.
    /// </summary>
    /// <remarks>
    /// The rates first — what the phone sent, what the decoder finished, what
    /// the window actually showed — then, for one picture's journey, the four
    /// stages it goes through: the wire, the wait for the decoder, the decoder,
    /// and this side. Anything that queues has its depth printed next to it.
    /// A stage that is not the culprit costs one number to rule out; guessing
    /// which one is costs an afternoon.
    ///
    /// <para>Then the same treatment for the other direction, which was missing
    /// and cost exactly that afternoon: how many reports left, how many
    /// positions were superseded, how deep the pump's queue got, how much the
    /// channel underneath is still waiting to be acknowledged, and — the number
    /// the rest exists to explain — how long a report waited between the hand
    /// letting go of it and the wire taking it.</para>
    /// </remarks>
    private void WriteDiagnosticLine(double elapsed, double span, double receivedFps, double presentedFps)
    {
        var media = _replay is { } replay ? replay.Stats : _session?.MediaStatistics;
        double packetRate = 0, decodedRate = 0;
        string queues = "file unites  n/a";
        string stages = "";
        string drift = "";
        if (media is MediaStats stats)
        {
            drift = $"  RTP derive {stats.DriftMeanMs,7:F0} / max {stats.DriftMaxMs,7:F0} ms" +
                    $" (fin {stats.DriftLastMs,7:F0}, horloge {stats.RtpClockKhz,5:F1} kHz)";
            packetRate = (stats.Packets - _lastPackets) / span;
            decodedRate = (stats.FramesDecoded - _lastDecoded) / span;
            _lastPackets = stats.Packets;
            _lastDecoded = stats.FramesDecoded;
            queues = $"file unites {stats.PendingUnits} (max {stats.MaxPendingUnits})" +
                     $"  abandons {stats.UnitsDropped}+{stats.LateDrops}  pertes RTP {stats.PacketsLost}";
            // The decoder's own backlog belongs on the same line as the stages:
            // it is the one queue none of the three timings can see, because a
            // picture the decoder is sitting on has not started any of them.
            stages = $"  fil {stats.WireMs,5:F1}  attente {stats.QueueMs,5:F1} (max {stats.MaxQueueMs,5:F1})" +
                     $"  decodage {stats.DecodeMs,5:F1}" +
                     $"  decodeur retient {stats.DecoderInFlight,2}" +
                     $"  faible latence {(stats.DecoderLowLatency ? "oui" : "NON")}";
            if (_replay is { } source) source.ResetPeaks(); else _session?.ResetMediaPeaks();
        }

        // Taken, not read: the window is cleared by the asking, so this is the
        // one place allowed to ask and it asks once a line.
        string tunnel = "";
        if (_session?.TunnelStatistics is { } pump)
            tunnel =
                $"  TUNNEL socket-dispo {pump.SocketAvailable,8} o" +
                $"  lus {pump.PacketsPerSecond,6:F0} paq/s {pump.KilobytesPerSecond,7:F0} ko/s" +
                $"  tour {pump.TurnMeanMs,6:F2} / max {pump.TurnMaxMs,7:F1} ms" +
                $"  attente {pump.ReadMeanMs,6:F2} / max {pump.ReadMaxMs,7:F1}" +
                $"  handlers {pump.HandlerMeanMs,6:F2} / max {pump.HandlerMaxMs,7:F1}" +
                $"  (UDP {pump.UdpMs,7:F1} + TCP {pump.TcpMs,7:F1} ms sur {pump.Seconds,4:F1} s)";

        var (gapMean, gapMax, gapP99) = RenderGaps();
        var (moveMean, moveMax) = MouseCost();
        double mouseHz = _moveCount / Math.Max(0.001, span);
        string sink = _replay is null ? "" : $"  puits {_sinkMoves}/{_sinkDropped}/{_sinkReports}";

        var input = _input?.Stats ?? default;
        double reportRate = (input.ReportsSent - _lineReports) / span;
        double dropRate = (input.MovesDropped - _lineDropped) / span;
        _lineReports = input.ReportsSent;
        _lineDropped = input.MovesDropped;
        _input?.ResetPeaks();

        string entrees =
            $"  ENTREES {reportRate,6:F0} rap/s  superseded {dropRate,6:F0}/s" +
            $"  file {input.QueueDepth} (deborde {input.QueueOverflows})" +
            $"  ecriture {input.SendWaiters} en attente / {input.UnackedSegments} seg / {input.UnackedBytes} o" +
            $"  commande->fil {input.SendMeanMs,6:F1} (max {input.SendMaxMs,7:F1}) ms" +
            $"  delais {input.SendTimeouts}  fenetre HTTP2 {input.WindowDrops}" +
            (input.LastFault is null ? "" : $"  dernier defaut : {input.LastFault}");

        _journal.Write(
            $"t={elapsed,5:F1} s  etat {_state}  " +
            $"{packetRate,6:F0} paq/s  {decodedRate,5:F1} i/s decodees  {receivedFps,5:F1} i/s recues  " +
            $"{presentedFps,5:F1} i/s affichees" + stages +
            $"  conv {_convertMs,5:F1} (max {_maxConvertMs,5:F1})  accept {_acceptMs,5:F1}" +
            $"  arrivee->ecran {_presentLatencyMs,6:F1} (max {_maxPresentLatencyMs,6:F1})" +
            $"  souris {mouseHz,6:F0}/s  OnMouseMove {moveMean,6:F1} us (max {moveMax,7:F1})" +
            $"  tick rendu {gapMean,5:F1} / p99 {gapP99,6:F1} / max {gapMax,7:F1} ms" +
            $"  images doublees {_framesSuperseded}  {queues}{sink}" + drift + tunnel + entrees +
            $"  erreurs {_errors}");

        _maxConvertMs = 0;
        _maxPresentLatencyMs = 0;
        _renderGapCount = 0;
        _renderGapSum = 0;
        _renderGapMax = 0;
        _moveTicks = 0;
        _moveCount = 0;
        _moveMaxTicks = 0;
    }

    /// <summary>
    /// The one thing that has to be readable without reading: green means the
    /// picture and the input are both there, amber means half of it is missing,
    /// red means nothing works.
    /// </summary>
    private void UpdateState()
    {
        bool video = Mirroring;
        bool driving = _input is not null;
        _hadStream |= _surface is not null;

        (string key, string label) = (video, driving) switch
        {
            (true, true) => ("SuccessPulse", _engaged ? $"pilotage · {_presentedFps:0} i/s" : $"{_presentedFps:0} i/s"),
            (true, false) => ("Warn", "sans clavier"),
            (false, true) => ("Warn", _hadStream ? "flux arrêté" : "en attente"),
            _ when _state == SessionState.Resetting => ("Warn", "relance…"),
            _ => ("Error", _state == SessionState.Faulted ? "erreur" : "hors ligne"),
        };

        StateDot.Fill = (Brush)FindResource(key);
        StateText.Text = label;
    }

    /// <summary>Says so, once, when the picture stops or comes back.</summary>
    private void WatchStream()
    {
        bool mirroring = Mirroring;
        if (mirroring == _wasMirroring)
            return;

        _wasMirroring = mirroring;
        if (!mirroring)
            _dimmed = false;

        if (!_hadStream)
            return;

        Report(mirroring ? "Flux repris." : $"Flux arrêté à {DateTime.Now:HH:mm:ss}. F3 pour les compteurs.");
    }

    /// <summary>Ends a timed diagnostic run and writes what it saw.</summary>
    private void WatchDiagnostic()
    {
        if (_diagnosticClosing)
            return;

        // Two ways to ask for a timed run — --diagnostic and --replay's own
        // duration — and when both are given the shorter one is the answer.
        int limit = _diagnosticSeconds > 0 && _replaySeconds > 0
            ? Math.Min(_diagnosticSeconds, _replaySeconds)
            : Math.Max(_diagnosticSeconds, _replaySeconds);
        if (limit <= 0)
            return;
        if (_clock.Elapsed.TotalSeconds - _diagnosticStarted < limit)
            return;

        _diagnosticClosing = true;
        double span = Math.Max(0.001, _clock.Elapsed.TotalSeconds - _diagnosticStarted);
        _journal.Write($"FIN  etat {_state}  {Interlocked.Read(ref _framesReceived)} images recues," +
                       $" {_framesPresented} affichees en {span:F1} s" +
                       $" -> {Interlocked.Read(ref _framesReceived) / span:F1} / {_framesPresented / span:F1} i/s," +
                       $" {_errors} erreur(s)");
        Close();
    }

    private void OnMetricsClicked(object sender, RoutedEventArgs e) => ToggleMetrics();

    private void ToggleMetrics() =>
        MetricsPanel.Visibility = MetricsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    // --- Status -----------------------------------------------------------------

    /// <summary>Shows a line of status, and refreshes the state dot with it.</summary>
    private void Report(string message)
    {
        StatusText.Text = message;
        _journal.Write(message);
        UpdateState();
    }

    /// <summary>A line worth keeping but not worth interrupting the status bar for.</summary>
    private void Note(string message)
    {
        _journal.Write(message);
        if (_state != SessionState.MediaUp)
            StatusText.Text = message;
    }

    /// <summary>Something went wrong, said once and counted.</summary>
    private void Fault(string message)
    {
        _errors++;
        _journal.Write("ERREUR " + message);
        StatusText.Text = message;
        UpdateState();
    }

    private void ShowIdleHint() =>
        Report(_input is not null
            ? "Clic dans l'image pour piloter  ·  Ctrl+Alt pour récupérer la souris"
            : "En attente de l'iPhone.");

    // --- Geometry ---------------------------------------------------------------

    /// <summary>How many window pixels one phone pixel occupies.</summary>
    private double PictureScale =>
        _surface is null || _surfaceWidth == 0 || Screen.ActualWidth <= 0
            ? 1
            : Screen.ActualWidth / _surfaceWidth;

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => FitDevice();

    /// <summary>Takes a detected orientation and re-lays the chassis around it.</summary>
    private void AdoptOrientation(DeviceGeometry.Orientation seen)
    {
        if (seen == _orientation)
            return;

        _orientation = seen;
        FitDevice();

        Report(seen switch
        {
            DeviceGeometry.Orientation.IslandLeft => "Téléphone en paysage, île à gauche.",
            DeviceGeometry.Orientation.IslandRight => "Téléphone en paysage, île à droite.",
            DeviceGeometry.Orientation.Unknown =>
                "Paysage — impossible de dire de quel côté est l'île (écran trop sombre). " +
                "Île et boutons masqués.",
            _ => "Téléphone en portrait.",
        });
    }

    /// <summary>Grows the phone to the largest size the window allows.</summary>
    /// <remarks>
    /// Falls back to the real screen of a 17 Pro Max when nothing is streaming
    /// yet. Without it the window would open empty and stay that way until the
    /// phone connected.
    /// </remarks>
    private void FitDevice()
    {
        int pixelWidth = _surface is null ? 1320 : _surfaceWidth;
        int pixelHeight = _surface is null ? 2868 : _surfaceHeight;

        var (width, height) = DeviceGeometry.FitPicture(
            Stage.ActualWidth, Stage.ActualHeight, pixelWidth, pixelHeight);

        if (width > 1 && height > 1)
            ShapeDevice(width, height);
    }

    /// <summary>Paints the chassis in whichever colour the phone actually is.</summary>
    private void ApplyChassis()
    {
        string suffix = _settings.ChassisColour.Trim().ToLowerInvariant() switch
        {
            "blue" or "deepblue" or "bleu" => "DeepBlue",
            "silver" or "argent" => "Silver",
            _ => "CosmicOrange",
        };

        Rail.Background = (Brush)FindResource("Chassis" + suffix);
        _buttonJoint = (Color)FindResource("BtnJoint" + suffix);
        _buttonChamfer = (Color)FindResource("BtnChamfer" + suffix);
        _buttonFace = (Color)FindResource("BtnFace" + suffix);

        if (_border > 0)
            ShapeButtons(_border);
    }

    /// <summary>
    /// Tints the Windows title bar to match the application instead of the OS.
    /// </summary>
    /// <remarks>
    /// <c>COLORREF</c> is BGR, not RGB: the window's #0A0912 is written 0x12090A.
    /// Both attributes are silently ignored on Windows versions that predate
    /// them, which is why neither return value is checked.
    /// </remarks>
    private void DarkenTitleBar()
    {
        const int UseImmersiveDarkMode = 20;
        const int CaptionColour = 35;
        const int BorderColour = 34;

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        int on = 1;
        int caption = 0x12090A;
        int border = 0x1E1825;
        DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref on, sizeof(int));
        DwmSetWindowAttribute(handle, CaptionColour, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, BorderColour, ref border, sizeof(int));
    }

    /// <summary>
    /// The usable part of the monitor this window is on, in the units the window
    /// itself is measured in.
    /// </summary>
    /// <remarks>
    /// <see cref="SystemParameters.WorkArea"/> cannot be trusted for this: it
    /// reports raw pixels, while the window's own units run larger than a pixel
    /// on a scaled display. Asking the monitor for its rectangle and converting
    /// with the window's device transform gives numbers that mean the same thing
    /// as <c>Width</c> and <c>Height</c>, whatever the scaling.
    /// </remarks>
    private Rect WorkArea()
    {
        var source = PresentationSource.FromVisual(this);
        double toDiuX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1;
        double toDiuY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1;

        IntPtr handle = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

        const uint NearestMonitor = 2;
        if (handle != IntPtr.Zero &&
            GetMonitorInfoW(MonitorFromWindow(handle, NearestMonitor), ref info))
        {
            return new Rect(
                info.Work.Left * toDiuX,
                info.Work.Top * toDiuY,
                (info.Work.Right - info.Work.Left) * toDiuX,
                (info.Work.Bottom - info.Work.Top) * toDiuY);
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>Puts the window back where it was, or sizes it for a 498 x 1080 picture.</summary>
    private void PlaceWindow()
    {
        var work = WorkArea();

        if (_settings.WindowBounds.Length == 4)
        {
            double[] saved = _settings.WindowBounds;
            if (saved[2] > MinWidth && saved[3] > MinHeight)
            {
                Left = saved[0];
                Top = saved[1];
                Width = saved[2];
                Height = saved[3];
                ClampToScreen();
                return;
            }
        }

        // Chrome — the title bar and the resize frame — is whatever the window is
        // beyond the space Stage got. Measuring it beats guessing: it moves with
        // the DPI and with the Windows theme.
        double chromeWidth = ActualWidth - Stage.ActualWidth;
        double chromeHeight = ActualHeight - Stage.ActualHeight;
        if (chromeWidth <= 0 || chromeHeight <= 0)
            return;                              // not measured yet; keep the XAML size

        const double WantedWidth = 498, WantedHeight = 1080;
        double wantedBorder = DeviceGeometry.Border * WantedWidth;

        double fit = Math.Min(1.0, Math.Min(
            (work.Height - 8 - chromeHeight) / (WantedHeight + 2 * wantedBorder),
            (work.Width - 8 - chromeWidth) / (WantedWidth + 2 * wantedBorder)));

        double pictureWidth = WantedWidth * fit;
        double border = DeviceGeometry.Border * pictureWidth;

        Width = pictureWidth + 2 * border + chromeWidth;
        Height = WantedHeight * fit + 2 * border + chromeHeight;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
    }

    /// <summary>Pulls the window back inside the monitor it actually landed on.</summary>
    /// <remarks>
    /// Saved bounds are in the window's own units, and a unit is not the same
    /// size on every monitor: restored onto a smaller display the same numbers
    /// can hang off the bottom of the screen, with the command bar among them.
    /// It has to fit, not merely intersect.
    /// </remarks>
    private void ClampToScreen()
    {
        var work = WorkArea();
        if (work.Width <= 0 || work.Height <= 0)
            return;

        Width = Math.Max(MinWidth, Math.Min(Width, work.Width - 8));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height - 8));
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _upkeep.Stop();
        _upkeep.Tick -= OnUpkeep;
        _display.Stop();

        if (WindowState == WindowState.Normal)
        {
            _settings.WindowBounds = [Left, Top, Width, Height];
            _settings.Save();
        }

        _watcher?.Dispose();
        var replay = _replay;
        _replay = null;
        replay?.Dispose();
        var session = _session;
        _session = null;
        _input = null;
        if (session is not null)
        {
            // Given a moment, not waited on for ever: the phone's daemon likes
            // to be told the stream is over, and a window that hangs on the way
            // out is worse than one that leaves a stream to time out.
            // Long enough for the whole hygiene of a proper stop: the BYE, the
            // stopmediastream, and a second each for the display and HID
            // channels to be closed by the phone rather than by us.
            try { session.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(6)); }
            catch (Exception) { /* leaving anyway */ }
        }
        Screen.Source = null;
        _surface = null;
        _surfaceWidth = _surfaceHeight = 0;

        _journal.Write("fenetre fermee");
        _journal.Dispose();
    }

    /// <summary>
    /// Lays out the chassis around a picture of the given size.
    /// </summary>
    /// <remarks>
    /// Called when the geometry changes, when the window is resized, and at no
    /// other time. Everything it touches is retained-mode: WPF composes the
    /// result once and leaves it alone while the video runs behind it. The one
    /// thing deliberately not used here is <c>OpacityMask</c> — it would round
    /// the picture just as well and force an intermediate surface on every
    /// frame. A <c>Clip</c> is a geometry, and costs nothing per frame.
    /// </remarks>
    private void ShapeDevice(double width, double height)
    {
        double shortSide = Math.Min(width, height);
        double border = _border = DeviceGeometry.Border * shortSide;
        double screenRadius = DeviceGeometry.ScreenRadius * shortSide;

        Device.Width = width + 2 * border;
        Device.Height = height + 2 * border;

        var chassis = new CornerRadius(DeviceGeometry.ChassisRadius * shortSide);
        Rail.CornerRadius = chassis;
        Shadow.CornerRadius = chassis;

        // The metal stops short of the picture; the rest of the border is the
        // black glass surround, which is what stops the rail reading as a case.
        double metal = DeviceGeometry.MetalShare * border;
        Glass.Margin = new Thickness(metal);
        Glass.CornerRadius = new CornerRadius(screenRadius + (border - metal));

        Bezel.Margin = new Thickness(border);
        Bezel.Clip = new RectangleGeometry(
            new Rect(0, 0, width, height), screenRadius, screenRadius);

        ShapeIsland(shortSide, border);
        ShapeButtons(border);
        ChooseScaling(width);
        DrawPointer();
    }

    /// <summary>
    /// Picks the scaling filter from the direction the picture is being resized.
    /// </summary>
    /// <remarks>
    /// <c>HighQuality</c> is Fant, and on a downscale it earns its cost: it
    /// averages the pixels it throws away instead of dropping them, which keeps
    /// small text legible in a portrait window. Upscaling it earns nothing —
    /// there is no detail in the frame to recover by stretching it — and it
    /// still pays for every pixel, sixty times a second.
    /// </remarks>
    private void ChooseScaling(double displayedWidth)
    {
        bool magnifying = _surface is not null && displayedWidth > _surfaceWidth;
        RenderOptions.SetBitmapScalingMode(
            Screen, magnifying ? BitmapScalingMode.Linear : BitmapScalingMode.HighQuality);
    }

    /// <summary>Puts the Dynamic Island on whichever edge the phone's top is.</summary>
    /// <remarks>
    /// Hidden outright when the phone is sideways and the detector could not say
    /// which way. The stream already carries the real island as black pixels, so
    /// showing nothing loses nothing — whereas drawing ours on the wrong edge
    /// would drop a black pill onto content.
    /// </remarks>
    private void ShapeIsland(double shortSide, double border)
    {
        double length = DeviceGeometry.IslandWidth * shortSide;
        double thickness = DeviceGeometry.IslandHeight * shortSide;
        double gap = border + DeviceGeometry.IslandTop * shortSide;
        double lens = thickness * 0.42;
        double inset = thickness * 0.32;

        if (_orientation == DeviceGeometry.Orientation.Unknown)
        {
            Island.Visibility = Visibility.Collapsed;
            return;
        }

        Island.Visibility = Visibility.Visible;
        Island.CornerRadius = new CornerRadius(thickness / 2);

        bool portrait = _orientation == DeviceGeometry.Orientation.Portrait;
        Island.Width = portrait ? length : thickness;
        Island.Height = portrait ? thickness : length;

        // The lens sits at the end of the island nearest the phone's right hand
        // side, which rotates with everything else.
        Lens.Width = Lens.Height = lens;

        switch (_orientation)
        {
            case DeviceGeometry.Orientation.Portrait:
                Island.HorizontalAlignment = HorizontalAlignment.Center;
                Island.VerticalAlignment = VerticalAlignment.Top;
                Island.Margin = new Thickness(0, gap, 0, 0);
                Lens.HorizontalAlignment = HorizontalAlignment.Right;
                Lens.VerticalAlignment = VerticalAlignment.Center;
                Lens.Margin = new Thickness(0, 0, inset, 0);
                break;

            case DeviceGeometry.Orientation.IslandLeft:
                Island.HorizontalAlignment = HorizontalAlignment.Left;
                Island.VerticalAlignment = VerticalAlignment.Center;
                Island.Margin = new Thickness(gap, 0, 0, 0);
                Lens.HorizontalAlignment = HorizontalAlignment.Center;
                Lens.VerticalAlignment = VerticalAlignment.Top;
                Lens.Margin = new Thickness(0, inset, 0, 0);
                break;

            default:   // IslandRight
                Island.HorizontalAlignment = HorizontalAlignment.Right;
                Island.VerticalAlignment = VerticalAlignment.Center;
                Island.Margin = new Thickness(0, 0, gap, 0);
                Lens.HorizontalAlignment = HorizontalAlignment.Center;
                Lens.VerticalAlignment = VerticalAlignment.Bottom;
                Lens.Margin = new Thickness(0, 0, 0, inset);
                break;
        }
    }

    /// <summary>Which way a button points away from the body, for shading it.</summary>
    private enum Outward { Left, Right, Up, Down }

    /// <summary>
    /// Places and shades the side buttons for the current orientation.
    /// </summary>
    /// <remarks>
    /// Seen from the front, a side button shows nothing but what stands past the
    /// silhouette: a lamella 0.45 mm wide running its length. Its own face points
    /// sideways and is never in view. That is why the drawn shape is so thin.
    ///
    /// <para>They live on the phone's long edges, so turning it sideways moves
    /// them to the top and bottom of the picture — and which of the two depends
    /// on the direction of the turn, which is the same question the island
    /// answers. When that is unknown they are hidden rather than left where they
    /// were: buttons drawn down the short edges of a landscape phone are not a
    /// small inaccuracy, they are a different object.</para>
    ///
    /// <para>Each lamella now carries an invisible twin as wide as the metal
    /// band, laid out here alongside it. Half a millimetre of drawn button is
    /// honest and unclickable; the twin is what the hand actually hits.</para>
    /// </remarks>
    private void ShapeButtons(double border)
    {
        var buttons = new[]
        {
            (Shape: BtnAction, Hit: (Rectangle?)HitAction, Spec: DeviceGeometry.Action, Left: true),
            (Shape: BtnVolUp, Hit: (Rectangle?)HitVolUp, Spec: DeviceGeometry.VolumeUp, Left: true),
            (Shape: BtnVolDn, Hit: (Rectangle?)HitVolDn, Spec: DeviceGeometry.VolumeDown, Left: true),
            (Shape: BtnSide, Hit: (Rectangle?)HitSide, Spec: DeviceGeometry.Side, Left: false),
            (Shape: BtnCamera, Hit: (Rectangle?)null, Spec: DeviceGeometry.Camera, Left: false),
        };

        bool show = _orientation != DeviceGeometry.Orientation.Unknown;
        bool portrait = _orientation == DeviceGeometry.Orientation.Portrait;
        double along = portrait ? Device.Height : Device.Width;

        foreach (var (shape, hit, spec, onLeft) in buttons)
        {
            shape.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (hit is not null)
                hit.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
                continue;

            bool flush = spec.Proud <= 0;
            double thickness = (flush ? DeviceGeometry.ButtonSeam
                                      : DeviceGeometry.ButtonThickness) * border;
            double length = spec.Length * along;
            double top = spec.Top * along;

            // A proud button hangs outside the silhouette; a seam is pushed the
            // other way, to the middle of the visible metal. The seam cannot sit
            // on the very edge where the drawing puts it: that edge carries the
            // ring that shows the session is up, and a seam laid over it reads as
            // a fault in the ring rather than as a control.
            double metal = DeviceGeometry.MetalShare * border;
            double proud = flush ? -(metal - thickness) * 0.5 : spec.Proud * border;

            shape.RadiusX = shape.RadiusY = (flush ? thickness : proud) * 0.5;

            // The twin is as thick as the metal band and reaches inwards from
            // the same edge, so the hand has a target it can see the shape of.
            double hitThickness = metal;
            double hitProud = proud - hitThickness + thickness;

            Outward outward;
            switch (_orientation)
            {
                case DeviceGeometry.Orientation.Portrait:
                    outward = onLeft ? Outward.Left : Outward.Right;
                    Lay(shape, thickness, length,
                        onLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                        VerticalAlignment.Top,
                        onLeft ? new Thickness(-proud, top, 0, 0)
                               : new Thickness(0, top, -proud, 0));
                    if (hit is not null)
                        Lay(hit, hitThickness, length,
                            onLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                            VerticalAlignment.Top,
                            onLeft ? new Thickness(-hitProud, top, 0, 0)
                                   : new Thickness(0, top, -hitProud, 0));
                    break;

                // Turned island-left: the phone's top points left, so its left
                // edge became the bottom of the picture and the offsets run from
                // the right.
                case DeviceGeometry.Orientation.IslandLeft:
                    outward = onLeft ? Outward.Down : Outward.Up;
                    Lay(shape, length, thickness,
                        HorizontalAlignment.Right,
                        onLeft ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                        onLeft ? new Thickness(0, 0, top, -proud)
                               : new Thickness(0, -proud, top, 0));
                    if (hit is not null)
                        Lay(hit, length, hitThickness,
                            HorizontalAlignment.Right,
                            onLeft ? VerticalAlignment.Bottom : VerticalAlignment.Top,
                            onLeft ? new Thickness(0, 0, top, -hitProud)
                                   : new Thickness(0, -hitProud, top, 0));
                    break;

                default:   // IslandRight — the mirror image of the above
                    outward = onLeft ? Outward.Up : Outward.Down;
                    Lay(shape, length, thickness,
                        HorizontalAlignment.Left,
                        onLeft ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                        onLeft ? new Thickness(top, -proud, 0, 0)
                               : new Thickness(top, 0, 0, -proud));
                    if (hit is not null)
                        Lay(hit, length, hitThickness,
                            HorizontalAlignment.Left,
                            onLeft ? VerticalAlignment.Top : VerticalAlignment.Bottom,
                            onLeft ? new Thickness(top, -hitProud, 0, 0)
                                   : new Thickness(top, 0, 0, -hitProud));
                    break;
            }

            shape.Fill = flush ? SeamBrush : ButtonBrush(outward);
        }

        static void Lay(Rectangle shape, double width, double height,
                        HorizontalAlignment horizontal, VerticalAlignment vertical,
                        Thickness margin)
        {
            shape.Width = width;
            shape.Height = height;
            shape.HorizontalAlignment = horizontal;
            shape.VerticalAlignment = vertical;
            shape.Margin = margin;
        }
    }

    /// <summary>The groove around a control that sits flush with the housing.</summary>
    private static readonly Brush SeamBrush = Freeze(
        new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)));

    /// <summary>
    /// A lamella of the same anodised metal as the rail, lit as the metal is.
    /// </summary>
    /// <remarks>
    /// The gradient has to be built here rather than declared in App.xaml,
    /// because it runs across the button and the button turns with the phone: a
    /// brush declared once would shade correctly in portrait and along the wrong
    /// axis the moment the phone is laid on its side.
    /// </remarks>
    private Brush ButtonBrush(Outward outward)
    {
        (Point from, Point to) = outward switch
        {
            Outward.Left => (new Point(1, 0.5), new Point(0, 0.5)),
            Outward.Right => (new Point(0, 0.5), new Point(1, 0.5)),
            Outward.Up => (new Point(0.5, 1), new Point(0.5, 0)),
            _ => (new Point(0.5, 0), new Point(0.5, 1)),
        };

        var brush = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        brush.GradientStops.Add(new GradientStop(_buttonJoint, 0.00));
        brush.GradientStops.Add(new GradientStop(_buttonJoint, 0.20));
        brush.GradientStops.Add(new GradientStop(_buttonChamfer, 0.40));
        brush.GradientStops.Add(new GradientStop(_buttonFace, 0.70));
        brush.GradientStops.Add(new GradientStop(_buttonJoint, 1.00));
        return Freeze(brush);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void DrawPointer()
    {
        if (!_pointerKnown || _surface is null)
        {
            Pointer.Visibility = Visibility.Collapsed;
            return;
        }

        // The overlay lives inside the screen, so there is no letterbox offset
        // left to add — the canvas and the picture share an origin.
        double scale = PictureScale;
        Pointer.Visibility = Visibility.Visible;
        Canvas.SetLeft(Pointer, _pointerX * scale - Pointer.Width / 2);
        Canvas.SetTop(Pointer, _pointerY * scale - Pointer.Height / 2);
    }

    // --- Chassis buttons ---------------------------------------------------------

    /// <summary>A drawn side button pressed with the mouse.</summary>
    private void OnChassisButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        string? name = sender is FrameworkElement element ? element.Name switch
        {
            "HitVolUp" => "volume-up",
            "HitVolDn" => "volume-down",
            "HitSide" => "lock",
            "HitAction" => "mute",
            _ => null,
        } : null;

        if (name is null)
            return;

        if (_input is null)
        {
            Report("Pas de session : le bouton n'a nulle part où aller.");
            return;
        }

        Report($"Bouton {name}.");
        Post(name, input => input.PressButtonAsync(name));
    }

    private void OnHomeClicked(object sender, RoutedEventArgs e)
    {
        if (_input is null)
        {
            Report("Pas de session : le bouton n'a nulle part où aller.");
            return;
        }
        Post("home", input => input.PressButtonAsync("home"));
    }

    // --- Engagement ---------------------------------------------------------------

    /// <summary>
    /// Takes over the mouse. Explicit rather than automatic, because a window
    /// that grabbed the pointer on hover would make the rest of the desktop
    /// unreachable while it has focus.
    /// </summary>
    private void Engage()
    {
        if (_engaged || _surface is null || !Drivable)
            return;

        _engaged = true;

        // On the screen, not on the whole window: the floating bar has to keep a
        // pointer the hand can see, and the chassis is not a target.
        Bezel.Cursor = Cursors.None;
        Rail.BorderBrush = (Brush)FindResource("Primary");
        Rail.BorderThickness = new Thickness(1.6);
        UpdateState();
        Report("Pilotage  ·  Ctrl+Alt gauche pour rendre la souris");
    }

    private void Disengage(string? reason = null)
    {
        ReleaseTouch();
        ReleaseKeys();

        if (!_engaged)
            return;

        _engaged = false;
        Bezel.Cursor = Cursors.Arrow;
        Rail.BorderThickness = new Thickness(0);
        UpdateState();
        Mouse.Capture(null);
        Report(reason is null
            ? "Souris rendue à Windows  ·  clic dans l'image pour reprendre"
            : $"{reason}  Clic dans l'image pour reprendre.");
    }

    /// <summary>Lifts whatever finger is still down, wherever it is.</summary>
    private void ReleaseTouch()
    {
        // A pending press was never sent, so there is nothing to release —
        // dropped, not flushed, or leaving the window would click it.
        _pressPending = false;
        if (!_touching)
            return;

        _touching = false;
        var (x, y) = Normalise(_pointerX, _pointerY);
        if (_input is { } injector) injector.QueueTouchUp(x, y); else SinkReport();
    }

    private void ReleaseKeys()
    {
        if (_heldKeys.Count == 0)
            return;
        _heldKeys.Clear();
        _input?.QueueKeyboard(Array.Empty<int>());
    }

    // --- Pointer -------------------------------------------------------------------

    /// <summary>
    /// How long a left press may stay unsent while it waits to become a tap.
    /// </summary>
    /// <remarks>
    /// Short clicks used to arrive on the phone as long presses. The click itself
    /// was innocent: the press left on the mouse-down and the release on the
    /// mouse-up, so the duration iOS saw was the physical one <i>plus whatever
    /// delayed the second report and not the first</i>. Any of those landing
    /// inside a 100 ms click stretched it past the half-second iOS reads as
    /// touch-and-hold.
    ///
    /// <para>So the press is not sent when the button goes down. If the button
    /// comes back up within this window it was a tap, and the whole thing goes
    /// out as one <c>TapAsync</c> — a press and a release composed after the
    /// release is already known to have happened. A press that outlives the
    /// window, or moves further than <see cref="TapSlopWindowPx"/>, is a real
    /// hold or a drag and is flushed the moment that becomes known.</para>
    /// </remarks>
    private const double TapDeferMs = 180.0;

    /// <summary>Wire duration of a synthesized tap, press to release.</summary>
    private const int TapPressMs = 40;

    /// <summary>
    /// Movement, in window pixels, past which a pending press is a drag.
    /// </summary>
    /// <remarks>
    /// Window pixels rather than phone pixels, because the hand lives in the
    /// window: the same wobble of a clicking finger covers three times more
    /// phone pixels when the picture is shown small.
    /// </remarks>
    private const double TapSlopWindowPx = 5.0;

    /// <summary>How far one wheel notch drags, in picture pixels.</summary>
    private const double WheelPixelsPerNotch = 220.0;

    /// <summary>How long that drag takes; slower than a flick, so iOS reads it as one.</summary>
    private const int WheelDurationMs = 90;

    /// <summary>
    /// One mouse event: a position, and nothing that the interface owns.
    /// </summary>
    /// <remarks>
    /// A gaming mouse produces about two thousand of these a second, so
    /// the standard for what may happen here is not "quickly" but "never".
    /// What is left is a coordinate read, four divisions, and handing the
    /// position to the injector's one-slot pump — no layout query, no cursor
    /// assignment, no text, no allocation and no overlay, all of which
    /// invalidate something and all of which have moved to the render tick or
    /// to the four-hertz timer.
    ///
    /// <para>Sending from here rather than from the render tick is the other
    /// half of it. The pump runs at a hundred and twenty hertz on a thread of
    /// its own and keeps one position, last one wins; a hand's positions
    /// therefore reach the phone at the pump's pace whatever the window is
    /// doing, instead of waiting for a render tick that a busy interface thread
    /// may owe by then.</para>
    /// </remarks>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        long entered = Stopwatch.GetTimestamp();
        base.OnMouseMove(e);
        _mouseEvents++;

        if (_pictureWidth == 0 || !IsActive)
        {
            NoteMouseCost(entered);
            return;
        }

        var position = e.GetPosition(Screen);
        double x = position.X / _scale;
        double y = position.Y / _scale;

        // Outside the picture is the chassis, the floating bar or the desk.
        bool inside = x >= 0 && y >= 0 && x < _pictureWidth && y < _pictureHeight;
        _pointerInside = inside;
        _mouseDirty = true;
        if (!inside)
        {
            NoteMouseCost(entered);
            return;
        }

        _pointerX = x;
        _pointerY = y;

        // A pending press that travels is a drag, and a drag needs its press on
        // the phone before the movement that follows — flushed here, so the wire
        // order is press-then-move. It happens once per drag, not per event.
        if (_pressPending &&
            (Math.Abs(x - _pressX) > _slop || Math.Abs(y - _pressY) > _slop))
            FlushPendingPress();

        if (_touching)
            Move(x / _pictureWidth, y / _pictureHeight);

        NoteMouseCost(entered);
    }

    /// <summary>Adds one mouse event to the running cost of the handler.</summary>
    private void NoteMouseCost(long entered)
    {
        long spent = Stopwatch.GetTimestamp() - entered;
        _moveTicks += spent;
        _moveCount++;
        if (spent > _moveMaxTicks)
            _moveMaxTicks = spent;
    }

    /// <summary>
    /// Everything the interface owes the pointer, once per drawn frame.
    /// </summary>
    /// <remarks>
    /// The cursor, the engagement and the overlay — three things that each
    /// invalidate something the layout system will have to answer for, and none
    /// of which a person can see happen more often than the screen refreshes.
    /// They used to run once per mouse event.
    /// </remarks>
    private void RefreshPointer()
    {
        if (_surface is null)
            return;

        if (!_pointerInside)
        {
            if (Bezel.Cursor != Cursors.Arrow)
                Bezel.Cursor = Cursors.Arrow;
            return;
        }

        if (!_engaged)
            Engage();
        if (Bezel.Cursor != Cursors.None)
            Bezel.Cursor = Cursors.None;
        _pointerKnown = true;
        DrawPointer();
    }

    /// <summary>
    /// Sends a pending press for real — it turned out to be a hold or a drag.
    /// </summary>
    /// <remarks>
    /// At the point where the hand pressed, not where the pointer has wandered to
    /// since: a drag that begins a few pixels off its anchor grabs the wrong
    /// thing, and the very next move report walks the finger to the current
    /// position anyway, which iOS reads as the start of the drag proper.
    /// </remarks>
    private void FlushPendingPress()
    {
        if (!_pressPending)
            return;

        _pressPending = false;
        _touching = true;
        var (x, y) = Normalise(_pressX, _pressY);
        if (_input is { } injector) injector.QueueTouchDown(x, y); else SinkReport();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Stage.Focus();
        e.Handled = true;

        if (_surface is null || !Drivable)
            return;

        // Only what lands on the picture drives the phone. The chassis carries
        // its own buttons and the bar carries its own controls; a press there
        // must not become a finger somewhere on the screen, whichever of the
        // two mouse events WPF happens to deliver first.
        var landing = e.GetPosition(Screen);
        if (landing.X < 0 || landing.Y < 0 ||
            landing.X >= Screen.ActualWidth || landing.Y >= Screen.ActualHeight)
            return;

        if (!_engaged)
        {
            // The click that takes control is not forwarded: nothing has been
            // aimed at yet.
            Engage();
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            Post("home", input => input.PressButtonAsync("home"));
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
            return;

        // Held only while a button is down. The cursor is never trapped, but
        // without capture a press that ends outside the window loses its
        // mouse-up, and the finger stays down on the phone for ever.
        Mouse.Capture(Stage);

        // Not sent yet. See TapDeferMs for why.
        _pressPending = true;
        _pressStartMs = _sendClock.Elapsed.TotalMilliseconds;
        _pressX = _pointerX;
        _pressY = _pointerY;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        e.Handled = true;

        if (!_engaged || e.ChangedButton != MouseButton.Left)
            return;

        if (_pressPending)
        {
            // The whole click fits inside the defer window: it is a tap, and the
            // phone hears about it as one indivisible pair.
            _pressPending = false;
            Mouse.Capture(null);
            var (tx, ty) = Normalise(_pressX, _pressY);
            if (_input is { } injector) injector.QueueTap(tx, ty, TapPressMs); else SinkReport();
            return;
        }

        if (_touching)
        {
            _touching = false;
            var (x, y) = Normalise(_pointerX, _pointerY);
            if (_input is { } injector) injector.QueueTouchUp(x, y); else SinkReport();
        }

        Mouse.Capture(null);
    }

    /// <summary>
    /// Lets go cleanly when something else takes the mouse mid-press.
    /// </summary>
    /// <remarks>
    /// Capture can be torn away — a popup, another window claiming the mouse —
    /// and the release event then never arrives. A pending press is simply
    /// forgotten, since the phone was never told about it; a sent one is released
    /// where it is, for the same reason <see cref="Disengage"/> does.
    /// </remarks>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        ReleaseTouch();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;

        if (!_engaged || _surface is null || !Drivable)
            return;

        int notches = Math.Clamp(e.Delta / 120, -10, 10);
        if (notches == 0)
            return;

        // A wheel notch has no equivalent on a touchscreen, so it is spelled as
        // what a finger would do: a short vertical drag at the pointer. One
        // notch down scrolls the content down, as on any PC, and on a touch
        // surface that is a finger travelling *up* — which is what the sign
        // below says. An earlier version flipped it in the name of iOS's
        // Natural Scrolling and got the direction backwards: Natural Scrolling
        // describes a trackpad, and the phone never sees a trackpad here, only
        // a finger on its own glass.
        double travel = notches * WheelPixelsPerNotch;
        if (_settings.InvertWheel)
            travel = -travel;

        double fromY = Math.Clamp(_pointerY, 0, _surfaceHeight - 1);
        double toY = Math.Clamp(fromY + travel, 0, _surfaceHeight - 1);
        var (x, y1) = Normalise(_pointerX, fromY);
        var (_, y2) = Normalise(_pointerX, toY);
        Post("molette", input => input.DragAsync(x, y1, x, y2, WheelDurationMs));
    }

    // --- Keyboard --------------------------------------------------------------------

    /// <summary>The modifier bits <see cref="HidKeyboard"/> reports, as HID usages.</summary>
    private static readonly (byte Bit, int Usage)[] ModifierUsages =
    [
        (HidKeyboard.ModifierControl, 0xE0),
        (HidKeyboard.ModifierShift, 0xE1),
        (HidKeyboard.ModifierAlt, 0xE2),
        (HidKeyboard.ModifierCommand, 0xE3),
    ];

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Before anything else, and deliberately also while piloting: the
        // counters are most wanted exactly when something is going wrong, and
        // stopping to hand the mouse back first would lose the moment.
        if (key == Key.F3)
        {
            ToggleMetrics();
            e.Handled = true;
            return;
        }

        // F2 rather than Ctrl+V: while piloting, every keystroke is forwarded to
        // the phone, so Ctrl+V would arrive there as a shortcut iOS does not use
        // — it wants Command+V — and would never reach this handler.
        if (key == Key.F2)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        // Ctrl+Alt is the way out, checked before anything is forwarded.
        //
        // The Alt has to be the LEFT one, and that is not a detail. Windows
        // encodes AltGr as right Alt plus a synthetic left Ctrl, so
        // Keyboard.Modifiers reads Control|Alt the moment AltGr is pressed —
        // exactly like the escape combination. On a French keyboard that made
        // the most useful key on the board an eject button.
        bool isModifier = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt;
        bool escaping = Keyboard.IsKeyDown(Key.LeftAlt) &&
                        (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl));
        if (isModifier && escaping)
        {
            Disengage();
            e.Handled = true;
            return;
        }

        if (!_engaged)
            return;

        // Tab and the arrows would otherwise be eaten by WPF focus navigation and
        // never reach the phone.
        byte usage = HidKeyboard.Translate(key);
        if (usage == 0)
        {
            // A modifier carries no usage of its own, but the phone still needs
            // to be told it went down — otherwise a Shift held for the next
            // keystroke is a Shift the phone never hears about.
            if (isModifier || key is Key.LWin or Key.RWin or Key.LeftShift or Key.RightShift)
            {
                SendKeyboard();
                e.Handled = true;
            }
            return;
        }

        _heldKeys.Add(usage);
        SendKeyboard();
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (!_engaged)
            return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        byte usage = HidKeyboard.Translate(key);

        // The release of a modifier used to be dropped here, and nothing else
        // ever sent one: Translate returns 0 for Ctrl, Shift, Alt and Windows,
        // so letting go of Ctrl after Ctrl+A emitted no report at all and the
        // phone went on believing Ctrl was down.
        if (usage != 0)
            _heldKeys.Remove(usage);

        SendKeyboard();
        e.Handled = true;
    }

    /// <summary>The whole state of the keyboard: held keys plus live modifiers.</summary>
    private void SendKeyboard()
    {
        byte modifiers = HidKeyboard.CurrentModifiers();
        var held = new List<int>(_heldKeys.Count + 4);
        held.AddRange(_heldKeys);
        foreach (var (bit, usageCode) in ModifierUsages)
            if ((modifiers & bit) != 0)
                held.Add(usageCode);

        _input?.QueueKeyboard(held);
    }

    // --- Paste ------------------------------------------------------------------------

    private bool _pasting;
    private bool _cancelPaste;

    private async void OnPasteClicked(object sender, RoutedEventArgs e) => await PasteAsync();

    /// <summary>
    /// Replays the Windows clipboard as keystrokes on the phone.
    /// </summary>
    /// <remarks>
    /// The only way to get text onto the phone from here, and it works because
    /// the phone believes a keyboard is attached. There is no clipboard to share
    /// and nothing to install: the text is typed, one key at a time, exactly as a
    /// person would — which means it obeys the same rule as every other
    /// keystroke, since what travels is the position of a key rather than the
    /// character on it.
    /// </remarks>
    private async Task PasteAsync()
    {
        if (_pasting)
        {
            _cancelPaste = true;
            return;
        }

        var input = _input;
        if (input is null)
        {
            Report("Pas de session — rien à coller.");
            return;
        }

        string text;
        try
        {
            text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
        }
        catch (Exception)
        {
            // Another process holds the clipboard open; it is not ours to fight over.
            Report("Presse-papiers illisible — une autre application le tient.");
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            Report("Presse-papiers vide.");
            return;
        }

        // Two thousand characters is over half a minute of typing. Past that the
        // gesture stops being a paste and becomes something to sit and watch.
        const int Ceiling = 2000;
        bool truncated = text.Length > Ceiling;
        if (truncated)
            text = text[..Ceiling];

        var strokes = HidKeyboard.Compose(text, out int skipped);
        if (strokes.Count == 0)
        {
            Report("Rien de saisissable dans le presse-papiers.");
            return;
        }

        _pasting = true;
        _cancelPaste = false;
        PasteLabel.Text = "Arrêter";

        try
        {
            var held = new List<int>(5);
            for (int i = 0; i < strokes.Count; i++)
            {
                if (_cancelPaste)
                {
                    Report($"Collage interrompu après {i} frappes.");
                    return;
                }

                if (_input is null)
                {
                    Report($"Collage interrompu à {i}/{strokes.Count} — la session est tombée.");
                    return;
                }

                held.Clear();
                foreach (var (bit, usageCode) in ModifierUsages)
                    if ((strokes[i].Modifiers & bit) != 0)
                        held.Add(usageCode);
                held.Add(strokes[i].Usage);

                await input.KeyboardReportAsync(held);
                await Task.Delay(9);
                await input.KeyboardReportAsync(Array.Empty<int>());
                await Task.Delay(9);

                if (i % 40 == 0)
                    Report($"Collage… {i}/{strokes.Count}");
            }

            string note = skipped > 0
                ? $" {skipped} caractère(s) hors de portée du clavier, ignoré(s)."
                : string.Empty;
            if (truncated)
                note += $" Coupé à {Ceiling} caractères.";

            Report($"Collé : {strokes.Count} frappes.{note}");
        }
        catch (Exception exception)
        {
            Fault($"Collage interrompu : {exception.Message}");
        }
        finally
        {
            // Nothing may be left held down, whatever happened above.
            try { await input.KeyboardReportAsync(Array.Empty<int>()); }
            catch (Exception) { /* the session is gone; so is the held key */ }
            _pasting = false;
            PasteLabel.Text = "Coller";
        }
    }

    // --- Brightness --------------------------------------------------------------------

    private bool _dimmed;

    /// <summary>
    /// Pulls the phone's brightness down through Control Centre.
    /// </summary>
    /// <remarks>
    /// Runs once per session, and only if <c>dimOnConnect</c> says so. Everything
    /// it does is a finger drag, because there is no HID usage for brightness
    /// that iOS would accept from here — and interpolated rather than jumped,
    /// because iOS reads a drag from the stream of positions while the contact is
    /// held, and a single report from one end to the other reads as a teleport.
    /// </remarks>
    private async Task DimAsync()
    {
        var input = _input;
        if (_dimmed || !_settings.DimOnConnect || input is null)
            return;

        double[] g = _settings.DimGesture;
        if (g.Length != 5)
        {
            Report("dimGesture attend cinq nombres — luminosité ignorée.");
            return;
        }

        _dimmed = true;
        try
        {
            Report("Ouverture du centre de contrôle…");
            await input.DragAsync(g[0], g[1], g[0], 0.35, 260);
            await Task.Delay(700);

            Report("Baisse de la luminosité…");
            await input.DragAsync(g[2], g[3], g[2], g[4], 200);
            await Task.Delay(400);

            // Close it again: a swipe back up from the bottom.
            await input.DragAsync(0.5, 0.97, 0.5, 0.55, 220);
            Report("Luminosité baissée. Si ce n'est pas ce qui s'est passé, ajuste dimGesture dans settings.json.");
        }
        catch (Exception exception)
        {
            Fault($"Luminosité : {exception.Message}");
        }
    }
}
