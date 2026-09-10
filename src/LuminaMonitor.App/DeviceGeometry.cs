namespace LuminaMonitor.App;

/// <summary>
/// The shape of an iPhone 17 Pro Max, as ratios of its own screen.
/// </summary>
/// <remarks>
/// Apple publishes the body (163.4 x 78.0 x 8.75 mm) and the display
/// (1320 x 2868 px at 460 ppi) but neither the border width nor the corner
/// radius. Both fall out of the published figures, and the result checks itself
/// on three independent axes:
///
/// <code>
/// screen width   1320 / 460 x 25.4  =  72.89 mm
/// screen height  2868 / 460 x 25.4  = 158.36 mm
/// diagonal       sqrt(72.89^2 + 158.36^2) = 174.3 mm = 6.86 in
///                                          ^ Apple's own figure for the
///                                            display measured as a full
///                                            rectangle.
/// border l/r     (78.0  -  72.89) / 2 = 2.56 mm
/// border t/b     (163.4 - 158.36) / 2 = 2.52 mm
///                                          ^ the same to within 0.04 mm, so
///                                            the screen is centred and the
///                                            border is uniform.
/// </code>
///
/// <para>2.54 mm at 460 ppi is 46 px. Rebuilding the body from there:
/// 1320 + 92 = 1412 px = 77.98 mm wide, 2868 + 92 = 2960 px = 163.47 mm tall,
/// against Apple's 78.0 x 163.4. It closes to a tenth of a millimetre, which is
/// what makes this the real geometry rather than a good-looking guess.</para>
///
/// <para>The corner radius is the one number that is measured rather than
/// derived: 62 pt, the value <c>UIScreen._displayCornerRadius</c> reports for
/// the 16 Pro, 16 Pro Max, 17, 17 Pro, 17 Pro Max and Air. At 3x that is 186 px.
/// The chassis radius is the screen radius plus the border, because the two
/// curves are concentric.</para>
///
/// <para>Everything is expressed against the <em>short</em> side of the picture
/// rather than its width, so the same numbers hold when the phone is turned on
/// its side.</para>
/// </remarks>
internal static class DeviceGeometry
{
    /// <summary>Border between the edge of the body and the first pixel: 2.56 mm of 72.86.</summary>
    /// <remarks>
    /// The derivation above landed on 2.56 mm from published figures and then
    /// rounded to 2.54 to get a whole 46 px. Apple's dimensional drawing settles
    /// it outright, in words: <c>2.56 ALL AROUND — EXTERIOR OF HOUSING TO
    /// DISPLAY ACTIVE AREA</c>, over an active area of 72.86 x 158.31 in a body
    /// of 77.98 x 163.43. So the derivation was right and the rounding was the
    /// only error — 0.8 %, a seventh of a pixel here.
    ///
    /// <para>It is corrected anyway, because <see cref="ButtonProud"/> is a
    /// fraction of this border and its numerator comes off the same sheet. Two
    /// numbers that describe the same millimetre should not disagree about how
    /// long it is.</para>
    /// </remarks>
    public const double Border = 2.56 / 72.86;

    /// <summary>Screen corner radius. 62 pt x 3 = 186 px, over 1320.</summary>
    public const double ScreenRadius = 186.0 / 1320.0;

    /// <summary>Dynamic Island width. 125 pt x 3 = 375 px — 20.71 mm, against a measured cutout of 20.76.</summary>
    public const double IslandWidth = 375.0 / 1320.0;

    /// <summary>Dynamic Island height. 36.67 pt x 3 = 110 px.</summary>
    public const double IslandHeight = 110.0 / 1320.0;

    /// <summary>Gap between the top of the screen and the top of the island. 11 pt x 3 = 33 px.</summary>
    public const double IslandTop = 33.0 / 1320.0;

    /// <summary>Chassis radius: concentric with the screen, so one border further out.</summary>
    public const double ChassisRadius = ScreenRadius + Border;

    /// <summary>
    /// How much of the border is anodised metal rather than black glass.
    /// </summary>
    /// <remarks>
    /// The 2.54 mm between the edge of the body and the first pixel is not all
    /// aluminium. Seen from the front it is a narrow chamfer catching the light,
    /// and then black — the glass border, quoted at about 1.2 mm on the Pro
    /// models. Painting the whole 2.54 mm in the body colour is what made the
    /// first version look like a phone case rather than a phone: the rail read
    /// as a thick coloured frame instead of the thin bright line it is.
    ///
    /// <para>1.14 mm of metal against 1.4 mm of glass — call it 45 % — puts the
    /// bright edge back where the eye expects it without narrowing the border
    /// itself, which is measured and correct.</para>
    /// </remarks>
    public const double MetalShare = 0.45;

