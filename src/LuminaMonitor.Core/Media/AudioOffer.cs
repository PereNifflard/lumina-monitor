using static LuminaMonitor.Core.Media.OfferWire;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// The levers the <em>audio</em> offer really carries — and the answer to the
/// question that decides everything downstream: there is no codec bank in it.
/// </summary>
/// <remarks>
/// The video offer's <c>VideoSettings</c> message carries a repeated field 3,
/// <c>videoPayloadCollections</c>: one bank per codec, each naming a payload
/// type and a feature string, offered in preference order. That is the field
/// this project uses to make the phone send H.264 instead of HEVC, and it is
/// the reason Windows can decode the picture at all.
///
/// <para><b>The audio settings message has no such field.</b> Every byte of it,
/// in Apple's own captured offer, is six varints:</para>
/// <code>
///   f1 = session id (padded to five bytes, as in the video settings)
///   f2 = 0
///   f3 = 0
///   f4 = 24191            // 0x5E7F, constant across Apple's captures
///   f5 = 0
///   f6 = 0
/// </code>
/// <para>No payload type is named, no feature string, no sample rate, no
/// channel count, no bank. Nothing in the message says "AAC-ELD" and nothing
/// offers an alternative to it: the phone answers <c>RxPayloadType = 101</c>
/// and <c>AudioStreamMode = 8</c> whatever we put in there, because the choice
/// is not ours to make in this schema. Compare the video settings, whose
/// eighteen-odd fields were recovered field by field from
/// <c>VCMediaNegotiationBlobVideoSettings</c>; the audio counterpart's field
/// names have not been recovered, so f2..f6 are named here by their numbers
/// and nothing else, and no field is invented.</para>
///
/// <para><b>What can still be probed.</b> Two things in the offer plausibly
/// touch the codec, and both are real fields rather than guesses:</para>
/// <list type="bullet">
/// <item><description><see cref="SettingsF4"/> — the 24191 above. A round
/// number in binary (0x5E7F) sitting alone in a message that says nothing else
/// looks like a capability mask; if it is one, changing it should change what
/// the phone offers back.</description></item>
/// <item><description><see cref="DropCodecTiers"/> — the top-level tier table
/// (field 9) is shared with the video offer, and three of its entry kinds are
/// not bitrate caps at all: kind 16 (4100), kind 4 (6500) and kind 1 (299).
/// The reference implementation reads those as codec markers — CELT-NB, SILK,
/// and possibly Opus — which are audio codecs, not video ones. If the audio
/// codec is negotiated anywhere in this offer, that table is the other
/// candidate.</description></item>
/// </list>
/// <para>Both are experiments whose result is in <c>docs/AUDIO.md</c>; the
/// default offer is Apple's own, byte for byte, and <see cref="SelfCheck"/>
/// proves it offline.</para>
/// </remarks>
internal sealed record AudioOfferOptions
{
    /// <summary>Apple's audio offer exactly as captured, which every default run uses.</summary>
    public static readonly AudioOfferOptions Default = new();

    /// <summary>Field 4 of the audio settings; null keeps the captured 24191.</summary>
    public long? SettingsF4 { get; init; }

    /// <summary>Field 2; null keeps the captured 0.</summary>
    public long? SettingsF2 { get; init; }

    /// <summary>Field 3; null keeps the captured 0.</summary>
    public long? SettingsF3 { get; init; }

    /// <summary>Field 5; null keeps the captured 0.</summary>
    public long? SettingsF5 { get; init; }

    /// <summary>Field 6; null keeps the captured 0.</summary>
    public long? SettingsF6 { get; init; }

    /// <summary>Drops the kind 1 / 4 / 16 tiers — the codec markers — from the table.</summary>
    public bool DropCodecTiers { get; init; }

    /// <summary>Keeps only the kind 1 / 4 / 16 tiers, dropping every bitrate cap.</summary>
    public bool CodecTiersOnly { get; init; }

    /// <summary>True when nothing is asked for, so the blob stays byte-identical to the capture.</summary>
    public bool IsDefault => SettingsF2 is null && SettingsF3 is null && SettingsF4 is null
        && SettingsF5 is null && SettingsF6 is null && !DropCodecTiers && !CodecTiersOnly;

