using LuminaMonitor.Core.RemoteXpc;
using XpcService = LuminaMonitor.Core.RemoteXpc.RemoteXpc;

namespace LuminaMonitor.Core.Hid;

/// <summary>
/// Touches, drags, hardware buttons and keystrokes, in the phone's own
/// coordinate model.
/// </summary>
/// <remarks>
/// Positions are absolute and normalised to 0..1, origin top-left, exactly as
/// the touchscreen surface expects them once scaled to 0..65535 — no pixels,
/// no screen size, no scale factor anywhere in the caller's code.
///
/// <para>Two doors, two channels: the touchscreen and the keyboard go through
/// the universal HID service (which needs the media stream up), the hardware
/// buttons through the daemon's Indigo door, which is born authenticated and
/// is therefore opened only when a button is actually pressed.</para>
///
/// <para>Two ways in, and the difference matters. The <c>…Async</c> methods
/// send one report each and are awaited: that is what a script wants, and what
/// the probe measures with. The <c>Queue…</c> methods hand the report to the
/// send pump below and return at once: that is what a hand on a mouse needs,
/// because a hand produces positions far faster than the wire carries
/// them. Never mix the two on one contact — the pump keeps its own order, and a
/// direct send has no way to slot into it.</para>
/// </remarks>
public sealed class InputInjector
{
    /// <summary>The surface a virtual keyboard's reports are addressed to.</summary>
    private const ulong KeyboardSurface = 512;

    /// <summary>
    /// How often the pump looks at the pending position, in hertz.
    /// </summary>
    /// <remarks>
    /// Twice the phone's refresh: enough that a drag never looks stepped, low
    /// enough that the wire is never the thing setting the pace. The exact
    /// figure matters far less than the fact that it is fixed — a rate the
    /// hand's speed cannot raise is the whole point of the pump.
    /// </remarks>
    private const double SendHz = 120.0;

    /// <summary>How long the phone is given to hang up on a channel before we do.</summary>
    private static readonly TimeSpan HangUpPatience = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long one report may take to reach the wire before the channel is
    /// declared stuck.
    /// </summary>
    /// <remarks>
    /// A report that is late by half a second is already useless: the hand has
    /// moved, and iOS will read a press that arrives that late as something the
    /// person did not do. So the pump gives up on it here and goes back to
    /// draining, which is the whole point — one send that never returns is one
    /// drain that never runs again.
    ///
    /// <para>Giving up is not the same as being free, though: the abandoned
    /// send still holds the channel's write lock underneath, and every report
    /// behind it is still parked. That is what <see cref="ChannelPatience"/>
    /// ends, one level down.</para>
    /// </remarks>
    private static readonly TimeSpan SendPatience = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a write on an input channel may wait on the phone's receive
    /// window before the connection underneath is declared stuck.
    /// </summary>
    /// <remarks>
    /// The backstop under <see cref="SendPatience"/>. Twice it, deliberately:
    /// the pump's ceiling is meant to fire first and keep the drain running,
    /// and this one exists for the thing the pump cannot reach — the write lock
    /// the abandoned send is still holding, which without a deadline it holds
    /// for ever. Past this the connection is in default, the lock is released
    /// on the way out, and the session builds a fresh channel.
    ///
    /// <para>An input channel only. A transfer — the service directory, a media
    /// offer — waits for as long as the phone needs, because arriving late is
    /// what a transfer is for and arriving never is not.</para>
    /// </remarks>
    internal static readonly TimeSpan ChannelPatience = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How many reports may wait for the pump before the oldest movements are
    /// thrown away.
    /// </summary>
    /// <remarks>
    /// Four ticks' worth. A queue deeper than that is not a queue any more, it
    /// is a recording being replayed onto the phone — which is exactly the
    /// failure the pump exists to prevent, and which comes back the moment a
    /// send stops returning. Presses and releases are never among what is
    /// dropped: a lost movement is a movement, a lost release is a finger left
    /// on the glass for ever.
    /// </remarks>
    private const int MaxCommands = 32;

