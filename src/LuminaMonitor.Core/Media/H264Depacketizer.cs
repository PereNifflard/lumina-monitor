namespace LuminaMonitor.Core.Media;

/// <summary>One decodable picture, in Annex B form, with what is known about it.</summary>
/// <remarks>
/// Two arrival stamps, not one: <paramref name="FirstArrivalTicks"/> is when the
/// first RTP packet of the picture landed and <paramref name="ArrivalTicks"/>
/// when the last one did. The gap between them is how long the picture spent on
/// the wire, which is the one part of the delay this side cannot shorten — worth
/// separating from the part it can.
/// </remarks>
internal readonly record struct AccessUnit(
    byte[] Data, uint Timestamp, bool KeyFrame, long FirstArrivalTicks, long ArrivalTicks, int Packets, int Nals);

/// <summary>
/// Turns the phone's RTP payloads into H.264 access units (RFC 6184).
/// </summary>
/// <remarks>
/// Three payload shapes appear on this stream: a whole NAL unit (types 1-23),
/// a STAP-A aggregate (24) and, for anything larger than the tunnel MTU, a
/// FU-A fragment (28). Fragments are reassembled by arrival order, an access
/// unit ends on the marker bit — or on a timestamp change, which is the same
/// boundary seen a packet late — and the result is Annex B, four-byte start
/// codes, because that is what the Media Foundation decoder eats.
///
/// <para>Two habits of this particular sender shape the code. It never sends a
/// parameter set in band: SPS and PPS arrive once, in the sample description of
/// the very first packet (see <see cref="ReadDescription"/>), and since
/// <c>KeyFrameInterval = 0</c> means a single IDR for the whole session, they
/// are kept and re-injected in front of every IDR — a decoder that joins late,
/// or one restarted after a stream change, still gets a self-contained first
/// picture. And every coded
/// slice carries a fixed proprietary trailer past the end of the slice data —
/// see <see cref="Trailers"/> — which is stripped, as Apple's own receiver
/// does, rather than handed to the decoder as trailing garbage.</para>
///
/// <para>A lost packet poisons the access unit it belonged to: it is dropped
/// whole and <see cref="KeyFrameWanted"/> goes up, for the caller to turn into
/// an RTCP request.</para>
/// </remarks>
internal sealed class H264Depacketizer
{
    /// <summary>
    /// The proprietary suffixes seen after the slice data, longest first.
    /// The 14-byte one is HEVC's (pymobiledevice3 reports it); the 10-byte one
    /// was measured on this project's own H.264 captures, identical on every
    /// coded slice of every session.
    /// </summary>
    private static readonly byte[][] KnownTrailers =
    [
        Convert.FromHexString("04F00AC0000003000004EC0AB003"),
        Convert.FromHexString("000003000005280B3402"),
    ];

    private readonly List<byte> _au = new(256 * 1024);
    private readonly List<byte> _fragment = new(256 * 1024);
    private byte[]? _sps;
    private byte[]? _pps;
    private int _lastSequence = -1;
    private uint _timestamp;
    private long _arrivalTicks;
    private long _firstArrivalTicks;
    private int _packets;
    private int _nals;
    private bool _hasIdr;
    private bool _auHasSps;
    private bool _auHasPps;
    private bool _poisoned = true;      // nothing decodable until the first IDR
    private bool _fragmenting;

    /// <summary>Every complete access unit, in arrival order.</summary>
    public Action<AccessUnit>? Completed { get; set; }

    public long AccessUnits { get; private set; }
    public long KeyFrames { get; private set; }
    public long Lost { get; private set; }
    public long Dropped { get; private set; }
    public long Trailers { get; private set; }
    public long Descriptions { get; private set; }
    public long KeyFrameWanted { get; private set; }
    public bool HaveParameterSets => _sps is not null && _pps is not null;

    /// <summary>What the phone's own sequence parameter set said, if one has been read.</summary>
    public SpsInfo? Original { get; private set; }

    /// <summary>What the set handed to the decoder says: the same, plus the reorder restriction.</summary>
    public SpsInfo? Restricted { get; private set; }

    /// <summary>Sets that could not be parsed and went through as they came. Expected: zero.</summary>
    public long SpsRewriteFailures { get; private set; }

    /// <summary>Forgets the partial work, keeps the parameter sets: after a decoder restart.</summary>
    public void Reset()
    {
        _au.Clear();
        _fragment.Clear();
        _fragmenting = false;
        _hasIdr = _auHasSps = _auHasPps = false;
        _poisoned = true;
        _packets = _nals = 0;
    }

