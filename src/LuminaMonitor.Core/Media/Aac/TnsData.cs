namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// One temporal noise shaping filter, as the bitstream describes it.
/// </summary>
/// <remarks>
/// The coefficients are kept quantized: dequantizing them is
/// <see cref="Tns"/>'s job, and it needs the resolution the whole frame shares.
/// </remarks>
internal sealed class TnsFilter
{
    /// <summary>The widest filter the low delay object types allow, from <c>TnsTables.MaxOrder</c>.</summary>
    public const int MaxOrder = 31;

    /// <summary>The first scalefactor band the filter covers.</summary>
    public int StartBand { get; set; }

    /// <summary>The first band past the filter.</summary>
    public int StopBand { get; set; }

    /// <summary>How many coefficients the filter has; zero means it does nothing.</summary>
    public int Order { get; set; }

    /// <summary>True when the filter runs down the spectrum rather than up it.</summary>
    public bool Downward { get; set; }

    /// <summary>The quantized reflection coefficients, sign extended.</summary>
    public short[] Coefficients { get; } = new short[MaxOrder];
}

/// <summary>
/// <c>tns_data()</c> for one channel of a low delay frame.
/// </summary>
/// <remarks>
/// <para><b>Syntax.</b> ISO/IEC 14496-3 §4.4.2.10 and Table 4.53 —
/// <c>get_tns()</c> of the reference software's <c>huffdec2.c</c>, long window
/// column: two bits of filter count, one bit of coefficient resolution added to
/// 3, then per filter six bits of length, five bits of order and, when the order
/// is not zero, one bit of direction, one bit of coefficient compression and the
/// coefficients themselves at <c>resolution − compression</c> bits each.</para>
///
/// <para><b>Bands counted downwards.</b> The lengths are read from the top of
/// the spectrum: the first filter stops at the last band and starts that many
/// bands lower, the second stops where the first started, and so on. A length
/// wider than what is left makes the start band negative, which is a frame this
/// decoder refuses rather than clamp — the reference clamps only under its
/// error-protection flag.</para>
///
/// <para><b>Sign extension.</b> The coefficients are two's complement in
/// <c>resolution − compression</c> bits, three or four of them, so the sign bit
/// is bit 2 or bit 3 and the value has to be extended by hand. The reference
/// does it with a pair of mask tables; the shift pair here is the same
/// arithmetic.</para>
///
/// <para>This object is reused frame after frame, so a frame that carries no TNS
/// must be told so: <see cref="Clear"/> is not optional.</para>
/// </remarks>
internal sealed class TnsFrame
{
    /// <summary>The most filters two bits can ask for.</summary>
    private const int MaxFilters = 3;

    /// <summary>How many filters this frame carries.</summary>
    public int FilterCount { get; private set; }

    /// <summary>How many bits each coefficient was quantized to, 3 or 4.</summary>
    public int CoefficientResolution { get; private set; }

    /// <summary>The filters, top of the spectrum first; only the first <see cref="FilterCount"/> are live.</summary>
    public TnsFilter[] Filters { get; } = [new TnsFilter(), new TnsFilter(), new TnsFilter()];

    /// <summary>Forgets the previous frame's filters.</summary>
    public void Clear() => FilterCount = 0;

    /// <summary>
    /// Reads the filters of one channel.
    /// </summary>
    /// <param name="reader">The bitstream, positioned on the filter count.</param>
    /// <param name="bandCount">How many scalefactor bands the frame has.</param>
    /// <param name="maxOrder">The order ceiling for this sampling frequency.</param>
    /// <exception cref="AacBitstreamException">The filters do not fit the spectrum, or the frame ends.</exception>
    public void Read(ref BitReader reader, int bandCount, int maxOrder)
    {
        Clear();
        int filters = (int)reader.Read(2);
        if (filters == 0)
            return;
        if (filters > MaxFilters)
            throw reader.Error($"{filters} filtres TNS pour un maximum de {MaxFilters}");

        CoefficientResolution = (int)reader.Read(1) + 3;
        int top = bandCount;
        for (int index = 0; index < filters; index++)
        {
            var filter = Filters[index];
            filter.StopBand = top;
            int length = (int)reader.Read(6);
            top -= length;
            filter.StartBand = top;
            if (top < 0)
                throw reader.Error($"filtre TNS {index} large de {length} bandes, en dessous de la bande 0");

            filter.Order = (int)reader.Read(5);
            if (filter.Order > maxOrder)
                throw reader.Error($"ordre TNS {filter.Order} au-dela du plafond {maxOrder}");
            if (filter.Order == 0)
                continue;

            filter.Downward = reader.ReadFlag();
            int compression = (int)reader.Read(1);
            int bits = CoefficientResolution - compression;
            int shift = 32 - bits;
            for (int coefficient = 0; coefficient < filter.Order; coefficient++)
            {
                // Two's complement in `bits` bits: shift the sign bit up to bit 31
                // and back down, and the arithmetic shift extends it.
                filter.Coefficients[coefficient] = (short)(((int)reader.Read(bits) << shift) >> shift);
            }
        }
        FilterCount = filters;
    }
}
