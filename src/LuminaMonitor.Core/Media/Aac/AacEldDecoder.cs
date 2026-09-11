namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// An AAC-ELD decoder: one access unit in, one frame of PCM out.
/// </summary>
/// <remarks>
/// <para><b>What it is for.</b> The phone sends its sound as ER AAC ELD
/// (audioObjectType 39) and Windows ships no decoder that will take it —
/// <c>aac-selftest</c> measures that refusal. So the codec is implemented here,
/// from ISO/IEC 14496-3 and the ISO reference software's syntax, with the
/// normative tables in <c>Media/Aac/Tables</c>. Nothing of FFmpeg, FDK-AAC or
/// faad2 was read, and no package is referenced.</para>
///
/// <para><b>The order of the tools.</b> Syntax for every channel first, then the
/// tools, in the sequence <c>decoder_tf.c</c> runs them and no other: the
/// mid/side mask is mapped into the right channel's codebooks, mid/side is
/// undone, noise substitution fills its bands, intensity copies the left channel
/// into the right, temporal noise shaping filters each channel, and then the
/// filterbank. Every one of those steps reads what the step before it wrote;
/// three of the four possible swaps produce a stream that decodes without error
/// and sounds wrong.</para>
///
/// <para><b>The frame length.</b> 480 or 512 samples, from the config, and the
/// config's frame length flag is not to be trusted on it — see
/// <see cref="AudioSpecificConfig"/>. The phone sends the flag for 512 and then
/// sends 480-sample frames.</para>
///
/// <para><b>Errors.</b> A frame that cannot be read throws an
/// <see cref="AacBitstreamException"/> inside, carrying the bit it failed at;
/// <see cref="Decode"/> catches it, reports it through
/// <see cref="LastFailure"/> and returns false, so a caller in a live stream
/// goes on to the next frame. The frame's output is the tail of the overlap the
/// good frames before it left, which fades rather than clicking.</para>
///
/// <para><b>Allocation.</b> None per frame: every buffer belongs to the decoder
/// or to a channel, and the access unit is read where it lies.</para>
/// </remarks>
internal sealed class AacEldDecoder
{
    /// <summary>
    /// What the filterbank's output has to be divided by for full scale to be 1.
    /// </summary>
    /// <remarks>
    /// The whole quantization chain of AAC is defined against integer PCM — the
    /// reference decoder writes <c>time_sample_vector</c> straight out as 16-bit
    /// samples with no scaling of its own — so the natural full scale of a decoded
    /// frame is 32768, not 1. Float PCM on Windows is ±1, so the division happens
    /// here, once, and <see cref="FullScale"/> is public so that anything writing
    /// integer samples can undo it exactly.
    /// </remarks>
    public const double FullScale = 32768.0;

    private readonly EldFrameShape shape;
    private readonly EldChannel[] channels;
    private readonly byte[] mask;
    private readonly Pns pns;
    private readonly double[] lpc;

    public AacEldDecoder(AudioSpecificConfig config)
    {
        config.Validate();
        Config = config;
        shape = new EldFrameShape(config.SamplingFrequencyIndex, config.FrameLength);
        channels = new EldChannel[config.Channels];
        for (int channel = 0; channel < channels.Length; channel++)
            channels[channel] = new EldChannel(shape);
        mask = new byte[shape.BandCount];
        pns = new Pns(shape.BandCount);
        lpc = new double[TnsFilter.MaxOrder + 1];
    }

    /// <summary>The config this decoder was built for.</summary>
    public AudioSpecificConfig Config { get; }

    /// <summary>How many samples per channel one frame produces.</summary>
    public int FrameLength => shape.FrameLength;

    /// <summary>How many channels the stream carries.</summary>
    public int ChannelCount => channels.Length;

    /// <summary>How many frames have been decoded without error.</summary>
    public long FramesDecoded { get; private set; }

    /// <summary>How many frames were refused.</summary>
    public long FramesFailed { get; private set; }

    /// <summary>How many bits the last frame's syntax used.</summary>
    public int BitsConsumed { get; private set; }

    /// <summary>How many bits the last frame held.</summary>
    public int BitsAvailable { get; private set; }

    /// <summary>
    /// True when everything after the last frame's syntax was zero — the padding
    /// a well formed frame ends on.
    /// </summary>
    public bool PaddingIsZero { get; private set; }

