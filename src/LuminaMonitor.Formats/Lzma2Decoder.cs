using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LuminaMonitor.Formats;

/// <summary>
/// Read-only stream that decodes LZMA2 data (the filter xz uses) as it is read.
/// </summary>
/// <remarks>
/// <para>
/// Implemented from the public-domain LZMA specification (lzma-specification.txt in the
/// LZMA SDK, 7-zip.org) for the range coder, the literal / length / distance models and
/// the state machine, plus the LZMA2 chunk layer the xz file-format specification refers
/// to (control byte, big-endian sizes, dictionary / state / property resets). No code from
/// any existing decoder was consulted or copied.
/// </para>
/// <para>
/// WHY a hand-rolled decoder: Windows has no xz / LZMA support and the project ships no
/// third-party code, yet Apple's Developer Disk Images arrive as pbzx streams whose chunks
/// are xz streams using exactly this one filter.
/// </para>
/// <para>
/// Design: the sliding dictionary is a circular byte array; the decoder writes into it and
/// <see cref="Read(Span{byte})"/> copies out of it, so each decoded byte is written once.
/// The hot loop works on locals and plain arrays (no virtual call per byte): the literal
/// path carries the range coder in plain locals (the JIT enregisters them), the rarer
/// match path goes through <see cref="RangeDecoder"/> helpers. Each compressed chunk (at
/// most 64 KiB) is loaded whole before decoding, which turns every input read of the range
/// coder into an array index. Measured against liblzma on the same machine: about 70 % of
/// its throughput on binaries, well above disk speed for a one-off unpack.
/// </para>
/// </remarks>
internal sealed class Lzma2Decoder : Stream
{
    // ----- Model geometry, straight from the LZMA specification -----
    private const int NumStates = 12;
    private const int PosBitsMax = 4;
    private const int NumPosStatesMax = 1 << PosBitsMax;
    private const int MatchMinLen = 2;
    private const int LenLowBits = 3;
    private const int LenMidBits = 3;
    private const int LenHighBits = 8;
    private const int LenLowSymbols = 1 << LenLowBits;
    private const int LenMidSymbols = 1 << LenMidBits;
    private const int LenHighSymbols = 1 << LenHighBits;
    private const int NumLenToPosStates = 4;
    private const int PosSlotBits = 6;
    private const int EndPosModelIndex = 14;
    private const int NumFullDistances = 1 << (EndPosModelIndex >> 1);
    private const int AlignBits = 4;
    private const int BitModelTotalBits = 11;
    private const ushort ProbInit = 1 << (BitModelTotalBits - 1);
    private const uint TopValue = 1u << 24;
    private const int MaxLcLp = 4; // LZMA2 restriction: lc + lp <= 4

    // ----- Layout of the single probability array (one array = one bounds-check base) -----
    private const int IsMatch = 0;
    private const int IsRep = IsMatch + (NumStates << PosBitsMax);
    private const int IsRepG0 = IsRep + NumStates;
    private const int IsRepG1 = IsRepG0 + NumStates;
    private const int IsRepG2 = IsRepG1 + NumStates;
    private const int IsRep0Long = IsRepG2 + NumStates;
    private const int PosSlot = IsRep0Long + (NumStates << PosBitsMax);
    private const int SpecPos = PosSlot + (NumLenToPosStates << PosSlotBits);
    private const int Align = SpecPos + 1 + NumFullDistances - EndPosModelIndex;
    private const int LenCoder = Align + (1 << AlignBits);
    // Inside a length coder: Choice, Choice2, Low[16][8], Mid[16][8], High[256].
    private const int LenChoice = 0;
    private const int LenChoice2 = 1;
    private const int LenLow = 2;
    private const int LenMid = LenLow + (NumPosStatesMax << LenLowBits);
    private const int LenHigh = LenMid + (NumPosStatesMax << LenMidBits);
    private const int LenCoderSize = LenHigh + LenHighSymbols;
    private const int RepLenCoder = LenCoder + LenCoderSize;
    private const int Literal = RepLenCoder + LenCoderSize;
    private const int LiteralCoderSize = 0x300;
    private const int ProbsCount = Literal + (LiteralCoderSize << MaxLcLp);

