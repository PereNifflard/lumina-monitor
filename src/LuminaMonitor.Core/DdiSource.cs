namespace LuminaMonitor.Core;

/// <summary>
/// A folder holding a faithful copy of the developer image's <c>Restore/</c>
/// tree: <c>BuildManifest.plist</c>, the images, <c>Firmware/*.trustcache</c>.
/// </summary>
/// <remarks>
/// Which image and which trust cache belong to a given phone is the manifest's
/// word, read at mount time — so the folder is all the caller has to name.
/// Filling it is a separate step (the probe's <c>extract-devsupport</c>, whose
/// core is <see cref="Ddi.DdiExtractor"/>).
/// </remarks>
public sealed record DdiSource(string Folder);
