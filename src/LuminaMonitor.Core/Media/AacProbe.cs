using System.Runtime.InteropServices;
using LuminaMonitor.Core.Media.MediaFoundation;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// What the AudioSpecificConfig the phone advertises actually says.
/// </summary>
/// <remarks>
/// Four bytes, read bit by bit as ISO/IEC 14496-3 §1.6.2.1 lays them out. The
/// only field that decides anything here is the audio object type: 2 is
/// AAC-LC, which Windows decodes, and 39 is ER AAC ELD, which it does not.
/// </remarks>
internal readonly record struct AudioSpecificConfig(int ObjectType, int SamplingFrequencyIndex, int ChannelConfiguration,
    int FrameLengthFlag, int SamplingFrequency)
{
    private static readonly int[] SamplingFrequencies =
        [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0];

    /// <summary>The object type's name, for the ones this project can meet.</summary>
    public string ObjectTypeName => ObjectType switch
    {
        2 => "AAC-LC",
        5 => "HE-AAC (SBR)",
        23 => "AAC-LD",
        29 => "HE-AAC v2 (PS)",
        39 => "ER AAC ELD",
        _ => "?",
    };

    /// <summary>Reads the config; throws when it is too short to hold one.</summary>
    public static AudioSpecificConfig Parse(ReadOnlySpan<byte> config)
    {
        if (config.Length < 2)
            throw new InvalidDataException("AudioSpecificConfig trop court.");
        int at = 0;
        int objectType = ReadBits(config, ref at, 5);
        if (objectType == 31)
            objectType = 32 + ReadBits(config, ref at, 6);
        int frequencyIndex = ReadBits(config, ref at, 4);
        int frequency = frequencyIndex == 15 ? ReadBits(config, ref at, 24) : SamplingFrequencies[frequencyIndex];
        int channels = ReadBits(config, ref at, 4);
        // For ER AAC ELD the next bit opens ELDSpecificConfig and is the frame
        // length flag; for the GA object types it is the same bit of
        // GASpecificConfig. Either way it is one bit after the channel config.
        int frameLengthFlag = ReadBits(config, ref at, 1);
        return new AudioSpecificConfig(objectType, frequencyIndex, channels, frameLengthFlag, frequency);
    }

    /// <summary>Reads <paramref name="bits"/> bits, most significant first; past the end reads zeroes.</summary>
    private static int ReadBits(ReadOnlySpan<byte> config, ref int at, int bits)
    {
        int value = 0;
        for (int i = 0; i < bits; i++, at++)
        {
            int index = at >> 3;
            int bit = index < config.Length ? (config[index] >> (7 - (at & 7))) & 1 : 0;
            value = (value << 1) | bit;
        }
        return value;
    }

    public override string ToString() =>
        $"objet {ObjectType} ({ObjectTypeName}), index de frequence {SamplingFrequencyIndex} ({SamplingFrequency} Hz),"
        + $" configuration de canaux {ChannelConfiguration}, frameLengthFlag {FrameLengthFlag}";
}

/// <summary>
/// Asks Windows's own AAC decoder, in so many words, whether it will take what
/// the phone sends.
/// </summary>
/// <remarks>
/// The Microsoft AAC Decoder MFT (<c>CLSID_CMSAACDecMFT</c>) is the only AAC
/// decoder Windows ships. Its documentation names AAC-LC and HE-AAC as the
/// profiles it handles, and says nothing about AAC-ELD — but documentation is
/// not a measurement, and the MFT's own <c>SetInputType</c> is: the input type
/// carries the AudioSpecificConfig in <c>MF_MT_USER_DATA</c>, and the decoder
/// parses it. So the question "can Windows decode what the phone sends" has an
/// exact answer with an exact HRESULT, and this is how it is obtained.
///
/// <para><c>MF_MT_USER_DATA</c> is not the config on its own: it is the tail of
/// <c>HEAACWAVEINFO</c> that follows the <c>WAVEFORMATEX</c> — twelve bytes,
/// of which only the payload type (0 = raw AudioSpecificConfig) and the
/// profile-level indication are read — and then the config itself.</para>
/// </remarks>
internal static class AacProbe
{
    /// <summary>The config the phone's own encoder advertises: AAC-ELD, 48 kHz, stereo.</summary>
    public static readonly byte[] PhoneConfig = [0xF8, 0xE6, 0x40, 0x00];

