using static LuminaMonitor.Core.Media.OfferWire;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// Which codec banks the offer advertises, and in which order.
/// </summary>
/// <remarks>
/// The phone picks from the banks we send, and its answer names a payload
/// type. Which bank is which was read off the wire, not guessed: bank 123
/// answers with RTP payloads <c>3C 81 …</c> — FU-A, RFC 6184 — so 123 is
/// <b>H.264/AVC</b>; bank 100 answers with <c>62 01 81 …</c> — FU, RFC 7798 —
/// so 100 is <b>HEVC</b>. Offered both, the phone takes HEVC; offered bank 123
/// alone it sends H.264, which is the one Windows decodes out of the box.
/// <see cref="AvcThenHevc"/> is the captured Xcode order and stays the
/// default: the blob must remain byte-identical to the template.
/// </remarks>
internal enum VideoCodecs
{
    /// <summary>AVC (payload type 123) then HEVC (100) — the captured Xcode order.</summary>
    AvcThenHevc,
    /// <summary>AVC (123) only — the offer this project runs on.</summary>
    AvcOnly,
    /// <summary>HEVC (100) only.</summary>
    HevcOnly,
    /// <summary>HEVC (100) first, AVC (123) second.</summary>
    HevcThenAvc,
}

/// <summary>
/// The levers the offer itself really carries, and nothing else.
/// </summary>
/// <remarks>
/// Everything here was read off Apple's own offers or off the
/// <c>VCMediaNegotiationBlobVideoSettings</c> field table recovered from the
/// framework's <c>__objc_methname</c> section; nothing is invented. What that
/// table gives, inside the <c>VideoSettings</c> message: f1 SSRC, f2
/// allowRTCPFB, f3 videoPayloadCollections, <b>f4 customVideoWidth</b>, <b>f5
/// customVideoHeight</b>, f6 tilesPerFrame, f7 ltrpEnabled, f8 pixelFormats,
/// f9 hdrModesSupported, f10 fecEnabled, f11 rtxEnabled, f12
/// blackFrameOnClearScreenEnabled, f13 foveationSupported, f14
/// enableInterleavedEncoding.
///
/// <para><b>Resolution.</b> f4 and f5 are the only resolution fields in the
/// whole schema. The captured Xcode offer omits both and no capture shows them
/// in use, so the phone's reaction is unknown: it may honour them, echo them in
/// <c>streamConfig</c>, or refuse the offer with "Invalid Parameter" the way it
/// refuses <c>rtxEnabled</c>. The <c>ResEntry</c> tiers inside each codec bank
/// are <em>not</em> a resolution table — every one of them carries the same
/// fixed capability id 50115 and a pair index of 1 or 2 — so there is nothing
/// to scale there.</para>
///
/// <para><b>Bitrate.</b> There is no single "max bitrate" field either. What
/// exists is the repeated tier table (top-level f9): entries of kind 0 are
/// network bitrate caps, <c>{bps, buffer}</c> pairs, and Apple declares six of
/// them from 6 to 100 Mbit/s. <see cref="MaxBitrateKbps"/> therefore prunes
/// that table rather than setting a field: every kind-0 tier above the ceiling
/// is dropped, and if the ceiling falls below all of them the lowest one is
/// rewritten to it. Whether the encoder reads the table as a ceiling or merely
/// as a capability list is untested — the one bitrate lever the reference
/// implementation confirmed on-device is the RCTL one, which is a different
/// channel entirely (see <see cref="RctlFeedback"/>).</para>
///
/// <para><b>Framerate.</b> There is none. No field of the schema, in the
/// capture or in the recovered table, carries a frame rate or a frame-rate cap,
/// so this record deliberately has no <c>FramerateCap</c>: the only way found
/// to slow the phone's output is the RCTL loop, where the receiver's own pace
/// is what the rate controller reads.</para>
/// </remarks>
internal sealed record VideoOfferOptions
{
    /// <summary>The offer exactly as Xcode sends it, which is what every default run uses.</summary>
    public static readonly VideoOfferOptions Default = new();

    /// <summary>Prunes the tier table to this ceiling; null leaves Apple's table untouched.</summary>
    public int? MaxBitrateKbps { get; init; }

    /// <summary><c>customVideoWidth</c> (f4), in pixels; null omits the field, as the capture does.</summary>
    public int? MaxWidth { get; init; }

    /// <summary><c>customVideoHeight</c> (f5), in pixels; null omits the field.</summary>
    public int? MaxHeight { get; init; }

    /// <summary>True when nothing is asked for, so the blob stays byte-identical to the template.</summary>
    public bool IsDefault => MaxBitrateKbps is null && MaxWidth is null && MaxHeight is null;

