using System.Buffers.Binary;
using System.Diagnostics;
using LuminaMonitor.Core.RemoteXpc;
using XpcService = LuminaMonitor.Core.RemoteXpc.RemoteXpc;

namespace LuminaMonitor.Core.Hid;

/// <summary>
/// The input reports the phone's HID daemon accepts, and the service that
/// carries them.
/// </summary>
/// <remarks>
/// These layouts were never published by Apple; they were read off the wire
/// of Xcode's own device mirror and reproduced here byte for byte. The
/// touch report is the real thing — it reaches UIKit as a genuine touch on
/// the main screen, anywhere on the system, at an <b>absolute</b> position
/// normalised to 0..65535 on both axes with the origin top-left. That is the
/// coordinate model this whole project exists for.
///
/// <para>Sending is fire-and-forget: no reply is awaited, which is what
/// keeps a tap at a few milliseconds. The type tags matter — the daemon's
/// Swift decoder rejects a signed integer where it expects an unsigned one —
/// so every number is spelled with its XPC type.</para>
/// </remarks>
internal static class Hid
{
    public const string ServiceName = "com.apple.coredevice.hid.universalhidservice";
    private const string Feature = "com.apple.coredevice.feature.remote.universalhidservice";

    public const ulong SurfaceMainTouchscreen = 257;
    public const ulong SurfaceTouchscreenGesture = 1281;

    private const byte TouchReportId = 0x09;
    private const byte TouchContact = 0xC2;
    private const byte TouchRelease = 0x02;
    private const byte GestureReportId = 0x13;

