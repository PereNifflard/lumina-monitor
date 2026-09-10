namespace LuminaMonitor.BleProbe;

/// <summary>
/// The three HID report maps under test, and how to build their payloads.
/// </summary>
/// <remarks>
/// This bench exists to answer one question on the owner's own iPhone: which
/// pointer semantics does iOS accept from a Windows PC acting as a BLE HID
/// peripheral? Three descriptors, three different answers on record:
///
/// <para><b>relative</b> — keyboard + 16-bit relative mouse, byte-for-byte the
/// map that abhishek-raj/windows-ble-hid verified against a real iPhone. The
/// known-good baseline: if this one pairs and moves the AssistiveTouch cursor,
/// the whole GATT plumbing is proven and the remaining questions are purely
/// about descriptor acceptance.</para>
///
/// <para><b>absolute</b> — the same keyboard plus this project's own absolute
/// pointer (X/Y 0..32767), transposed verbatim from the Pi's report ID 3 in
/// pi/bt_hid_bridge.py. iOS accepts it over Bluetooth Classic; the field
/// evidence says it refuses it over BLE. This mode settles the question for
/// the owner's iOS version rather than trusting 2020-era forum posts.</para>
///
/// <para><b>digitizer</b> — a single-contact touch-screen digitizer (stylus
/// usage, X/Y 0..10000), byte-for-byte the map sakx7/WinBleTouch reports as
/// delivering REAL touches on iOS 18.6.2 and 26.6 — but only while the phone
/// has Accessibilite &gt; Zoom enabled (plein ecran, 1x, controleur off).
/// No AssistiveTouch, no cursor, no acceleration: a tap lands at the sent
/// coordinate. The most promising and the least official of the three.</para>
/// </remarks>
internal static class ReportMaps
{
    // --- Keyboard collection, report ID 1 (relative and absolute modes) ----
    // Standard 8-byte keyboard: modifiers, reserved, six usages.
    private static readonly byte[] Keyboard =
    [
        0x05, 0x01,        // Usage Page (Generic Desktop)
        0x09, 0x06,        // Usage (Keyboard)
        0xA1, 0x01,        // Collection (Application)
        0x85, 0x01,        //   Report ID (1)
        0x05, 0x07,        //   Usage Page (Keyboard/Keypad)
        0x19, 0xE0, 0x29, 0xE7,
        0x15, 0x00, 0x25, 0x01,
        0x75, 0x01, 0x95, 0x08,
        0x81, 0x02,        //   modifier bits
        0x95, 0x01, 0x75, 0x08,
        0x81, 0x01,        //   reserved byte
        0x95, 0x06, 0x75, 0x08,
        0x15, 0x00, 0x25, 0x65,
        0x05, 0x07, 0x19, 0x00, 0x29, 0x65,
        0x81, 0x00,        //   six concurrent keycodes
        0xC0,
    ];

    // --- Relative mouse, report ID 2 ---------------------------------------
    // 16-bit deltas rather than the Pi's 8-bit ones: taken unchanged from the
    // windows-ble-hid map because that exact byte sequence is the one with an
    // iPhone pairing on record. Payload: [buttons, xLo, xHi, yLo, yHi, wheel].
    private static readonly byte[] MouseRelative =
    [
        0x05, 0x01,        // Usage Page (Generic Desktop)
        0x09, 0x02,        // Usage (Mouse)
        0xA1, 0x01,        // Collection (Application)
        0x85, 0x02,        //   Report ID (2)
        0x09, 0x01,        //   Usage (Pointer)
        0xA1, 0x00,        //   Collection (Physical)
        0x05, 0x09, 0x19, 0x01, 0x29, 0x03,
        0x15, 0x00, 0x25, 0x01,
        0x95, 0x03, 0x75, 0x01,
        0x81, 0x02,        //     three buttons
        0x95, 0x01, 0x75, 0x05,
        0x81, 0x01,        //     padding
        0x05, 0x01, 0x09, 0x30, 0x09, 0x31,
        0x16, 0x01, 0x80,  //     Logical Minimum (-32767)
        0x26, 0xFF, 0x7F,  //     Logical Maximum (32767)
        0x75, 0x10, 0x95, 0x02,
        0x81, 0x06,        //     X, Y relative
        0x09, 0x38,
        0x15, 0x81, 0x25, 0x7F,
        0x75, 0x08, 0x95, 0x01,
        0x81, 0x06,        //     wheel
        0xC0, 0xC0,
    ];