    /// <summary>Largest LZMA2 chunk: 64 KiB compressed (2 MiB uncompressed).</summary>
    private const int MaxChunkPacked = 1 << 16;

    /// <summary>Smallest window ever allocated; also the smallest encodable dictionary.</summary>
    private const int MinWindow = 1 << 12;

    /// <summary>Decode at least this much per step so tiny reads do not pay the per-step setup each time.</summary>
    private const int MinStep = 1 << 15;

    private readonly BufferedByteReader _in;
    private readonly Stream? _ownedInner;
    private readonly ushort[] _probs = new ushort[ProbsCount];
    private readonly byte[] _chunk = new byte[MaxChunkPacked];

    // Dictionary window (circular).
    private readonly byte[] _window;
    private readonly int _winSize;
    private int _winPos;      // next write index
    private int _full;        // valid bytes in the window (grows to _winSize, then stays)
    private long _totalPos;   // bytes produced since the last dictionary reset (literal / pos contexts)
    private int _readPos;     // next byte to hand to the caller
    private int _pending;     // decoded bytes not yet handed to the caller

    // LZMA state carried across chunks.
    private int _lc, _lp, _pb;
    private int _state;
    private uint _rep0, _rep1, _rep2, _rep3;
    private int _matchLeft;   // bytes of the current match still to copy
    private uint _range, _code;
    private int _inPos, _inEnd;

    // Chunk layer.
    private bool _needDictReset = true;
    private bool _needProps = true;
    private bool _chunkCompressed;
    private int _chunkLeft;   // uncompressed bytes left in the current chunk
    private bool _finished;
    private long _position;

    /// <summary>Decodes LZMA2 data starting at the current position of <paramref name="inner"/>.</summary>
    /// <param name="inner">Stream positioned at the first LZMA2 control byte.</param>
    /// <param name="dictionaryProperty">The one-byte LZMA2 filter property from the xz block header.</param>
    public Lzma2Decoder(Stream inner, byte dictionaryProperty)
        : this(new BufferedByteReader(inner), dictionaryProperty, null)
    {
        _ownedInner = inner;
    }

    /// <summary>
    /// Shares an already-buffered reader with the container parser, which must keep reading
    /// (block padding, check, index) right after the LZMA2 end marker.
    /// </summary>
    /// <param name="uncompressedSizeHint">
    /// Uncompressed size of the block when the header declares it: no match can reach further
    /// back than that, so the window is shrunk to it (a 16 MiB pbzx chunk never needs more).
    /// </param>
    internal Lzma2Decoder(BufferedByteReader reader, byte dictionaryProperty, long? uncompressedSizeHint)
    {
        _in = reader;
        uint dictSize = DictionarySize(dictionaryProperty);
        long window = dictSize;
        if (uncompressedSizeHint is { } hint && hint < window)
            window = hint;
        if (window < MinWindow)
            window = MinWindow;
        if (window > Array.MaxLength)
            throw new NotSupportedException(
                $"LZMA2 dictionary of {dictSize} bytes exceeds what a single managed array can hold");
        _winSize = (int)window;
        _window = new byte[_winSize];
    }

    /// <summary>Dictionary size encoded by the LZMA2 filter property byte (xz spec 5.3.1).</summary>
    internal static uint DictionarySize(byte property)
    {
        if ((property & 0xC0) != 0)
            throw new InvalidDataException("LZMA2 filter property has reserved bits set");
        int bits = property & 0x3F;
        if (bits > 40)
            throw new InvalidDataException("LZMA2 filter property encodes a dictionary larger than 4 GiB");
        if (bits == 40)
            return uint.MaxValue;
        return (2u | (uint)(bits & 1)) << (bits / 2 + 11);
    }

