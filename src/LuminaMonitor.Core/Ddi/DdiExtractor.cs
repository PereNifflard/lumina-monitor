using LuminaMonitor.Core.Usb;
using LuminaMonitor.Formats;

namespace LuminaMonitor.Core.Ddi;

/// <summary>
/// Builds a <see cref="DdiSource"/> folder out of what Apple ships: the whole
/// official chain, from either end.
/// </summary>
/// <remarks>
/// Source is any of the three shapes Apple hands the image out in: the whole
/// Xcode archive (<c>.xip</c>, ~2 GB), the Device Support component
/// (<c>.dmg</c>, ~100 MB), or the <c>.pkg</c> lifted out of either. The first
/// four bytes and the archive's own table of contents say which: <c>xar!</c>
/// plus a <c>Content</c> entry is Xcode, <c>xar!</c> plus a <c>Payload</c> is
/// the package, anything else is tried as a disk image.
///
/// <para>From the package on there is one road: Payload (pbzx/xz) → cpio →
/// iOS_DDI.dmg → UDIF → APFS/HFS+ → a faithful copy of <c>Restore/</c>
/// (BuildManifest.plist, the images, Firmware/*.trustcache).</para>
///
/// <para>Same tree, same names: which image and which trust cache belong to a
/// given phone is BuildManifest.plist's word, read at mount time, not
/// something a file extension can tell.</para>
/// </remarks>
public static class DdiExtractor
{
    /// <summary>What one extraction produced.</summary>
    public sealed record Result(DdiSource? Source, bool ManifestPresent, bool ManifestParsed, string? ProductBuildVersion, int BuildIdentities);

    public static Result Extract(string sourcePath, string outDir, IProgress<string>? progress = null)
    {
        void Say(string line) => progress?.Report(line);

        if (!File.Exists(sourcePath))
            throw new LuminaException($"{Path.GetFileName(sourcePath)} : fichier introuvable.");

        Directory.CreateDirectory(outDir);
        string ddiDmg = Path.Combine(outDir, "iOS_DDI.dmg");

        // Which of the three it is, the first four bytes start to say: "xar!"
        // is an archive — Xcode's or the package's — anything else is tried as
        // a disk image.
        byte[] sourceMagic = new byte[4];
        using (var probe = File.OpenRead(sourcePath))
        {
            if (probe.Length < 512)
                throw new LuminaException($"{Path.GetFileName(sourcePath)} : fichier trop court pour être une archive Apple ({probe.Length:N0} octets) — téléchargement incomplet ?");
            probe.ReadExactly(sourceMagic, 0, sourceMagic.Length);
        }
        bool sourceIsArchive = System.Text.Encoding.ASCII.GetString(sourceMagic) == "xar!";

        // The disk image, when there is one, must stay open as long as the
        // package read out of it — the xar reads through the HFS+ stream, it
        // does not copy. The Xcode archive is the other way round: the package
        // is lifted out to a file of its own first, because a xar seeks and a
        // cpio entry does not rewind.
        UdifImage? sourceImage = null;
        string? liftedPackage = null;
        Xar pkg;
        if (sourceIsArchive)
        {
            var archive = Xar.Open(sourcePath);
            if (archive.Entries.Any(e => e.Name == "Payload"))
            {
                Say($"Paquet : {Path.GetFileName(sourcePath)} (xar direct)");
                pkg = archive;
            }
            else if (archive.Entries.Any(e => e.Name == "Content"))
            {
                archive.Dispose();
                liftedPackage = LiftPackageFromXcode(sourcePath, progress);
                pkg = Xar.Open(liftedPackage);
            }
            else
            {
                archive.Dispose();
                throw new LuminaException($"{Path.GetFileName(sourcePath)} : archive xar sans Content ni Payload — ce n'est ni une archive Xcode ni un paquet Apple.");
            }
        }
        else
        {
            try
            {
                sourceImage = UdifImage.Open(sourcePath);
                var hfs = sourceImage.Partitions.First(p => p.Name.Contains("HFS", StringComparison.OrdinalIgnoreCase));
                var volume = LuminaMonitor.Formats.Hfs.HfsVolume.Open(new UdifBlockDevice(sourceImage, hfs.FirstSector, hfs.SectorCount));
                var pkgName = volume.ListDirectory("/").First(e => !e.IsDirectory && e.Name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)).Name;
                Say($"Paquet : {pkgName}");
                pkg = Xar.Open(volume.OpenFile("/" + pkgName));
            }
            catch (Exception exception) when (exception is not LuminaException)
            {
                sourceImage?.Dispose();
                throw new LuminaException($"{Path.GetFileName(sourcePath)} : fichier inattendu — attendu une archive Xcode (.xip),"
                    + $" un composant Device Support (.dmg) ou un paquet (.pkg). Détail : {exception.Message}", exception);
            }
        }