    /// <summary>
    /// Forgets the stream's sequence numbers as well: the same capture, played
    /// again from the top.
    /// </summary>
    /// <remarks>
    /// <see cref="Reset"/> deliberately keeps <c>_lastSequence</c>, because a
    /// decoder restarted in the middle of a live stream is still listening to
    /// the same numbering and a packet from before the restart is a duplicate.
    /// A replay is the opposite case: the numbering itself starts again, so
    /// every packet of the new turn looks like one that has already been seen
    /// and is dropped as reordered — silently, for ever. Only the replay source
    /// calls this.
    /// </remarks>
    public void Rewind()
    {
        Reset();
        _lastSequence = -1;
    }

    public void Add(in RtpPacket packet, long arrivalTicks)
    {
        if (!packet.Valid || packet.IsRtcp || packet.Payload.Length < 1)
            return;

        if (_lastSequence >= 0)
        {
            int step = (packet.Sequence - _lastSequence) & 0xFFFF;
            if (step is 0 or >= 0x8000)
                return;                                 // duplicate or reordered: nothing safe to do
            if (step > 1)
            {
                Lost += step - 1;
                Poison();
            }
        }
        _lastSequence = packet.Sequence;

        if (_packets > 0 && packet.Timestamp != _timestamp)
            Flush();
        if (_packets == 0)
            _firstArrivalTicks = arrivalTicks;
        _timestamp = packet.Timestamp;
        _arrivalTicks = arrivalTicks;
        _packets++;

        var payload = packet.Payload;
        if ((payload[0] & 0x80) != 0)
        {
            // The forbidden_zero_bit is set, so this is not a NAL unit at all:
            // it is the sample description the phone sends first, and the only
            // place SPS and PPS ever appear on this stream.
            ReadDescription(payload);
            return;
        }
        int type = payload[0] & 0x1F;
        switch (type)
        {
            case 28: Fragment(payload); break;
            case 24: Aggregate(payload); break;
            case >= 1 and <= 23: Append(payload); break;
            default: break;                             // 25-27 and 29 are not sent by this phone
        }

        if (packet.Marker)
            Flush();
    }

    // --- Payload shapes ----------------------------------------------------------