    // --- Absolute mouse, report ID 2 ---------------------------------------
    // The Pi's proven absolute pointer (report ID 3 in bt_hid_bridge.py),
    // renumbered: X/Y absolute 0..32767. Payload: [buttons, xLo, xHi, yLo,
    // yHi, wheel]. Whether iOS honours this over BLE is exactly what the
    // absolute mode measures.
    private static readonly byte[] MouseAbsolute =
    [
        0x05, 0x01,        // Usage Page (Generic Desktop)
        0x09, 0x02,        // Usage (Mouse)
        0xA1, 0x01,        // Collection (Application)
        0x85, 0x02,        //   Report ID (2)
        0x09, 0x01,        //   Usage (Pointer)
        0xA1, 0x00,        //   Collection (Physical)
        0x05, 0x09, 0x19, 0x01, 0x29, 0x03,
        0x15, 0x00, 0x25, 0x01,
        0x75, 0x01, 0x95, 0x03,
        0x81, 0x02,        //     three buttons
        0x75, 0x05, 0x95, 0x01,
        0x81, 0x01,        //     padding
        0x05, 0x01, 0x09, 0x30, 0x09, 0x31,
        0x16, 0x00, 0x00,  //     Logical Minimum (0)
        0x26, 0xFF, 0x7F,  //     Logical Maximum (32767)
        0x75, 0x10, 0x95, 0x02,
        0x81, 0x02,        //     X, Y absolute
        0x09, 0x38,
        0x15, 0x81, 0x25, 0x7F,
        0x75, 0x08, 0x95, 0x01,
        0x81, 0x06,        //     wheel
        0xC0, 0xC0,
    ];

    // --- Touch-screen digitizer, report ID 1 -------------------------------
    // Byte-for-byte the WinBleTouch map (stylus usage inside a touch-screen
    // application collection, X/Y 0..10000 with physical units). Payload:
    // [state, xLo, xHi, yLo, yHi] where state 0x03 = tip + in-range (touch
    // down), 0x02 = in-range only (hover), 0x00 = released.
    private static readonly byte[] Digitizer =
    [
        0x05, 0x0D,        // Usage Page (Digitizers)
        0x09, 0x04,        // Usage (Touch Screen)
        0xA1, 0x01,        // Collection (Application)
        0x85, 0x01,        //   Report ID (1)
        0x09, 0x20,        //   Usage (Stylus)
        0xA1, 0x00,        //   Collection (Physical)
        0x09, 0x42,        //     Usage (Tip Switch)
        0x09, 0x32,        //     Usage (In Range)
        0x15, 0x00, 0x25, 0x01,
        0x75, 0x01, 0x95, 0x02,
        0x81, 0x02,        //     two state bits
        0x75, 0x01, 0x95, 0x06,
        0x81, 0x01,        //     padding
        0x05, 0x01,        //     Usage Page (Generic Desktop)
        0x09, 0x01,        //     Usage (Pointer)
        0xA1, 0x00,        //     Collection (Physical)
        0x09, 0x30, 0x09, 0x31,
        0x16, 0x00, 0x00,  //       Logical Minimum (0)
        0x26, 0x10, 0x27,  //       Logical Maximum (10000)
        0x36, 0x00, 0x00,  //       Physical Minimum (0)
        0x46, 0x10, 0x27,  //       Physical Maximum (10000)
        0x66, 0x00, 0x00,  //       Unit (none)
        0x75, 0x10, 0x95, 0x02,
        0x81, 0x02,        //       X, Y absolute
        0xC0, 0xC0, 0xC0,
    ];

    public static byte[] RelativeMap { get; } = [.. Keyboard, .. MouseRelative];
    public static byte[] AbsoluteMap { get; } = [.. Keyboard, .. MouseAbsolute];
    public static byte[] DigitizerMap { get; } = Digitizer;

    /// <summary>bcdHID 1.11, no country code, RemoteWake | NormallyConnectable.</summary>
    public static byte[] HidInformation { get; } = [0x11, 0x01, 0x00, 0x03];

    // --- Payload builders ---------------------------------------------------

    public static byte[] KeyboardReport(byte modifiers, byte usage) =>
        [modifiers, 0x00, usage, 0x00, 0x00, 0x00, 0x00, 0x00];

    public static byte[] MouseRelativeReport(byte buttons, int dx, int dy, int wheel) =>
    [
        buttons,
        (byte)(dx & 0xFF), (byte)((dx >> 8) & 0xFF),
        (byte)(dy & 0xFF), (byte)((dy >> 8) & 0xFF),
        unchecked((byte)(sbyte)Math.Clamp(wheel, -127, 127)),
    ];

    public static byte[] MouseAbsoluteReport(byte buttons, int x, int y, int wheel)
    {
        x = Math.Clamp(x, 0, 32767);
        y = Math.Clamp(y, 0, 32767);
        return
        [
            buttons,
            (byte)(x & 0xFF), (byte)(x >> 8),
            (byte)(y & 0xFF), (byte)(y >> 8),
            unchecked((byte)(sbyte)Math.Clamp(wheel, -127, 127)),
        ];
    }

    public static byte[] DigitizerReport(byte state, int x, int y)
    {
        x = Math.Clamp(x, 0, 10000);
        y = Math.Clamp(y, 0, 10000);
        return
        [
            state,
            (byte)(x & 0xFF), (byte)(x >> 8),
            (byte)(y & 0xFF), (byte)(y >> 8),
        ];
    }
}