    /// <summary>A plain AAC-LC 48 kHz stereo config, the control case Windows must accept.</summary>
    /// <remarks>Object type 2, frequency index 3 (48 kHz), channel configuration 2.</remarks>
    public static readonly byte[] AacLcConfig = [0x11, 0x90];

    /// <summary>
    /// One shape of AAC input type: the attributes around the config.
    /// </summary>
    /// <remarks>
    /// The decoder refuses a type as a whole, with one HRESULT and no hint as
    /// to which attribute it disliked, so "does Windows accept this codec" can
    /// only be answered by offering the same config in several shapes and
    /// seeing whether <em>any</em> of them is taken. The shapes below are not
    /// invented: they are what the MFT advertises for itself (see
    /// <see cref="DescribeInputTypes"/>) and what its documentation asks for,
    /// which turn out not to be the same thing.
    /// </remarks>
    public readonly record struct TypeShape(string Label, uint BitsPerSample, uint BlockAlignment,
        uint? AvgBytesPerSecond, uint? ProfileLevel, bool PreferWaveFormatEx, uint Channels = 2,
        bool IncludeConfig = true);

    /// <summary>The shapes worth trying, cheapest hypothesis first.</summary>
    /// <remarks>
    /// The third and fourth are the MFT's own advertised type 0, which it
    /// accepts back untouched — so a refusal of one of those isolates the one
    /// thing changed in it: the AudioSpecificConfig, or the channel count.
    /// </remarks>
    public static readonly TypeShape[] Shapes =
    [
        new("documentee (16 bits, align 1)", 16, 1, 16000, 0x29, false),
        new("documentee + PREFER_WAVEFORMATEX", 16, 1, 16000, 0x29, true),
        new("gabarit annonce par le MFT (32 bits, align 24, 6 canaux)", 32, 24, 1152000, 0, true, Channels: 6),
        new("gabarit annonce, 2 canaux", 32, 24, 1152000, 0, true),
        new("gabarit annonce, 6 canaux, SANS AudioSpecificConfig", 32, 24, 1152000, 0, true,
            Channels: 6, IncludeConfig: false),
        new("minimale (rien que le codec)", 0, 0, null, null, false),
    ];

    /// <summary>What one attempt at an input type came to.</summary>
    /// <param name="Label">The case, for the transcript.</param>
    /// <param name="Shape">Which shape of type was offered.</param>
    /// <param name="Config">The AudioSpecificConfig offered.</param>
    /// <param name="Parsed">What that config says, read here rather than taken on trust.</param>
    /// <param name="HResult">Exactly what <c>SetInputType</c> answered.</param>
    public readonly record struct Attempt(string Label, string Shape, byte[] Config, AudioSpecificConfig Parsed, int HResult)
    {
        public bool Accepted => HResult >= 0;

        /// <summary>The HRESULT's name where Media Foundation has one for it.</summary>
        public string HResultName => HResult switch
        {
            0 => "S_OK",
            MfNative.InvalidMediaType => "MF_E_INVALIDMEDIATYPE",
            unchecked((int)0xC00D36B3) => "MF_E_INVALIDTYPE",
            unchecked((int)0x80070057) => "E_INVALIDARG",
            unchecked((int)0xC00D36BA) => "MF_E_TRANSFORM_TYPE_NOT_SET",
            _ => "?",
        };
    }

