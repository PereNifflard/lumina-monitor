using LuminaMonitor.Core.Media;
using LuminaMonitor.Core.Media.Aac;
// Media has an AudioSpecificConfig of its own — the one the negotiation reads —
// so the codec's is named here, as the probe names it.
using AacConfig = LuminaMonitor.Core.Media.Aac.AudioSpecificConfig;

namespace LuminaMonitor.Core.Audio;

/// <summary>
/// The whole chain, from a datagram off the wire to a sample on its way to a
/// sound card: demultiplex, decode, queue, play.
/// </summary>
/// <remarks>
/// One object for the four steps, and deliberately: it is what lets the probe's
/// <c>audio-play</c> exercise exactly the chain the application runs, from a
/// recorded capture, instead of a second implementation that agrees with it
/// until it stops agreeing. The only thing that differs between the two is which
/// sink is behind it — a Windows endpoint, or nothing at all.
///
/// <para><b>The datagram is the unit.</b> The phone sends one AAC-ELD access
/// unit per RTP packet, 480 samples, ten milliseconds, payload type 101 (measured,
/// docs/AUDIO.md §2). There is no depacketizer to write: a packet is a frame.
/// What there is instead is a sequence number, and the honest answer to a missing
/// one is a frame of silence — not a skip. Skipping shortens the sound by ten
/// milliseconds and every frame after it plays early; silence keeps the
/// timeline.</para>
///
/// <para><b>Allocation.</b> None per frame: the frame buffers belong to the
/// jitter buffer's ring, the access unit is read where it lies, and the decoder
/// allocates nothing of its own. The chain runs on the tunnel's receive path,
/// which must not stop to collect garbage.</para>
/// </remarks>
internal sealed class AudioRenderer : IDisposable
{
    /// <summary>The payload type the phone's audio stream uses.</summary>
    public const int PayloadType = 101;

    /// <summary>
    /// The most silent frames one gap is filled with, before it is counted and
    /// left alone.
    /// </summary>
    /// <remarks>
    /// Two hundred milliseconds. A gap longer than that is not a lost packet, it
    /// is a stream that stopped and started — the phone reopening its encoder,
    /// this side losing the tunnel for a moment — and filling it with two seconds
    /// of silence would only delay everything after it by two seconds.
    /// </remarks>
    private const int MaxSilenceFrames = 20;

    private readonly AacEldDecoder _decoder;
    private readonly AudioJitterBuffer _buffer;
    private readonly IAudioSink _sink;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private int _lastSequence = -1;
    private long _packets, _lost, _outOfOrder, _ignored;

    /// <summary>
    /// Builds the chain: the phone's own codec configuration, a jitter buffer
    /// sized for it, and a sink.
    /// </summary>
    /// <param name="options">Volume, mute, target delay and which endpoint.</param>
    /// <param name="log">Where the chain says what it did; the journal, in French.</param>
    /// <param name="dry">
    /// True for a run that must not make a sound: the frames are pulled on the
    /// same beat and dropped. What the continuous integration uses, and what any
    /// measurement on a machine somebody is working at uses.
    /// </param>
    public AudioRenderer(AudioOptions options, Action<string>? log, bool dry = false)
    {
        _log = log;
        // The configuration the phone advertises, with the frame length the wire
        // showed rather than the one the flag claims: 480, not 512. See
        // AudioSpecificConfig and docs/AAC_ELD_TABLES.md §5.
        var config = AacConfig.Parse(AacProbe.PhoneConfig)
            .WithFrameLength(AacConfig.DefaultFrameLength);
        _decoder = new AacEldDecoder(config);
        _buffer = new AudioJitterBuffer(_decoder.FrameLength * _decoder.ChannelCount,
            _decoder.FrameLength * 1000.0 / config.SamplingFrequency);
        _sink = dry
            ? new DryAudioSink(_buffer, _decoder.FrameLength)
            : new WasapiOutput(_buffer, options.DeviceId, log);
        Apply(options);
        _sink.Start();
    }

    /// <summary>The sink, for a dry run that wants its own fill measurements.</summary>
    public IAudioSink Sink => _sink;

    /// <summary>Volume, mute, target delay and endpoint, all four applied at once.</summary>
    public void Apply(AudioOptions options)
    {
        _buffer.TargetMs = options.ClampedDelayMs;
        _sink.Gain = options.Gain;
        _sink.Use(options.DeviceId);
    }

