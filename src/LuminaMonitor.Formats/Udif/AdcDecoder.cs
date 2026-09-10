namespace LuminaMonitor.Formats;

/// <summary>
/// Decoder for ADC (Apple Data Compression), the LZ77 variant of early
/// UDIF images (run type 0x80000004).
/// </summary>
/// <remarks>
/// The stream is a sequence of chunks selected by the top bits of their
/// first byte, as published in the format descriptions:
/// <list type="bullet">
/// <item>bit 0x80 set: literal chunk, (b &amp; 0x7F) + 1 bytes follow verbatim;</item>
/// <item>bit 0x40 set: three-byte back-reference, length (b &amp; 0x3F) + 4,
/// distance (next &lt;&lt; 8 | next2) + 1;</item>
/// <item>otherwise: two-byte back-reference, length ((b &amp; 0x3F) &gt;&gt; 2) + 3,
/// distance ((b &amp; 3) &lt;&lt; 8 | next) + 1.</item>
/// </list>
/// The three-byte form is the "long" one (16-bit distance); the two-byte
/// form is the "short" one (10-bit distance). Back-references may overlap
/// their own output, so they are copied one byte at a time.
/// </remarks>
internal static class AdcDecoder
{
    /// <summary>Decodes into <paramref name="output"/> and returns the number of bytes produced.</summary>
    /// <remarks>Stops when the output is full, so an over-long run cannot overflow the sector buffer.</remarks>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int i = 0, o = 0;
        while (i < input.Length && o < output.Length)
        {
            byte b = input[i++];
            if ((b & 0x80) != 0)
            {
                int length = (b & 0x7F) + 1;
                if (i + length > input.Length)
                    throw new InvalidDataException("ADC literal chunk runs past the end of the input");
                int n = Math.Min(length, output.Length - o);
                input.Slice(i, n).CopyTo(output.Slice(o, n));
                i += length;
                o += n;
                continue;
            }

            int copy, distance;
            if ((b & 0x40) != 0)
            {
                if (i + 2 > input.Length)
                    throw new InvalidDataException("ADC long back-reference truncated");
                copy = (b & 0x3F) + 4;
                distance = ((input[i] << 8) | input[i + 1]) + 1;
                i += 2;
            }
            else
            {
                if (i + 1 > input.Length)
                    throw new InvalidDataException("ADC short back-reference truncated");
                copy = ((b & 0x3F) >> 2) + 3;
                distance = (((b & 0x03) << 8) | input[i]) + 1;
                i += 1;
            }

            if (distance > o)
                throw new InvalidDataException($"ADC back-reference of distance {distance} at output offset {o}");
            int count = Math.Min(copy, output.Length - o);
            for (int k = 0; k < count; k++, o++)
                output[o] = output[o - distance];
        }
        return o;
    }
}