    /// <summary>
    /// Offers every config in every shape and reports the HRESULT of each.
    /// </summary>
    /// <remarks>
    /// A fresh MFT per attempt: an MFT that has refused a type is not
    /// guaranteed to be in the state it started in, and the point of the
    /// exercise is that each answer is about its own type and nothing else.
    /// Must run on an MTA thread, like every other Media Foundation call here.
    /// </remarks>
    public static List<Attempt> TryInputTypes(params (string Label, byte[] Config)[] cases)
    {
        MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, MfNative.CoinitMultithreaded), "CoInitializeEx");
        MfNative.Check(MfNative.MFStartup(MfNative.MfVersion, MfNative.MfStartupLite), "MFStartup");
        var results = new List<Attempt>();
        foreach (var (label, config) in cases)
        {
            foreach (var shape in Shapes)
            {
                MfNative.Check(MfNative.CoCreateInstance(ref MfGuids.ClsidAacDecoder, IntPtr.Zero,
                    MfNative.ClsCtxInprocServer, ref MfGuids.IidTransform, out object instance),
                    "CoCreateInstance(AAC MFT)");
                var transform = (IMFTransform)instance;
                try
                {
                    results.Add(new Attempt(label, shape.Label, config, AudioSpecificConfig.Parse(config),
                        SetInputType(transform, config, shape)));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(transform);
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Starts from the one type the decoder is known to accept and changes one
    /// thing at a time.
    /// </summary>
    /// <remarks>
    /// The MFT answers every bad type with the same <c>MF_E_INVALIDMEDIATYPE</c>
    /// and never says which attribute it disliked, so the only way to learn
    /// anything is to start from something it accepts — its own advertised
    /// input type 0 — and walk towards the type we want, one attribute per step.
    /// The first step that flips from S_OK to a refusal names the cause.
    /// </remarks>
    public static List<(string Step, int HResult, bool Decisive)> Bisect(byte[] config)
    {
        MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, MfNative.CoinitMultithreaded), "CoInitializeEx");
        MfNative.Check(MfNative.MFStartup(MfNative.MfVersion, MfNative.MfStartupLite), "MFStartup");
        var steps = new List<(string, int, bool)>();
        for (int step = -1; step <= 5; step++)
        {
            MfNative.Check(MfNative.CoCreateInstance(ref MfGuids.ClsidAacDecoder, IntPtr.Zero,
                MfNative.ClsCtxInprocServer, ref MfGuids.IidTransform, out object instance),
                "CoCreateInstance(AAC MFT)");
            var transform = (IMFTransform)instance;
            IMFMediaType? type = null;
            try
            {
                MfNative.Check(transform.GetInputAvailableType(0, 0, out type), "GetInputAvailableType(0)");
                if (type is null)
                    break;
                string label = "type annonce 0 tel quel";
                byte[] zeros = new byte[12];
                if (step == 0)
                {
                    // The control of the bisection: one attribute rewritten to
                    // the value it already had. A refusal here would mean the
                    // MFT recognises its own type object rather than validating
                    // it, and every other step below would be meaningless.
                    label = "+ SAMPLES_PER_SECOND reecrit a 48000 (valeur inchangee)";
                    MfNative.Check(type.SetUINT32(ref MfGuids.AudioSamplesPerSecondKey, 48000), "SAMPLES_PER_SECOND");
                }
                if (step >= 1)
                {
                    // A recognisable pattern, written and read straight back:
                    // writing zeros over zeros could not tell a working SetBlob
                    // from one that does nothing, and the answer to that decides
                    // whether the refusals below mean anything at all.
                    byte[] pattern = [0xAA, 0xBB, 0xCC, 0xDD, 1, 2, 3, 4, 5, 6, 7, 8];
                    MfNative.Check(WriteBlob(type, ref MfGuids.UserDataKey, pattern), "SetBlob(motif)");
                    byte[]? readBack = ReadBlob(type, ref MfGuids.UserDataKey);
                    label = "+ USER_DATA reecrit a douze zeros"
                        + $" (motif relu : {(readBack is null ? "rien" : Convert.ToHexString(readBack))})";
                    MfNative.Check(WriteBlob(type, ref MfGuids.UserDataKey, zeros), "SetBlob(12 zeros)");
                }
                if (step >= 2)
                {
                    label = "+ AudioSpecificConfig ajoute au USER_DATA";
                    byte[] full = UserData(config, 0);
                    MfNative.Check(WriteBlob(type, ref MfGuids.UserDataKey, full), "SetBlob(config)");
                }
                if (step >= 3)
                {
                    label = "+ deux canaux";
                    MfNative.Check(type.SetUINT32(ref MfGuids.AudioNumChannelsKey, 2), "NUM_CHANNELS=2");
                }
                if (step >= 4)
                {
                    label = "+ seize bits par echantillon, alignement 4";
                    MfNative.Check(type.SetUINT32(ref MfGuids.AudioBitsPerSampleKey, 16), "BITS_PER_SAMPLE=16");
                    MfNative.Check(type.SetUINT32(ref MfGuids.AudioBlockAlignmentKey, 4), "BLOCK_ALIGNMENT=4");
                }
                if (step >= 5)
                {
                    label = "+ debit moyen 16000 octets/s";
                    MfNative.Check(type.SetUINT32(ref MfGuids.AudioAvgBytesPerSecondKey, 16000), "AVG_BYTES=16000");
                }
                steps.Add((label, transform.SetInputType(0, type, 0), step == 2));
            }
            finally
            {
                if (type is not null) Marshal.FinalReleaseComObject(type);
                Marshal.FinalReleaseComObject(transform);
            }
        }
        return steps;
    }

    /// <summary>
    /// Takes each type the decoder advertises, writes one AudioSpecificConfig
    /// into it, and offers it back.
    /// </summary>
    /// <remarks>
    /// The decisive form of the question. A type built from nothing can differ
    /// from the advertised one in some way the attribute listing does not show,
    /// and then a refusal says nothing about the codec; a type the decoder
    /// itself handed over, with <em>only</em> the config changed, cannot. So if
    /// this is refused for object type 39 and accepted for object type 2, the
    /// refusal is about AAC-ELD and nothing else.
    /// </remarks>
    public static List<Attempt> TryAdvertisedWithConfig(params (string Label, byte[] Config)[] cases)
    {
        MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, MfNative.CoinitMultithreaded), "CoInitializeEx");
        MfNative.Check(MfNative.MFStartup(MfNative.MfVersion, MfNative.MfStartupLite), "MFStartup");
        var results = new List<Attempt>();
        foreach (var (label, config) in cases)
        {
            for (uint index = 0; index < 8; index++)
            {
                MfNative.Check(MfNative.CoCreateInstance(ref MfGuids.ClsidAacDecoder, IntPtr.Zero,
                    MfNative.ClsCtxInprocServer, ref MfGuids.IidTransform, out object instance),
                    "CoCreateInstance(AAC MFT)");
                var transform = (IMFTransform)instance;
                try
                {
                    int hr = transform.GetInputAvailableType(0, index, out IMFMediaType? type);
                    if (hr == MfNative.NoMoreTypes || type is null)
                        break;
                    MfNative.Check(hr, "GetInputAvailableType");
                    byte[] userData = UserData(config, 0x29);
                    int written = WriteBlob(type, ref MfGuids.UserDataKey, userData);
                    int accepted = written == MfNative.Ok ? transform.SetInputType(0, type, 0) : written;
                    results.Add(new Attempt(label, $"type annonce {index} + AudioSpecificConfig",
                        config, AudioSpecificConfig.Parse(config), accepted));
                    Marshal.FinalReleaseComObject(type);
                }
                finally
                {
                    Marshal.FinalReleaseComObject(transform);
                }
            }
        }
        return results;
    }

    /// <summary>
    /// What input types the decoder advertises, attribute by attribute.
    /// </summary>
    /// <remarks>
    /// An MFT's available input types are it saying what it wants, which beats
    /// guessing from documentation: every attribute of every offered type is
    /// listed here, named where the GUID is one this project knows and printed
    /// raw where it is not.
    /// </remarks>
    public static List<string> DescribeInputTypes()
    {
        MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, MfNative.CoinitMultithreaded), "CoInitializeEx");
        MfNative.Check(MfNative.MFStartup(MfNative.MfVersion, MfNative.MfStartupLite), "MFStartup");
        MfNative.Check(MfNative.CoCreateInstance(ref MfGuids.ClsidAacDecoder, IntPtr.Zero,
            MfNative.ClsCtxInprocServer, ref MfGuids.IidTransform, out object instance), "CoCreateInstance(AAC MFT)");
        var transform = (IMFTransform)instance;
        var lines = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                int hr = transform.GetInputAvailableType(0, index, out IMFMediaType? type);
                if (hr == MfNative.NoMoreTypes || type is null)
                    break;
                MfNative.Check(hr, "GetInputAvailableType");
                lines.Add($"type d'entree {index} :");
                if (type.GetCount(out uint count) == MfNative.Ok)
                {
                    for (uint slot = 0; slot < count; slot++)
                    {
                        if (type.GetItemByIndex(slot, out Guid key, IntPtr.Zero) != MfNative.Ok)
                            continue;
                        lines.Add($"  {Name(key)}{Value(type, key)}");
                    }
                }
                // The control of the controls: the MFT's own advertised type,
                // offered back to it untouched. A refusal here would mean the
                // interop is wrong rather than the type.
                int accepted = transform.SetInputType(0, type, 0);
                lines.Add($"  rendu tel quel a SetInputType -> HRESULT 0x{accepted:X8}");
                Marshal.FinalReleaseComObject(type);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(transform);
        }
        return lines;
    }

    /// <summary>The attribute's name where this project knows the GUID, the GUID itself otherwise.</summary>
    private static string Name(Guid key) =>
        key == MfGuids.MajorTypeKey ? "MF_MT_MAJOR_TYPE"
        : key == MfGuids.SubtypeKey ? "MF_MT_SUBTYPE"
        : key == MfGuids.AudioNumChannelsKey ? "MF_MT_AUDIO_NUM_CHANNELS"
        : key == MfGuids.AudioSamplesPerSecondKey ? "MF_MT_AUDIO_SAMPLES_PER_SECOND"
        : key == MfGuids.AudioBitsPerSampleKey ? "MF_MT_AUDIO_BITS_PER_SAMPLE"
        : key == MfGuids.AudioBlockAlignmentKey ? "MF_MT_AUDIO_BLOCK_ALIGNMENT"
        : key == MfGuids.AudioAvgBytesPerSecondKey ? "MF_MT_AUDIO_AVG_BYTES_PER_SECOND"
        : key == MfGuids.UserDataKey ? "MF_MT_USER_DATA"
        : key == MfGuids.AacPayloadTypeKey ? "MF_MT_AAC_PAYLOAD_TYPE"
        : key == MfGuids.AacProfileLevelKey ? "MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION"
        : key.ToString();

    /// <summary>The value, when it is one of the two shapes worth reading here.</summary>
    private static string Value(IMFMediaType type, Guid key)
    {
        if (type.GetGUID(ref key, out Guid guid) == MfNative.Ok)
            return $" = {guid}";
        if (type.GetUINT32(ref key, out uint number) == MfNative.Ok)
            return $" = {number}";
        if (ReadBlob(type, ref key) is byte[] blob)
            return $" = {blob.Length} octets, {Convert.ToHexString(blob)}";
        return "";
    }

    /// <summary>Builds the AAC input type around one AudioSpecificConfig and offers it.</summary>
    private static int SetInputType(IMFTransform transform, byte[] audioSpecificConfig, TypeShape shape)
    {
        MfNative.Check(MfNative.MFCreateMediaType(out IMFMediaType type), "MFCreateMediaType");
        try
        {
            MfNative.Check(type.SetGUID(ref MfGuids.MajorTypeKey, ref MfGuids.MediaTypeAudio), "MF_MT_MAJOR_TYPE");
            MfNative.Check(type.SetGUID(ref MfGuids.SubtypeKey, ref MfGuids.AudioFormatAac), "MF_MT_SUBTYPE");
            MfNative.Check(type.SetUINT32(ref MfGuids.AudioSamplesPerSecondKey, 48000), "MF_MT_AUDIO_SAMPLES_PER_SECOND");
            MfNative.Check(type.SetUINT32(ref MfGuids.AudioNumChannelsKey, shape.Channels), "MF_MT_AUDIO_NUM_CHANNELS");
            MfNative.Check(type.SetUINT32(ref MfGuids.AacPayloadTypeKey, 0), "MF_MT_AAC_PAYLOAD_TYPE");
            if (shape.BitsPerSample != 0)
                MfNative.Check(type.SetUINT32(ref MfGuids.AudioBitsPerSampleKey, shape.BitsPerSample), "MF_MT_AUDIO_BITS_PER_SAMPLE");
            if (shape.BlockAlignment != 0)
                MfNative.Check(type.SetUINT32(ref MfGuids.AudioBlockAlignmentKey, shape.BlockAlignment), "MF_MT_AUDIO_BLOCK_ALIGNMENT");
            if (shape.AvgBytesPerSecond is uint average)
                MfNative.Check(type.SetUINT32(ref MfGuids.AudioAvgBytesPerSecondKey, average), "MF_MT_AUDIO_AVG_BYTES_PER_SECOND");
            if (shape.ProfileLevel is uint level)
                MfNative.Check(type.SetUINT32(ref MfGuids.AacProfileLevelKey, level), "MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION");
            if (shape.PreferWaveFormatEx)
                MfNative.Check(type.SetUINT32(ref MfGuids.PreferWaveFormatExKey, 1), "MF_MT_AUDIO_PREFER_WAVEFORMATEX");
            byte[] userData = UserData(shape.IncludeConfig ? audioSpecificConfig : [], shape.ProfileLevel ?? 0x29);
            MfNative.Check(WriteBlob(type, ref MfGuids.UserDataKey, userData), "MF_MT_USER_DATA");
            return transform.SetInputType(0, type, 0);
        }
        finally
        {
            Marshal.FinalReleaseComObject(type);
        }
    }

    /// <summary>
    /// Writes one blob attribute through a native buffer.
    /// </summary>
    /// <remarks>
    /// The buffer is allocated and copied by hand because a <c>byte[]</c>
    /// parameter on a COM interface method defaults to
    /// <c>UnmanagedType.SafeArray</c>: declared that way, <c>SetBlob</c>
    /// returned S_OK and stored twelve zero bytes whatever was handed to it,
    /// which cost an afternoon and looked exactly like a decoder refusing a
    /// codec.
    /// </remarks>
    private static int WriteBlob(IMFMediaType type, ref Guid key, byte[] value)
    {
        IntPtr buffer = Marshal.AllocCoTaskMem(Math.Max(1, value.Length));
        try
        {
            Marshal.Copy(value, 0, buffer, value.Length);
            return type.SetBlob(ref key, buffer, (uint)value.Length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>Reads one blob attribute back, or null when there is none.</summary>
    private static byte[]? ReadBlob(IMFMediaType type, ref Guid key)
    {
        if (type.GetBlobSize(ref key, out uint size) != MfNative.Ok || size == 0)
            return null;
        IntPtr buffer = Marshal.AllocCoTaskMem((int)size);
        try
        {
            if (type.GetBlob(ref key, buffer, size, out uint read) != MfNative.Ok)
                return null;
            byte[] data = new byte[Math.Min(read, size)];
            Marshal.Copy(buffer, data, 0, data.Length);
            return data;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>The HEAACWAVEINFO tail — payload type 0, raw config — followed by the config.</summary>
    private static byte[] UserData(byte[] audioSpecificConfig, uint profileLevel)
    {
        byte[] data = new byte[12 + audioSpecificConfig.Length];
        data[0] = 0; data[1] = 0;                                  // wPayloadType = 0: the config below is raw
        data[2] = (byte)profileLevel; data[3] = (byte)(profileLevel >> 8);
        data[4] = 0; data[5] = 0;                                  // wStructType = 0
        // wReserved1 and wReserved2 stay zero.
        audioSpecificConfig.CopyTo(data, 12);
        return data;
    }
}