    /// <summary>One report waiting for the pump, and what may be done to it.</summary>
    /// <param name="Work">The send itself.</param>
    /// <param name="Droppable">A movement: superseded by anything newer, so it may be evicted.</param>
    /// <param name="Timed">One report rather than a composed gesture, so its wait means something.</param>
    /// <param name="QueuedTicks">When the caller handed it over.</param>
    private readonly record struct Command(Func<Task> Work, bool Droppable, bool Timed, long QueuedTicks);

    private readonly Rsd _rsd;
    private XpcService _hid;
    private XpcService? _indigo;

    // --- The send pump ---------------------------------------------------------
    // One pending position, last one wins, plus the discrete reports that must
    // keep their place in the sequence around it.

    private readonly object _gate = new();
    private readonly Queue<Command> _commands = new();
    private readonly SemaphoreSlim _drainGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _closing = new();
    private Task? _pump;
    private Task? _waker;

    private int _pendingX, _pendingY;
    private bool _hasPendingMove;
    private long _pendingTicks;
    private int _lastSentX = -1, _lastSentY = -1;
    private long _reportsSent;
    private long _movesDropped;
    private long _queueOverflows;
    private long _sendTimeouts;
    private long _sendTicks, _sendSamples, _sendMaxTicks;
    private string? _lastFault;

    internal InputInjector(Rsd rsd, XpcService hid)
    {
        _rsd = rsd;
        _hid = hid;
    }

    /// <summary>Messages the Indigo channel gave up on, for the probe's report.</summary>
    internal long DroppedMessages => _indigo?.DroppedMessages ?? 0;

    /// <summary>Where a queued report's failure is reported; the caller decides what to say.</summary>
    public Action<Exception>? Faulted { get; set; }

    /// <summary>Reports waiting for the pump: the pending position counts as one.</summary>
    public int QueueDepth
    {
        get { lock (_gate) return _commands.Count + (_hasPendingMove ? 1 : 0); }
    }

    /// <summary>What the input path has counted since the session came up.</summary>
    public InputStats Stats
    {
        get
        {
            var hid = Volatile.Read(ref _hid);
            var (waiters, segments, bytes) = hid.SendDepth;
            long samples = Interlocked.Read(ref _sendSamples);
            double mean = samples > 0 ? Ms(Interlocked.Read(ref _sendTicks)) / samples : 0;
            return new InputStats(
                Interlocked.Read(ref _reportsSent), Interlocked.Read(ref _movesDropped),
                hid.DroppedMessages, QueueDepth, waiters, segments, bytes,
                Interlocked.Read(ref _queueOverflows), Interlocked.Read(ref _sendTimeouts),
                mean, Ms(Interlocked.Read(ref _sendMaxTicks)), Volatile.Read(ref _lastFault));
        }
    }

