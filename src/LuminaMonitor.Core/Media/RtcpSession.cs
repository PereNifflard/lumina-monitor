using System.Buffers.Binary;
using System.Diagnostics;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// The receiver half of RTCP: the reports that keep the phone encoding.
/// </summary>
/// <remarks>
/// The phone sends a sender report every second on the media port itself
/// (RTP and RTCP are multiplexed) and expects receiver reports back. It is not
/// politeness: <c>RTCPTimeoutInterval = 20</c>, and a stream nobody reports on
/// stops after twenty seconds — measured, twice, before this class existed.
/// So a report goes out every second, as a proper compound packet (RR then
/// SDES with a CNAME, RFC 3550 §6.1), addressed to the port the sender reports
/// come from, falling back to the <c>SourcePort</c> the daemon named in its
/// answer.
///
/// <para>The same channel carries the only way to ask for a fresh picture:
/// this stream is encoded with <c>KeyFrameInterval = 0</c>, one IDR at the very
/// start and never another, so after a lost packet the decoder has nothing to
/// resynchronise on until a Picture Loss Indication (RFC 4585 §6.3.1) asks for
/// one.</para>
/// </remarks>
internal sealed class RtcpSession
{
    /// <summary>
    /// The clock the jitter is reported in, in hertz: this stream's own RTP
    /// clock, measured at 24 kHz — 400 timestamp units per picture at sixty a
    /// second — and not the 90 kHz H.264 usually carries.
    /// </summary>
    /// <remarks>
    /// The figure only ever appears in the receiver report, where it converts
    /// arrival times into timestamp units so the phone can compare the two. Sent
    /// in the wrong clock the jitter is off by the ratio of the two, and the
    /// encoder is being told about a network that does not exist.
    ///
    /// <para>The audio stream runs on a different clock — its own timestamps
    /// advance at the sample rate, 48 kHz — so the figure is a constructor
    /// parameter rather than a constant, defaulting to the video's.</para>
    /// </remarks>
    public const int VideoJitterClock = 24_000;

    /// <summary>The audio stream's RTP clock: 48 kHz, one unit per sample.</summary>
    public const int AudioJitterClock = 48_000;

    private readonly int _jitterClock;

    /// <summary>NTP counts from 1900, not 1970.</summary>
    private static readonly DateTime NtpEpoch = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Func<ushort, ReadOnlyMemory<byte>, Task> _send;
    private readonly object _gate = new();
    private readonly uint _localSsrc;
    private readonly uint _remoteSsrc;
    private readonly ushort _configuredPort;

    private ushort _observedPort;
    private int _baseSequence = -1;
    private int _lastSequence = -1;
    private int _cycles;
    private long _received;
    private long _expectedPrior;
    private long _receivedPrior;
    private uint _lastSenderNtp;
    private long _lastSenderReportTicks;
    private double _jitter;
    private uint _lastTransitTimestamp;
    private long _lastTransitTicks;
    private bool _haveTransit;
    private RctlFeedback? _rctl;

    public RtcpSession(uint localSsrc, uint remoteSsrc, ushort sourcePort, Func<ushort, ReadOnlyMemory<byte>, Task> send,
        int jitterClock = VideoJitterClock)
    {
        _localSsrc = localSsrc;
        _remoteSsrc = remoteSsrc;
        _configuredPort = sourcePort;
        _send = send;
        _jitterClock = jitterClock;
    }

    public long ReportsSent { get; private set; }
    public long SenderReports { get; private set; }
    public long KeyFrameRequests { get; private set; }
    public long ByesSent { get; private set; }
    public long Lost { get; private set; }

    /// <summary>
    /// How long a picture takes to reach us after the phone timestamped it, in
    /// milliseconds, or NaN until a sender report has been seen.
    /// </summary>
    /// <remarks>
    /// The drift can only see the delay <i>change</i>; this sees the delay
    /// itself, and it is the sender report that makes it possible. RFC 3550
    /// §6.4.1 has the sender publish a pair — its own wall clock as an NTP
    /// timestamp, and the media timestamp that instant corresponds to — which
    /// is exactly the conversion the receiver otherwise lacks. Given the pair,
    /// the phone's clock at which any RTP timestamp was stamped is arithmetic,
    /// and comparing it with the moment the packet reached us is the
    /// end-to-end delay of everything between the two.
    ///
    /// <para>It measures the transport and whatever the phone does after it
    /// stamps a picture, and nothing before: if the phone stamps at capture,
    /// this is the whole delay; if it stamps as it sends, the capture and
    /// encode queue sit outside it. The two clocks are compared directly, so
    /// the figure carries the offset between the phone's NTP-synchronised
    /// clock and this PC's — the reason <c>clock-test</c> also prints what a
    /// clock painted on the phone's own screen says.</para>
    /// </remarks>
    public double PipelineMs { get; private set; } = double.NaN;

