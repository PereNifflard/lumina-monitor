namespace LuminaMonitor.Core.Audio;

/// <summary>
/// The queue between a stream that arrives in ten-millisecond lumps and a sound
/// card that asks for samples on its own beat.
/// </summary>
/// <remarks>
/// Two clocks that nobody synchronised: the phone's, which produces one frame
/// every ten milliseconds, and the endpoint's, which asks for a period's worth
/// whenever it feels like it. Handing one straight to the other gives a click on
/// every mismatch, so the frames wait here, and the whole design is about how
/// long they wait.
///
/// <para><b>The target.</b> The queue is primed to <see cref="TargetMs"/> before
/// a single sample is played, and that fill is what the delay setting really
/// sets. Below it the output would run dry on the first hiccup; far above it the
/// sound arrives late for no reason.</para>
///
/// <para><b>Drift.</b> The two clocks are not the same clock, so over minutes
/// one of them wins. A queue that keeps growing is answered by dropping its
/// oldest frames once it is <see cref="Slack"/> frames past the target — thirty
/// milliseconds of margin, then back down to the target in one cut — and a queue that keeps
/// emptying is answered by silence and a counter. Neither is hidden: both are in
/// <see cref="AudioStats"/>, because an unexplained tick every few minutes is
/// exactly the kind of fault that gets blamed on the decoder.</para>
///
/// <para><b>Allocation.</b> None, ever, after construction: the frames live in a
/// fixed ring of buffers, handed out and handed back. The producer is the
/// tunnel's receive path and the consumer is the output thread, so both sides are
/// under one lock — a hundred operations a second on each side, where the cost
/// of a lock does not exist.</para>
/// </remarks>
internal sealed class AudioJitterBuffer
{
    /// <summary>How many frames past the target the queue may grow before the oldest is dropped.</summary>
    public const int Slack = 3;

    /// <summary>Frames the ring holds. 64 at ten milliseconds each is 640 ms, well past any target.</summary>
    private const int SlotCount = 64;

    private readonly int _samplesPerFrame;
    private readonly double _frameMs;
    private readonly float[][] _slots;
    private readonly Queue<int> _ready = new(SlotCount);
    private readonly Queue<int> _free = new(SlotCount);
    private readonly object _gate = new();

    /// <summary>How many samples of the frame at the head of the queue have been played.</summary>
    private int _readAt;
    private int _target = 5;
    private bool _priming = true;
    private long _underruns, _skipped, _played;

    public AudioJitterBuffer(int samplesPerFrame, double frameMs)
    {
        _samplesPerFrame = samplesPerFrame;
        _frameMs = frameMs;
        _slots = new float[SlotCount][];
        for (int slot = 0; slot < SlotCount; slot++)
        {
            _slots[slot] = new float[samplesPerFrame];
            _free.Enqueue(slot);
        }
    }

    /// <summary>How long one frame lasts, in milliseconds.</summary>
    public double FrameMs => _frameMs;

    /// <summary>The target fill, in milliseconds; whole frames, at least one.</summary>
    public double TargetMs
    {
        get { lock (_gate) return _target * _frameMs; }
        set
        {
            lock (_gate)
                _target = Math.Clamp((int)Math.Round(value / _frameMs), 1, SlotCount - Slack - 1);
        }
    }

    /// <summary>How much sound is waiting, in milliseconds.</summary>
    public double QueuedMs
    {
        get
        {
            lock (_gate)
                return Math.Max(0, ((_ready.Count * _samplesPerFrame) - _readAt) * _frameMs / _samplesPerFrame);
        }
    }

    /// <summary>Times the output asked for samples that were not there.</summary>
    public long Underruns { get { lock (_gate) return _underruns; } }

    /// <summary>Frames thrown away because the queue had drifted past its target.</summary>
    public long Skipped { get { lock (_gate) return _skipped; } }

    /// <summary>Frames handed to the output, whole.</summary>
    public long Played { get { lock (_gate) return _played; } }

    /// <summary>
    /// A frame buffer to write into, and the slot to hand back to
    /// <see cref="Commit"/>.
    /// </summary>
    /// <remarks>
    /// The buffer is the producer's alone until it commits: taken out of the free
    /// list and not yet in the ready queue, it is invisible to the output thread,
    /// so the decoder writes into it outside the lock.
    /// </remarks>
    public float[] Rent(out int slot)
    {
        lock (_gate)
        {
            // Over target by the whole margin: back down to the target in one
            // go, oldest frames first — they are the ones whose absence costs
            // the least, being the furthest in the past. One go, not one frame
            // per arrival: the first version trimmed to target-plus-margin and
            // then dropped one frame for every frame the burst still brought,
            // which is one audible cut per frame for as long as the burst lasts
            // (fifteen in ten seconds around a screen lock, 11 September 2026).
            // A burst is the phone catching up after a pause of its own — the
            // volume HUD, a lock — and the catching-up is done once.
            if (_ready.Count > _target + Slack || _free.Count == 0)
            {
                while (_ready.Count > _target || _free.Count == 0)
                {
                    if (_ready.Count == 0)
                        break;
                    _free.Enqueue(_ready.Dequeue());
                    _readAt = 0;
                    _skipped++;
                }
            }
            slot = _free.Count > 0 ? _free.Dequeue() : _ready.Dequeue();
            return _slots[slot];
        }
    }

    /// <summary>Puts a rented frame in the queue, at the back.</summary>
    public void Commit(int slot)
    {
        lock (_gate) _ready.Enqueue(slot);
    }

    /// <summary>A rented frame given back unused; nothing is played from it.</summary>
    public void Return(int slot)
    {
        lock (_gate) _free.Enqueue(slot);
    }

    /// <summary>
    /// Fills the destination entirely — with sound where there is sound, with
    /// silence for the rest — and applies the gain on the way.
    /// </summary>
    /// <remarks>
    /// Always fills. An output buffer handed back part written plays whatever the
    /// driver left in it, which is the previous period again: a stutter loud
    /// enough to be mistaken for a decoder fault. Silence is the honest answer
    /// and the counter says how often it was needed.
    ///
    /// <para>Running dry also puts the queue back to priming, so the cushion is
    /// rebuilt before playing resumes. That trades one longer gap for a series of
    /// short ones, which is what a listener prefers and what keeps the underrun
    /// count meaningful — one per event, not one per period.</para>
    /// </remarks>
    public void Fill(Span<float> destination, float gain)
    {
        int written = 0;
        lock (_gate)
        {
            if (_priming && _ready.Count < _target)
            {
                destination.Clear();
                return;
            }
            _priming = false;

            while (written < destination.Length && _ready.Count > 0)
            {
                var frame = _slots[_ready.Peek()].AsSpan(_readAt);
                int take = Math.Min(frame.Length, destination.Length - written);
                frame[..take].CopyTo(destination.Slice(written, take));
                written += take;
                _readAt += take;
                if (_readAt == _samplesPerFrame)
                {
                    _readAt = 0;
                    _free.Enqueue(_ready.Dequeue());
                    _played++;
                }
            }

            if (written < destination.Length)
            {
                destination[written..].Clear();
                _underruns++;
                _priming = true;
            }
        }

        if (gain == 1f)
            return;
        var sound = destination[..written];
        for (int at = 0; at < sound.Length; at++)
            sound[at] *= gain;
    }

    /// <summary>Empties the queue and starts priming again, for a stream that starts over.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            while (_ready.Count > 0)
                _free.Enqueue(_ready.Dequeue());
            _readAt = 0;
            _priming = true;
        }
    }
}
