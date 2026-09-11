using LuminaMonitor.Core.Media.Aac.Tables;

namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// What every frame of one stream has in common: band edges and the two TNS
/// ceilings.
/// </summary>
/// <remarks>
/// An ELD frame is always one long window, so the band table never changes from
/// frame to frame — which is why it is read out of <c>ScalefactorBands</c> once,
/// at construction, rather than looked up per frame.
/// </remarks>
internal sealed class EldFrameShape
{
    public EldFrameShape(int samplingFrequencyIndex, int frameLength)
    {
        FrameLength = frameLength;
        BandOffsets = ScalefactorBands.LowDelay(samplingFrequencyIndex, frameLength).ToArray();
        BandCount = BandOffsets.Length - 1;
        TnsMaxBands = TnsTables.MaxBands(samplingFrequencyIndex, frameLength);
        TnsMaxOrder = TnsTables.MaxOrder(samplingFrequencyIndex);
    }

    /// <summary>How many spectral lines, and samples, one frame holds.</summary>
    public int FrameLength { get; }

    /// <summary>Where each scalefactor band starts; the last entry is the frame length.</summary>
    public ushort[] BandOffsets { get; }

    /// <summary>How many scalefactor bands the frame has.</summary>
    public int BandCount { get; }

    /// <summary>The highest band a TNS filter may reach at this frequency.</summary>
    public int TnsMaxBands { get; }

    /// <summary>The widest TNS filter this frequency allows.</summary>
    public int TnsMaxOrder { get; }
}

/// <summary>
/// One channel's working state, allocated once and reused every frame.
/// </summary>
internal sealed class EldChannel
{
    public EldChannel(EldFrameShape shape)
    {
        Codebooks = new byte[shape.BandCount];
        Factors = new short[shape.BandCount];
        Quantized = new int[shape.FrameLength];
        Spectrum = new double[shape.FrameLength];
        Samples = new double[shape.FrameLength];
        Tns = new TnsFrame();
        Bank = new EldFilterBank(shape.FrameLength);
    }

    /// <summary>The Huffman codebook of each band, as <c>section_data</c> gave it.</summary>
    public byte[] Codebooks { get; }

    /// <summary>The scalefactor, noise energy or intensity position of each band.</summary>
    public short[] Factors { get; }

    /// <summary>The quantized spectral lines.</summary>
    public int[] Quantized { get; }

    /// <summary>The dequantized, scaled spectral lines.</summary>
    public double[] Spectrum { get; }

    /// <summary>The channel's samples for this frame.</summary>
    public double[] Samples { get; }

    /// <summary>This frame's TNS filters.</summary>
    public TnsFrame Tns { get; }

    /// <summary>The channel's filterbank, which carries the overlap between frames.</summary>
    public EldFilterBank Bank { get; }

    /// <summary>How many bands this channel transmitted.</summary>
    public int MaxSfb { get; set; }

    /// <summary>The eight-bit gain the channel opened with.</summary>
    public int GlobalGain { get; set; }
}

/// <summary>
/// The ELD frame: which elements it holds, and what an element holds.
/// </summary>
/// <remarks>
/// <para><b>What ELD took out.</b> ISO/IEC 14496-3 §4.4.2.1 and Table 4.19 for
/// the ordinary frame; the ELD differences are §4.5.2.2 and, in the reference
/// software, every branch guarded by <c>MULTICHANNEL_ELD</c> in
/// <c>huffdec1.c</c> and <c>huffdec2.c</c>. There is no <c>id_syn_ele</c>: the
/// channel configuration alone says which elements follow and in what order,
/// which is why a decoder that looks for a three-bit element identifier reads a
/// frame's first field as something else entirely. There is no
/// <c>element_instance_tag</c>, no <c>common_window</c> bit — a channel pair is
/// always a common window — and no <c>ics_info</c>: a frame is one long window
/// of one group with one window shape, so all that survives of it is
/// <c>max_sfb</c> on six bits. Pulse coding and Sony gain control are gone
/// too.</para>
///
/// <para><b>What stays, and in what order.</b> For a pair: <c>max_sfb</c>, the
/// mid/side mask, then the two channels' <c>individual_channel_stream</c> one
/// after the other. For one channel: the stream on its own, with its own
/// <c>max_sfb</c> inside it. Inside a stream: <c>global_gain</c> on eight bits,
/// <c>max_sfb</c> when there is no common window, <c>section_data</c>,
/// <c>scale_factor_data</c>, the one-bit <c>tns_data_present</c>, the TNS data
/// when it is set, and <c>spectral_data</c>. The TNS data sits between the flag
/// and the spectrum, not immediately after the flag as in plain AAC — in the ER
/// object types the flag is read where plain AAC reads it and the data is read
/// later, and in ELD nothing comes between the two, so the difference is
/// invisible until a stream with resilience turned on shows up.</para>
/// </remarks>
internal static class EldSyntax
{
    /// <summary>How many bits <c>max_sfb</c> takes in a long window.</summary>
    private const int MaxSfbBits = 6;

    /// <summary>
    /// Reads the one element of a mono frame.
    /// </summary>
    public static void ReadSingleChannel(ref BitReader reader, EldFrameShape shape, EldChannel channel)
        => ReadChannelStream(ref reader, shape, channel, commonMaxSfb: -1);

    /// <summary>
    /// Reads the element of a stereo frame, and the mid/side mask that governs it.
    /// </summary>
    /// <returns>The two-bit <c>ms_mask_present</c>, which 0 and 1 do not exhaust.</returns>
    public static int ReadChannelPair(ref BitReader reader, EldFrameShape shape,
        EldChannel left, EldChannel right, Span<byte> mask)
    {
        int maxSfb = (int)reader.Read(MaxSfbBits);
        if (maxSfb > shape.BandCount)
            throw reader.Error($"max_sfb {maxSfb} pour {shape.BandCount} bandes");

        int maskState = Stereo.ReadMask(ref reader, maxSfb, mask);
        ReadChannelStream(ref reader, shape, left, maxSfb);
        ReadChannelStream(ref reader, shape, right, maxSfb);
        return maskState;
    }

    /// <summary>
    /// <c>individual_channel_stream()</c>, ELD flavour, followed by inverse
    /// quantization.
    /// </summary>
    /// <param name="commonMaxSfb">
    /// The pair's <c>max_sfb</c>, or −1 for a channel that carries its own.
    /// </param>
    private static void ReadChannelStream(ref BitReader reader, EldFrameShape shape, EldChannel channel,
        int commonMaxSfb)
    {
        channel.GlobalGain = (int)reader.Read(8);
        if (commonMaxSfb < 0)
        {
            commonMaxSfb = (int)reader.Read(MaxSfbBits);
            if (commonMaxSfb > shape.BandCount)
                throw reader.Error($"max_sfb {commonMaxSfb} pour {shape.BandCount} bandes");
        }
        channel.MaxSfb = commonMaxSfb;

        SectionData.Read(ref reader, channel.MaxSfb, channel.Codebooks);
        ScaleFactors.Read(ref reader, channel.Codebooks, channel.GlobalGain, channel.Factors);

        channel.Tns.Clear();
        if (reader.ReadFlag())
            channel.Tns.Read(ref reader, shape.BandCount, shape.TnsMaxOrder);

        SpectralData.Read(ref reader, channel.Codebooks, shape.BandOffsets, channel.Quantized);
        Dequantizer.Dequantize(channel.Quantized, channel.Codebooks, channel.Factors,
            shape.BandOffsets, channel.Spectrum);
    }
}