    /// <summary>Bytes of LZMA2 input consumed so far (the container needs it to size the block).</summary>
    internal long CompressedPosition => _in.Position;

    // ------------------------------------------------------------------ Stream surface

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int written = 0;
        while (written < buffer.Length)
        {
            if (_pending > 0)
            {
                int n = Math.Min(_pending, buffer.Length - written);
                int first = Math.Min(n, _winSize - _readPos);
                _window.AsSpan(_readPos, first).CopyTo(buffer.Slice(written, first));
                if (n > first)
                    _window.AsSpan(0, n - first).CopyTo(buffer.Slice(written + first, n - first));
                _readPos += n;
                if (_readPos >= _winSize)
                    _readPos -= _winSize;
                _pending -= n;
                written += n;
                _position += n;
                continue;
            }

            if (_finished)
                break;
            if (_chunkLeft == 0 && !BeginChunk())
            {
                _finished = true;
                break;
            }
            Produce(buffer.Length - written);
        }
        return written;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 1 ? one[0] : -1;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _ownedInner?.Dispose();
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ LZMA2 chunk layer

    /// <summary>
    /// Reads the next chunk header. Returns false on the 0x00 end marker.
    /// </summary>
    /// <remarks>
    /// Control byte: 0x00 end; 0x01 stored chunk after a dictionary reset; 0x02 stored chunk;
    /// 0x80-0xFF LZMA chunk whose bits 5-6 say what is reset (0 nothing, 1 state, 2 state +
    /// new properties, 3 everything) and whose low 5 bits are the top bits of the size. Sizes
    /// are big-endian "minus one". The first chunk must reset the dictionary, and after a
    /// stored chunk with a dictionary reset the next LZMA chunk must carry properties, because
    /// the decoder has no probabilities or lc/lp/pb to continue from.
    /// </remarks>
    private bool BeginChunk()
    {
        Debug.Assert(_pending == 0, "chunk boundaries are only crossed with an empty output queue");
        int control = _in.ReadByte();
        if (control < 0)
            throw Truncated();
        if (control == 0x00)
            return false;

        if (control < 0x80)
        {
            if (control > 0x02)
                throw new InvalidDataException($"invalid LZMA2 control byte 0x{control:X2}");
            if (control == 0x01)
            {
                ResetDictionary();
                _needProps = true;
            }
            else if (_needDictReset)
            {
                throw new InvalidDataException("LZMA2 data does not start with a dictionary reset");
            }
            _chunkLeft = ReadBigEndian16() + 1;
            _chunkCompressed = false;
        }
        else
        {
            int mode = (control >> 5) & 3;
            if (_needDictReset && mode != 3)
                throw new InvalidDataException("LZMA2 data does not start with a dictionary reset");
            if (_needProps && mode < 2)
                throw new InvalidDataException("LZMA2 chunk lacks the properties a dictionary reset requires");

            int unpacked = ((control & 0x1F) << 16) + ReadBigEndian16() + 1;
            int packed = ReadBigEndian16() + 1;
            if (mode >= 2)
            {
                SetProperties(_in.ReadByte());
                _needProps = false;
            }
            if (mode == 3)
                ResetDictionary();
            if (mode >= 1)
                ResetState();

            // The range coder restarts in every LZMA chunk: 0x00 then the 32-bit code.
            if (packed < 5)
                throw new InvalidDataException("LZMA2 chunk too short to hold a range coder");
            _in.ReadExactly(_chunk.AsSpan(0, packed));
            if (_chunk[0] != 0)
                throw new InvalidDataException("LZMA2 chunk: range coder does not start with a zero byte");
            _code = (uint)(_chunk[1] << 24 | _chunk[2] << 16 | _chunk[3] << 8 | _chunk[4]);
            _range = uint.MaxValue;
            if (_code == _range)
                throw new InvalidDataException("LZMA2 chunk: invalid initial range coder value");
            _inPos = 5;
            _inEnd = packed;
            _chunkLeft = unpacked;
            _chunkCompressed = true;
        }
        _needDictReset = false;
        return true;
    }