    /// <summary>
    /// Side buttons, as fractions of the body length, from the top of the body.
    /// </summary>
    /// <remarks>
    /// These used to say, in this very comment, that Apple publishes no button
    /// positions and that they were therefore read off photographs and "placed
    /// to look right rather than to be right". Both halves were wrong. Apple
    /// does publish them, in the dimensional drawing that ships with the
    /// Accessory Design Guidelines, and the eye had put every button 15 to
    /// 22 mm too low:
    ///
    /// <code>
    ///                 was      drawing    error
    /// Action         0.290     0.1886     +16.9 mm too low
    /// Volume +       0.375     0.2621     +18.5 mm too low
    /// Volume -       0.485     0.3490     +22.2 mm too low
    /// Side           0.375     0.2856     +14.6 mm too low
    /// Camera         0.585     0.6319      -7.7 mm too high
    /// </code>
    ///
    /// <para>Source: <c>iPhone 17 Pro Max Dimensional Drawings</c>, sheet 1 of 4,
    /// drawing revision 2025-09-09, from
    /// <c>developer.apple.com/download/files/accessories/dimensional-drawings/</c>.
    /// The sheet's text is vectorised, so no extractor reads it; it has to be
    /// rendered and looked at. Two crops were read directly to confirm the
    /// figures below rather than take them on trust.</para>
    ///
    /// <para>Apple dimensions the <em>axis</em> of each button and its
    /// <em>half</em> length, never its top edge, so every value here is one
    /// subtraction away from the sheet and the subtraction is written out:</para>
    ///
    /// <code>
    /// left  34.28 / 2X 3.45   Action        62.63 / 4X 5.60   Volume -
    ///       48.43 / 4X 5.60   Volume +
    /// right 55.53 / 2X 8.85   Side         111.82 / 2X 8.55   Camera Control
    /// front 0.45 ACTION BUTTON / (+) / (-) / SIDE BUTTON, and
    ///       0.00 CAMERA CONTROL
    /// </code>
    ///
    /// <para>163.43 is the sheet's own PRODUCT LENGTH, so these fractions are
    /// published millimetres over a published millimetre and nothing else.</para>
    /// </remarks>
    /// <param name="Top">Top edge of the button, over the body length.</param>
    /// <param name="Length">Length along the body, over the body length.</param>
    /// <param name="Proud">
    /// How far it stands out past the silhouette, as a fraction of the border.
    /// Zero means flush, which is a different thing to draw, not a small one.
    /// </param>
    public readonly record struct SideButton(double Top, double Length, double Proud);

    /// <summary>
    /// How far the four mechanical buttons stand proud: 0.45 mm of a 2.56 mm
    /// border.
    /// </summary>
    /// <remarks>
    /// This was 0.32 — 0.81 mm — which is what made them read as tabs stuck to
    /// the side. Worth noting against the opposite error: 0.45 mm is not
    /// negligible either. Guessing "a tenth of a millimetre, it hardly shows"
    /// would have been wrong by a factor of four in the other direction. Apple
    /// prints 0.45 four times on the front view, and the vector geometry of the
    /// sheet measures 0.4498 mm on the left flank and 0.4503 on the right.
    /// </remarks>
    public const double ButtonProud = 0.45 / 2.56;

    /// <summary>
    /// Drawn width of the lamella: the 0.45 mm that shows, plus 0.10 mm tucked
    /// behind the silhouette.
    /// </summary>
    /// <remarks>
    /// The overlap is not geometry, it is anti-aliasing. Butting the lamella
    /// exactly against the body edge leaves both shapes painting a half-covered
    /// pixel on the same column, and the two half-coverages do not add back to
    /// one: a dark hairline appears down the join. A tenth of a millimetre of
    /// overlap costs nothing — it lands on rail of the same colour — and removes
    /// the seam.
    /// </remarks>
    public const double ButtonThickness = 0.55 / 2.56;

    /// <summary>Width of the seam drawn for a flush control, as a fraction of the border.</summary>
    /// <remarks>
    /// The Camera Control is dimensioned 0.00 on the front view — the sheet
    /// traces its outline exactly on the housing edge, and the measured geometry
    /// even sits 0.04 mm inside it. So it must not be drawn as a lamella at all;
    /// face on, all there is to see is the seam around it.
    /// </remarks>
    public const double ButtonSeam = 0.25 / 2.56;

