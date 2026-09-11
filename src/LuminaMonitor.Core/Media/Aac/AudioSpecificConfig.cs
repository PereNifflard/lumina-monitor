namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// The AudioSpecificConfig of an AAC-ELD stream, and the ELDSpecificConfig
/// inside it.
/// </summary>
/// <remarks>
/// <para><b>Syntax.</b> ISO/IEC 14496-3 §1.6.2.1 for the AudioSpecificConfig —
/// five bits of object type, 31 meaning "six more bits, plus 32" — and
/// §4.4.2.2 for the ELDSpecificConfig that follows the channel configuration
/// when the object type is 39. The field order of the latter is the one the
/// reference software reads and writes in <c>flex_mux.c</c>,
/// <c>advanceELDspecConf()</c>: frame length flag, three resilience flags, the
/// SBR flag and its two parameters, then a chain of extension payloads closed
/// by a zero tag.</para>
///
/// <para><b>The frame length.</b> The flag says 512 samples when clear and 480
/// when set, which is the opposite way round from the GA object types. The
/// phone sends it clear and then sends 480-sample frames — its RTP timestamps
/// step by 480 — so <see cref="FrameLength"/> is what the caller decides, not
/// what the flag says, and <see cref="WithFrameLength"/> is how the caller says
/// it. The flag is kept as read so the disagreement stays visible.</para>
///
/// <para>This is a second AudioSpecificConfig reader in this project: the one in
/// <c>Media/AacProbe.cs</c> answers "will Windows take this" from four fields
/// and stops at the first bit of the ELDSpecificConfig. That one is about
/// Windows; this one is about decoding, and needs the whole thing.</para>
/// </remarks>
internal readonly record struct AudioSpecificConfig(
    int ObjectType,
    int SamplingFrequencyIndex,
    int SamplingFrequency,
    int ChannelConfiguration,
    int FrameLengthFlag,
    bool SectionDataResilience,
    bool ScalefactorDataResilience,
    bool SpectralDataResilience,
    bool SbrPresent,
    int ExtensionCount,
    int FrameLength,
    int ConfigBits)
{
    /// <summary>The object type this decoder is for: ER AAC ELD.</summary>
    public const int ErAacEld = 39;

    /// <summary>The frame length the ELD object type uses when nothing overrides the flag.</summary>
    public const int DefaultFrameLength = 480;

    private static readonly int[] SamplingFrequencies =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0];

    /// <summary>How many channels the channel configuration asks for.</summary>
    public int Channels => ChannelConfiguration;

    /// <summary>Reads a config; the frame length comes from the flag and can be overridden after.</summary>
    /// <exception cref="AacBitstreamException">The config ends before its fields do.</exception>
    public static AudioSpecificConfig Parse(ReadOnlySpan<byte> config)
    {
        var reader = new BitReader(config);
        int objectType = (int)reader.Read(5);
        if (objectType == 31)
            objectType = 32 + (int)reader.Read(6);
        int frequencyIndex = (int)reader.Read(4);
        int frequency = frequencyIndex == 15 ? (int)reader.Read(24) : SamplingFrequencies[frequencyIndex];
        int channels = (int)reader.Read(4);

        if (objectType != ErAacEld)
        {
            // Nothing past the channel configuration is ELDSpecificConfig, so
            // there is nothing further this reader can honestly say.
            return new AudioSpecificConfig(objectType, frequencyIndex, frequency, channels,
                0, false, false, false, false, 0, 0, reader.Position);
        }

        int frameLengthFlag = reader.ReadBit();
        bool sectionResilience = reader.ReadFlag();
        bool scalefactorResilience = reader.ReadFlag();
        bool spectralResilience = reader.ReadFlag();
        bool sbrPresent = reader.ReadFlag();

        // With SBR the next fields are ldSbrSamplingRate, ldSbrCrcFlag and then
        // the SBR headers, whose length follows the SBR syntax this decoder does
        // not implement. The extension chain after them is therefore out of
        // reach: the config is reported as far as it was read, and Validate()
        // refuses it rather than guessing at the rest.
        int extensions = sbrPresent ? -1 : CountExtensions(ref reader);

        return new AudioSpecificConfig(objectType, frequencyIndex, frequency, channels,
            frameLengthFlag, sectionResilience, scalefactorResilience, spectralResilience,
            sbrPresent, extensions, frameLengthFlag != 0 ? 480 : 512, reader.Position);
    }

    /// <summary>
    /// Walks the ELD extension chain to its terminator, counting what it holds.
    /// </summary>
    /// <remarks>
    /// Each link is a four-bit tag, then a four-bit length that escapes through
    /// 15 to a further eight bits and through 255 to a further sixteen, then
    /// that many bytes of payload. Tag zero — ELDEXT_TERM — ends the chain and
    /// has no length. The payloads are SAOC and low delay MPEG Surround
    /// configurations, neither of which this decoder implements; they are
    /// skipped here so that the config's own length comes out right, and
    /// refused by <see cref="Validate"/>. A chain that never terminates counts
    /// as one extension of its own, so that it is refused too rather than
    /// silently taken for an empty one.
    /// </remarks>
    private static int CountExtensions(ref BitReader reader)
    {
        int extensions = 0;
        while (reader.Remaining >= 4)
        {
            int tag = (int)reader.Read(4);
            if (tag == 0)
                return extensions;
            int length = (int)reader.Read(4);
            if (length == 15)
            {
                int more = (int)reader.Read(8);
                length += more;
                if (more == 255)
                    length += (int)reader.Read(16);
            }
            reader.SkipBytes(length);
            extensions++;
        }
        return extensions + 1;
    }

    /// <summary>The same config with the frame length the wire actually shows.</summary>
    public AudioSpecificConfig WithFrameLength(int frameLength) => frameLength is 480 or 512
        ? this with { FrameLength = frameLength }
        : throw new ArgumentOutOfRangeException(nameof(frameLength), frameLength,
            "L'ELD ne connait que 480 ou 512 echantillons par trame.");

    /// <summary>
    /// Refuses, with a reason, every stream this decoder does not implement.
    /// </summary>
    /// <exception cref="NotSupportedException">The config is legal and unsupported.</exception>
    public void Validate()
    {
        if (ObjectType != ErAacEld)
            throw new NotSupportedException($"Objet audio {ObjectType} : ce decodeur ne fait que l'ER AAC ELD (39).");
        if (SamplingFrequencyIndex is < 0 or > 12)
            throw new NotSupportedException($"Index de frequence {SamplingFrequencyIndex} : pas de table basse latence.");
        if (ChannelConfiguration is not (1 or 2))
            throw new NotSupportedException(
                $"Configuration de canaux {ChannelConfiguration} : ce decodeur fait le mono et la paire stereo.");
        if (SbrPresent)
            throw new NotSupportedException("SBR basse latence present : ce decodeur n'a pas de banc SBR.");
        if (SectionDataResilience || ScalefactorDataResilience || SpectralDataResilience)
            throw new NotSupportedException("Drapeaux de resilience actifs : syntaxe HCR/RVLC non implementee.");
        if (ExtensionCount != 0)
            throw new NotSupportedException($"{ExtensionCount} extension(s) ELD (SAOC ou MPEG Surround) non implementee(s).");
        if (FrameLength is not (480 or 512))
            throw new NotSupportedException($"Longueur de trame {FrameLength} : l'ELD ne connait que 480 ou 512.");
    }

    public override string ToString() =>
        $"objet {ObjectType}, frequence {SamplingFrequency} Hz (index {SamplingFrequencyIndex}),"
        + $" {ChannelConfiguration} canal/canaux, frameLengthFlag {FrameLengthFlag}"
        + $" -> {FrameLength} echantillons, SBR {(SbrPresent ? "oui" : "non")},"
        + $" resilience {(SectionDataResilience ? 1 : 0)}{(ScalefactorDataResilience ? 1 : 0)}{(SpectralDataResilience ? 1 : 0)},"
        + $" {ConfigBits} bits de configuration";
}