    /// <summary>FU-A (RFC 6184 §5.8): indicator, header, then the slice bytes.</summary>
    private void Fragment(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3)
            return;
        byte header = payload[1];
        bool start = (header & 0x80) != 0;
        bool end = (header & 0x40) != 0;
        if (start)
        {
            _fragment.Clear();
            _fragment.Add((byte)((payload[0] & 0xE0) | (header & 0x1F)));
            _fragmenting = true;
        }
        else if (!_fragmenting)
        {
            return;                                     // a fragment whose start we missed
        }
        _fragment.AddRange(payload[2..]);
        if (!end)
            return;
        _fragmenting = false;
        Append(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_fragment));
        _fragment.Clear();
    }

    /// <summary>
    /// The sample description, and the parameter sets hidden in it.
    /// </summary>
    /// <remarks>
    /// The first packet of the stream is not RTP/H.264 at all: it is an ISO
    /// base media <c>avc1</c> sample entry — the same structure an MP4 track
    /// header carries — sent as an RTP payload with the forbidden bit set so
    /// nothing mistakes it for a NAL unit. Inside it, an <c>avcC</c> box holds
    /// the SPS and the PPS. They are sent nowhere else: this stream never puts
    /// a parameter set in band, so a receiver that ignores this packet has an
    /// IDR it cannot decode. The picture size lives in the same entry, sixteen
    /// bytes before the box (1328x2896 on this phone), and is read for the log.
    /// </remarks>
    private void ReadDescription(ReadOnlySpan<byte> payload)
    {
        Descriptions++;
        int at = payload.IndexOf("avcC"u8);
        if (at < 0 || at + 11 > payload.Length)
            return;
        var record = payload[(at + 4)..];
        int cursor = 5;                                     // version, profile, compatibility, level, length size
        if (cursor >= record.Length)
            return;
        int count = record[cursor++] & 0x1F;
        for (int i = 0; i < count && cursor + 2 <= record.Length; i++)
        {
            int size = (record[cursor] << 8) | record[cursor + 1];
            cursor += 2;
            if (cursor + size > record.Length) return;
            if (i == 0) _sps = Restrict(record.Slice(cursor, size));
            cursor += size;
        }
        if (cursor >= record.Length)
            return;
        count = record[cursor++];
        for (int i = 0; i < count && cursor + 2 <= record.Length; i++)
        {
            int size = (record[cursor] << 8) | record[cursor + 1];
            cursor += 2;
            if (cursor + size > record.Length) return;
            if (i == 0) _pps = record.Slice(cursor, size).ToArray();
            cursor += size;
        }
    }

    /// <summary>STAP-A (§5.7.1): one byte of header, then [u16 size][NAL] pairs.</summary>
    private void Aggregate(ReadOnlySpan<byte> payload)
    {
        int at = 1;
        while (at + 2 <= payload.Length)
        {
            int size = (payload[at] << 8) | payload[at + 1];
            at += 2;
            if (size <= 0 || at + size > payload.Length)
                return;
            Append(payload.Slice(at, size));
            at += size;
        }
    }

    // --- Access unit assembly ----------------------------------------------------

    private void Append(ReadOnlySpan<byte> nal)
    {
        nal = StripTrailer(nal);
        if (nal.Length == 0)
            return;
        int type = nal[0] & 0x1F;
        switch (type)
        {
            case 7:
                // The restricted set replaces the original outright: what goes
                // into the access unit has to be the same set the decoder was
                // configured with, or it would reconfigure itself back to a
                // twelve-picture reorder queue on the next key frame.
                _sps = Restrict(nal);
                _auHasSps = true;
                StartCode(_sps);
                _nals++;
                return;
            case 8: _pps = nal.ToArray(); _auHasPps = true; break;
            case 5:
                // A self-contained IDR: the sets first, whether or not this
                // access unit brought its own.
                _hasIdr = true;
                _poisoned = false;
                if (!_auHasSps && _sps is not null) StartCode(_sps);
                if (!_auHasPps && _pps is not null) StartCode(_pps);
                break;
        }
        StartCode(nal);
        _nals++;
    }

    /// <summary>
    /// The one place a parameter set is allowed in: rewritten to say that this
    /// stream never reorders a picture.
    /// </summary>
    /// <remarks>
    /// The phone's own SPS says nothing about reordering, and a decoder reading
    /// that has to assume a full level-5.1 buffer — twelve pictures held before
    /// the first comes out, measured. <see cref="SpsRewriter"/> puts the
    /// bitstream restriction in; a set it cannot parse is passed through
    /// untouched rather than dropped, because a decoder with the phone's own
    /// set is late and a decoder with no set at all is blind.
    /// </remarks>
    private byte[] Restrict(ReadOnlySpan<byte> sps)
    {
        try
        {
            byte[] restricted = SpsRewriter.Rewrite(sps, out SpsInfo info);
            Original = info;
            Restricted = SpsRewriter.Parse(restricted);
            return restricted;
        }
        catch (InvalidDataException)
        {
            SpsRewriteFailures++;
            return sps.ToArray();
        }
    }

    private void StartCode(ReadOnlySpan<byte> nal)
    {
        _au.Add(0); _au.Add(0); _au.Add(0); _au.Add(1);
        _au.AddRange(nal);
    }

    private void Flush()
    {
        if (_au.Count > 0)
        {
            if (_poisoned)
            {
                Dropped++;
            }
            else
            {
                AccessUnits++;
                if (_hasIdr) KeyFrames++;
                Completed?.Invoke(new AccessUnit(_au.ToArray(), _timestamp, _hasIdr, _firstArrivalTicks, _arrivalTicks,
                    _packets, _nals));
            }
        }
        _au.Clear();
        _hasIdr = _auHasSps = _auHasPps = false;
        _packets = _nals = 0;
    }

    /// <summary>A hole in the sequence: this picture cannot be trusted, and a fresh IDR is wanted.</summary>
    private void Poison()
    {
        _poisoned = true;
        _fragmenting = false;
        _fragment.Clear();
        KeyFrameWanted++;
    }

    private ReadOnlySpan<byte> StripTrailer(ReadOnlySpan<byte> nal)
    {
        foreach (byte[] trailer in KnownTrailers)
        {
            if (nal.Length > trailer.Length && nal[^trailer.Length..].SequenceEqual(trailer))
            {
                Trailers++;
                return nal[..^trailer.Length];
            }
        }
        return nal;
    }
}