    /// <summary>Why the last refused frame was refused, or null when none was.</summary>
    public AacBitstreamException? LastFailure { get; private set; }

    /// <summary>
    /// Decodes one access unit into interleaved float PCM.
    /// </summary>
    /// <param name="accessUnit">One raw ELD frame, as an RTP packet carries it.</param>
    /// <param name="pcmInterleaved">
    /// At least <see cref="FrameLength"/> × <see cref="ChannelCount"/> samples,
    /// channel by channel within each sample.
    /// </param>
    /// <param name="samplesPerChannel">How many samples per channel were written.</param>
    /// <returns>False when the frame could not be read; see <see cref="LastFailure"/>.</returns>
    public bool Decode(ReadOnlySpan<byte> accessUnit, Span<float> pcmInterleaved, out int samplesPerChannel)
    {
        samplesPerChannel = shape.FrameLength;
        int wanted = samplesPerChannel * channels.Length;
        if (pcmInterleaved.Length < wanted)
            throw new ArgumentException(
                $"{pcmInterleaved.Length} echantillons de sortie pour {wanted} attendus.", nameof(pcmInterleaved));

        BitsAvailable = accessUnit.Length * 8;
        BitsConsumed = 0;
        PaddingIsZero = false;
        try
        {
            DecodeFrame(accessUnit);
            LastFailure = null;
            FramesDecoded++;
        }
        catch (AacBitstreamException failure)
        {
            LastFailure = failure;
            BitsConsumed = failure.BitPosition;
            FramesFailed++;
            foreach (var channel in channels)
                channel.Bank.Flush(channel.Samples);
        }

        Interleave(pcmInterleaved[..wanted]);
        return LastFailure is null;
    }

    /// <summary>Empties every channel's overlap, for a stream that starts again.</summary>
    public void Reset()
    {
        foreach (var channel in channels)
        {
            channel.Bank.Reset();
            Array.Clear(channel.Samples);
        }
        pns.Reset();
    }

    /// <summary>The frame itself; throws on anything it cannot read.</summary>
    private void DecodeFrame(ReadOnlySpan<byte> accessUnit)
    {
        var reader = new BitReader(accessUnit);
        int maskState = channels.Length == 2
            ? EldSyntax.ReadChannelPair(ref reader, shape, channels[0], channels[1], mask)
            : ReadMono(ref reader);

        BitsConsumed = reader.Position;
        PaddingIsZero = reader.PaddingIsZero();

        if (channels.Length == 2)
        {
            // The mask has to be mapped before mid/side reads it, because a band
            // that is both intensity-coded and mask-flagged is an out-of-phase
            // intensity band and not a mid/side one.
            Stereo.MapMask(channels[1].Codebooks, mask, maskState);
            if (maskState != 0)
                Stereo.MiddleSide(channels[0].Spectrum, channels[1].Spectrum, mask, shape.BandOffsets);
        }

        // Noise substitution left channel first: the left is what stores the
        // generator state a correlated right channel reads back.
        foreach (var channel in channels)
            pns.Apply(channel.Spectrum, channel.Codebooks, channel.Factors, shape.BandOffsets);

        if (channels.Length == 2)
        {
            Stereo.Intensity(channels[0].Spectrum, channels[1].Spectrum,
                channels[1].Codebooks, channels[1].Factors, shape.BandOffsets);
        }

        foreach (var channel in channels)
        {
            Tns.Apply(channel.Spectrum, channel.Tns, shape.BandOffsets,
                channel.MaxSfb, shape.TnsMaxBands, lpc);
            channel.Bank.Synthesize(channel.Spectrum, channel.Samples);
        }
    }

    /// <summary>A mono frame's single element; no mask, so no mask state.</summary>
    private int ReadMono(ref BitReader reader)
    {
        EldSyntax.ReadSingleChannel(ref reader, shape, channels[0]);
        return 0;
    }

    /// <summary>
    /// Channel buffers to interleaved floats, at ±1 full scale.
    /// </summary>
    private void Interleave(Span<float> pcm)
    {
        int frame = shape.FrameLength;
        int count = channels.Length;
        for (int channel = 0; channel < count; channel++)
        {
            double[] samples = channels[channel].Samples;
            for (int sample = 0, at = channel; sample < frame; sample++, at += count)
                pcm[at] = (float)(samples[sample] / FullScale);
        }
    }
}