    /// <summary>Decodes part of the current chunk into the window and queues it for the caller.</summary>
    private void Produce(int wanted)
    {
        Debug.Assert(_pending == 0);
        int n = Math.Min(_chunkLeft, Math.Min(_winSize, Math.Max(wanted, MinStep)));
        if (_chunkCompressed)
            DecodeLzma(n);
        else
            CopyStored(n);
        _chunkLeft -= n;
        _pending += n;
        if (_chunkLeft == 0)
            EndChunk();
    }

    /// <summary>Consistency checks the specification implies at the end of an LZMA chunk.</summary>
    private void EndChunk()
    {
        if (!_chunkCompressed)
            return;
        if (_matchLeft != 0)
            throw new InvalidDataException("LZMA2 chunk: a match runs past the end of the chunk");
        if (_inPos != _inEnd)
            throw new InvalidDataException($"LZMA2 chunk: {_inEnd - _inPos} compressed byte(s) left unused");
        // A properly flushed range encoder leaves the decoder's code at exactly zero.
        if (_code != 0)
            throw new InvalidDataException("LZMA2 chunk: range coder was not flushed cleanly");
    }

    private void CopyStored(int n)
    {
        int first = Math.Min(n, _winSize - _winPos);
        _in.ReadExactly(_window.AsSpan(_winPos, first));
        if (n > first)
            _in.ReadExactly(_window.AsSpan(0, n - first));
        _winPos += n;
        if (_winPos >= _winSize)
            _winPos -= _winSize;
        _totalPos += n;
        _full = _winSize - _full < n ? _winSize : _full + n;
    }

    private void ResetDictionary()
    {
        _winPos = 0;
        _readPos = 0;
        _full = 0;
        _totalPos = 0;
    }

    private void ResetState()
    {
        Array.Fill(_probs, ProbInit);
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
        _matchLeft = 0;
    }

    /// <summary>Properties byte: lc + 9 * (lp + 5 * pb); LZMA2 additionally demands lc + lp &lt;= 4.</summary>
    private void SetProperties(int props)
    {
        if (props < 0)
            throw Truncated();
        if (props >= 9 * 5 * 5)
            throw new InvalidDataException($"invalid LZMA properties byte 0x{props:X2}");
        int lc = props % 9;
        props /= 9;
        int lp = props % 5;
        int pb = props / 5;
        if (lc + lp > MaxLcLp)
            throw new InvalidDataException($"LZMA2 forbids lc + lp > 4 (got lc={lc}, lp={lp})");
        _lc = lc;
        _lp = lp;
        _pb = pb;
    }

    private int ReadBigEndian16()
    {
        int hi = _in.ReadByte();
        int lo = _in.ReadByte();
        if (hi < 0 || lo < 0)
            throw Truncated();
        return (hi << 8) | lo;
    }

    private static InvalidDataException Truncated() =>
        new("LZMA2 data ends before its end marker");

    // ------------------------------------------------------------------ LZMA proper