    // 163.43 is PRODUCT LENGTH as printed on the sheet.
    public static readonly SideButton Action = new((34.28 - 3.45) / 163.43, 6.90 / 163.43, ButtonProud);
    public static readonly SideButton VolumeUp = new((48.43 - 5.60) / 163.43, 11.20 / 163.43, ButtonProud);
    public static readonly SideButton VolumeDown = new((62.63 - 5.60) / 163.43, 11.20 / 163.43, ButtonProud);
    public static readonly SideButton Side = new((55.53 - 8.85) / 163.43, 17.70 / 163.43, ButtonProud);
    public static readonly SideButton Camera = new((111.82 - 8.55) / 163.43, 17.10 / 163.43, 0.0);

    /// <summary>Which way up the phone is, and therefore where its island sits.</summary>
    public enum Orientation
    {
        /// <summary>Upright: island at the top, buttons down the sides.</summary>
        Portrait,

        /// <summary>Turned so the island is on the left edge.</summary>
        IslandLeft,

        /// <summary>Turned so the island is on the right edge.</summary>
        IslandRight,

        /// <summary>Sideways, but which way could not be established.</summary>
        Unknown,
    }

    /// <summary>
    /// Works out which edge the Dynamic Island is on by looking at the picture.
    /// </summary>
    /// <remarks>
    /// A landscape stream says nothing about which way the phone was turned —
    /// the frames are the same shape either way — so the answer has to come from
    /// the pixels. The island is a black pill of known size at a known distance
    /// from its edge, so the two candidate columns are sampled and compared.
    ///
    /// <para>It only answers when one column is almost entirely black and the
    /// other is clearly not. A phone showing a dark app has black down both
    /// edges and there is nothing to tell them apart; the caller then draws no
    /// island at all, which costs nothing — the stream already carries the real
    /// one as black pixels, and drawing ours on the wrong edge would put a black
    /// pill over content.</para>
    /// </remarks>
    /// <remarks>
    /// Reads the luma plane of the decoded picture rather than a converted
    /// bitmap: the window no longer holds one picture in BGRA outside the tick
    /// that shows it, and the question — is this pixel black — is answered by
    /// luma alone anyway. Studio range puts black at 16 and the old test was
    /// "green below 24", which is luma below 16 + 24 / 1.164, hence the
    /// threshold below.
    /// </remarks>
    public static Orientation DetectNv12(
        ReadOnlySpan<byte> nv12, int width, int height, int stride)
    {
        if (height >= width)
            return Orientation.Portrait;

        double shortSide = height;
        int column = (int)(IslandTop * shortSide + IslandHeight * shortSide / 2);
        int half = (int)(IslandWidth * shortSide / 2);
        int from = Math.Max(0, height / 2 - half);
        int to = Math.Min(height - 1, height / 2 + half);

        if (column <= 0 || column >= width / 2 || to <= from)
            return Orientation.Unknown;

        double left = BlackFraction(nv12, stride, column, from, to);
        double right = BlackFraction(nv12, stride, width - 1 - column, from, to);

        const double Certain = 0.95, Clear = 0.60;
        if (left >= Certain && right <= Clear) return Orientation.IslandLeft;
        if (right >= Certain && left <= Clear) return Orientation.IslandRight;
        return Orientation.Unknown;
    }

    /// <summary>Luma at or below this is black; see <see cref="DetectNv12"/>.</summary>
    private const byte BlackLuma = 37;

    private static double BlackFraction(
        ReadOnlySpan<byte> nv12, int stride, int x, int fromY, int toY)
    {
        int black = 0, total = 0;
        for (int y = fromY; y <= toY; y++)
        {
            int offset = y * stride + x;
            if (offset >= nv12.Length)
                break;

            if (nv12[offset] < BlackLuma)
                black++;
            total++;
        }

        return total == 0 ? 0 : (double)black / total;
    }

    /// <summary>
    /// Largest picture that fits in the space available once the chassis is
    /// added around it.
    /// </summary>
    /// <remarks>
    /// The border depends on the picture, and the picture depends on the border,
    /// so this solves for both at once rather than iterating. Writing the device
    /// size in terms of a single scale factor <c>s</c>:
    ///
    /// <code>
    /// short   = min(pixelWidth, pixelHeight) x s
    /// device  = (pixelWidth x s + 2b x short, pixelHeight x s + 2b x short)
    /// </code>
    ///
    /// which is linear in <c>s</c>, so the largest <c>s</c> that fits both
    /// dimensions is one division each way.
    /// </remarks>
    public static (double Width, double Height) FitPicture(
        double availableWidth, double availableHeight, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
            return (0, 0);

        double shortSide = Math.Min(pixelWidth, pixelHeight);
        double widthPerScale = pixelWidth + 2 * Border * shortSide;
        double heightPerScale = pixelHeight + 2 * Border * shortSide;

        double scale = Math.Min(availableWidth / widthPerScale, availableHeight / heightPerScale);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            return (0, 0);

        return (pixelWidth * scale, pixelHeight * scale);
    }
}