    /// <summary>The wall clock the phone published in its last sender report, or null.</summary>
    public DateTime? SenderClock { get; private set; }

    /// <summary>Where the reports go: where the sender reports came from, or what the daemon said.</summary>
    public ushort Destination => _observedPort != 0 ? _observedPort : _configuredPort;

    public bool Usable => _localSsrc != 0 && _remoteSsrc != 0 && Destination != 0;

    /// <summary>Receipts and reports the RCTL loop has sent, or zeroes when it is off.</summary>
    public (long Receipts, long Reports) RctlCounts
    {
        get { lock (_gate) return _rctl is null ? (0, 0) : (_rctl.Receipts, _rctl.Reports); }
    }

    /// <summary>
    /// Turns on the AVConference receiver-feedback loop, off by default.
    /// </summary>
    /// <remarks>
    /// Nothing else in this class changes: the receiver reports still go out
    /// every second and a PLI is still what asks for a fresh picture. The loop
    /// only adds the two APP packets described in <see cref="RctlFeedback"/>,
    /// and it is opt-in precisely because it is reversed rather than
    /// documented.
    /// </remarks>
    public void EnableRctl(int maxBitrateKbps, RctlArrivalClock arrival = RctlArrivalClock.MediaClock)
    {
        lock (_gate) _rctl = new RctlFeedback(_localSsrc, maxBitrateKbps, arrival);
    }

    /// <summary>Sends one periodic RCTL report; called twenty times a second while the loop is on.</summary>
    public Task SendRctlReportAsync()
    {
        byte[] report;
        lock (_gate)
        {
            if (_rctl is not { Ready: true } feedback)
                return Task.CompletedTask;
            report = feedback.BuildReport();
        }
        return _send(Destination, report);
    }

    /// <summary>Counts one media packet: sequence numbers for the loss figures, arrival for the jitter.</summary>
    /// <param name="marker">The RTP marker bit: the last packet of a picture, which is where a receipt goes out.</param>
    public void OnRtp(ushort sequence, uint timestamp, bool marker, long arrivalTicks)
    {
        byte[]? receipt;
        lock (_gate)
        {
            if (_baseSequence < 0)
            {
                _baseSequence = _lastSequence = sequence;
            }
            else
            {
                int step = (sequence - _lastSequence) & 0xFFFF;
                if (step < 0x8000)
                {
                    if (sequence < _lastSequence) _cycles++;
                    _lastSequence = sequence;
                }
            }
            _received++;

            // RFC 3550 §6.4.1 jitter, in RTP units: the smoothed difference
            // between the packet spacing on the wire and in the timestamps.
            if (_haveTransit)
            {
                double elapsed = (arrivalTicks - _lastTransitTicks) * (double)_jitterClock / Stopwatch.Frequency;
                double difference = Math.Abs(elapsed - (int)(timestamp - _lastTransitTimestamp));
                _jitter += (difference - _jitter) / 16.0;
            }
            _lastTransitTimestamp = timestamp;
            _lastTransitTicks = arrivalTicks;
            _haveTransit = true;

            receipt = _rctl?.OnPacket(timestamp, marker);
        }

        // Sent here rather than handed to the periodic loop, and started
        // straight away rather than queued behind a scheduler: the reference
        // implementation found that a receipt reaching the phone after it has
        // aged the picture out of its sent-history window makes it credit one
        // packet out of eight, read the difference as uplink loss, and turn
        // defensive. The send itself is not awaited — this runs on the tunnel's
        // receive path, which must not stop to write.
        if (receipt is not null)
            Observe(_send(Destination, receipt));
    }