    /// <summary>
    /// Decodes exactly <paramref name="limit"/> bytes of the current chunk into the window.
    /// A match may straddle the limit; its remainder is kept in <see cref="_matchLeft"/> and
    /// copied first on the next call.
    /// </summary>
    private void DecodeLzma(int limit)
    {
        // Everything the loop touches lives in locals; fields are written back once at the end.
        ushort[] probs = _probs;
        byte[] win = _window;
        int winSize = _winSize;
        int winPos = _winPos;
        int full = _full;
        long totalPos = _totalPos;
        byte[] inBuf = _chunk;
        int inPos = _inPos;
        int inEnd = _inEnd;
        uint range = _range;
        uint code = _code;
        int state = _state;
        uint rep0 = _rep0, rep1 = _rep1, rep2 = _rep2, rep3 = _rep3;
        int lc = _lc;
        int lpMask = (1 << _lp) - 1;
        int pbMask = (1 << _pb) - 1;
        int matchLeft = _matchLeft;
        int remaining = limit;

        while (remaining > 0)
        {
            if (matchLeft > 0)
            {
                int n = Math.Min(matchLeft, remaining);
                winPos = CopyMatch(win, winSize, winPos, (int)rep0 + 1, n);
                matchLeft -= n;
                remaining -= n;
                totalPos += n;
                full = winSize - full < n ? winSize : full + n;
                continue;
            }

            int posState = (int)totalPos & pbMask;

            // The IsMatch bit and the literal tree are decoded with a hand-inlined copy of
            // RangeDecoder.Bit on plain locals: this is the hottest path (most bit decodes on
            // binary data are literals) and the JIT keeps locals in registers, whereas a struct
            // whose address is passed to the length / distance helpers ends up on the stack.
            int idx = IsMatch + (state << PosBitsMax) + posState;
            uint prob = probs[idx];
            uint bound = (range >> BitModelTotalBits) * prob;
            if (code < bound)
            {
                range = bound;
                probs[idx] = (ushort)(prob + (((1u << BitModelTotalBits) - prob) >> 5));
                if (range < TopValue)
                {
                    range <<= 8;
                    code = (code << 8) | (inPos < inEnd ? inBuf[inPos++] : RangeDecoder.ThrowOverrun());
                }

                // Literal, coded in the context of the previous byte's high lc bits and the
                // low lp bits of the position. Right after a match the byte at rep0 steers the
                // tree until the first mismatching bit ("matched literal").
                int prevByte = full == 0 ? 0 : win[winPos == 0 ? winSize - 1 : winPos - 1];
                int litBase = Literal + LiteralCoderSize * ((((int)totalPos & lpMask) << lc) + (prevByte >> (8 - lc)));
                int symbol = 1;
                if (state >= 7)
                {
                    int matchByte = win[Back(winPos, winSize, (int)rep0 + 1)];
                    do
                    {
                        int matchBit = (matchByte >> 7) & 1;
                        matchByte <<= 1;
                        idx = litBase + ((1 + matchBit) << 8) + symbol;
                        prob = probs[idx];
                        bound = (range >> BitModelTotalBits) * prob;
                        int bit;
                        if (code < bound)
                        {
                            range = bound;
                            probs[idx] = (ushort)(prob + (((1u << BitModelTotalBits) - prob) >> 5));
                            bit = 0;
                        }
                        else
                        {
                            range -= bound;
                            code -= bound;
                            probs[idx] = (ushort)(prob - (prob >> 5));
                            bit = 1;
                        }
                        if (range < TopValue)
                        {
                            range <<= 8;
                            code = (code << 8) | (inPos < inEnd ? inBuf[inPos++] : RangeDecoder.ThrowOverrun());
                        }
                        symbol = (symbol << 1) | bit;
                        if (matchBit != bit)
                            break;
                    } while (symbol < 0x100);
                }
                while (symbol < 0x100)
                {
                    idx = litBase + symbol;
                    prob = probs[idx];
                    bound = (range >> BitModelTotalBits) * prob;
                    if (code < bound)
                    {
                        range = bound;
                        probs[idx] = (ushort)(prob + (((1u << BitModelTotalBits) - prob) >> 5));
                        symbol <<= 1;
                    }
                    else
                    {
                        range -= bound;
                        code -= bound;
                        probs[idx] = (ushort)(prob - (prob >> 5));
                        symbol = (symbol << 1) | 1;
                    }
                    if (range < TopValue)
                    {
                        range <<= 8;
                        code = (code << 8) | (inPos < inEnd ? inBuf[inPos++] : RangeDecoder.ThrowOverrun());
                    }
                }

                win[winPos++] = (byte)symbol;
                if (winPos == winSize)
                    winPos = 0;
                totalPos++;
                remaining--;
                if (full < winSize)
                    full++;
                state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }

            range -= bound;
            code -= bound;
            probs[idx] = (ushort)(prob - (prob >> 5));
            if (range < TopValue)
            {
                range <<= 8;
                code = (code << 8) | (inPos < inEnd ? inBuf[inPos++] : RangeDecoder.ThrowOverrun());
            }

            // Matches are rarer per bit decoded; they go through the struct-based helpers, and
            // the locals are synchronised on the way in and out.
            var rc = new RangeDecoder(inBuf, inPos, inEnd, range, code);
            int len;
            if (rc.Bit(probs, IsRep + state) != 0)
            {
                if (full == 0)
                    throw new InvalidDataException("LZMA: repeated match before any data");
                if (rc.Bit(probs, IsRepG0 + state) == 0)
                {
                    if (rc.Bit(probs, IsRep0Long + (state << PosBitsMax) + posState) == 0)
                    {
                        // Short rep: a single byte from distance rep0.
                        state = state < 7 ? 9 : 11;
                        win[winPos] = win[Back(winPos, winSize, (int)rep0 + 1)];
                        if (++winPos == winSize)
                            winPos = 0;
                        totalPos++;
                        remaining--;
                        if (full < winSize)
                            full++;
                        inPos = rc.Pos;
                        range = rc.Range;
                        code = rc.Code;
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (rc.Bit(probs, IsRepG1 + state) == 0)
                    {
                        dist = rep1;
                    }
                    else
                    {
                        if (rc.Bit(probs, IsRepG2 + state) == 0)
                        {
                            dist = rep2;
                        }
                        else
                        {
                            dist = rep3;
                            rep3 = rep2;
                        }
                        rep2 = rep1;
                    }
                    rep1 = rep0;
                    rep0 = dist;
                }
                len = DecodeLength(ref rc, probs, RepLenCoder, posState);
                state = state < 7 ? 8 : 11;
            }
            else
            {
                rep3 = rep2;
                rep2 = rep1;
                rep1 = rep0;
                len = DecodeLength(ref rc, probs, LenCoder, posState);
                state = state < 7 ? 7 : 10;
                rep0 = DecodeDistance(ref rc, probs, len);
                if (rep0 == uint.MaxValue)
                    throw new InvalidDataException("LZMA2 chunks must not contain an end-of-payload marker");
            }

            inPos = rc.Pos;
            range = rc.Range;
            code = rc.Code;
            if (rep0 >= (uint)full)
                throw new InvalidDataException("LZMA: match distance reaches before the start of the data");
            matchLeft = len + MatchMinLen;
        }

        _winPos = winPos;
        _full = full;
        _totalPos = totalPos;
        _inPos = inPos;
        _range = range;
        _code = code;
        _state = state;
        _rep0 = rep0;
        _rep1 = rep1;
        _rep2 = rep2;
        _rep3 = rep3;
        _matchLeft = matchLeft;
    }

    /// <summary>Index of the byte <paramref name="dist"/> positions behind the write cursor.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Back(int winPos, int winSize, int dist)
    {
        int i = winPos - dist;
        return i < 0 ? i + winSize : i;
    }

    /// <summary>
    /// Copies <paramref name="n"/> bytes from <paramref name="dist"/> back to the write cursor.
    /// Overlap is the LZ77 run case (a pattern shorter than the copy repeats), so the copy is
    /// done in doubling pieces that never overlap; wrapping around the window falls back to
    /// a byte loop.
    /// </summary>
    private static int CopyMatch(byte[] win, int winSize, int winPos, int dist, int n)
    {
        int src = winPos - dist;
        if (src < 0)
            src += winSize;

        if (src < winPos && winPos + n <= winSize)
        {
            int left = n;
            while (left > 0)
            {
                int k = Math.Min(left, winPos - src);
                win.AsSpan(src, k).CopyTo(win.AsSpan(winPos, k));
                winPos += k;
                left -= k;
            }
            return winPos == winSize ? 0 : winPos;
        }

        for (int i = 0; i < n; i++)
        {
            win[winPos] = win[src];
            if (++winPos == winSize)
                winPos = 0;
            if (++src == winSize)
                src = 0;
        }
        return winPos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitTree(ref RangeDecoder rc, ushort[] probs, int offset, int numBits)
    {
        int m = 1;
        for (int i = 0; i < numBits; i++)
            m = (m << 1) + rc.Bit(probs, offset + m);
        return m - (1 << numBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BitTreeReverse(ref RangeDecoder rc, ushort[] probs, int offset, int numBits)
    {
        int m = 1;
        int symbol = 0;
        for (int i = 0; i < numBits; i++)
        {
            int bit = rc.Bit(probs, offset + m);
            m = (m << 1) + bit;
            symbol |= bit << i;
        }
        return symbol;
    }

    /// <summary>Length minus 2: 0-7 from the low tree, 8-15 from the mid tree, 16-271 from the high tree.</summary>
    private static int DecodeLength(ref RangeDecoder rc, ushort[] probs, int coder, int posState)
    {
        if (rc.Bit(probs, coder + LenChoice) == 0)
            return BitTree(ref rc, probs, coder + LenLow + (posState << LenLowBits), LenLowBits);
        if (rc.Bit(probs, coder + LenChoice2) == 0)
            return LenLowSymbols + BitTree(ref rc, probs, coder + LenMid + (posState << LenMidBits), LenMidBits);
        return LenLowSymbols + LenMidSymbols + BitTree(ref rc, probs, coder + LenHigh, LenHighBits);
    }

    /// <summary>
    /// Zero-based distance: a 6-bit slot, then for slots 4-13 a reverse bit tree of the low
    /// bits, and for larger slots direct bits followed by a 4-bit reverse-coded alignment.
    /// </summary>
    private static uint DecodeDistance(ref RangeDecoder rc, ushort[] probs, int len)
    {
        int lenState = Math.Min(len, NumLenToPosStates - 1);
        int posSlot = BitTree(ref rc, probs, PosSlot + (lenState << PosSlotBits), PosSlotBits);
        if (posSlot < 4)
            return (uint)posSlot;

        int numDirectBits = (posSlot >> 1) - 1;
        uint dist = (2u | (uint)(posSlot & 1)) << numDirectBits;
        if (posSlot < EndPosModelIndex)
        {
            dist += (uint)BitTreeReverse(ref rc, probs, SpecPos + (int)dist - posSlot, numDirectBits);
        }
        else
        {
            dist += rc.DirectBits(numDirectBits - AlignBits) << AlignBits;
            dist += (uint)BitTreeReverse(ref rc, probs, Align, AlignBits);
        }
        return dist;
    }

    /// <summary>
    /// Binary range decoder over an in-memory chunk. Adaptive probabilities are 11-bit
    /// (kNumBitModelTotalBits) with a move-shift of 5, exactly as the specification fixes them.
    /// <see cref="DecodeLzma"/> repeats <see cref="Bit"/> by hand on its literal path; keep
    /// the two in step if either changes.
    /// </summary>
    private struct RangeDecoder
    {
        private readonly byte[] _buf;
        private readonly int _end;
        public int Pos;
        public uint Range;
        public uint Code;

        public RangeDecoder(byte[] buf, int pos, int end, uint range, uint code)
        {
            _buf = buf;
            _end = end;
            Pos = pos;
            Range = range;
            Code = code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Bit(ushort[] probs, int index)
        {
            uint prob = probs[index];
            uint bound = (Range >> BitModelTotalBits) * prob;
            int bit;
            if (Code < bound)
            {
                Range = bound;
                probs[index] = (ushort)(prob + (((1u << BitModelTotalBits) - prob) >> 5));
                bit = 0;
            }
            else
            {
                Range -= bound;
                Code -= bound;
                probs[index] = (ushort)(prob - (prob >> 5));
                bit = 1;
            }
            if (Range < TopValue)
            {
                Range <<= 8;
                Code = (Code << 8) | NextByte();
            }
            return bit;
        }

        /// <summary>Fixed-probability bits (the middle of long distances).</summary>
        public uint DirectBits(int numBits)
        {
            uint result = 0;
            do
            {
                Range >>= 1;
                Code -= Range;
                uint t = 0u - (Code >> 31); // all ones when the subtraction borrowed: bit is 0
                Code += Range & t;
                if (Range < TopValue)
                {
                    Range <<= 8;
                    Code = (Code << 8) | NextByte();
                }
                result = (result << 1) + (t + 1);
            } while (--numBits != 0);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint NextByte()
        {
            if (Pos < _end)
                return _buf[Pos++];
            return ThrowOverrun();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static uint ThrowOverrun() =>
            throw new InvalidDataException("LZMA2 chunk: range coder reads past the compressed data");
    }
}

/// <summary>
/// Minimal buffered reader shared by the xz container parser and the LZMA2 decoder.
/// </summary>
/// <remarks>
/// WHY not BufferedStream: the container needs a byte-level position (block sizes, padding
/// alignment) and the decoder needs cheap single-byte reads for chunk headers; both must
/// consume from the same buffer, since the index and footer follow the LZMA2 data directly.
/// Reading ahead past the logical end of the xz data is harmless: callers hand over a
/// bounded window and re-seek the underlying stream themselves.
/// </remarks>
internal sealed class BufferedByteReader
{
    private readonly Stream _stream;
    private readonly byte[] _buf;
    private int _pos;
    private int _end;
    private long _taken; // bytes pulled from the stream so far

    public BufferedByteReader(Stream stream, int bufferSize = 1 << 16)
    {
        _stream = stream;
        _buf = new byte[bufferSize];
    }

    /// <summary>Bytes consumed so far, counted from where reading began.</summary>
    public long Position => _taken - (_end - _pos);

    public int ReadByte()
    {
        if (_pos == _end && !Fill())
            return -1;
        return _buf[_pos++];
    }

    public int PeekByte()
    {
        if (_pos == _end && !Fill())
            return -1;
        return _buf[_pos];
    }

    /// <summary>
    /// Fills <paramref name="dst"/> completely. A short read is reported as
    /// <see cref="InvalidDataException"/>: for this parser a truncated file is corrupt data,
    /// and callers get one exception type for every way the input can be wrong.
    /// </summary>
    public void ReadExactly(Span<byte> dst)
    {
        while (!dst.IsEmpty)
        {
            int buffered = _end - _pos;
            if (buffered > 0)
            {
                int n = Math.Min(buffered, dst.Length);
                _buf.AsSpan(_pos, n).CopyTo(dst);
                _pos += n;
                dst = dst[n..];
            }
            else if (dst.Length >= _buf.Length)
            {
                // Large request: bypass the buffer instead of copying twice.
                int n = _stream.Read(dst);
                if (n <= 0)
                    throw Truncated();
                _taken += n;
                dst = dst[n..];
            }
            else if (!Fill())
            {
                throw Truncated();
            }
        }
    }

    private static InvalidDataException Truncated() => new("xz data ends prematurely");

    private bool Fill()
    {
        _pos = 0;
        _end = _stream.Read(_buf, 0, _buf.Length);
        if (_end < 0)
            _end = 0;
        _taken += _end;
        return _end > 0;
    }
}
