namespace LuminaMonitor.Core.Media;

/// <summary>
/// What one sequence parameter set says: the fields that must survive a rewrite
/// untouched, and the three about reordering that the rewrite exists to change.
/// </summary>
internal readonly record struct SpsInfo(
    int ProfileIdc, int LevelIdc, int ChromaFormatIdc, int MaxNumRefFrames,
    int WidthMbs, int HeightMapUnits, bool FrameMbsOnly,
    int CropLeft, int CropRight, int CropTop, int CropBottom,
    bool HasVui, bool HasRestriction, int MaxNumReorderFrames, int MaxDecFrameBuffering)
{
    public int Width => WidthMbs * 16;
    public int Height => HeightMapUnits * 16 * (FrameMbsOnly ? 1 : 2);
    public override string ToString() =>
        $"profil {ProfileIdc} niveau {LevelIdc / 10.0:F1} chroma {ChromaFormatIdc} {Width}x{Height}"
        + $" ({WidthMbs}x{HeightMapUnits} MB) ref {MaxNumRefFrames} frame_mbs_only {(FrameMbsOnly ? 1 : 0)}"
        + $" rognage {CropLeft}/{CropRight}/{CropTop}/{CropBottom} VUI {(HasVui ? "oui" : "non")}"
        + $" restriction {(HasRestriction ? $"oui (reorder {MaxNumReorderFrames}, dpb {MaxDecFrameBuffering})" : "non")}";
}

/// <summary>Rewrites the stream's SPS so it admits that nothing is ever reordered.</summary>
/// <remarks>
/// The phone sends High profile at level 5.1 for a 1328x2896 picture and a VUI
/// with no <c>bitstream_restriction_flag</c>. A decoder reading that has to assume
/// the worst the level allows — <c>MaxDpbMbs(5.1) / (83 * 181)</c> capped at
/// sixteen is twelve pictures — so it holds twelve before handing the first one
/// back, a fifth of a second at sixty a second and measured exactly that way here.
/// This stream has no B pictures at all, so the true reorder depth is zero; saying
/// so is the whole fix, and the decoder then lets each picture out as it finishes.
///
/// <para>Every field is exp-Golomb or a bare flag, so nothing is byte-aligned and
/// nothing can be patched in place: the walk copies each field straight back out
/// until <c>bitstream_restriction_flag</c>, the last thing in a VUI, and the tail is
/// then ours. Emulation prevention comes out before the walk and goes back after —
/// <c>00 00 03</c> is transport, not syntax.</para>
/// </remarks>
internal static class SpsRewriter
{
    /// <param name="sps">The SPS NAL, header byte included, no start code; <paramref name="info"/> reports what it said.</param>
    public static byte[] Rewrite(ReadOnlySpan<byte> sps, out SpsInfo info)
    {
        if (sps.Length < 5 || (sps[0] & 0x1F) != 7)
            throw new InvalidDataException($"pas un SPS : {sps.Length} octet(s), type {(sps.Length > 0 ? sps[0] & 0x1F : -1)}.");

        var bits = new Bits(Unescape(sps[1..]));
        int profile = (int)bits.CopyBits(8);
        bits.CopyBits(8);                                   // constraint flags and reserved bits
        int level = (int)bits.CopyBits(8);
        bits.CopyUe();                                      // seq_parameter_set_id
        int chroma = 1;
        if (IsHighProfile(profile))
        {
            chroma = (int)bits.CopyUe();
            if (chroma == 3) bits.CopyBits(1);              // separate_colour_plane_flag
            bits.CopyUe(); bits.CopyUe();                   // bit depths, luma then chroma
            bits.CopyBits(1);                               // qpprime_y_zero_transform_bypass_flag
            if (bits.CopyBits(1) != 0)                      // seq_scaling_matrix_present_flag
                for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                    if (bits.CopyBits(1) != 0)
                        ScalingList(bits, i < 6 ? 16 : 64);
        }
        bits.CopyUe();                                      // log2_max_frame_num_minus4
        uint pocType = bits.CopyUe();
        if (pocType == 0) bits.CopyUe();                    // log2_max_pic_order_cnt_lsb_minus4
        else if (pocType == 1)
        {
            bits.CopyBits(1);                               // delta_pic_order_always_zero_flag
            bits.CopySe(); bits.CopySe();                   // offsets: non-ref picture, top to bottom field
            uint cycle = bits.CopyUe();
            for (uint i = 0; i < cycle; i++) bits.CopySe(); // offset_for_ref_frame[i]
        }
        int refFrames = (int)bits.CopyUe();
        bits.CopyBits(1);                                   // gaps_in_frame_num_value_allowed_flag
        int widthMbs = (int)bits.CopyUe() + 1;
        int heightUnits = (int)bits.CopyUe() + 1;
        bool frameMbsOnly = bits.CopyBits(1) != 0;
        if (!frameMbsOnly) bits.CopyBits(1);                // mb_adaptive_frame_field_flag
        bits.CopyBits(1);                                   // direct_8x8_inference_flag
        int left = 0, right = 0, top = 0, bottom = 0;
        if (bits.CopyBits(1) != 0)                          // frame_cropping_flag
            (left, right, top, bottom) =
                ((int)bits.CopyUe(), (int)bits.CopyUe(), (int)bits.CopyUe(), (int)bits.CopyUe());

        // From here the output stops following the input: a VUI with a restriction, always.
        bool hasVui = bits.ReadBit() != 0;
        bits.WriteBit(1);
        (bool hadRestriction, int reorder, int buffering) = (false, -1, -1);
        if (hasVui) (hadRestriction, reorder, buffering) = CopyVui(bits);
        else bits.WriteBits(0, 8);                          // eight "not present" flags, up to pic_struct
        WriteRestriction(bits, (uint)refFrames);

        info = new SpsInfo(profile, level, chroma, refFrames, widthMbs, heightUnits, frameMbsOnly,
            left, right, top, bottom, hasVui, hadRestriction, reorder, buffering);
        return Escape(sps[0], bits.Finish());
    }