    /// <summary>
    /// One datagram from the audio port: control packets counted and dropped,
    /// media packets decoded and queued.
    /// </summary>
    /// <remarks>
    /// The same demultiplexing rule as the video path, and for the same reason:
    /// the phone multiplexes RTCP on the media port, and a sender report handed to
    /// a decoder is a frame of noise. Payload types 200 to 206 are control — see
    /// <see cref="RtpPacket.LooksLikeRtcp"/>.
    /// </remarks>
    public void Accept(ReadOnlySpan<byte> datagram)
    {
        if (RtpPacket.LooksLikeRtcp(datagram))
            return;
        var packet = RtpPacket.Parse(datagram);
        if (!packet.Valid)
            return;
        if (packet.PayloadType != PayloadType || packet.Payload.Length == 0)
        {
            lock (_gate) _ignored++;
            return;
        }

        int missing = 0;
        lock (_gate)
        {
            _packets++;
            if (_lastSequence < 0)
            {
                _lastSequence = packet.Sequence;
            }
            else
            {
                int step = (packet.Sequence - _lastSequence) & 0xFFFF;
                if (step == 0 || step >= 0x8000)
                {
                    // A duplicate, or a packet overtaken by a later one. Its
                    // place in the timeline has already been filled with silence:
                    // playing it now would play it in the wrong order.
                    _outOfOrder++;
                    return;
                }
                missing = step - 1;
                _lastSequence = packet.Sequence;
                if (missing > 0)
                {
                    _lost += missing;
                    if (missing > MaxSilenceFrames)
                    {
                        _log?.Invoke($"flux audio : {missing} trames manquantes d'un coup"
                            + $" — {MaxSilenceFrames} trames de silence posees, le reste est un trou assume.");
                        missing = MaxSilenceFrames;
                    }
                }
            }
        }

        for (int frame = 0; frame < missing; frame++)
            Silence();
        Decode(packet.Payload);
    }

    /// <summary>What the chain has counted, without the parts only the session knows.</summary>
    public AudioStats Stats
    {
        get
        {
            long packets, lost, outOfOrder;
            lock (_gate) (packets, lost, outOfOrder) = (_packets, _lost, _outOfOrder);
            return new AudioStats(
                Streaming: false, Failure: _sink.Failure,
                Packets: packets,
                FramesDecoded: _decoder.FramesDecoded,
                FramesFailed: _decoder.FramesFailed,
                FramesLost: lost,
                PacketsOutOfOrder: outOfOrder,
                Underruns: _buffer.Underruns,
                FramesSkipped: _buffer.Skipped,
                QueuedMs: _buffer.QueuedMs,
                TargetMs: _buffer.TargetMs,
                OutputLatencyMs: _sink.LatencyMs,
                Device: _sink.Device,
                Format: _sink.Format,
                SkewMs: double.NaN);
        }
    }

    /// <summary>Datagrams that were neither control nor payload type 101.</summary>
    public long Ignored { get { lock (_gate) return _ignored; } }

    /// <summary>The last frame the decoder refused, for a report that wants to say why.</summary>
    public string? LastDecodeFailure => _decoder.LastFailure?.Message;

    public void Dispose() => _sink.Dispose();

    /// <summary>
    /// One access unit decoded straight into a queue slot.
    /// </summary>
    /// <remarks>
    /// A frame the decoder refuses is queued all the same, and that is deliberate:
    /// what it wrote is the tail of the overlap the good frames before it left —
    /// the decoder drains it rather than zeroing it — so the sound fades instead
    /// of clicking, and the ten milliseconds are still spent, which is what keeps
    /// the timeline straight.
    /// </remarks>
    private void Decode(ReadOnlySpan<byte> accessUnit)
    {
        float[] frame = _buffer.Rent(out int slot);
        _decoder.Decode(accessUnit, frame, out _);
        _buffer.Commit(slot);
    }

    /// <summary>Ten milliseconds of nothing, to hold a lost packet's place.</summary>
    private void Silence()
    {
        float[] frame = _buffer.Rent(out int slot);
        Array.Clear(frame);
        _buffer.Commit(slot);
    }
}