        try
        {
            using (sourceImage)
            using (pkg)
            {
                var payload = pkg.Entries.First(x => x.Name == "Payload");
                using var raw = pkg.Open(payload);
                var cpio = new CpioReader(OpenPayload(raw));
                int found = 0;
                while (cpio.Next() is { } e)
                {
                    string? target = e.Name.EndsWith("CandidateDDIs/iOS_DDI.dmg", StringComparison.Ordinal) ? ddiDmg
                        : e.Name.EndsWith("CandidateDDIs/iOS/version.plist", StringComparison.Ordinal) ? Path.Combine(outDir, "version.plist")
                        : null;
                    if (target is null) continue;
                    using (var output = File.Create(target))
                        cpio.OpenCurrent().CopyTo(output, 1 << 20);
                    Say($"  extrait : {Path.GetFileName(target)} ({e.Size:N0} octets)");
                    if (++found == 2) break;
                }
                if (!File.Exists(ddiDmg)) { Say("iOS_DDI.dmg absent de ce composant."); return new Result(null, false, false, null, 0); }
            }
        }
        finally
        {
            // Scratch, and large: the package lifted out of Xcode has given up
            // the one disk image it was wanted for, whichever way this ended.
            if (liftedPackage is not null)
            {
                try { Directory.Delete(Path.GetDirectoryName(liftedPackage)!, recursive: true); }
                catch (Exception) { /* the temporary folder is Windows's problem now */ }
            }
        }

        // Now the DDI container itself.
        using (var ddi = UdifImage.Open(ddiDmg))
        {
            Say($"iOS_DDI.dmg : {ddi.SectorCount:N0} secteurs, partitions : {string.Join(", ", ddi.Partitions.Select(p => p.Name))}");
            foreach (var (name, first, count) in ddi.Partitions)
            {
                byte[] probe = new byte[512 * 3];
                int readable = (int)Math.Min(3, Math.Min(count, ddi.SectorCount - first));
                try { ddi.ReadSectors(first, readable, probe.AsSpan(0, readable * 512)); } catch { continue; }
                string sig = System.Text.Encoding.ASCII.GetString(probe, 1024, 2);
                string nx = System.Text.Encoding.ASCII.GetString(probe, 32, 4);
                var device = new UdifBlockDevice(ddi, first, count);
                Func<string, IEnumerable<(string Name, bool IsDir, long Size)>>? list = null;
                Func<string, Stream>? open = null;
                if (nx == "NXSB")
                {
                    var v = LuminaMonitor.Formats.Apfs.ApfsVolume.Open(device);
                    list = p => v.ListDirectory(p).Select(x => (x.Name, x.IsDirectory, x.IsDirectory ? 0L : v.FileSize(p == "/" ? "/" + x.Name : p + "/" + x.Name)));
                    open = v.OpenFile;
                }
                else if (sig is "H+" or "HX")
                {
                    var v = LuminaMonitor.Formats.Hfs.HfsVolume.Open(device);
                    list = p => v.ListDirectory(p).Select(x => (x.Name, x.IsDirectory, x.Size));
                    open = v.OpenFile;
                }
                if (list is null) continue;
                Say($"  partition {name} : {(nx == "NXSB" ? "APFS" : "HFS+")}");
                foreach (var (n, d, s) in list("/"))
                    Say($"    {(d ? "[dir] " : $"{s,12:N0} ")} /{n}");
                if (!list("/").Any(x => x.IsDir && x.Name == "Restore")) continue;

                // A faithful copy: same tree, same names.
                void CopyTree(string sourceDir, string targetDir)
                {
                    Directory.CreateDirectory(targetDir);
                    foreach (var (n, d, s) in list!(sourceDir))
                    {
                        string child = sourceDir + "/" + n;
                        if (d) { CopyTree(child, Path.Combine(targetDir, n)); continue; }
                        using (var src = open!(child))
                        using (var dst = File.Create(Path.Combine(targetDir, n)))
                            src.CopyTo(dst, 1 << 20);
                        Say($"    {child.TrimStart('/')} ({s:N0} octets)");
                    }
                }
                CopyTree("/Restore", outDir);
            }
        }

