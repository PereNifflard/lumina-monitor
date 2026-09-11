namespace LuminaMonitor.Core.Media.Aac;

/// <summary>
/// What a frame that cannot be read says about itself: where it stopped making
/// sense, and why.
/// </summary>
/// <remarks>
/// An AAC frame carries no lengths and no synchronisation word inside itself:
/// every field's position depends on every field before it, so the first wrong
/// bit is not detected where it is but wherever the syntax next becomes
/// impossible. The bit position is therefore the only useful thing to report —
/// it says how far the parse got, which is what separates "the configuration is
/// wrong" (fails in the first dozen bits, every frame) from "one packet was
/// corrupted" (fails deep in one frame only).
/// </remarks>
internal sealed class AacBitstreamException : IOException
{
    public AacBitstreamException(string reason, int bitPosition, int bitsAvailable)
        : base($"{reason} — bit {bitPosition} sur {bitsAvailable} disponibles")
    {
        Reason = reason;
        BitPosition = bitPosition;
        BitsAvailable = bitsAvailable;
    }

    /// <summary>The failure on its own, without the position.</summary>
    public string Reason { get; }

    /// <summary>How many bits had been consumed when the frame stopped parsing.</summary>
    public int BitPosition { get; }

    /// <summary>How many bits the access unit held in all.</summary>
    public int BitsAvailable { get; }
}

/// <summary>
/// A most-significant-bit-first reader over one access unit.
/// </summary>
/// <remarks>
/// <para>ISO/IEC 14496-3 §1.3.3: every bitstream field is written most
/// significant bit first, and fields are not aligned — a six-bit field may
/// straddle a byte boundary. So the reader counts bits, not bytes, and the bit
/// count is the position an error is reported at.</para>
///
/// <para>Reading past the end throws rather than returning zeroes. Returning
/// zeroes is what a lenient reader does, and it turns a truncated frame into a
/// plausible-looking one full of silence, which is exactly the failure that
/// cannot be diagnosed later. The one place zero-extension is legitimate is the
/// end-of-frame padding, and <see cref="PaddingIsZero"/> asks that question
/// explicitly.</para>
///
/// <para>A ref struct because it borrows the caller's span: the access unit is
/// read where it arrived, without a copy, so decoding a frame allocates
/// nothing.</para>
/// </remarks>
internal ref struct BitReader
{
    private readonly ReadOnlySpan<byte> bytes;
    private int at;

    public BitReader(ReadOnlySpan<byte> bytes)
    {
        this.bytes = bytes;
        at = 0;
    }

    /// <summary>How many bits have been consumed.</summary>
    public int Position => at;

    /// <summary>How many bits the access unit holds.</summary>
    public int Length => bytes.Length * 8;

    /// <summary>How many bits are left unread.</summary>
    public int Remaining => Length - at;

    /// <summary>Reads <paramref name="count"/> bits, most significant first.</summary>
    /// <exception cref="AacBitstreamException">The frame ends first.</exception>
    public uint Read(int count)
    {
        if (count is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Un champ de bitstream tient sur 0 a 32 bits.");
        if (count > Remaining)
            throw Error($"champ de {count} bits au-dela de la fin de la trame");

        uint value = 0;
        int left = count;
        while (left > 0)
        {
            int used = at & 7;
            int take = Math.Min(8 - used, left);
            int taken = (bytes[at >> 3] >> (8 - used - take)) & ((1 << take) - 1);
            value = (value << take) | (uint)taken;
            at += take;
            left -= take;
        }
        return value;
    }

    /// <summary>Reads one bit.</summary>
    /// <exception cref="AacBitstreamException">The frame ends first.</exception>
    public int ReadBit()
    {
        if (at >= Length)
            throw Error("bit au-dela de la fin de la trame");
        int bit = (bytes[at >> 3] >> (7 - (at & 7))) & 1;
        at++;
        return bit;
    }

    /// <summary>Reads one bit as a flag.</summary>
    public bool ReadFlag() => ReadBit() != 0;

    /// <summary>Skips whole bytes, for a payload this decoder does not read.</summary>
    public void SkipBytes(int count)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Longueur negative.");
        if (count * 8 > Remaining)
            throw Error($"saut de {count} octets au-dela de la fin de la trame");
        at += count * 8;
    }

    /// <summary>
    /// True when everything left in the frame is zero — the padding a well
    /// formed frame ends on.
    /// </summary>
    /// <remarks>
    /// ISO/IEC 14496-3 §4.4.2.1: the access unit is padded to a byte boundary,
    /// so up to seven bits may follow the last field. Anything non-zero there,
    /// or more than seven bits of it, means the parse and the encoder disagree
    /// about where the frame ends — which is a syntax defect even when every
    /// sample came out finite.
    /// </remarks>
    public bool PaddingIsZero()
    {
        for (int bit = at; bit < Length; bit++)
        {
            if (((bytes[bit >> 3] >> (7 - (bit & 7))) & 1) != 0)
                return false;
        }
        return true;
    }

    /// <summary>The exception for a frame that cannot be read here; the caller throws it.</summary>
    public AacBitstreamException Error(string reason) => new(reason, at, Length);
}