    /// <summary>Reads a set without rewriting it, for the checks and the log.</summary>
    public static SpsInfo Parse(ReadOnlySpan<byte> sps) { Rewrite(sps, out SpsInfo info); return info; }

    /// <summary>A coded slice.s type, folded to 0=P 1=B 2=I 3=SP 4=SI, or -1 for anything else:
    /// how the "no B pictures" claim is checked rather than believed.</summary>
    public static int SliceType(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 2 || (nal[0] & 0x1F) is not (1 or 5)) return -1;
        var bits = new Bits(Unescape(nal[1..Math.Min(nal.Length, 16)]));
        bits.ReadUe();                                      // first_mb_in_slice
        return (int)(bits.ReadUe() % 5);
    }

    /// <summary>Copies the VUI up to its restriction, and reads the old one if there was one.</summary>
    private static (bool Had, int Reorder, int Buffering) CopyVui(Bits bits)
    {
        // aspect_ratio_info_present_flag, then aspect_ratio_idc; 255 means the SAR follows.
        if (bits.CopyBits(1) != 0 && bits.CopyBits(8) == 255) { bits.CopyBits(16); bits.CopyBits(16); }
        if (bits.CopyBits(1) != 0) bits.CopyBits(1);        // overscan_appropriate_flag
        if (bits.CopyBits(1) != 0)                          // video_signal_type_present_flag
        {
            bits.CopyBits(4);                               // video_format, video_full_range_flag
            if (bits.CopyBits(1) != 0) bits.CopyBits(24);   // primaries, transfer, matrix
        }
        if (bits.CopyBits(1) != 0) { bits.CopyUe(); bits.CopyUe(); }    // chroma_loc_info
        if (bits.CopyBits(1) != 0) { bits.CopyBits(32); bits.CopyBits(32); bits.CopyBits(1); }
        bool nalHrd = bits.CopyBits(1) != 0; if (nalHrd) Hrd(bits);
        bool vclHrd = bits.CopyBits(1) != 0; if (vclHrd) Hrd(bits);
        if (nalHrd || vclHrd) bits.CopyBits(1);             // low_delay_hrd_flag
        bits.CopyBits(1);                                   // pic_struct_present_flag
        // The flag itself is not copied: WriteRestriction puts it back, so the
        // branch with no VUI at all writes exactly the same tail as this one.
        bool had = bits.ReadBit() != 0;
        if (!had) return (false, -1, -1);
        bits.ReadBit();                                     // motion_vectors_over_pic_boundaries_flag
        bits.ReadUe(); bits.ReadUe(); bits.ReadUe(); bits.ReadUe();
        return (true, (int)bits.ReadUe(), (int)bits.ReadUe());
    }

    private static void WriteRestriction(Bits bits, uint refFrames)
    {
        bits.WriteBit(1);                                   // bitstream_restriction_flag
        bits.WriteBit(1);                                   // motion_vectors_over_pic_boundaries_flag
        bits.WriteUe(0);                                    // max_bytes_per_pic_denom: no claim
        bits.WriteUe(0);                                    // max_bits_per_mb_denom: no claim
        bits.WriteUe(16);                                   // log2_max_mv_length_horizontal: the ceiling
        bits.WriteUe(16);                                   // log2_max_mv_length_vertical: idem
        bits.WriteUe(0);                                    // max_num_reorder_frames: the point of all this
        bits.WriteUe(refFrames);                            // max_dec_frame_buffering
    }

    private static void Hrd(Bits bits)
    {
        uint cpbCount = bits.CopyUe();                      // cpb_cnt_minus1
        bits.CopyBits(8);                                   // bit_rate_scale, cpb_size_scale
        for (uint i = 0; i <= cpbCount; i++) { bits.CopyUe(); bits.CopyUe(); bits.CopyBits(1); }
        bits.CopyBits(20);                                  // four five-bit length fields
    }

    /// <summary>A scaling list stops being coded the moment a delta brings the scale to zero.</summary>
    private static void ScalingList(Bits bits, int size)
    {
        int next = 8;
        for (int i = 0; i < size && next != 0; i++) next = (next + bits.CopySe() + 256) % 256;
    }

    private static bool IsHighProfile(int profile) =>
        profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135;

    private static byte[] Unescape(ReadOnlySpan<byte> payload)
    {
        var raw = new List<byte>(payload.Length);
        int zeros = 0;
        foreach (byte value in payload)
        {
            if (zeros == 2 && value == 3) { zeros = 0; continue; }
            raw.Add(value); zeros = value == 0 ? zeros + 1 : 0;
        }
        return [.. raw];
    }

    private static byte[] Escape(byte header, byte[] raw)
    {
        var nal = new List<byte>(raw.Length + 8) { header };
        int zeros = 0;
        foreach (byte value in raw)
        {
            if (zeros == 2 && value <= 3) { nal.Add(3); zeros = 0; }
            nal.Add(value); zeros = value == 0 ? zeros + 1 : 0;
        }
        return [.. nal];
    }

    // --- The bit engine -----------------------------------------------------------

    /// <summary>An RBSP read head and a write head, moving together while the fields match.</summary>
    private sealed class Bits(byte[] raw)
    {
        private readonly List<byte> _out = new(64);
        private int _at, _pending, _accumulator;

        /// <summary>Past the end reads as zero: a truncated set gives absurd fields, not an exception mid-walk.</summary>
        public int ReadBit()
        {
            int bit = (_at >> 3) < raw.Length ? (raw[_at >> 3] >> (7 - (_at & 7))) & 1 : 0;
            _at++;
            return bit;
        }

        /// <summary>Exp-Golomb ue(v): as many leading zeros as the coded value has bits after its first.</summary>
        public uint ReadUe()
        {
            int zeros = 0;
            while (zeros < 32 && ReadBit() == 0) zeros++;
            if (zeros >= 32) throw new InvalidDataException("code exp-Golomb aberrant.");
            return zeros == 0 ? 0 : ((1u << zeros) - 1) + ReadBits(zeros);
        }

        public void WriteUe(uint value)
        {
            uint shifted = value + 1;
            int length = 0;
            while (shifted >> length != 0) length++;
            WriteBits(0, length - 1);
            WriteBits(shifted, length);
        }

        /// <summary>Eight bits at a time is the only alignment this format ever has; the rest is bit by bit.</summary>
        public void WriteBit(int bit)
        {
            _accumulator = (_accumulator << 1) | (bit & 1);
            if (++_pending != 8) return;
            _out.Add((byte)_accumulator);
            _accumulator = _pending = 0;
        }

        public uint ReadBits(int n) { uint v = 0; for (int i = 0; i < n; i++) v = (v << 1) | (uint)ReadBit(); return v; }
        public int ReadSe() { uint c = ReadUe(); return (c & 1) != 0 ? (int)((c + 1) / 2) : -(int)(c / 2); }
        public void WriteBits(uint v, int n) { for (int i = n - 1; i >= 0; i--) WriteBit((int)(v >> i) & 1); }
        public void WriteSe(int v) => WriteUe(v <= 0 ? (uint)(-2 * v) : (uint)(2 * v - 1));
        public uint CopyBits(int n) { uint v = ReadBits(n); WriteBits(v, n); return v; }
        public uint CopyUe() { uint v = ReadUe(); WriteUe(v); return v; }
        public int CopySe() { int v = ReadSe(); WriteSe(v); return v; }

        /// <summary>Closes the RBSP: the stop bit, then zeros to the byte.</summary>
        public byte[] Finish()
        {
            WriteBit(1);
            while (_pending != 0) WriteBit(0);
            return [.. _out];
        }
    }
}