    /// <summary>Lets a fire-and-forget send fail quietly rather than on the finaliser thread.</summary>
    private static void Observe(Task task)
    {
        if (task.IsCompletedSuccessfully)
            return;
        _ = task.ContinueWith(static faulted => _ = faulted.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>Reads a control packet: only the sender reports matter, for their NTP echo.</summary>
    public void OnRtcp(ReadOnlySpan<byte> datagram, ushort sourcePort)
    {
        if (datagram.Length < 8)
            return;
        _observedPort = sourcePort;
        int at = 0;
        while (at + 4 <= datagram.Length)
        {
            int type = datagram[at + 1];
            int length = 4 + 4 * BinaryPrimitives.ReadUInt16BigEndian(datagram[(at + 2)..]);
            if (length <= 0 || at + length > datagram.Length)
                break;
            if (type == 200 && length >= 28)
            {
                lock (_gate)
                {
                    SenderReports++;
                    _lastSenderNtp = BinaryPrimitives.ReadUInt32BigEndian(datagram[(at + 10)..]);   // middle 32 bits
                    _lastSenderReportTicks = Stopwatch.GetTimestamp();

                    // The pair the report exists for: the phone's own wall
                    // clock, and the media timestamp that instant was. NTP
                    // counts seconds from 1900 in the high word and 1/2^32 of a
                    // second in the low one.
                    ulong seconds = BinaryPrimitives.ReadUInt32BigEndian(datagram[(at + 8)..]);
                    ulong fraction = BinaryPrimitives.ReadUInt32BigEndian(datagram[(at + 12)..]);
                    uint senderTimestamp = BinaryPrimitives.ReadUInt32BigEndian(datagram[(at + 16)..]);
                    if (seconds > 0 && _haveTransit)
                    {
                        var sent = NtpEpoch.AddSeconds(seconds + fraction / 4294967296.0);
                        SenderClock = sent;
                        // The phone's clock when it stamped the newest picture
                        // we hold, against the moment that picture reached us.
                        double media = unchecked((int)(_lastTransitTimestamp - senderTimestamp)) * 1000.0 / _jitterClock;
                        double waited = (Stopwatch.GetTimestamp() - _lastTransitTicks) * 1000.0 / Stopwatch.Frequency;
                        PipelineMs = (DateTime.UtcNow - sent).TotalMilliseconds - media - waited;
                    }
                }
            }
            at += length;
        }
    }

    /// <summary>Sends one receiver report; called once a second by the media session.</summary>
    public Task ReportAsync()
    {
        byte[] packet;
        lock (_gate)
        {
            if (_baseSequence < 0)
                return Task.CompletedTask;
            packet = BuildReport();
            ReportsSent++;
        }
        return _send(Destination, packet);
    }

    /// <summary>Asks the encoder for a fresh key frame (PLI, payload type 206, format 1).</summary>
    public Task RequestKeyFrameAsync()
    {
        byte[] pli = new byte[12];
        pli[0] = 0x81;
        pli[1] = 206;
        BinaryPrimitives.WriteUInt16BigEndian(pli.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(pli.AsSpan(4), _localSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(pli.AsSpan(8), _remoteSsrc);
        lock (_gate) KeyFrameRequests++;
        return _send(Destination, pli);
    }

    /// <summary>
    /// Says we are leaving (BYE, payload type 203, RFC 3550 §6.6).
    /// </summary>
    /// <remarks>
    /// Sent before <c>stopmediastream</c>, not after: the daemon should learn
    /// that the receiver is gone from the receiver itself, while the session it
    /// belongs to still exists. A stream whose receiver simply stops answering
    /// is left to the twenty-second timeout instead, and the working hypothesis
    /// for a display service that freezes after a few sessions is exactly that
    /// pile of half-closed sessions.
    /// </remarks>
    public Task SendByeAsync()
    {
        byte[] bye = new byte[8];
        bye[0] = 0x81;                                      // version 2, one source
        bye[1] = 203;                                       // BYE
        BinaryPrimitives.WriteUInt16BigEndian(bye.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bye.AsSpan(4), _localSsrc);
        lock (_gate) ByesSent++;
        return _send(Destination, bye);
    }

    // --- Packet building ---------------------------------------------------------

    /// <summary>RR with one report block, then an SDES chunk with an empty CNAME, as Xcode sends.</summary>
    private byte[] BuildReport()
    {
        long extendedHighest = ((long)_cycles << 16) | (uint)_lastSequence;
        long expected = extendedHighest - _baseSequence + 1;
        long lost = Math.Clamp(expected - _received, 0, 0x7FFFFF);
        Lost = lost;

        long expectedInterval = expected - _expectedPrior;
        long receivedInterval = _received - _receivedPrior;
        _expectedPrior = expected;
        _receivedPrior = _received;
        long lostInterval = expectedInterval - receivedInterval;
        byte fraction = expectedInterval <= 0 || lostInterval <= 0
            ? (byte)0
            : (byte)((lostInterval << 8) / expectedInterval);

        uint delay = _lastSenderReportTicks == 0
            ? 0
            : (uint)((Stopwatch.GetTimestamp() - _lastSenderReportTicks) * 65536 / Stopwatch.Frequency);

        byte[] packet = new byte[32 + 12];
        var report = packet.AsSpan(0, 32);
        report[0] = 0x81;                                   // version 2, one report block
        report[1] = 201;                                    // RR
        BinaryPrimitives.WriteUInt16BigEndian(report[2..], 7);
        BinaryPrimitives.WriteUInt32BigEndian(report[4..], _localSsrc);
        BinaryPrimitives.WriteUInt32BigEndian(report[8..], _remoteSsrc);
        report[12] = fraction;
        report[13] = (byte)(lost >> 16); report[14] = (byte)(lost >> 8); report[15] = (byte)lost;
        BinaryPrimitives.WriteUInt32BigEndian(report[16..], (uint)extendedHighest);
        BinaryPrimitives.WriteUInt32BigEndian(report[20..], (uint)_jitter);
        BinaryPrimitives.WriteUInt32BigEndian(report[24..], _lastSenderNtp);
        BinaryPrimitives.WriteUInt32BigEndian(report[28..], delay);

        var sdes = packet.AsSpan(32);
        sdes[0] = 0x81;                                     // version 2, one chunk
        sdes[1] = 202;                                      // SDES
        BinaryPrimitives.WriteUInt16BigEndian(sdes[2..], 2);
        BinaryPrimitives.WriteUInt32BigEndian(sdes[4..], _localSsrc);
        sdes[8] = 1;                                        // CNAME, empty, then the end marker
        return packet;
    }
}

/// <summary>
/// The receiver feedback Apple's own mirror sends, and the phone listens to.
/// </summary>
/// <remarks>
/// Above the receiver reports of RFC 3550 there is a second, private channel:
/// two RTCP application packets (payload type 204, APP) that Xcode's mirror
/// streams continuously and that AVConference's rate controller reads. It is
/// the loop the reference implementation used to stop the phone throttling its
/// own framerate and collapsing its resolution under motion, and the only
/// bitrate lever it confirmed the phone honours. None of it is documented: the
/// layout below was decoded field by field from a live capture of Xcode's
/// mirror, and this class reproduces those bytes.
///
/// <para><b>The receipt</b>, sixteen bytes, one per picture, sent the instant
/// the marker bit arrives:</para>
/// <code>
/// offset 0   u8    0x80        version 2, no padding, subtype 0
/// offset 1   u8    204         APP
/// offset 2   u16   3           length in words minus one -> 16 bytes
/// offset 4   u32   SSRC        ours
/// offset 8   u32   5           where a name normally sits: four bytes, value 5,
///                              not ASCII. Verbatim from the capture.
/// offset 12  u32   RTP ts      the timestamp of the picture being acknowledged
/// </code>
/// <para>Exactly one per picture is the point: the phone maps the timestamp
/// back to the span of packets it sent for that picture and credits them. A
/// receipt on a timer instead of on the frame boundary desynchronises from the
/// real cadence and the phone reads the difference as congestion.</para>
///
/// <para><b>The report</b>, thirty-two bytes, twenty times a second:</para>
/// <code>
/// offset 0   u8    0x80
/// offset 1   u8    204         APP
/// offset 2   u16   7           -> 32 bytes
/// offset 4   u32   SSRC        ours
/// offset 8   4s    "RCTL"      this one is a real ASCII name
/// offset 12  u32   0x85000004  fixed tag
/// offset 16  u32   w2 = ((RTP ts >> 8) &amp; 0xFFFF) &lt;&lt; 16
/// offset 20  u32   w3 = packets in the picture just completed
/// offset 24  u32   w4 = (arrival clock &lt;&lt; 16) | interarrival jitter
/// offset 28  u32   w5 = (packets received &lt;&lt; 16) | max bitrate in kbit/s
/// </code>
/// <para>Everything is big-endian, as RTCP always is.</para>
///
/// <para><b>What is certain and what is not.</b> The two layouts, the constants
/// (0x85000004, the name 5, the "RCTL" name) and the cadences are certain: they
/// are the capture. The meaning of the four words is the reference's reading of
/// it, and two of them carry a caveat.</para>
/// <list type="bullet">
/// <item><description><b>w4, the arrival clock.</b> The phone derives a one-way
/// delay from it, so it has to be on the same base as the timestamp it is
/// compared against, not a clock of our own: reported here as the picture's own
/// RTP timestamp divided by 24, which makes the delay come out at roughly zero.
/// Truthful on a tunnel whose round trip is a millisecond, and the reference
/// found that reporting anything else produced a phantom congestion signal and
/// a throttled framerate. The jitter half is sent as zero, as the reference
/// does.</description></item>
/// <item><description><b>w5's low half, the bitrate.</b> The capture holds
/// 0xEA61, 60001, next to a reference implementation whose bitrate option
/// defaults to 60000 kbit/s — which is what identifies the field, and the
/// reference notes the phone honours the cap. But its own code sends the
/// captured constant rather than its option, so the pairing has never actually
/// been exercised at another value. <see cref="CapturedMaxBitrateKbps"/> is
/// therefore the default here, which keeps the packet byte-identical to the
/// capture, and any other value is an experiment.</description></item>
/// </list>
/// </remarks>
internal sealed class RctlFeedback
{
    /// <summary>The value the captured report carries, 0xEA61 — sending it keeps the bytes Xcode's.</summary>
    public const int CapturedMaxBitrateKbps = 60001;

    /// <summary>The report's own cadence, in hertz: 407 reports over 20 s in the capture.</summary>
    public const double ReportHz = 20.0;

    /// <summary>The RTP clock of this stream, for the arrival half of w4.</summary>
    private const uint MediaClockKhz = 24;

    private const uint Tag = 0x85000004;
    private const uint ReceiptName = 5;

    private readonly uint _ssrc;
    private readonly uint _maxBitrateKbps;
    private readonly RctlArrivalClock _arrival;

    private uint _lastTimestamp;
    private long _packets;
    private int _packetsThisFrame;
    private int _packetsLastFrame;
    private long _firstPacketTicks;

    public RctlFeedback(uint ssrc, int maxBitrateKbps, RctlArrivalClock arrival = RctlArrivalClock.MediaClock)
    {
        _ssrc = ssrc;
        _maxBitrateKbps = (uint)Math.Clamp(maxBitrateKbps, 1, 0xFFFF);
        _arrival = arrival;
    }

    /// <summary>How many per-picture receipts have gone out.</summary>
    public long Receipts { get; private set; }

    /// <summary>How many periodic reports have gone out.</summary>
    public long Reports { get; private set; }

    /// <summary>False until the first packet: there is nothing truthful to report before it.</summary>
    public bool Ready => _packets > 0;

    /// <summary>
    /// Counts one media packet and, on the last packet of a picture, returns
    /// the receipt that acknowledges it.
    /// </summary>
    public byte[]? OnPacket(uint timestamp, bool marker)
    {
        _lastTimestamp = timestamp;
        if (_firstPacketTicks == 0) _firstPacketTicks = Stopwatch.GetTimestamp();
        _packets++;
        _packetsThisFrame++;
        if (!marker)
            return null;
        _packetsLastFrame = _packetsThisFrame;
        _packetsThisFrame = 0;
        Receipts++;
        return BuildReceipt();
    }

    /// <summary>The sixteen-byte per-picture receipt.</summary>
    public byte[] BuildReceipt()
    {
        byte[] packet = new byte[16];
        packet[0] = 0x80;
        packet[1] = 204;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 3);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), _ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ReceiptName);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), _lastTimestamp);
        return packet;
    }

    /// <summary>The thirty-two-byte periodic report.</summary>
    public byte[] BuildReport()
    {
        uint timestamp = _lastTimestamp;
        uint w2 = ((timestamp >> 8) & 0xFFFF) << 16;
        uint w3 = (uint)_packetsLastFrame;
        // The arrival half of w4: the media's own base by default, so the phone
        // computes a one-way delay of about zero, which is the truth on a
        // tunnel whose round trip is a millisecond. WallClock reproduces the
        // reference's earlier reading — a clock of our own, a different origin
        // from the timestamps it is compared against — and exists only so the
        // difference can be measured rather than argued about.
        uint arrival = _arrival == RctlArrivalClock.MediaClock
            ? (timestamp / MediaClockKhz) & 0xFFFF
            : (uint)((Stopwatch.GetTimestamp() - _firstPacketTicks) * 1000 / Stopwatch.Frequency) & 0xFFFF;
        uint w4 = arrival << 16;                                            // jitter half left at zero
        uint w5 = (((uint)_packets & 0xFFFF) << 16) | _maxBitrateKbps;

        byte[] packet = new byte[32];
        packet[0] = 0x80;
        packet[1] = 204;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), _ssrc);
        packet[8] = (byte)'R'; packet[9] = (byte)'C'; packet[10] = (byte)'T'; packet[11] = (byte)'L';
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), Tag);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), w2);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(20), w3);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(24), w4);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(28), w5);
        Reports++;
        return packet;
    }
}