    /// <summary>The 58-byte main-touchscreen report; coordinates 0..65535.</summary>
    public static byte[] TouchReport(bool contact, int x, int y)
    {
        byte[] r = new byte[58];
        r[0] = TouchReportId;
        r[1] = 0x01;
        r[2] = 0x05;
        r[3] = contact ? TouchContact : TouchRelease;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), (ushort)Math.Clamp(x, 0, 65535));
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), (ushort)Math.Clamp(y, 0, 65535));
        r[40] = 0x02;                                            // bytes 8..39 stay zero
        WriteTimestamp(r.AsSpan(44, 6));                         // bytes 50..57 stay zero
        return r;
    }

    /// <summary>The 19-byte gesture-pointer report; signed pixel deltas/positions.</summary>
    public static byte[] GestureReport(int x, int y)
    {
        byte[] r = new byte[19];
        r[0] = GestureReportId;
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(1), x);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(5), y);
        WriteTimestamp(r.AsSpan(11, 6));
        return r;
    }

    /// <summary>
    /// 48-bit monotonic nanoseconds. Only monotonicity matters to the
    /// daemon; the value itself is never compared to the phone's clock.
    /// </summary>
    private static void WriteTimestamp(Span<byte> target)
    {
        ulong ns = (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency)) & 0xFFFFFFFFFFFF;
        Span<byte> tmp = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(tmp, ns);
        tmp[..6].CopyTo(target);
    }

    // --- Virtual keyboard -------------------------------------------------------

    private const byte KeyboardReportId = 0x01;
    public const ulong KeyboardServiceId = 0x100002001;      // bit 32 set: session-specific, like Universal Control

    /// <summary>
    /// The 39-byte keyboard report: a 240-bit bitmap of usages currently held
    /// (bit u%8 of byte 1+u/8), then the timestamp. Every report carries the
    /// full pressed set, so a key is released by resending without its bit.
    /// </summary>
    public static byte[] KeyboardReport(IEnumerable<int> usages)
    {
        byte[] r = new byte[39];
        r[0] = KeyboardReportId;
        foreach (int u in usages)
            if (u is >= 0 and < 240)
                r[1 + u / 8] |= (byte)(1 << (u % 8));
        WriteTimestamp(r.AsSpan(31, 6));
        return r;
    }

    /// <summary>US-layout ASCII → (usage, shift). Letters, digits, common punctuation.</summary>
    public static (int Usage, bool Shift)? UsageFor(char c)
    {
        if (c is >= 'a' and <= 'z') return (0x04 + (c - 'a'), false);
        if (c is >= 'A' and <= 'Z') return (0x04 + (c - 'A'), true);
        if (c is >= '1' and <= '9') return (0x1E + (c - '1'), false);
        return c switch
        {
            '0' => (0x27, false), ' ' => (0x2C, false), '\n' => (0x28, false), '\t' => (0x2B, false),
            '-' => (0x2D, false), '_' => (0x2D, true), '=' => (0x2E, false), '+' => (0x2E, true),
            '.' => (0x37, false), ',' => (0x36, false), '/' => (0x38, false), '?' => (0x38, true),
            ';' => (0x33, false), ':' => (0x33, true), '\'' => (0x34, false), '"' => (0x34, true),
            '!' => (0x1E, true), '@' => (0x1F, true), '#' => (0x20, true), '$' => (0x21, true),
            '%' => (0x22, true), '&' => (0x24, true), '*' => (0x25, true), '(' => (0x26, true), ')' => (0x27, true),
            _ => null,
        };
    }
    public const int UsageLeftShift = 0xE1;

    // Declares the surface as a keyboard so backboardd hides the software
    // keyboard; the bitmap report we send diverges from it on purpose, exactly
    // as the captured Universal Control session did.
    private static readonly byte[] KeyboardDescriptor =
    [
        0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00, 0x25, 0x01,
        0x95, 0x08, 0x75, 0x01, 0x81, 0x02, 0x95, 0x01, 0x75, 0x08, 0x81, 0x01, 0x05, 0x07, 0x19, 0x00,
        0x29, 0xFF, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x95, 0x06, 0x75, 0x08, 0x81, 0x00, 0x05, 0x08, 0x19,
        0x01, 0x29, 0x05, 0x15, 0x00, 0x25, 0x01, 0x95, 0x05, 0x75, 0x01, 0x91, 0x02, 0x95, 0x01, 0x75,
        0x03, 0x91, 0x01, 0xC0,
    ];

    /// <summary>
    /// Registers a virtual keyboard with the daemon and returns its service id.
    /// The storage block is Swift-Codable: every leaf wrapped as {type: value},
    /// with the exact signed/unsigned choices the captured session used.
    /// </summary>
    public static async Task<ulong> CreateKeyboardAsync(XpcService service, ulong serviceId = KeyboardServiceId,
        string product = "LuminaMonitor virtual keyboard", string manufacturer = "LuminaMonitor",
        long vendorId = 0x05AC, long productId = 0x0250)
    {
        var usage = new XpcInt64(6);
        var usagePage = new XpcInt64(1);
        var storage = new Dictionary<string, object?>
        {
            ["Manufacturer"] = W("string", manufacturer),
            ["Product"] = W("string", product),
            ["ProductID"] = W("int", new XpcInt64(productId)),
            ["VendorID"] = W("int", new XpcInt64(vendorId)),
            ["PrimaryUsage"] = W("int", usage),
            ["PrimaryUsagePage"] = W("int", usagePage),
            ["DeviceUsagePairs"] = W("array", new List<object?>
            {
                W("dictionary", new Dictionary<string, object?>
                {
                    ["DeviceUsage"] = W("int", usage),
                    ["DeviceUsagePage"] = W("int", usagePage),
                }),
            }),
            ["Transport"] = W("string", "USB"),
            ["ReportDescriptor"] = W("data", KeyboardDescriptor),
            ["UniversalControlVirtualService"] = W("bool", true),
            ["_ServiceID"] = W("uint", new XpcUInt64(serviceId)),
        };
        var reply = await service.SendReceiveAsync(Envelope(new Dictionary<string, object?>
        {
            ["createService"] = new Dictionary<string, object?>
            {
                ["_0"] = new Dictionary<string, object?>
                {
                    ["DeviceUsagePairs"] = new List<object?>
                    {
                        new Dictionary<string, object?> { ["DeviceUsage"] = usage, ["DeviceUsagePage"] = usagePage },
                    },
                    ["PrimaryUsage"] = new XpcUInt64(6),
                    ["PrimaryUsagePage"] = new XpcUInt64(1),
                    ["Product"] = product,
                    ["ProductID"] = new XpcInt64(productId),
                    ["VendorID"] = new XpcInt64(vendorId),
                    ["_CoreDevice_codablePropertyStorage"] = storage,
                    ["_ServiceID"] = new XpcUInt64(serviceId),
                },
            },
        }));
        return reply.GetValueOrDefault("serviceID") switch
        {
            XpcUInt64 u => u.Value,
            XpcInt64 i => (ulong)i.Value,
            _ => serviceId,
        };
    }

    private static Dictionary<string, object?> W(string type, object? value) => new() { [type] = value };

    /// <summary>Lists the HID surfaces the daemon exposes (each carries a _ServiceID).</summary>
    public static Task<Dictionary<string, object?>> ListSurfacesAsync(XpcService service) =>
        service.SendReceiveAsync(Envelope(new Dictionary<string, object?> { ["connectedServices"] = new Dictionary<string, object?>() }));

    /// <summary>One raw report to one surface, no reply awaited.</summary>
    public static Task SendReportAsync(XpcService service, ulong surface, byte[] report) =>
        service.SendAsync(Envelope(new Dictionary<string, object?>
        {
            ["send"] = new Dictionary<string, object?>
            {
                ["_0"] = report,
                ["_1"] = new XpcUInt64(surface),
            },
        }));

    private static Dictionary<string, object?> Envelope(Dictionary<string, object?> payload) => new()
    {
        ["featureIdentifier"] = Feature,
        ["messageType"] = "Request",
        ["payload"] = payload,
    };
}