    /// <summary>One line for the probe's log.</summary>
    public override string ToString()
    {
        if (IsDefault)
            return "defaut (gabarit Xcode)";
        var parts = new List<string>();
        if (SettingsF2 is long f2) parts.Add($"f2={f2}");
        if (SettingsF3 is long f3) parts.Add($"f3={f3}");
        if (SettingsF4 is long f4) parts.Add($"f4={f4} (0x{f4:X})");
        if (SettingsF5 is long f5) parts.Add($"f5={f5}");
        if (SettingsF6 is long f6) parts.Add($"f6={f6}");
        if (DropCodecTiers) parts.Add("paliers de codec retires");
        if (CodecTiersOnly) parts.Add("paliers de codec seuls");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Builds the <c>negotiatorOffer</c> the display service demands before it will
/// start an <b>audio</b> stream: the same ceremony as the video offer, in
/// negotiator mode 6 and with the audio settings message.
/// </summary>
/// <remarks>
/// Same four plist entries, same zlib-at-best-level, same host identity. The
/// two differences are the settings message (field 3 of the blob rather than
/// field 5, and six varints rather than the video's bank list) and the order of
/// the tier table, which Apple captured in a different order for audio and
/// which is kept verbatim so <see cref="SelfCheck"/> can prove the builder
/// still reproduces the capture.
/// </remarks>
internal static class AudioOffer
{
    /// <summary>Negotiator mode 6: audio. Video is 5.</summary>
    private const int NegotiatorModeAudio = 6;

    /// <summary>Field 4 of the audio settings, constant across every Apple capture.</summary>
    public const long CapturedSettingsF4 = 24191;

    private const ulong CapturedAudioTimestamp = 17137179377605574656;

    /// <summary>
    /// Apple's tier table for audio, in the captured order.
    /// </summary>
    /// <remarks>
    /// The same ten entries as the video table, shuffled — Apple captured the
    /// two in different orders and the order is part of what the byte-for-byte
    /// check verifies. Kind 0 entries are network bitrate caps, <c>{bps,
    /// buffer}</c>; kinds 16, 4 and 1 are codec markers and 4074 is the table's
    /// header.
    /// </remarks>
    private static readonly (long Kind, long Value, long? Cap)[] AudioTiers =
    [
        (4074, 0, 16384), (1, 299, null), (0, 60_000_000, 262144), (4, 6500, null),
        (0, 20_000_000, 98304), (0, 100_000_000, 1048576), (0, 40_000_000, 12288),
        (0, 6_000_000, 131072), (16, 4100, null), (0, 75_000_000, 524288),
    ];

    /// <summary>The complete audio offer, as binary plist bytes.</summary>
    /// <remarks>
    /// <paramref name="hostModel"/> defaults to the identity this project's
    /// video offer runs under, not the <c>Mac16,11</c> of Apple's captured audio
    /// offer: when the two streams share a session the phone sees one host, and
    /// it should be the one the video path was proven under.
    /// </remarks>
    public static byte[] Build(string callId, uint sessionId,
        string hostModel = "Mac15,9", string hostOsVersion = "2205.3.1", string hostBuild = "25F80",
        AudioOfferOptions? options = null)
    {
        byte[] blob = BuildBlob(sessionId, options);
        return Bplist.Write(new Dictionary<string, object>
        {
            ["avcMediaStreamOptionRemoteEndpointInfo"] = EndpointInfo(hostModel, hostOsVersion, hostBuild),
            ["avcMediaStreamNegotiatorMode"] = (long)NegotiatorModeAudio,
            ["avcMediaStreamNegotiatorMediaBlob"] = Deflate(blob),
            ["avcMediaStreamOptionCallID"] = callId,
        });
    }

    /// <summary>Reproduces the captured Xcode audio template; throws with a diff position otherwise.</summary>
    public static void SelfCheck()
    {
        byte[] built = BuildBlob(2934526132, null);
        byte[] captured = Convert.FromHexString(
            "080110011a1208b4a1a5f70a1000180020ffbc0128003000320d56696365726f79" +
            "20312e372e3040004a0908ea1f1000188080014a05080110ab024a0b080010808e" +
            "ce1c188080104a05080410e4324a0b08001080dac409188080064a0b08001080c2" +
            "d72f188080404a0a08001080b489131880604a0b080010809bee02188080084a05" +
            "08101084204a0b080010c0d1e12318808020688080d2b28ebbdfe9ed0170028001" +
            "00900101");
        if (built.AsSpan().SequenceEqual(captured))
            return;
        int at = 0;
        while (at < built.Length && at < captured.Length && built[at] == captured[at]) at++;
        throw new InvalidOperationException(
            $"Le blob audio diverge du gabarit Xcode a l'octet {at} (construit {built.Length} octets, gabarit {captured.Length}).");
    }

    private static byte[] BuildBlob(uint sessionId, AudioOfferOptions? options)
    {
        options ??= AudioOfferOptions.Default;
        var audio = new List<byte>();
        audio.AddRange(Tag(1, 0));
        audio.AddRange(VarintPadded(sessionId, 5));
        audio.AddRange(FVarint(2, options.SettingsF2 ?? 0));
        audio.AddRange(FVarint(3, options.SettingsF3 ?? 0));
        audio.AddRange(FVarint(4, options.SettingsF4 ?? CapturedSettingsF4));
        audio.AddRange(FVarint(5, options.SettingsF5 ?? 0));
        audio.AddRange(FVarint(6, options.SettingsF6 ?? 0));

        var top = new List<byte>();
        top.AddRange(FVarint(1, 1));
        top.AddRange(FVarint(2, 1));
        top.AddRange(FBytes(3, [.. audio]));          // field 3, where the video offer has field 5
        top.AddRange(FString(6, DecoderName));
        top.AddRange(FVarint(8, 0));
        foreach (var (kind, value, cap) in Tiers(options))
            top.AddRange(Tier(kind, value, cap));
        top.AddRange(Tag(13, 0)); top.AddRange(Varint(CapturedAudioTimestamp));
        top.AddRange(FVarint(14, 2));
        top.AddRange(FVarint(16, 0));
        top.AddRange(FVarint(18, 1));
        return [.. top];
    }

    /// <summary>The tier table, pruned as the experiment asks; Apple's own order, never reshuffled.</summary>
    private static IEnumerable<(long Kind, long Value, long? Cap)> Tiers(AudioOfferOptions options)
    {
        if (options.DropCodecTiers)
            return AudioTiers.Where(tier => tier.Kind is 0 or 4074);
        if (options.CodecTiersOnly)
            return AudioTiers.Where(tier => tier.Kind is not 0);
        return AudioTiers;
    }
}