        string manifestFile = Path.Combine(outDir, "BuildManifest.plist");
        bool manifestPresent = File.Exists(manifestFile);
        bool manifestParsed = false;
        string? productBuildVersion = null;
        int identities = 0;
        if (manifestPresent)
        {
            byte[] manifestData = File.ReadAllBytes(manifestFile);
            if (manifestData.Length > 5 && System.Text.Encoding.ASCII.GetString(manifestData, 0, 5) == "<?xml"
                && Plist.Read(manifestData) as Dictionary<string, object> is { } parsedManifest)
            {
                manifestParsed = true;
                identities = (parsedManifest.GetValueOrDefault("BuildIdentities") as List<object>)?.Count ?? 0;
                productBuildVersion = parsedManifest.GetValueOrDefault("ProductBuildVersion")?.ToString();
            }
        }
        return new Result(new DdiSource(outDir), manifestPresent, manifestParsed, productBuildVersion, identities);
    }

    /// <summary>The entry inside Xcode that carries the developer images.</summary>
    /// <remarks>
    /// The leading path is deliberately absent from the match: the application
    /// is <c>Xcode.app</c> in a release and <c>Xcode-beta.app</c> in a beta, and
    /// the archive prefixes both with a <c>.</c> of its own.
    /// </remarks>
    private const string XcodeResourcesPackage = "/Contents/Resources/Packages/XcodeSystemResources.pkg";

    /// <summary>
    /// Copies one entry out of an Xcode archive (<c>.xip</c>), named by the
    /// end of its path; null when the archive does not hold it.
    /// </summary>
    /// <remarks>
    /// One pass over ~2 GB of xz, about two minutes: xar → pbzx → cpio, every
    /// reader ours. It stops the moment the entry is copied, which for the
    /// resources package is well before the end.
    ///
    /// <para>The suffix rather than the whole path, because the leading
    /// directory is <c>Xcode.app</c> in a release and <c>Xcode-beta.app</c> in
    /// a beta, and the archive prefixes both with a <c>.</c> of its own.</para>
    /// </remarks>
    public static CpioReader.Entry? CopyFromXcodeArchive(string xipPath, string entrySuffix, string targetPath, IProgress<string>? progress = null)
    {
        void Say(string line) => progress?.Report(line);

        Say($"Archive Xcode : {Path.GetFileName(xipPath)} ({new FileInfo(xipPath).Length / (double)(1L << 30):N1} Go) — environ deux minutes.");
        using var xar = Xar.Open(xipPath);
        var content = xar.Entries.FirstOrDefault(e => e.Name == "Content")
            ?? throw new LuminaException($"{Path.GetFileName(xipPath)} : pas d'entrée Content — ce n'est pas une archive Xcode.");

        using var pbzx = new PbzxStream(xar.Open(content));
        var cpio = new CpioReader(pbzx);
        long entries = 0, bytes = 0, announced = 0;
        try
        {
            while (cpio.Next() is { } entry)
            {
                entries++;
                bytes += entry.Size;
                if (bytes >> 30 > announced)
                {
                    announced = bytes >> 30;
                    Say($"Lecture de l'archive Xcode… {announced} Go parcourus");
                }
                if (!entry.Name.EndsWith(entrySuffix, StringComparison.Ordinal))
                    continue;
                Say($"Trouvé : {entry.Name.TrimStart('.')} ({entry.Size:N0} octets)");
                using (var output = File.Create(targetPath))
                    cpio.OpenCurrent().CopyTo(output, 1 << 20);
                return entry;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            // The xz block checksums are the archive's own integrity check, and
            // a download that stopped short trips them: say so in those terms
            // rather than in the decoder's.
            throw new LuminaException($"{Path.GetFileName(xipPath)} : archive incomplète ou abîmée"
                + $" après {bytes / (double)(1L << 30):N1} Go — retélécharge-la chez Apple. Détail : {exception.Message}", exception);
        }
        Say($"{entries:N0} entrées parcourues, {entrySuffix} absent.");
        return null;
    }

    /// <summary>
    /// Lifts <c>XcodeSystemResources.pkg</c> out of an Xcode archive, into a
    /// temporary folder of its own, and returns where it went.
    /// </summary>
    /// <remarks>
    /// The package cannot be handed on as a stream — a xar seeks to its table
    /// of contents and then all over its heap, while a cpio entry is read once,
    /// forwards — so the ~145 MB lands on disk and the caller deletes it when
    /// it has served.
    /// </remarks>
    private static string LiftPackageFromXcode(string xipPath, IProgress<string>? progress)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "LuminaMonitor-xcode-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        string target = Path.Combine(scratch, "XcodeSystemResources.pkg");

        try
        {
            if (CopyFromXcodeArchive(xipPath, XcodeResourcesPackage, target, progress) is not null)
                return target;
        }
        catch (Exception)
        {
            // Whatever the walk hit, the folder it was writing into goes with it.
            try { Directory.Delete(scratch, recursive: true); } catch (Exception) { }
            throw;
        }

        try { Directory.Delete(scratch, recursive: true); } catch (Exception) { }
        throw new LuminaException($"{Path.GetFileName(xipPath)} : XcodeSystemResources.pkg introuvable"
            + " — cette archive n'est pas celle qui porte les images développeur (attendu Xcode 27).");
    }

    /// <summary>A package Payload is a cpio wrapped in pbzx (modern) or gzip (older); pick by magic.</summary>
    private static Stream OpenPayload(Stream raw)
    {
        byte[] magic = new byte[6];
        raw.Position = 0;
        int got = raw.Read(magic, 0, magic.Length);
        raw.Position = 0;
        if (got >= 4 && magic[0] == (byte)'p' && magic[1] == (byte)'b' && magic[2] == (byte)'z' && magic[3] == (byte)'x')
            return new PbzxStream(raw);
        if (got >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
            return new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress);
        throw new InvalidDataException($"charge utile inconnue, magie {Convert.ToHexString(magic, 0, Math.Max(0, got))}");
    }
}