    /// <summary>Forgets the high-water marks, so the next window of the journal measures itself.</summary>
    public void ResetPeaks()
    {
        Interlocked.Exchange(ref _sendTicks, 0);
        Interlocked.Exchange(ref _sendSamples, 0);
        Interlocked.Exchange(ref _sendMaxTicks, 0);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    // --- Queued: the hand's path -----------------------------------------------

    /// <summary>
    /// Where the finger should be. Returns at once; the pump sends it.
    /// </summary>
    /// <remarks>
    /// A position handed over here replaces whatever was still waiting: there is
    /// one slot, never a queue. That is the whole fix for a lag that grew with
    /// the speed of the hand — a thousand positions a second used to become a
    /// thousand reports a second, each one queued behind the last, so the phone
    /// was replaying where the mouse had been rather than showing where it
    /// was. What is thrown away here is counted in
    /// <see cref="InputStats.MovesDropped"/>, and throwing it away is the
    /// correct answer: a superseded position has no reader.
    /// </remarks>
    public void QueueMove(double xNorm, double yNorm)
    {
        int x = Scale(xNorm), y = Scale(yNorm);
        lock (_gate)
        {
            if (_hasPendingMove)
                _movesDropped++;
            else
                _pendingTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _pendingX = x;
            _pendingY = y;
            _hasPendingMove = true;
        }
        Start();
    }

    /// <summary>A press, sent without waiting for the next tick.</summary>
    public void QueueTouchDown(double xNorm, double yNorm)
    {
        int x = Scale(xNorm), y = Scale(yNorm);
        Post(() =>
        {
            lock (_gate) { _lastSentX = x; _lastSentY = y; }
            return SendTouchScaledAsync(true, x, y);
        }, timed: true);
    }

    /// <summary>A release, sent without waiting for the next tick.</summary>
    public void QueueTouchUp(double xNorm, double yNorm)
    {
        int x = Scale(xNorm), y = Scale(yNorm);
        Post(() =>
        {
            lock (_gate) { _lastSentX = -1; _lastSentY = -1; }
            return SendTouchScaledAsync(false, x, y);
        }, timed: true);
    }

    /// <summary>A press and its release, composed as one indivisible pair.</summary>
    public void QueueTap(double xNorm, double yNorm, int holdMs = 60)
    {
        int x = Scale(xNorm), y = Scale(yNorm);
        Post(async () =>
        {
            lock (_gate) { _lastSentX = -1; _lastSentY = -1; }
            await SendTouchScaledAsync(true, x, y).ConfigureAwait(false);
            await Task.Delay(holdMs).ConfigureAwait(false);
            await SendTouchScaledAsync(false, x, y).ConfigureAwait(false);
        });
    }

    /// <summary>The whole keyboard state, in its place in the sequence.</summary>
    public void QueueKeyboard(IReadOnlyCollection<int> usagesHeld) =>
        Post(() => KeyboardReportAsync(usagesHeld), timed: true);

    /// <summary>A hardware button, in its place in the sequence.</summary>
    public void QueueButton(string name) => Post(() => PressButtonAsync(name));

    /// <summary>
    /// Any composed gesture — a wheel drag, a keystroke — in its place in the
    /// sequence, so it cannot overtake the position still waiting.
    /// </summary>
    public void Queue(Func<Task> work) => Post(work);

    // --- Awaited: the script's path ---------------------------------------------

    public async Task TapAsync(double xNorm, double yNorm, int holdMs = 60)
    {
        await TouchDownAsync(xNorm, yNorm);
        await Task.Delay(holdMs);
        await TouchUpAsync(xNorm, yNorm);
    }

    public Task TouchDownAsync(double xNorm, double yNorm) => SendTouchAsync(true, xNorm, yNorm);

    /// <summary>A moving contact: the same contact report at a new position.</summary>
    public Task TouchMoveAsync(double xNorm, double yNorm) => SendTouchAsync(true, xNorm, yNorm);

    public Task TouchUpAsync(double xNorm, double yNorm) => SendTouchAsync(false, xNorm, yNorm);

    public async Task DragAsync(double x1, double y1, double x2, double y2, int durationMs, int steps = 20)
    {
        if (steps < 1) steps = 1;
        await TouchDownAsync(x1, y1);
        int slice = Math.Max(0, durationMs) / steps;
        for (int i = 1; i <= steps; i++)
        {
            await Task.Delay(slice);
            double t = (double)i / steps;
            await TouchMoveAsync(x1 + (x2 - x1) * t, y1 + (y2 - y1) * t);
        }
        await TouchUpAsync(x2, y2);
    }

    /// <summary>home, lock, volume-up, volume-down, mute, siri — down, hold, up.</summary>
    public async Task PressButtonAsync(string name)
    {
        if (!IndigoHid.Named.ContainsKey(name))
            throw new LuminaException($"Bouton inconnu : {name} (attendu : {string.Join(", ", IndigoHid.Named.Keys)})");
        _indigo ??= await _rsd.OpenAsync(IndigoHid.ServiceName, writePatience: ChannelPatience);
        await IndigoHid.PressAsync(_indigo, name);
    }

    /// <summary>
    /// The whole state of the virtual keyboard: every HID usage currently held,
    /// modifiers included (0xE0..0xE3 for the left control, shift, alt and
    /// command keys).
    /// </summary>
    /// <remarks>
    /// The report carries the full pressed set rather than a change, so a key is
    /// released by sending again without its usage and an empty collection
    /// releases everything. Fire-and-forget, like every other report: nothing is
    /// awaited from the daemon, which is what keeps a keystroke at a few
    /// milliseconds.
    ///
    /// <para>The caller's own translation table decides which usage a physical
    /// key carries — the phone reads a position, not a character, and applies
    /// whichever hardware layout it is set to.</para>
    /// </remarks>
    public Task KeyboardReportAsync(IReadOnlyCollection<int> usagesHeld)
    {
        Interlocked.Increment(ref _reportsSent);
        return Hid.SendReportAsync(Volatile.Read(ref _hid), KeyboardSurface, Hid.KeyboardReport(usagesHeld));
    }

    /// <summary>
    /// Types a line on the virtual keyboard: one report per keystroke, then an
    /// empty one to release. Characters the US layout cannot spell are skipped.
    /// </summary>
    public async Task TypeAsync(string text)
    {
        foreach (char c in text)
        {
            if (Hid.UsageFor(c) is not { } key) continue;
            int[] held = key.Shift ? [Hid.UsageLeftShift, key.Usage] : [key.Usage];
            await KeyboardReportAsync(held);
            await Task.Delay(20);
            await KeyboardReportAsync([]);
            await Task.Delay(20);
        }
    }

    /// <summary>Lists the HID surfaces the daemon exposes, for the probe's trace.</summary>
    internal Task<Dictionary<string, object?>> ListSurfacesAsync() =>
        Hid.ListSurfacesAsync(Volatile.Read(ref _hid));

    /// <summary>
    /// Replaces the HID channel with a fresh one, keeping the pump and the
    /// session.
    /// </summary>
    /// <remarks>
    /// A channel whose writes have stopped returning does not recover: the
    /// frames already handed to it are on a socket nobody is reading, and the
    /// write lock underneath them is held for as long as that lasts. There is
    /// nothing to wait for and nothing to cancel — only a second door to open,
    /// which the directory has been advertising all along.
    ///
    /// <para>Whatever was queued is thrown away with the old channel. It was
    /// aimed at a moment that has passed, and a release from before a stall
    /// would land on a screen the person has since scrolled. The window is told
    /// to forget the finger it thought was down for the same reason.</para>
    /// </remarks>
    internal async Task ReopenAsync()
    {
        var fresh = await _rsd.OpenAsync(Hid.ServiceName, writePatience: ChannelPatience).ConfigureAwait(false);
        var stale = Interlocked.Exchange(ref _hid, fresh);
        lock (_gate)
        {
            _commands.Clear();
            _hasPendingMove = false;
            _lastSentX = -1;
            _lastSentY = -1;
        }
        Volatile.Write(ref _lastFault, null);
        try { await stale.CloseAsync(HangUpPatience).ConfigureAwait(false); }
        catch (Exception) { /* it is the channel we gave up on */ }
    }

    /// <summary>
    /// Stops the pump, then hangs up on the phone's terms.
    /// </summary>
    /// <remarks>
    /// The pump is drained before the channels close, so no report is ever
    /// written onto a socket that is already going away; then each channel is
    /// given a second to be closed by the daemon that owns it, and closed with a
    /// FIN rather than a RST if it is not. The same courtesy as the media
    /// session, for the same reason: a service cut off mid sentence is a service
    /// that may not open again. Returns how long the whole thing took, for the
    /// journal.
    /// </remarks>
    internal async Task<double> CloseAsync()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        _closing.Cancel();
        foreach (var task in new[] { _pump, _waker })
            if (task is not null) { try { await task.ConfigureAwait(false); } catch (Exception) { } }
        _pump = null;
        _waker = null;

        var indigo = _indigo;
        _indigo = null;
        if (indigo is not null) { try { await indigo.CloseAsync(HangUpPatience).ConfigureAwait(false); } catch (Exception) { } }
        try { await Volatile.Read(ref _hid).CloseAsync(HangUpPatience).ConfigureAwait(false); } catch (Exception) { }

        return (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0
            / System.Diagnostics.Stopwatch.Frequency;
    }

    // --- The pump itself ---------------------------------------------------------

    /// <summary>
    /// Puts a discrete report at the end of the sequence, behind the position
    /// still waiting, and wakes the pump so it goes out now rather than at the
    /// next tick.
    /// </summary>
    /// <remarks>
    /// The pending position is folded into the queue rather than left in its
    /// slot: it was produced before this report, so it has to leave before it.
    /// A press that overtook the move preceding it would land at the wrong
    /// place, and a release that overtook one would leave a finger on the
    /// screen.
    /// </remarks>
    private void Post(Func<Task> work, bool timed = false)
    {
        lock (_gate)
        {
            if (_hasPendingMove)
            {
                int x = _pendingX, y = _pendingY;
                long stamped = _pendingTicks;
                _hasPendingMove = false;
                EnqueueLocked(() => SendTouchScaledAsync(true, x, y), droppable: true, timed: true, stamped);
            }
            EnqueueLocked(work, droppable: false, timed,
                System.Diagnostics.Stopwatch.GetTimestamp());
        }
        Start();
        _wake.Release();
    }

    /// <summary>
    /// Adds one report to the sequence, making room first if the queue is full.
    /// </summary>
    /// <remarks>
    /// The queue is bounded because an unbounded one was: a send that never
    /// returns stops the drain, and every position the hand produces meanwhile
    /// piles up behind it. When the wire came back the phone was handed several
    /// seconds of a journey the hand had long finished — the delay one
    /// felt, played back rather than caught up.
    /// </remarks>
    private void EnqueueLocked(Func<Task> work, bool droppable, bool timed, long stamped)
    {
        if (_commands.Count >= MaxCommands)
            MakeRoomLocked();
        _commands.Enqueue(new Command(work, droppable, timed, stamped));
    }

    /// <summary>
    /// Throws away the oldest movement in the queue, and nothing else.
    /// </summary>
    /// <remarks>
    /// A queue whose entries are all presses, releases and keystrokes is left to
    /// grow past the bound and counted instead: dropping any of those would
    /// leave the phone holding a state nothing releases. It is also the shape
    /// worth seeing in the journal, because nothing a hand does produces it.
    /// </remarks>
    private void MakeRoomLocked()
    {
        int count = _commands.Count;
        bool room = false;
        for (int i = 0; i < count; i++)
        {
            var command = _commands.Dequeue();
            if (!room && command.Droppable)
            {
                room = true;
                _movesDropped++;
                continue;
            }
            _commands.Enqueue(command);
        }
        if (!room)
            Interlocked.Increment(ref _queueOverflows);
    }

    private void Start()
    {
        if (_pump is not null)
            return;
        lock (_gate)
        {
            _pump ??= Task.Run(TickAsync);
            _waker ??= Task.Run(WakeAsync);
        }
    }

    /// <summary>The fixed cadence: whatever the pending position is, once per tick.</summary>
    private async Task TickAsync()
    {
        // Without this the platform timer idles at 15.6 ms and the pump below
        // runs at 75 Hz however politely it asks for 120.
        using var fineTimer = TimerResolution.Hold();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / SendHz));
        try
        {
            while (await timer.WaitForNextTickAsync(_closing.Token).ConfigureAwait(false))
                await DrainAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>The other way in: a discrete report does not wait for the tick.</summary>
    private async Task WakeAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_closing.Token).ConfigureAwait(false);
                await DrainAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Sends what is waiting, in order, one report at a time.
    /// </summary>
    /// <remarks>
    /// The gate is what makes the two ways in safe: only one drain runs, so the
    /// order the reports were posted in is the order they reach the wire. It is
    /// also what bounds the whole path — a report is never handed to the channel
    /// while another is still being written, so nothing can pile up below us
    /// either.
    ///
    /// <para>Which is why the deadline is here and not somewhere polite. One
    /// send that never returns is one drain that never runs again, and the pump
    /// stops being a pump. Past <see cref="SendPatience"/> the report is
    /// abandoned, counted, and reported through <see cref="Faulted"/> so the
    /// session can build a fresh channel; the abandoned send is left to finish
    /// or not on its own, since there is no way to take a frame back off a
    /// socket.</para>
    /// </remarks>
    private async Task DrainAsync()
    {
        await _drainGate.WaitAsync().ConfigureAwait(false);
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                Func<Task>? work = null;
                bool timed = false;
                long stamped = 0;
                int x = 0, y = 0;
                bool move = false;
                lock (_gate)
                {
                    if (_commands.Count > 0)
                    {
                        var command = _commands.Dequeue();
                        work = command.Work;
                        timed = command.Timed;
                        stamped = command.QueuedTicks;
                    }
                    else if (_hasPendingMove)
                    {
                        _hasPendingMove = false;
                        // Unchanged since the last report: the phone already
                        // knows, and a still finger needs no repeating.
                        if (_pendingX != _lastSentX || _pendingY != _lastSentY)
                        {
                            x = _pendingX;
                            y = _pendingY;
                            _lastSentX = x;
                            _lastSentY = y;
                            stamped = _pendingTicks;
                            timed = true;
                            move = true;
                        }
                    }
                    if (work is null && !move)
                        return;
                }
                Task? send = null;
                try
                {
                    send = work is not null ? work() : SendTouchScaledAsync(true, x, y);
                    await send.WaitAsync(SendPatience).ConfigureAwait(false);
                    if (timed)
                        NoteSent(stamped);
                }
                catch (TimeoutException)
                {
                    // Abandoned, not cancelled: there is no taking a frame back
                    // off a socket. Its failure is swallowed here rather than
                    // left to the finalizer, which would raise it as an
                    // unhandled exception of the process minutes later, in the
                    // middle of a journal that has moved on.
                    Forget(send);
                    Interlocked.Increment(ref _sendTimeouts);
                    Report(new LuminaException(
                        $"canal HID bloque : un rapport n'est pas parti en {SendPatience.TotalMilliseconds:0} ms"));
                }
                catch (Exception exception)
                {
                    Report(exception);
                }
            }
        }
        finally
        {
            _drainGate.Release();
        }
    }

    /// <summary>Looks at an abandoned send's outcome, and does nothing with it.</summary>
    private static void Forget(Task? send) =>
        send?.ContinueWith(static finished => { _ = finished.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Adds one report's wait, from the caller's hand to the wire, to the running figures.</summary>
    private void NoteSent(long queuedTicks)
    {
        long spent = System.Diagnostics.Stopwatch.GetTimestamp() - queuedTicks;
        Interlocked.Add(ref _sendTicks, spent);
        Interlocked.Increment(ref _sendSamples);
        long peak;
        while (spent > (peak = Interlocked.Read(ref _sendMaxTicks)))
            if (Interlocked.CompareExchange(ref _sendMaxTicks, spent, peak) == peak)
                break;
    }

    /// <summary>Keeps the last failure for the journal, and tells whoever asked to be told.</summary>
    private void Report(Exception exception)
    {
        Volatile.Write(ref _lastFault, exception.Message);
        Faulted?.Invoke(exception);
    }

    private Task SendTouchAsync(bool contact, double xNorm, double yNorm) =>
        SendTouchScaledAsync(contact, Scale(xNorm), Scale(yNorm));

    private Task SendTouchScaledAsync(bool contact, int x, int y)
    {
        Interlocked.Increment(ref _reportsSent);
        return Hid.SendReportAsync(Volatile.Read(ref _hid), Hid.SurfaceMainTouchscreen,
            Hid.TouchReport(contact, x, y));
    }

    /// <summary>0..1 to the surface's absolute 0..65535, the phone's own model.</summary>
    private static int Scale(double normalised) => (int)Math.Round(normalised * 65535);
}