    /// <summary>One line for the probe's log.</summary>
    public override string ToString()
    {
        if (IsDefault)
            return "defaut (gabarit Xcode)";
        var parts = new List<string>();
        if (MaxWidth is int w && MaxHeight is int h) parts.Add($"resolution demandee {w}x{h}");
        else if (MaxWidth is int only) parts.Add($"largeur demandee {only}");
        else if (MaxHeight is int tall) parts.Add($"hauteur demandee {tall}");
        if (MaxBitrateKbps is int kbps) parts.Add($"paliers de debit <= {kbps} kbit/s");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Builds the <c>negotiatorOffer</c> the display service demands before it
/// will start a video stream.
/// </summary>
/// <remarks>
/// The offer is a binary plist of four entries: a small protobuf describing
/// the host, the negotiator mode (5 = video), a zlib-compressed protobuf of
/// codec parameters and bitrate tiers, and a call id. None of it was ever
/// documented; every field number and constant below was recovered from
/// Xcode's own offers, and the builder is kept byte-identical to a captured
/// template — <see cref="SelfCheck"/> proves it offline, no phone needed.
/// Two details the daemon is strict about: the plist must be binary, and
/// the compression must be zlib at its best level, or the answer is a bare
/// "Invalid Parameter".
/// </remarks>
internal static class MediaOffer
{
    private const int NegotiatorModeVideo = 5;
    private const long ResEntryCodecCapId = 50115;
    private const string AvcFeatures = "FLS;SW:1;";
    private const string HevcFeatures = "FLS;VRAE:0;SW:1;";
    private const ulong CapturedVideoTimestamp = 17137042128614416384;

    // Apple's canonical bitrate-tier table, in the captured order.
    private static readonly (long Kind, long Bps, long? Cap)[] VideoTiers =
    [
        (4074, 0, 16384), (0, 75_000_000, 524288), (0, 40_000_000, 12288), (16, 4100, null),
        (0, 20_000_000, 98304), (4, 6500, null), (0, 6_000_000, 131072), (0, 100_000_000, 1048576),
        (0, 60_000_000, 262144), (1, 299, null),
    ];

    /// <summary>The complete offer for a video stream, as binary plist bytes.</summary>
    public static byte[] BuildVideo(string callId, uint sessionId,
        string hostModel = "Mac15,9", string hostOsVersion = "2205.3.1", string hostBuild = "25F80",
        bool allowRtcpFb = false, bool ltrpEnabled = false, bool fecEnabled = true, int tilesPerFrame = 1,
        VideoCodecs codecs = VideoCodecs.AvcThenHevc, VideoOfferOptions? options = null)
    {
        byte[] blob = BuildVideoBlob(sessionId, allowRtcpFb, ltrpEnabled, fecEnabled, tilesPerFrame, codecs, options);
        return Bplist.Write(new Dictionary<string, object>
        {
            ["avcMediaStreamOptionRemoteEndpointInfo"] = EndpointInfo(hostModel, hostOsVersion, hostBuild),
            ["avcMediaStreamNegotiatorMode"] = (long)NegotiatorModeVideo,
            ["avcMediaStreamNegotiatorMediaBlob"] = Deflate(blob),
            ["avcMediaStreamOptionCallID"] = callId,
        });
    }

    public static string NewCallId() => Guid.NewGuid().ToString().ToUpperInvariant();

    /// <summary>Reproduces the captured Xcode template; throws with a diff position otherwise.</summary>
    public static void SelfCheck()
    {
        byte[] built = BuildVideoBlob(2368635137, allowRtcpFb: false, ltrpEnabled: true, fecEnabled: false, tilesPerFrame: 1);
        byte[] captured = Convert.FromHexString(
            "080110012a7f088182bae90810001a3f087b120a0801100118c387032000120a" +
            "0801100218c387032000120a0801100118c387032000120a0801100218c38703" +
            "20001a09464c533b53573a313b20011a2e0864120a0801100118c38703200012" +
            "0a0801100218c3870320001a10464c533b565241453a303b53573a313b200e38" +
            "01403f6001320d56696365726f7920312e372e3040004a0908ea1f1000188080" +
            "014a0b080010c0d1e123188080204a0a08001080b489131880604a0508101084" +
            "204a0b08001080dac409188080064a05080410e4324a0b080010809bee021880" +
            "80084a0b08001080c2d72f188080404a0b080010808ece1c188080104a050801" +
            "10ab026880c0dd87d2a0c0e9ed017002800100900101");
        if (built.AsSpan().SequenceEqual(captured))
            return;
        int at = 0;
        while (at < built.Length && at < captured.Length && built[at] == captured[at]) at++;
        throw new InvalidOperationException(
            $"Le blob media diverge du gabarit Xcode a l'octet {at} (construit {built.Length} octets, gabarit {captured.Length}).");
    }

    // --- Protobuf pieces ---------------------------------------------------------

    private static byte[] BuildVideoBlob(uint sessionId, bool allowRtcpFb, bool ltrpEnabled, bool fecEnabled, int tilesPerFrame,
        VideoCodecs codecs = VideoCodecs.AvcThenHevc, VideoOfferOptions? options = null)
    {
        options ??= VideoOfferOptions.Default;
        var video = new List<byte>();
        video.AddRange(Tag(1, 0));
        video.AddRange(VarintPadded(sessionId, 5));
        video.AddRange(FVarint(2, allowRtcpFb ? 1 : 0));
        // Field 3 repeated, in the order the banks are offered: the phone reads
        // it as a preference list, so the order is the whole experiment.
        foreach (byte[] bank in CodecBanks(codecs))
            video.AddRange(FBytes(3, bank));
        // f4 and f5, customVideoWidth and customVideoHeight: the schema's only
        // resolution fields. The captured offer omits both, so they are omitted
        // here too unless asked for — see VideoOfferOptions for what is known
        // about them and what is not.
        if (options.MaxWidth is int width) video.AddRange(FVarint(4, width));
        if (options.MaxHeight is int height) video.AddRange(FVarint(5, height));
        if (tilesPerFrame != 1) video.AddRange(FVarint(6, tilesPerFrame));
        video.AddRange(FVarint(7, ltrpEnabled ? 1 : 0));
        video.AddRange(FVarint(8, 63));
        if (fecEnabled) video.AddRange(FVarint(10, 1));
        video.AddRange(FVarint(12, 1));

        var top = new List<byte>();
        top.AddRange(FVarint(1, 1));
        top.AddRange(FVarint(2, 1));
        top.AddRange(FBytes(5, [.. video]));
        top.AddRange(FString(6, DecoderName));
        top.AddRange(FVarint(8, 0));
        foreach (var (kind, bps, cap) in Tiers(options.MaxBitrateKbps))
            top.AddRange(Tier(kind, bps, cap));
        top.AddRange(Tag(13, 0)); top.AddRange(Varint(CapturedVideoTimestamp));
        top.AddRange(FVarint(14, 2));
        top.AddRange(FVarint(16, 0));
        top.AddRange(FVarint(18, 1));
        return [.. top];
    }

    /// <summary>The banks to advertise, in the offered order.</summary>
    private static IEnumerable<byte[]> CodecBanks(VideoCodecs codecs)
    {
        byte[] avc = CodecBank(123, AvcFeatures, 1, resPairs: 4);
        byte[] hevc = CodecBank(100, HevcFeatures, 14, resPairs: 2);
        return codecs switch
        {
            VideoCodecs.AvcOnly => [avc],
            VideoCodecs.HevcOnly => [hevc],
            VideoCodecs.AvcThenHevc => [avc, hevc],
            _ => [hevc, avc],
        };
    }

    /// <summary>
    /// Apple's bitrate-tier table, pruned to a ceiling.
    /// </summary>
    /// <remarks>
    /// Only the kind-0 entries are network bitrate caps; the others — kinds 1,
    /// 4 and 16, and the 4074 header — are codec markers and are kept whatever
    /// the ceiling. The order is Apple's own and is never reshuffled, and with
    /// no ceiling asked for the list comes back untouched, which is what keeps
    /// the default blob byte-identical to the captured template.
    /// </remarks>
    private static IEnumerable<(long Kind, long Bps, long? Cap)> Tiers(int? maxKbps)
    {
        if (maxKbps is not int kbps)
            return VideoTiers;
        long ceiling = Math.Max(1, (long)kbps) * 1000;
        long lowest = VideoTiers.Where(tier => tier.Kind == 0).Min(tier => tier.Bps);
        var kept = new List<(long Kind, long Bps, long? Cap)>();
        foreach (var tier in VideoTiers)
        {
            if (tier.Kind != 0 || tier.Bps <= ceiling) kept.Add(tier);
            else if (tier.Bps == lowest) kept.Add((0, ceiling, tier.Cap));    // below every tier: the lowest becomes the ceiling
        }
        return kept;
    }

    private static byte[] CodecBank(long payloadType, string features, long f4, int resPairs)
    {
        var body = new List<byte>();
        body.AddRange(FVarint(1, payloadType));
        for (int i = 0; i < resPairs; i++)
        {
            var entry = new List<byte>();
            entry.AddRange(FVarint(1, 1));
            entry.AddRange(FVarint(2, 1 + (i % 2)));
            entry.AddRange(FVarint(3, ResEntryCodecCapId));
            entry.AddRange(FVarint(4, 0));
            body.AddRange(FBytes(2, [.. entry]));
        }
        body.AddRange(FString(3, features));
        body.AddRange(FVarint(4, f4));
        return [.. body];
    }
}
