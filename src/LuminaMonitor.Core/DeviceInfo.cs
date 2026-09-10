namespace LuminaMonitor.Core;

/// <summary>
/// What lockdown says the phone is, read once the TLS session is open.
/// </summary>
/// <remarks>
/// These five keys are the ones the desktop app shows and the ones the DDI
/// resolution reasons about; anything else is a <c>GetValue</c> away and does
/// not belong in a record every caller carries around.
/// </remarks>
public sealed record DeviceInfo(
    string Udid,
    string? Name,
    string? ProductType,
    string? ProductVersion,
    string? BuildVersion)
{
    /// <summary>
    /// A UDID with its middle hidden, for anything that will be read by
    /// someone other than its owner.
    /// </summary>
    /// <remarks>
    /// The second half of a modern UDID is the chip's unique identifier
    /// written out, the same number Apple's signing server is given to
    /// personalise an image — so a whole UDID in a log file is a durable
    /// hardware identity, and log files get pasted into bug reports. The ends
    /// are kept because they are what tells two phones apart at a glance, and
    /// the first eight characters are the chip model, which is public.
    /// </remarks>
    public static string Mask(string? udid) =>
        string.IsNullOrEmpty(udid) ? "(inconnu)"
        : udid.Length <= 14 ? udid
        : $"{udid[..8]}…{udid[^6..]}";
}
