using System.Runtime.InteropServices;

namespace LuminaMonitor.Core.Audio;

/// <summary>
/// A <c>WAVEFORMATEX</c>, read and written by hand, and the one conversion the
/// project does itself.
/// </summary>
/// <remarks>
/// Normally there is nothing to convert: the stream is opened at 48 kHz, stereo,
/// 32-bit float — exactly what the decoder produces — and
/// <c>AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM</c> has the audio engine match the
/// endpoint. That path is a copy.
///
/// <para>The other path exists because a driver may refuse that flag. Then the
/// only format certain to be accepted is the engine's own mix format, and the
/// samples have to be laid out to suit it. Two differences are handled here —
/// how many channels it wants, and whether it wants floats or 16-bit integers —
/// and the third is refused rather than bodged: a mix format at another sample
/// rate would need a resampler, and a resampler written in an afternoon is a
/// resampler that sounds wrong. See <see cref="WasapiOutput"/> for what it says
/// when that happens.</para>
/// </remarks>
internal readonly record struct AudioFormat(int SampleRate, int Channels, bool Float, int Bits)
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    /// <summary>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT, the subformat an extensible float mix format carries.</summary>
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>What the chain produces: 48 kHz, stereo, 32-bit float.</summary>
    public static AudioFormat Source { get; } = new(48_000, 2, true, 32);

    public int BytesPerFrame => Channels * Bits / 8;

    /// <summary>True when the samples can be copied rather than converted.</summary>
    public bool IsSource => SampleRate == Source.SampleRate && Channels == Source.Channels && Float && Bits == 32;

    public override string ToString() =>
        $"{SampleRate} Hz, {Channels} canaux, {(Float ? "flottant" : "entier")} {Bits} bits";

    /// <summary>A <c>WAVEFORMATEX</c> in CoTaskMem, which the caller frees.</summary>
    public IntPtr Allocate()
    {
        IntPtr format = Marshal.AllocCoTaskMem(18);
        Marshal.WriteInt16(format, 0, (short)(Float ? FormatFloat : FormatPcm));
        Marshal.WriteInt16(format, 2, (short)Channels);
        Marshal.WriteInt32(format, 4, SampleRate);
        Marshal.WriteInt32(format, 8, SampleRate * BytesPerFrame);   // nAvgBytesPerSec
        Marshal.WriteInt16(format, 12, (short)BytesPerFrame);        // nBlockAlign
        Marshal.WriteInt16(format, 14, (short)Bits);
        Marshal.WriteInt16(format, 16, 0);                           // cbSize
        return format;
    }

    /// <summary>
    /// Reads what <c>GetMixFormat</c> handed over.
    /// </summary>
    /// <remarks>
    /// The engine's format is almost always <c>WAVE_FORMAT_EXTENSIBLE</c>, whose
    /// tag says nothing about the samples: the kind is in the subformat GUID, 24
    /// bytes in — after the eighteen of <c>WAVEFORMATEX</c>, the valid bits and
    /// the channel mask. Reading the tag alone and calling it PCM is how float
    /// samples come out as noise.
    /// </remarks>
    public static AudioFormat Read(IntPtr format)
    {
        ushort tag = (ushort)Marshal.ReadInt16(format, 0);
        int channels = (ushort)Marshal.ReadInt16(format, 2);
        int rate = Marshal.ReadInt32(format, 4);
        int bits = (ushort)Marshal.ReadInt16(format, 14);
        bool isFloat = tag == FormatFloat;
        if (tag == FormatExtensible && (ushort)Marshal.ReadInt16(format, 16) >= 22)
            isFloat = Marshal.PtrToStructure<Guid>(format + 24) == SubtypeFloat;
        return new AudioFormat(rate, channels, isFloat, bits);
    }

    /// <summary>
    /// Writes interleaved stereo floats into an endpoint buffer in this format.
    /// </summary>
    /// <remarks>
    /// Channels beyond the second are left silent and a single-channel endpoint
    /// gets the mean of the pair: a hands-free-shaped output should carry the
    /// sound, not half of it. Nothing here changes the sample rate — the caller
    /// has already made sure it matches.
    /// </remarks>
    public unsafe void Write(ReadOnlySpan<float> source, IntPtr destination, int frames)
    {
        if (IsSource)
        {
            source[..(frames * Source.Channels)]
                .CopyTo(new Span<float>((void*)destination, frames * Source.Channels));
            return;
        }

        byte* target = (byte*)destination;
        new Span<byte>(target, frames * BytesPerFrame).Clear();
        int width = Bits / 8;
        for (int frame = 0; frame < frames; frame++)
        {
            float left = source[frame * Source.Channels];
            float right = source[(frame * Source.Channels) + 1];
            for (int channel = 0; channel < Channels && channel < 2; channel++)
            {
                float sample = Channels == 1 ? (left + right) * 0.5f : channel == 0 ? left : right;
                byte* at = target + (frame * BytesPerFrame) + (channel * width);
                if (Float && Bits == 32)
                    *(float*)at = sample;
                else if (!Float && Bits == 16)
                    *(short*)at = (short)Math.Clamp(MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);
                else if (!Float && Bits == 32)
                    *(int*)at = (int)Math.Clamp(Math.Round((double)sample * int.MaxValue), int.MinValue, int.MaxValue);
            }
        }
    }

    /// <summary>True when <see cref="Write"/> knows how to lay samples out in this format.</summary>
    public bool IsWritable =>
        SampleRate == Source.SampleRate && Channels >= 1 && ((Float && Bits == 32) || (!Float && Bits is 16 or 32));
}
