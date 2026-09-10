namespace LuminaMonitor.Core;

/// <summary>
/// How far up the USB ladder a <see cref="DeviceSession"/> has climbed.
/// </summary>
/// <remarks>
/// The rungs are published in the order they are actually reached, which is
/// not quite the declaration order: the developer image goes up before the
/// tunnel, because the RSD directory only lists the HID services once the
/// image is mounted. The last two sit apart from the ladder —
/// <see cref="Resetting"/> says the whole climb is being made again from the
/// developer image up, and <see cref="Faulted"/> says it stopped, with the
/// reason travelling with the exception.
/// </remarks>
public enum SessionState
{
    Detached,
    Attached,
    Paired,
    TunnelUp,
    DdiMounted,
    MediaUp,
    Resetting,
    Faulted,
}