/// <summary>
/// The daemon's other door: hardware-button events (home, lock, volume,
/// mute, Siri) as single state changes on the Consumer usage page. No media
/// stream is needed for these — the button surface is born authenticated.
/// </summary>
internal static class IndigoHid
{
    public const string ServiceName = "com.apple.coredevice.hid.indigo";
    private const string Feature = "com.apple.coredevice.feature.remote.hid.button";
    public const ulong StateDown = 1, StateUp = 2, StateCanceled = 3;

    /// <summary>Named buttons → (usage page, usage, hold in ms). iOS tells a tap from a hold by the time held.</summary>
    public static readonly Dictionary<string, (ushort Page, ushort Code, int HoldMs)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        // Consumer / Menu. It is also the only thing measured to bring a dark
        // screen back: see the note on "lock" below.
        ["home"] = (0x0C, 0x40, 50),
        // Consumer / Power. Held half a second it puts the screen out and locks
        // the phone — measured on 9 September 2026, decoded frame at luminance
        // 0.0/255 and the packet rate down from 42/s to 2/s. It does NOT toggle:
        // the same usage tapped for 40 ms, and held again for 500 ms, both leave
        // the screen dark. Waking is "home".
        ["lock"] = (0x0C, 0x30, 500),
        ["volume-up"] = (0x0C, 0xE9, 50),     // Consumer / Volume Increment
        ["volume-down"] = (0x0C, 0xEA, 50),   // Consumer / Volume Decrement
        ["mute"] = (0x0C, 0xE2, 50),          // Consumer / Mute
        ["siri"] = (0x0C, 0xCF, 1000),        // Consumer / Voice Command, held to listen
    };

    /// <summary>One button state change, no reply awaited.</summary>
    public static Task SendButtonAsync(XpcService service, ushort usagePage, ushort usageCode, ulong state) =>
        service.SendAsync(new Dictionary<string, object?>
        {
            ["messageType"] = "IndigoButtonEvent",
            ["payload"] = new Dictionary<string, object?>
            {
                ["state"] = new XpcUInt64(state),
                ["usagePage"] = new XpcUInt64(usagePage),
                ["usageCode"] = new XpcUInt64(usageCode),
            },
            ["featureIdentifier"] = Feature,
        });

    /// <summary>Down, hold, up — a press as iOS expects it.</summary>
    public static async Task PressAsync(XpcService service, string name)
    {
        if (!Named.TryGetValue(name, out var button))
            throw new ArgumentException($"bouton inconnu : {name} (attendu : {string.Join(", ", Named.Keys)})");
        await SendButtonAsync(service, button.Page, button.Code, StateDown);
        await Task.Delay(button.HoldMs);
        await SendButtonAsync(service, button.Page, button.Code, StateUp);
    }
}
