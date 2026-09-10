namespace LuminaMonitor.Core.Media;

/// <summary>
/// Which clock the RCTL report puts in the arrival half of w4.
/// </summary>
/// <remarks>
/// The phone subtracts that figure from the picture's own send time to get a
/// one-way delay. Two readings of the field were tried, and they are not
/// equivalent: see <see cref="RctlFeedback"/> for the whole argument.
/// </remarks>
internal enum RctlArrivalClock
{
    /// <summary>The picture's RTP timestamp divided by 24 — same base as the send time, so the delay comes out at zero.</summary>
    MediaClock,
    /// <summary>Milliseconds since the first packet of the session, a clock of our own with a different origin.</summary>
    WallClock,
}

/// <summary>
/// Every lever of the video negotiation in one place, so that one measurement
/// can be told from another by a single value.
/// </summary>
/// <remarks>
/// The levers live in three different envelopes and it matters which: the
/// <c>options</c> dictionary of <c>startmediastream</c> (<see
/// cref="AccessNetworkType"/>, <see cref="TransportProtocolType"/>), the
/// request beside it (<see cref="ClientSupportedFeatures"/>), and the
/// compressed protobuf of the offer itself (<see cref="LtrpEnabled"/>, <see
/// cref="FecEnabled"/>, <see cref="AllowRtcpFb"/>, <see cref="TilesPerFrame"/>,
/// <see cref="HostModel"/>, and everything in <see cref="Offer"/>). Only the
/// last of the three can make the daemon answer "Invalid Parameter"; the first
/// two have never been seen to be validated at all.
///
/// <para>The RCTL fields are not part of the negotiation: they describe the
/// feedback we send afterwards, on the media port. They sit here because a run
/// is identified by the whole set, negotiation and feedback together.</para>
///
/// <para><see cref="Default"/> is the negotiation exactly as Xcode conducts it,
/// which is what keeps <see cref="MediaOffer.SelfCheck"/> meaningful.</para>
/// </remarks>
internal sealed record StreamTuning
{
    /// <summary>
    /// Xcode's own negotiation, with no feedback loop — and, measured, the
    /// fastest thing this project has found.
    /// </summary>
    /// <remarks>
    /// Not a default by default. On 9 September 2026 every lever below was
    /// measured one at a time against a clock painted on the phone's own screen
    /// (<c>clock-test</c>, which times the instant a second turns over against
    /// the PC clock of that picture's last packet): the RCTL loop at four
    /// ceilings and two report cadences, both readings of its arrival clock,
    /// the four values of the access-network type and of the transport-protocol
    /// type, four masks of <c>clientSupportedFeatures</c>, the four protobuf
    /// flags, the other captured host model, and the offer's resolution and
    /// bitrate-tier levers. Twenty sessions, and the answer was the same every
    /// time: 1,55 to 1,78 s, with no lever moving it and no <c>streamConfig</c>
    /// field changing. The delay is not in the negotiation — the sender
    /// reports place it upstream of the phone's own RTP timestamp — so the
    /// negotiation stays exactly as Xcode conducts it, which is also what keeps
    /// <see cref="MediaOffer.SelfCheck"/> meaningful. The whole argument, and
    /// the one thing that did move the figure (a finger on the screen), is in
    /// <c>docs/VIDEO_DESIGN.md</c>, "Étape 4 — la latence absolue".
    /// </remarks>
    public static readonly StreamTuning Default = new();

    /// <summary>Resolution and bitrate-tier levers inside the offer's protobuf.</summary>
    public VideoOfferOptions Offer { get; init; } = VideoOfferOptions.Default;

    /// <summary>Whether the AVConference receiver-feedback loop runs.</summary>
    public bool Rctl { get; init; }

    /// <summary>The ceiling the feedback loop advertises, in kbit/s.</summary>
    public int RctlMaxBitrateKbps { get; init; } = RctlFeedback.CapturedMaxBitrateKbps;

    /// <summary>How often the periodic RCTL report goes out, in hertz.</summary>
    public double RctlReportHz { get; init; } = RctlFeedback.ReportHz;

    /// <summary>Which clock fills the arrival half of w4.</summary>
    public RctlArrivalClock RctlArrival { get; init; } = RctlArrivalClock.MediaClock;

    /// <summary><c>AVCMediaStreamNegotiatorAccessNetworkType</c>; 1 in every capture.</summary>
    public long AccessNetworkType { get; init; } = 1;

    /// <summary><c>AVCMediaStreamNegotiatorTransportProtocolType</c>; 2 in every capture.</summary>
    public long TransportProtocolType { get; init; } = 2;

    /// <summary><c>clientSupportedFeatures</c>, the bit mask devicectl announces.</summary>
    public ulong ClientSupportedFeatures { get; init; } = 140;

    /// <summary>Long-term reference pictures (blob f7). Off: the reference found LTRP tears live decoders.</summary>
    public bool LtrpEnabled { get; init; }

    /// <summary>Forward error correction (blob f10). On, as the reference leaves it.</summary>
    public bool FecEnabled { get; init; } = true;

    /// <summary>RTCP feedback inside the blob (f2). Off; no observable effect in the answer.</summary>
    public bool AllowRtcpFb { get; init; }

    /// <summary>Tiles per encoded picture (blob f6). One; the phone ignores anything else on AVC.</summary>
    public int TilesPerFrame { get; init; } = 1;

    /// <summary>The host model announced in <c>avcMediaStreamOptionRemoteEndpointInfo</c>.</summary>
    public string HostModel { get; init; } = "Mac15,9";

    /// <summary>True when nothing has been touched, so the offer stays byte-identical to Xcode's.</summary>
    public bool IsDefaultOffer =>
        Offer.IsDefault && !LtrpEnabled && FecEnabled && !AllowRtcpFb && TilesPerFrame == 1
        && HostModel == Default.HostModel;

    /// <summary>One line for the probe's log: only what differs from the capture.</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (!Offer.IsDefault) parts.Add(Offer.ToString());
        if (AccessNetworkType != Default.AccessNetworkType) parts.Add($"reseau {AccessNetworkType}");
        if (TransportProtocolType != Default.TransportProtocolType) parts.Add($"transport {TransportProtocolType}");
        if (ClientSupportedFeatures != Default.ClientSupportedFeatures) parts.Add($"features {ClientSupportedFeatures}");
        if (LtrpEnabled) parts.Add("ltrp");
        if (!FecEnabled) parts.Add("sans fec");
        if (AllowRtcpFb) parts.Add("allowRtcpFb");
        if (TilesPerFrame != 1) parts.Add($"{TilesPerFrame} tuiles");
        if (HostModel != Default.HostModel) parts.Add($"hote {HostModel}");
        if (Rctl)
            parts.Add($"RCTL {RctlReportHz:F0} Hz, borne {RctlMaxBitrateKbps} kbit/s,"
                + $" arrivee {(RctlArrival == RctlArrivalClock.MediaClock ? "sur l'horloge media" : "sur notre montre")}");
        return parts.Count == 0 ? "negociation Xcode telle quelle, sans boucle RCTL" : string.Join(", ", parts);
    }
}
