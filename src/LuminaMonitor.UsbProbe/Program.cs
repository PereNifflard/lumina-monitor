using LuminaMonitor.Core;
using LuminaMonitor.Core.Ddi;
using LuminaMonitor.Core.Hid;
using LuminaMonitor.Core.Media;
using LuminaMonitor.Core.RemoteXpc;
using LuminaMonitor.Core.Tunnel;
using LuminaMonitor.Core.Usb;

// Rungs of the USB ladder, climbed one command at a time:
//
//   LuminaMonitor.UsbProbe list            devices currently attached (default)
//   LuminaMonitor.UsbProbe info            identity keys via lockdown, unpaired
//   LuminaMonitor.UsbProbe session         TLS session with the stored pairing record
//   LuminaMonitor.UsbProbe devmode reveal  make the Developer Mode switch appear in Settings
//   LuminaMonitor.UsbProbe devmode enable  turn Developer Mode on (the phone reboots)
//   LuminaMonitor.UsbProbe listen [sec]    watch attach/detach events
//   LuminaMonitor.UsbProbe keys <texte>    type a line on the virtual keyboard
//   LuminaMonitor.UsbProbe latency-test    end-to-end latency, measured on the pixels
//   LuminaMonitor.UsbProbe motion-test [sec] [default|half|bitrate:N|rctl]
//                                          does the mirror fall behind when the screen moves?
//   LuminaMonitor.UsbProbe mirror-test [sec] [sortie.bmp] [--suite=<dossier>]
//                                          live mirror; --suite writes one still a second,
//                                          named by the PC time of its last packet
//   LuminaMonitor.UsbProbe clock-test [sec] [--variant=…] [--out=<dossier>]
//                                          absolute delay: every second turning over on the
//                                          phone, written out with the PC time it arrived
//   LuminaMonitor.UsbProbe audio-info [sec] [capture.rtp] [--variant=…] [--video]
//                                          the phone's sound: the whole negotiation, what the
//                                          RTP carries, and a raw capture
//   LuminaMonitor.UsbProbe aac-selftest    does Windows decode the phone's AAC-ELD? Offline
//   LuminaMonitor.UsbProbe sps-selftest    the SPS rewriter: read back, and a second pass
//   LuminaMonitor.UsbProbe watchdog-selftest  the stream watch's ladder, offline
//   LuminaMonitor.UsbProbe tcp-selftest    the tunnel's TCP against a paper phone, offline
//   LuminaMonitor.UsbProbe flood [sec]     load the input path and ping through it
//   LuminaMonitor.UsbProbe mouse-flood <sec> [hz] [hold|hover]
//                                          synthetic mouse storm aimed at the app window
//   LuminaMonitor.UsbProbe chassis-test [--out=<dossier>]
//                                          the four chassis controls on the real phone,
//                                          and what locking the screen does to the session
//   LuminaMonitor.UsbProbe clipboard [texte]
//                                          the phone's pasteboard: read it, or write it
//   LuminaMonitor.UsbProbe pair <udid>     read the stored pairing record
//   LuminaMonitor.UsbProbe buid            the multiplexer's host identifier
//
// Commands that open a session need a copy of the developer image's Restore/
// tree, which extract-devsupport produces. The probe does not go looking for
// one: it uses the folder named by LUMINA_DDI, or "ddi27" beside the current
// directory when that variable is unset. (The desktop application remembers its
// own copy in %APPDATA% and ignores both.)
//
// Every byte on the wire here is composed in this project; the only Apple
// component on the PC side is the multiplexer the "Appareils Apple" app ships.

const int LockdownPort = 62078;

// Which copy of Restore/ the session commands (tap, button) climb with: they
// open a full DeviceSession, which makes sure that very image is mounted.
// mount takes the folder as an argument instead.
//
// The probe, unlike the application, does not go looking: it uses whatever this
// names, relative to the current directory unless it is absolute. "ddi27" is
// only the habit of this workspace — the folder extract-devsupport was last
// pointed at. Set LUMINA_DDI to your own, or extract into a folder of that
// name; the desktop application finds its copy on its own and ignores this.
string DefaultDdiFolder = Environment.GetEnvironmentVariable("LUMINA_DDI") is { Length: > 0 } fromEnv
    ? fromEnv
    : "ddi27";

string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";

if (command == "extract-devsupport")
{
    // extract-devsupport <source> [dossier] : the whole official chain, from
    // either end Apple ships it — the Device Support DMG (UDIF → HFS+ → pkg) or
    // that same .pkg lifted straight out of the Xcode archive. The chain itself
    // lives in Core (DdiExtractor), because the desktop app needs it too; the
    // probe only prints what it reports.
    string sourcePath = args[1];
    string outDir = args.Length > 2 ? args[2] : "ddi";
    var extracted = DdiExtractor.Extract(sourcePath, outDir, new ConsoleLog());
    if (extracted.Source is null) return 3;
    Say($"{(extracted.ManifestPresent ? "OK " : "MANQUE ")}BuildManifest.plist");
    if (extracted.ManifestParsed)
        Say($"  ProductBuildVersion {extracted.ProductBuildVersion}, {extracted.BuildIdentities} BuildIdentities.");
    Say($"Dossier pret : {Path.GetFullPath(outDir)} — etape suivante : mount {outDir}");
    return 0;
}

if (command == "inspect-dmg")
{
    // inspect-dmg <image.dmg> [regex] : partitions and the filesystem signature
    // of each; with a regex, every path matching it (case-insensitive).
    using var image = LuminaMonitor.Formats.UdifImage.Open(args[1]);
    var filter = args.Length > 2 ? new System.Text.RegularExpressions.Regex(args[2], System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
    Say($"{image.SectorCount:N0} secteurs, {image.Partitions.Count} partition(s) blkx.");
    foreach (var (name, first, count) in image.Partitions)
    {
        byte[] sector = new byte[512 * 3];
        int readable = (int)Math.Min(3, Math.Min(count, image.SectorCount - first));
        try { image.ReadSectors(first, readable, sector.AsSpan(0, readable * 512)); } catch (Exception e) { Say($"  {name}: lecture impossible ({e.Message})"); continue; }
        string sig = System.Text.Encoding.ASCII.GetString(sector, 1024, 2);
        string nx = System.Text.Encoding.ASCII.GetString(sector, 32, 4);
        string fs = sig is "H+" or "HX" ? $"HFS+ ({sig})" : nx == "NXSB" ? "APFS" : "?";
        Say($"  {name,-24} secteurs [{first:N0}, +{count:N0})  {fs}");

        var device = new LuminaMonitor.Formats.UdifBlockDevice(image, first, count);
        if (nx == "NXSB")
        {
            try
            {
                var volume = LuminaMonitor.Formats.Apfs.ApfsVolume.Open(device);
                int files = 0, dirs = 0;
                void WalkApfs(string path, int depth)
                {
                    foreach (var entry in volume.ListDirectory(path))
                    {
                        string full = path == "/" ? "/" + entry.Name : path + "/" + entry.Name;
                        if (entry.IsDirectory) dirs++; else files++;
                        bool show = filter is null ? depth == 0 : filter.IsMatch(full);
                        if (show)
                            Say($"      {(entry.IsDirectory ? "[dir] " : $"{volume.FileSize(full),12:N0} ")} {full}");
                        if (entry.IsDirectory && depth < 16)
                        {
                            try { WalkApfs(full, depth + 1); }
                            catch (Exception e) { Say($"      {full}: {e.Message}"); }
                        }
                    }
                }
                WalkApfs("/", 0);
                Say($"      APFS : {dirs} dossier(s), {files} fichier(s).");
            }
            catch (Exception e) { Say($"      APFS : {e.Message}"); }
        }
        else if (sig is "H+" or "HX")
        {
            try
            {
                var volume = LuminaMonitor.Formats.Hfs.HfsVolume.Open(device);
                int files = 0, dirs = 0;
                var interesting = new System.Text.RegularExpressions.Regex(@"BuildManifest|trustcache|\.dmg$|Restore$|DDI|\.pkg$|\.plist$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                void Walk(string path, int depth)
                {
                    foreach (var (entryName, isDir, _, size) in volume.ListDirectory(path))
                    {
                        string full = path == "/" ? "/" + entryName : path + "/" + entryName;
                        if (isDir) dirs++; else files++;
                        if (filter is not null ? filter.IsMatch(full) : depth <= 1 || interesting.IsMatch(entryName))
                            Say($"      {(isDir ? "[dir] " : $"{size,12:N0} ")} {full}");
                        if (isDir && depth < 12 && !entryName.StartsWith('\0'))
                        {
                            try { Walk(full, depth + 1); }
                            catch (Exception e) { Say($"      {full}: {e.Message}"); }
                        }
                    }
                }
                Walk("/", 0);
                Say($"      HFS+ : {dirs} dossier(s), {files} fichier(s).");

                // An Apple installer package is a xar; its Payload a pbzx cpio.
                // The same three readers that opened Xcode open it here.
                foreach (var (entryName, isDir, _, _) in volume.ListDirectory("/"))
                {
                    if (isDir || !entryName.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) continue;
                    Say($"      --- paquet {entryName} ---");
                    using var pkg = LuminaMonitor.Formats.Xar.Open(volume.OpenFile("/" + entryName));
                    foreach (var x in pkg.Entries)
                        Say($"        xar {x.Path,-40} {x.Length,14:N0} octets ({x.Encoding})");
                    var payloads = pkg.Entries.Where(x => x.Name == "Payload").ToList();
                    var wanted = new System.Text.RegularExpressions.Regex(@"DDI|Restore|BuildManifest|trustcache|\.dmg$|DeveloperDiskImage", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    foreach (var payload in payloads)
                    {
                        Say($"        --- charge utile {payload.Path} ---");
                        using var raw = pkg.Open(payload);
                        var cpio = new LuminaMonitor.Formats.CpioReader(OpenPayload(raw));
                        int total = 0, shown = 0;
                        while (cpio.Next() is { } e)
                        {
                            total++;
                            if (wanted.IsMatch(e.Name) && shown++ < 60)
                                Say($"        {e.Size,14:N0}  {e.Name}");
                        }
                        Say($"        {total:N0} entree(s) dans la charge utile, {shown} affichee(s).");
                    }
                }
            }
            catch (Exception e) { Say($"      HFS+ : {e.Message}"); }
        }
    }
    return 0;
}

if (command == "list-xip")
{
    // list-xip <Xcode.xip> <regex> : names every archive entry matching the
    // pattern, with its size — a map of where Apple put things this release.
    string xipPath = args[1];
    var pattern = new System.Text.RegularExpressions.Regex(args.Length > 2 ? args[2] : "DDI", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    using var xar = LuminaMonitor.Formats.Xar.Open(xipPath);
    var content = xar.Entries.First(e => e.Name == "Content");
    using var pbzx = new LuminaMonitor.Formats.PbzxStream(xar.Open(content));
    var cpio = new LuminaMonitor.Formats.CpioReader(pbzx);
    int matches = 0;
    while (cpio.Next() is { } entry)
    {
        if (!pattern.IsMatch(entry.Name)) continue;
        matches++;
        Say($"{entry.Size,14:N0}  {Convert.ToString(entry.Mode & 0xF000, 8),3}  {entry.Name}");
    }
    Say($"{matches} entree(s) correspondante(s).");
    return 0;
}

if (command == "grep-xip")
{
    // grep-xip <Xcode.xip> <aiguille1,aiguille2,...> [maxParFichier] : a single
    // pass over the whole payload, hunting ASCII needles inside file contents —
    // which asset names and URLs Xcode's own binaries carry. The search itself
    // lives in XipGrep because spans are barred from this async body (CS8652).
    string xipPath = args.Length > 1 ? args[1] : throw new ArgumentException("grep-xip <Xcode.xip> <aiguilles> [maxParFichier]");
    string needleList = args.Length > 2 ? args[2] : throw new ArgumentException("grep-xip <Xcode.xip> <aiguilles> [maxParFichier]");
    int maxPerFile = args.Length > 3 && int.TryParse(args[3], out int parsed) ? parsed : 3;
    string[] needles = needleList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return XipGrep.Run(xipPath, needles, maxPerFile, Say);
}

// Offline work needs no phone and no multiplexer.
if (command == "extract-xip")
{
    // extract-xip <Xcode.xip> [dossier de sortie] [suffixe d'entree] : pulls one
    // file out of Xcode without opening anything else — xar → pbzx/xz → cpio,
    // all ours. The suffix names the archive entry to take (iOS_DDI.dmg unless
    // told otherwise); it lands in the folder under its own leaf name. The walk
    // itself lives in Core, because extract-devsupport now makes the same one
    // to reach XcodeSystemResources.pkg.
    string xipPath = args.Length > 1 ? args[1] : throw new ArgumentException("extract-xip <Xcode.xip> [dossier] [suffixe d'entree]");
    string outDir = args.Length > 2 ? args[2] : "ddi";
    string wantedSuffix = args.Length > 3 ? args[3] : "iOS_DDI.dmg";
    Directory.CreateDirectory(outDir);

    string target = Path.Combine(outDir, Path.GetFileName(wantedSuffix));
    var clock = System.Diagnostics.Stopwatch.StartNew();
    if (DdiExtractor.CopyFromXcodeArchive(xipPath, wantedSuffix, target, new ConsoleLog()) is null)
    {
        Say($"{wantedSuffix} introuvable dans l'archive.");
        return 3;
    }
    Say($"Extrait dans {target} en {clock.Elapsed:mm\\:ss} ({new FileInfo(target).Length:N0} octets).");
    return 0;
}

if (command == "formats-check")
{
    // Builds a synthetic XIP in memory — xar around pbzx (raw chunks) around
    // a cpio holding a fake iOS_DDI.dmg — and runs it through our readers.
    // Everything but the xz decoder is exercised, before a 4 GB real run.
    static byte[] Cpio(params (string Name, byte[] Data)[] files)
    {
        var ms = new MemoryStream();
        void Entry(string name, byte[] data)
        {
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
            string header = "070707" + "000000" + "000001" + "100644" + "000000" + "000000" + "000001" + "000000"
                + "00000000000" + Convert.ToString(nameBytes.Length, 8).PadLeft(6, '0') + Convert.ToString(data.Length, 8).PadLeft(11, '0');
            ms.Write(System.Text.Encoding.ASCII.GetBytes(header));
            ms.Write(nameBytes);
            ms.Write(data);
        }
        foreach (var (name, data) in files) Entry(name, data);
        Entry("TRAILER!!!", []);
        return ms.ToArray();
    }
    // Built in a plain (non-async) local function: spans are not allowed in
    // this async body, and they are the natural tool for header packing.
    static string BuildFakeXip(byte[] payload)
    {
        // pbzx: magic, flags, then two raw chunks (the second closes the run).
        var pb = new MemoryStream();
        void U64(ulong v) { Span<byte> b = stackalloc byte[8]; System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(b, v); pb.Write(b); }
        pb.Write("pbzx"u8); U64(0x01000000);
        int half = payload.Length / 2;
        U64(0x01000000); U64((ulong)half); pb.Write(payload, 0, half);
        U64(0); U64((ulong)(payload.Length - half)); pb.Write(payload, half, payload.Length - half);
        byte[] content = pb.ToArray();
        // xar: header + zlib TOC + heap.
        string toc = $"<?xml version=\"1.0\"?><xar><toc><file id=\"1\"><name>Content</name><type>file</type><data><offset>0</offset><size>{content.Length}</size><length>{content.Length}</length><encoding style=\"application/octet-stream\"/></data></file></toc></xar>";
        byte[] tocBytes = System.Text.Encoding.UTF8.GetBytes(toc);
        var tocZ = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(tocZ, System.IO.Compression.CompressionLevel.Optimal, true)) z.Write(tocBytes);
        string path = Path.Combine(Path.GetTempPath(), "formats-check.xip");
        using var f = File.Create(path);
        Span<byte> h = stackalloc byte[28];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(h, 0x78617221);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(h[4..], 28);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(h[6..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(h[8..], (ulong)tocZ.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(h[16..], (ulong)tocBytes.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(h[24..], 1);
        f.Write(h); f.Write(tocZ.ToArray()); f.Write(content);
        return path;
    }
    byte[] payload = Cpio(("Xcode.app/Contents/Info.plist", "<plist/>"u8.ToArray()),
        ("Xcode.app/Contents/Resources/CoreDeviceDDIs/iOS_DDI.dmg", System.Text.Encoding.ASCII.GetBytes(new string('D', 70000))));
    string xipPath = BuildFakeXip(payload);
    int seen = 0; long ddiSize = -1; bool contentOk = false; int chunks;
    using (var xar = LuminaMonitor.Formats.Xar.Open(xipPath))
    {
        var entry = xar.Entries.Single();
        using var pbzx = new LuminaMonitor.Formats.PbzxStream(xar.Open(entry));
        var cpio = new LuminaMonitor.Formats.CpioReader(pbzx);
        while (cpio.Next() is { } e)
        {
            seen++;
            if (e.Name.EndsWith("iOS_DDI.dmg", StringComparison.Ordinal))
            {
                using var ms = new MemoryStream();
                cpio.OpenCurrent().CopyTo(ms);
                ddiSize = ms.Length;
                contentOk = ms.ToArray().All(b => b == (byte)'D');
            }
        }
        chunks = pbzx.ChunksRead;
    }
    Say($"xar/pbzx/cpio : {seen} entrees, iOS_DDI.dmg = {ddiSize} octets, contenu {(contentOk ? "intact" : "CORROMPU")}, {chunks} blocs pbzx.");
    File.Delete(xipPath);
    return seen == 2 && ddiSize == 70000 && contentOk ? 0 : 9;
}

// Offline checks need no phone and no multiplexer.
if (command == "offer-check")
{
    MediaOffer.SelfCheck();
    Say("Blob media identique au gabarit Xcode, octet pour octet.");
    byte[] offer = MediaOffer.BuildVideo(MediaOffer.NewCallId(), 0x12345678);
    Say($"Offre complete : {offer.Length} octets de plist binaire (entete {System.Text.Encoding.ASCII.GetString(offer, 0, 8)}).");
    Say("Banques offertes par defaut : 123 (H.264/AVC) puis 100 (HEVC) — l'ordre du gabarit Xcode.");
    // Les leviers, hors chemin par defaut : la taille de l'offre change, donc
    // les champs partent bien sur le fil. Ce que le telephone en fait, seul
    // motion-test le dira.
    var tuning = new VideoOfferOptions { MaxWidth = 664, MaxHeight = 1448, MaxBitrateKbps = 2000 };
    byte[] tuned = MediaOffer.BuildVideo(MediaOffer.NewCallId(), 0x12345678, options: tuning);
    Say($"Leviers ({tuning}) : offre de {tuned.Length} octets, {tuned.Length - offer.Length:+0;-0;0} par rapport au defaut.");
    return 0;
}

if (command == "mf-selftest")
{
    // Instancie le decodeur H.264 de Windows et regle les types, rien de plus :
    // le HRESULT exact suffit a dire si l'interop COM tient.
    return VideoTools.OnMta(() =>
    {
        try
        {
            using var decoder = new H264Decoder();
            Say("Decodeur Media Foundation instancie, types entree H264 / sortie NV12 regles.");
            Say($"Geometrie annoncee avant tout echantillon : {decoder.Width}x{decoder.Height}, stride {decoder.Stride}.");
            return 0;
        }
        catch (Exception exception)
        {
            Say($"Echec du decodeur : {exception.Message}");
            return 8;
        }
    });
}

if (command == "aac-selftest")
{
    // aac-selftest : le decodeur AAC de Windows contre l'AudioSpecificConfig du
    // telephone (F8 E6 40 00, objet 39 = ER AAC ELD) puis contre un AAC-LC
    // ordinaire. Deux HRESULT, et la question « Windows peut-il decoder ce que
    // le telephone envoie » est tranchee sans telephone.
    return AudioTools.AacSelfTest(Say);
}

if (command == "sps-selftest")
{
    // sps-selftest : la reecriture du SPS, sans telephone. Un bit faux dans un
    // jeu de parametres ne donne pas une image fausse mais aucune image, donc
    // chaque cas est relu apres coup et repasse une seconde fois.
    return VideoTools.SpsSelfTest(Say);
}

if (command == "tcp-selftest")
{
    // tcp-selftest : la pile TCP du tunnel contre un telephone de papier — deux
    // tuyaux en memoire et un pair minimal. Le seul endroit ou la fenetre de
    // reception peut etre fermee a volonte, c'est-a-dire ou la panne qui
    // bloquait l'entree est reproductible.
    return await TunnelTools.TcpSelfTestAsync(Say);
}

if (command == "watchdog-selftest")
{
    // watchdog-selftest : joue la veille du flux sur des instants inventes.
    // Ni telephone ni reseau : c'est le seul moyen d'exercer l'echelle
    // image cle -> relance -> reset doux, qui par nature ne tourne que quand
    // tout le reste est deja casse.
    return WatchdogTools.SelfTest(Say);
}

if (command == "decode-capture")
{
    // decode-capture <fichier.rtp> [n] [sortie.bmp] : rejoue une capture hors
    // ligne, decode, ecrit la n-ieme image en BMP 24 bits.
    if (args.Length < 2) { Say("usage : decode-capture <fichier.rtp> [n] [sortie.bmp]"); return 2; }
    string capture = args[1];
    int wanted = args.Length > 2 ? int.Parse(args[2]) : 1;
    string bmp = args.Length > 3 ? args[3] : Path.ChangeExtension(capture, $".{wanted}.bmp");
    return VideoTools.DecodeCapture(capture, wanted, bmp, Say);
}

if (command == "mouse-flood")
{
    // mouse-flood <secondes> [hz=2000] [hold|hover] : envoie des mouvements de
    // souris de synthese dans la fenetre de l'app, a la cadence d'une souris
    // rapide, pour mesurer ce que le fil d'interface fait du flot. Ni telephone
    // ni multiplexeur : la fenetre suffit, y compris en --replay.
    int floodSeconds = args.Length > 1 && int.TryParse(args[1], out int fs) ? Math.Clamp(fs, 1, 600) : 10;
    int floodHz = args.Length > 2 && int.TryParse(args[2], out int fh) ? Math.Clamp(fh, 10, 8000) : 2000;
    bool hold = args.Length <= 3 || !args[3].Equals("hover", StringComparison.OrdinalIgnoreCase);
    return MouseFlood.Run(floodSeconds, floodHz, hold, Say);
}

UsbmuxClient mux;
try
{
    mux = await UsbmuxClient.ConnectAsync();
    Say("Multiplexeur Apple joignable sur 127.0.0.1:27015.");
}
catch (Exception exception)
{
    Say($"Multiplexeur Apple injoignable : {exception.Message}");
    Say("Il demarre a la demande — brancher l'iPhone en USB, ou ouvrir l'app Appareils Apple.");
    return 1;
}

using (mux)
{
    switch (command)
    {
        case "list":
        {
            var devices = await ListDevicesAsync(mux);
            if (devices.Count == 0) { Say("Aucun appareil attache."); break; }
            Say($"{devices.Count} appareil(s) :");
            foreach (var d in devices)
                Console.Write(Plist.Dump(d, 1));
            break;
        }

        case "info":
        {
            var (deviceId, udid) = await FirstDeviceAsync(mux);
            using var pipe = await UsbmuxClient.ConnectAsync();
            var lockdown = new LockdownClient(await pipe.ConnectToDeviceAsync(deviceId, LockdownPort));
            Say($"Tunnel usbmux ouvert vers lockdownd (port {LockdownPort}).");
            var type = await lockdown.QueryTypeAsync();
            Say($"QueryType -> {type.GetValueOrDefault("Type") ?? Plist.Dump(type).Trim()}");
            await ShowValues(lockdown, "ProductType", "ProductVersion", "BuildVersion", "DeviceName",
                "DeviceClass", "HardwareModel", "UniqueDeviceID", "SerialNumber", "WiFiAddress", "CPUArchitecture");
            break;
        }

        case "session":
        {
            var (deviceId, udid) = await FirstDeviceAsync(mux);
            var record = await ReadPairRecordAsync(mux, udid);
            using var pipe = await UsbmuxClient.ConnectAsync();
            var lockdown = new LockdownClient(await pipe.ConnectToDeviceAsync(deviceId, LockdownPort));
            string? refusal = await lockdown.StartSessionAsync(record);
            if (refusal is not null)
            {
                Say($"StartSession refuse : {refusal}");
                Say(refusal switch
                {
                    "InvalidHostID" => "Le telephone ne connait pas cet hote : l'appairage n'a pas ete valide (Se fier).",
                    "UserDeniedPairing" => "Le telephone a refuse l'appairage.",
                    "PairingDialogResponsePending" => "Le telephone attend la reponse a « Se fier a cet ordinateur ».",
                    _ => "Voir le nom de l'erreur.",
                });
                return 4;
            }
            Say("Session TLS ouverte avec lockdownd — l'hote est de confiance.");
            // Proof: keys that were GetProhibited a moment ago.
            await ShowValues(lockdown, "SerialNumber", "ProductVersion", "DevicePublicKey",
                "TimeZone", "PasswordProtected", "ActivationState");
            break;
        }

        case "devmode":
        {
            string action = args.Length > 1 ? args[1].ToLowerInvariant() : "reveal";

            var (deviceId, udid) = await FirstDeviceAsync(mux);
            var record = await ReadPairRecordAsync(mux, udid);
            using var pipe = await UsbmuxClient.ConnectAsync();
            var lockdown = new LockdownClient(await pipe.ConnectToDeviceAsync(deviceId, LockdownPort));
            string? refusal = await lockdown.StartSessionAsync(record);
            if (refusal is not null) { Say($"StartSession refuse : {refusal}"); return 4; }
            Say("Session TLS ouverte.");

            if (action == "status")
            {
                // The switch's state lives in the amfi domain; true means the
                // tunnel layer may proceed, false means the phone still has to
                // be rebooted into Developer Mode.
                var status = await lockdown.GetValueRawAsync("DeveloperModeStatus", "com.apple.security.mac.amfi");
                Say($"DeveloperModeStatus = {status.GetValueOrDefault("Value") ?? status.GetValueOrDefault("Error") ?? "?"}");
                break;
            }

            long code = action switch { "reveal" => 0, "enable" => 1, "confirm" => 2,
                _ => throw new ArgumentException("devmode status|reveal|enable|confirm") };

            var (port, ssl) = await lockdown.StartServiceAsync("com.apple.amfi.lockdown");
            Say($"Service amfi demarre sur le port {port} (TLS={ssl}).");
            using var servicePipe = await UsbmuxClient.ConnectAsync();
            Stream service = await servicePipe.ConnectToDeviceAsync(deviceId, port);
            if (ssl)
                service = await PlistService.WrapTlsAsync(service, record.HostCertificate);

            await PlistService.WriteAsync(service, new Dictionary<string, object> { ["action"] = code });
            var reply = await PlistService.ReadAsync(service);
            Say($"amfi action {code} ({action}) -> {Plist.Dump(reply).Trim()}");
            Say(code switch
            {
                0 => "Regarder Reglages > Confidentialite et securite : « Mode developpeur » doit etre apparu.",
                1 => "Le telephone va redemarrer, puis demander de confirmer le Mode developpeur.",
                _ => "Confirmation envoyee.",
            });
            break;
        }

        case "tunnel":
        {
            var (deviceId, udid) = await FirstDeviceAsync(mux);
            var record = await ReadPairRecordAsync(mux, udid);
            using var pipe = await UsbmuxClient.ConnectAsync();
            var lockdown = new LockdownClient(await pipe.ConnectToDeviceAsync(deviceId, LockdownPort));
            string? refusal = await lockdown.StartSessionAsync(record);
            if (refusal is not null) { Say($"StartSession refuse : {refusal}"); return 4; }
            Say("Session TLS ouverte.");

            var (port, ssl) = await lockdown.StartServiceAsync(CdTunnel.ServiceName);
            Say($"CoreDeviceProxy demarre sur le port {port} (TLS={ssl}).");
            using var servicePipe = await UsbmuxClient.ConnectAsync();
            Stream service = await servicePipe.ConnectToDeviceAsync(deviceId, port);
            if (ssl)
                service = await PlistService.WrapTlsAsync(service, record.HostCertificate);

            var tunnel = new CdTunnel(service);
            var hs = await tunnel.EstablishAsync();
            Say("*** TUNNEL COREDEVICE OUVERT ***");
            Say($"  notre adresse   : {hs.ClientAddress}  (MTU {hs.Mtu})");
            Say($"  telephone       : {hs.ServerAddress}");
            Say($"  annuaire RSD    : port {hs.ServerRsdPort}");

            // Anything the phone volunteers right away (neighbour solicitation,
            // router advertisement) tells us the pipe really carries IPv6.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                var packet = await tunnel.ReceivePacketAsync().WaitAsync(cts.Token);
                Say($"  premier paquet IPv6 recu : {packet.Length} octets, next header {packet[6]}");
            }
            catch (OperationCanceledException) { Say("  (aucun paquet spontane en 3 s — normal)"); }
            break;
        }

        case "rsd":
        {
            var (deviceId, udid) = await FirstDeviceAsync(mux);
            var record = await ReadPairRecordAsync(mux, udid);
            using var pipe = await UsbmuxClient.ConnectAsync();
            var lockdown = new LockdownClient(await pipe.ConnectToDeviceAsync(deviceId, LockdownPort));
            string? refusal = await lockdown.StartSessionAsync(record);
            if (refusal is not null) { Say($"StartSession refuse : {refusal}"); return 4; }

            var (port, ssl) = await lockdown.StartServiceAsync(CdTunnel.ServiceName);
            using var servicePipe = await UsbmuxClient.ConnectAsync();
            Stream service = await servicePipe.ConnectToDeviceAsync(deviceId, port);
            if (ssl)
                service = await PlistService.WrapTlsAsync(service, record.HostCertificate);
            var tunnel = new CdTunnel(service);
            var hs = await tunnel.EstablishAsync();
            Say($"Tunnel ouvert : nous {hs.ClientAddress}, telephone {hs.ServerAddress}, RSD {hs.ServerRsdPort}.");

            await using var net = new TunnelNet(tunnel, hs) { Log = Say };
            net.Start();

            // Our own TCP over our own IPv6 over the tunnel: the phone must
            // answer the handshake with a SYN-ACK for any of this to be real.
            using var tcp = await net.ConnectTcpAsync(hs.ServerRsdPort);
            Say($"*** TCP ETABLI vers [{hs.ServerAddress}]:{hs.ServerRsdPort} (port local {tcp.LocalPort}) ***");

            // HTTP/2 client preface + empty SETTINGS. RSD speaks HTTP/2 and
            // answers a preface with its own SETTINGS frame — nine bytes that
            // prove the whole stack, from checksum to window, end to end.
            byte[] preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();
            byte[] settings = [0, 0, 0, 4, 0, 0, 0, 0, 0];
            await tcp.WriteAsync(preface);
            await tcp.WriteAsync(settings);
            Say("Preface HTTP/2 envoyee, en attente de la reponse…");

            byte[] buffer = new byte[4096];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            int total = 0;
            try
            {
                while (true)
                {
                    int n = await tcp.ReadAsync(buffer.AsMemory(total), cts.Token);
                    if (n == 0) break;
                    total += n;
                    Say($"  recu {n} octet(s) : {Convert.ToHexString(buffer, total - n, Math.Min(n, 32))}{(n > 32 ? "…" : "")}");
                    if (total >= 9)
                    {
                        int length = (buffer[0] << 16) | (buffer[1] << 8) | buffer[2];
                        Say($"  trame HTTP/2 : type {buffer[3]} ({(buffer[3] == 4 ? "SETTINGS" : "?")}), flags 0x{buffer[4]:X2}, longueur {length}");
                    }
                    if (total >= buffer.Length - 64) break;
                }
            }
            catch (OperationCanceledException) { Say($"  fin de l'attente ({total} octet(s) au total)."); }
            break;
        }

        case "ddi":
        {
            var (deviceId, udid) = await FirstDeviceAsync(mux);
            var record = await ReadPairRecordAsync(mux, udid);
            using var pipe = await UsbmuxClient.ConnectAsync();
            var lockdown = new LockdownClient(await pipe.ConnectToDeviceAsync(deviceId, LockdownPort));
            string? refusal = await lockdown.StartSessionAsync(record);
            if (refusal is not null) { Say($"StartSession refuse : {refusal}"); return 4; }

            var (port, ssl) = await lockdown.StartServiceAsync(ImageMounter.ServiceName);
            Say($"mobile_image_mounter demarre sur le port {port} (TLS={ssl}).");
            using var servicePipe = await UsbmuxClient.ConnectAsync();
            Stream service = await servicePipe.ConnectToDeviceAsync(deviceId, port);
            if (ssl)
                service = await PlistService.WrapTlsAsync(service, record.HostCertificate);
            var mounter = new ImageMounter(service);

            var dev = await mounter.QueryDeveloperModeStatusAsync();
            Say($"QueryDeveloperModeStatus -> {Plist.Dump(dev).Trim()}");

            var devices = await mounter.CopyDevicesAsync();
            Say("CopyDevices (images montees) :");
            Console.Write(Plist.Dump(devices, 1));

            var lookup = await mounter.LookupImageAsync("Personalized");
            Say("LookupImage Personalized :");
            Console.Write(Plist.Dump(lookup, 1));

            var ids = await mounter.QueryPersonalizationIdentifiersAsync();
            Say("QueryPersonalizationIdentifiers :");
            Console.Write(Plist.Dump(ids, 1));

            var nonce = await mounter.QueryNonceAsync();
            Say($"QueryNonce -> {Plist.Dump(nonce).Trim()}");
            break;
        }

        case "catalogue":
        {
            var (deviceId, udid) = await FirstDeviceAsync(mux);
            var record = await ReadPairRecordAsync(mux, udid);
            using var pipe = await UsbmuxClient.ConnectAsync();
            var lockdown = new LockdownClient(await pipe.ConnectToDeviceAsync(deviceId, LockdownPort));
            string? refusal = await lockdown.StartSessionAsync(record);
            if (refusal is not null) { Say($"StartSession refuse : {refusal}"); return 4; }

            var (port, ssl) = await lockdown.StartServiceAsync(CdTunnel.ServiceName);
            using var servicePipe = await UsbmuxClient.ConnectAsync();
            Stream service = await servicePipe.ConnectToDeviceAsync(deviceId, port);
            if (ssl)
                service = await PlistService.WrapTlsAsync(service, record.HostCertificate);
            var tunnel = new CdTunnel(service);
            var hs = await tunnel.EstablishAsync();
            await using var net = new TunnelNet(tunnel, hs) { Log = Say };
            net.Start();
            using var tcp = await net.ConnectTcpAsync(hs.ServerRsdPort);
            Say($"TCP etabli vers RSD [{hs.ServerAddress}]:{hs.ServerRsdPort}.");

            using var xpc = new RemoteXpc(tcp) { Log = Say };
            await xpc.ConnectAsync();
            Say("RemoteXPC initialise (HTTP/2 + poignee de main XPC).");

            var reply = await xpc.SendReceiveAsync(new Dictionary<string, object?>
            {
                ["MessageType"] = "Handshake",
                ["MessagingProtocolVersion"] = new XpcUInt64(7),
                ["UUID"] = new XpcUuid(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)),
                ["Properties"] = new Dictionary<string, object?>
                {
                    ["RemoteXPCVersionFlags"] = new XpcUInt64(0x0100000000000006),
                    ["SensitivePropertiesVisible"] = true,
                },
                ["Services"] = new Dictionary<string, object?>(),
            });
            Say("*** ANNUAIRE RSD RECU ***");

            if (reply.TryGetValue("Properties", out var p) && p is Dictionary<string, object?> props)
            {
                foreach (var key in new[] { "UniqueDeviceID", "ProductType", "OSVersion", "BuildVersion", "Name", "Model" })
                    if (props.TryGetValue(key, out var v))
                        Say($"  {key,-16} = {Xpc.Dump(v)}");
            }

            if (reply.TryGetValue("Services", out var s) && s is Dictionary<string, object?> services)
            {
                Say($"  {services.Count} service(s) :");
                foreach (var (name, entry) in services.OrderBy(e => e.Key))
                {
                    var e = entry as Dictionary<string, object?>;
                    string portText = e?.GetValueOrDefault("Port") is { } pv ? Xpc.Dump(pv) : "?";
                    bool usesXpc = e?.GetValueOrDefault("Properties") is Dictionary<string, object?> ep
                        && ep.GetValueOrDefault("UsesRemoteXPC") is true;
                    Say($"    {name,-64} port {portText,-6} {(usesXpc ? "RemoteXPC" : "lockdown")}");
                }
                bool hid = services.ContainsKey("com.apple.coredevice.hid.universalhidservice");
                Say(hid
                    ? "  => service HID PRESENT (une image developpeur est montee)."
                    : "  => service HID ABSENT : il vit dans l'image developpeur, pas encore montee (attendu).");
            }
            else
            {
                Console.Write(Xpc.Dump(reply, 1));
            }
            break;
        }

        case "ping":
        {
            // ping [n] — n ICMPv6 Echo Requests to the phone, 200 ms apart, over
            // the same climb as catalogue but with nothing above IPv6: what it
            // times is the cable, Apple's multiplexer and the daemon's own packet
            // loop. Over USB that round trip should be a couple of milliseconds;
            // anything above thirty means the transport, not the display path, is
            // where a click goes to wait.
            int pings = args.Length > 1 ? int.Parse(args[1]) : 20;
            if (pings is < 1 or > 1000) { Say("usage : ping [1..1000]"); return 2; }

            var (deviceId, udid) = await FirstDeviceAsync(mux);
            var record = await ReadPairRecordAsync(mux, udid);
            using var pipe = await UsbmuxClient.ConnectAsync();
            var lockdown = new LockdownClient(await pipe.ConnectToDeviceAsync(deviceId, LockdownPort));
            string? refusal = await lockdown.StartSessionAsync(record);
            if (refusal is not null) { Say($"StartSession refuse : {refusal}"); return 4; }

            var (port, ssl) = await lockdown.StartServiceAsync(CdTunnel.ServiceName);
            using var servicePipe = await UsbmuxClient.ConnectAsync();
            Stream service = await servicePipe.ConnectToDeviceAsync(deviceId, port);
            if (ssl)
                service = await PlistService.WrapTlsAsync(service, record.HostCertificate);
            var tunnel = new CdTunnel(service);
            var hs = await tunnel.EstablishAsync();
            await using var net = new TunnelNet(tunnel, hs) { Log = Say };
            net.Start();
            Say($"Tunnel ouvert : nous {hs.ClientAddress}, telephone {hs.ServerAddress} (MTU {hs.Mtu}).");

            // One identifier for the whole run, so a reply to somebody else's
            // ping — or to one of ours from a previous run — is not counted.
            ushort identifier = (ushort)Random.Shared.Next(1, 0x10000);
            byte[] echoPayload = new byte[32];
            Random.Shared.NextBytes(echoPayload);

            // Sequence -> when it left, and who is waiting. A dictionary rather
            // than a single slot because a reply that arrives after its timeout
            // must be dropped, not credited to the next request.
            var pending = new System.Collections.Concurrent
                .ConcurrentDictionary<ushort, (long Sent, TaskCompletionSource<double> Reply)>();
            net.EchoReply = message =>
            {
                long arrived = System.Diagnostics.Stopwatch.GetTimestamp();
                var span = message.Span;
                if (span.Length < 8) return;
                ushort id = (ushort)((span[4] << 8) | span[5]);
                ushort seq = (ushort)((span[6] << 8) | span[7]);
                if (id != identifier || !pending.TryRemove(seq, out var waiting)) return;
                waiting.Reply.TrySetResult(
                    (arrived - waiting.Sent) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            };

            Say($"{pings} echo(s) ICMPv6 de 32 octets vers {hs.ServerAddress}, un toutes les 200 ms…");
            var trips = new List<double>(pings);
            int lostEchoes = 0;
            for (ushort sequence = 1; sequence <= pings; sequence++)
            {
                var reply = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                pending[sequence] = (System.Diagnostics.Stopwatch.GetTimestamp(), reply);
                await net.SendEchoAsync(identifier, sequence, echoPayload);
                try
                {
                    double milliseconds = await reply.Task.WaitAsync(TimeSpan.FromSeconds(1));
                    trips.Add(milliseconds);
                    Say($"  echo {sequence,3} : {milliseconds:F2} ms");
                }
                catch (TimeoutException)
                {
                    pending.TryRemove(sequence, out _);
                    lostEchoes++;
                    Say($"  echo {sequence,3} : perdu (1 s sans reponse)");
                }
                if (sequence < pings)
                    await Task.Delay(200);
            }
            net.EchoReply = null;

            if (trips.Count == 0)
            {
                Say($"*** AUCUNE REPONSE *** — {pings} demande(s) envoyee(s), {lostEchoes} sans reponse.");
                return 8;
            }
            trips.Sort();
            double middle = trips.Count % 2 == 1
                ? trips[trips.Count / 2]
                : (trips[trips.Count / 2 - 1] + trips[trips.Count / 2]) / 2;
            Say($"=== ICMPv6 a travers le tunnel : {trips.Count}/{pings} reponse(s),"
                + $" {lostEchoes} perte(s) ({100.0 * lostEchoes / pings:F0} %) ===");
            Say($"  aller-retour : min {trips[0]:F2} ms, mediane {middle:F2} ms, max {trips[^1]:F2} ms,"
                + $" moyenne {trips.Average():F2} ms");
            break;
        }

        case "mount":
        {
            // mount <dossier> : copie de Restore/ ; image et trustcache resolus
            // via BuildManifest.plist. Toute la ceremonie vit dans Core
            // (DdiManager) ; la sonde n'imprime que ce qu'il rapporte.
            string dir = args.Length > 1 ? args[1] : "ddi";
            var (deviceId, udid) = await FirstDeviceAsync(mux);
            var record = await ReadPairRecordAsync(mux, udid);

            var manager = new DdiManager(deviceId, record);
            manager.UnlockRequired += message => Say(message);
            var status = await manager.EnsureMountedAsync(new DdiSource(dir), new ConsoleLog());
            if (status == DdiStatus.AlreadyMounted)
                Say("Image developpeur deja montee : rien n'a ete envoye.");
            else if (status == DdiStatus.Mounted)
                Say("*** IMAGE DEVELOPPEUR MONTEE *** — relancer 'catalogue' : le service HID doit apparaitre.");
            else
                return status switch
                {
                    DdiStatus.ManifestBinary => 5,
                    DdiStatus.UnmountRefused or DdiStatus.MountRefused => 6,
                    DdiStatus.StillLocked => 7,
                    _ => 3,
                };
            break;
        }

        case "unmount":
        {
            // unmount : takes the developer image down (its daemons with it);
            // the next mount brings them back fresh.
            var (deviceId, udid) = await FirstDeviceAsync(mux);
            var record = await ReadPairRecordAsync(mux, udid);
            var manager = new DdiManager(deviceId, record);
            var (count, refusal) = await manager.UnmountAllAsync(new ConsoleLog());
            if (refusal is not null) { Say($"Demontage refuse : {refusal}"); return 6; }
            Say(count == 0 ? "Aucune image developpeur montee." : $"*** {count} IMAGE(S) DEMONTEE(S) ***");
            break;
        }

        case "offer-check":
        {
            MediaOffer.SelfCheck();
            Say("Blob media identique au gabarit Xcode, octet pour octet.");
            byte[] offer = MediaOffer.BuildVideo(MediaOffer.NewCallId(), 0x12345678);
            Say($"Offre complete : {offer.Length} octets de plist binaire (entete {System.Text.Encoding.ASCII.GetString(offer, 0, 8)}).");
            break;
        }

        case "tap":
        {
            // tap <x%> <y%> [dossier ddi] — a genuine touch on the main screen,
            // through the whole stack: DeviceSession climbs every rung, media
            // stream included, then the injector sends the two reports.
            double px = args.Length > 1 ? double.Parse(args[1]) : 50;
            double py = args.Length > 2 ? double.Parse(args[2]) : 50;
            string tapDdi = args.Length > 3 ? args[3] : DefaultDdiFolder;
            int tx = (int)Math.Round(px / 100.0 * 65535), ty = (int)Math.Round(py / 100.0 * 65535);

            await using var session = new DeviceSession(new DdiSource(tapDdi), new ConsoleLog());
            long rtpPackets = 0;
            session.RtpPacket += _ => Interlocked.Increment(ref rtpPackets);
            session.UnlockRequired += message => Say(message);
            await session.ConnectAsync();

            Say($"TAP a {px}% / {py}% (0x{tx:X4}, 0x{ty:X4})…");
            await session.Input.TapAsync(px / 100.0, py / 100.0);
            // The daemon dispatches asynchronously: leave the stream up a moment.
            await Task.Delay(500);
            Say($"*** TAP ENVOYE *** (paquets RTP recus pendant la session : {rtpPackets})");
            break;
        }

        case "keys":
        {
            // keys <texte> — the same full climb as tap, then the text typed on
            // the virtual keyboard (surface 512). Open Notes on the phone first:
            // the proof is the text appearing in the field, which nothing on this
            // side can read back.
            string text = args.Length > 1 ? string.Join(' ', args[1..]) : "bonjour";

            await using var session = new DeviceSession(new DdiSource(DefaultDdiFolder), new ConsoleLog());
            session.UnlockRequired += message => Say(message);
            await session.ConnectAsync();

            Say($"FRAPPE de « {text} » ({text.Length} caractere(s))…");
            await session.Input.TypeAsync(text);
            // The daemon dispatches asynchronously, and the media stream has to
            // stay up while it does: closing at once loses the last keystrokes.
            await Task.Delay(5000);
            Say($"*** TEXTE ENVOYE *** (messages abandonnes : {session.Input.DroppedMessages})");
            break;
        }

        case "stream-info":
        {
            // stream-info [avc-only|hevc-only|avc-then-hevc|hevc-then-avc] [secondes] [capture]
            // — the same climb as tap, but the media stream is the subject: which
            // codec bank the phone answers with, and what the RTP really carries.
            string codecName = args.Length > 1 ? args[1].ToLowerInvariant() : "avc-only";
            VideoCodecs codecs;
            switch (codecName)
            {
                case "hevc-then-avc": codecs = VideoCodecs.HevcThenAvc; break;
                case "avc-only": codecs = VideoCodecs.AvcOnly; break;
                case "hevc-only": codecs = VideoCodecs.HevcOnly; break;
                case "avc-then-hevc": codecs = VideoCodecs.AvcThenHevc; break;
                default:
                    Say("usage : stream-info [avc-only|hevc-only|avc-then-hevc|hevc-then-avc] [secondes] [fichier de capture]");
                    return 2;
            }
            int watchSeconds = args.Length > 2 ? int.Parse(args[2]) : 5;
            string? capturePath = args.Length > 3 ? args[3] : null;

            MediaOffer.SelfCheck();
            Say("Blob media par defaut identique au gabarit Xcode (SelfCheck OK).");
            Say($"Offre demandee : {codecName}, observation {watchSeconds} s"
                + (capturePath is null ? ", pas de capture." : $", capture -> {capturePath}"));

            using var tap = new RtpTap(capturePath);
            await using var streamSession = new DeviceSession(new DdiSource(DefaultDdiFolder), new ConsoleLog())
            {
                VideoCodecs = codecs,
            };
            streamSession.RtpPacket += tap.Add;
            streamSession.UnlockRequired += message => Say(message);
            // Arme avant la montee : le telephone emet des l'instant ou il repond
            // a startmediastream, et les jeux de parametres et l'unique image cle
            // de la session sont dans ces premieres millisecondes. Le chronometre
            // ne part qu'au premier paquet, donc les debits restent justes.
            tap.Arm();
            await streamSession.ConnectAsync();

            var media = streamSession.Media;
            if (media is null || !media.Streaming)
            {
                Say($"*** OFFRE REFUSEE ({codecName}) *** — erreur CoreDevice, verbatim :");
                Console.WriteLine(media?.Failure ?? "(aucun motif rapporte)");
                break;
            }

            Say($"*** OFFRE ACCEPTEE ({codecName}) *** — reponse du daemon, complete :");
            Console.Write(Xpc.Dump(media.StreamConfig, 1));
            DumpBlobs(media.StreamConfig, "", Say);

            Say($"Comptage RTP pendant {watchSeconds} s…");
            await Task.Delay(watchSeconds * 1000);
            tap.Report(Say);
            Say("Arret du flux video et de la session…");
            break;
        }

        case "media-status":
        {
            // media-status : ce que le serveur media du telephone croit encore
            // en cours. Une session qui y traine apres une sonde tuee est ce qui
            // rend le micro du telephone indisponible a ses autres apps.
            int watch = args.Length > 1 && int.TryParse(args[1], out int parsedWatch) ? Math.Clamp(parsedWatch, 0, 600) : 0;
            int statusCode = await AudioTools.MediaStatusAsync(DefaultDdiFolder, Say, watch);
            if (statusCode != 0) return statusCode;
            break;
        }

        case "media-release":
        {
            // media-release : ferme toutes les sessions media que le telephone
            // croit encore en cours. Le remede au micro reste reserve.
            int releaseCode = await AudioTools.MediaReleaseAsync(DefaultDdiFolder, Say);
            if (releaseCode != 0) return releaseCode;
            break;
        }

        case "audio-leak-test":
        {
            // audio-leak-test : ouvre un flux audio et NE LE FERME PAS, puis
            // rend la main. A lancer et tuer pour reproduire exactement ce qui
            // arrive quand la sonde ou l'app est tuee : c'est le seul moyen
            // d'observer la session fantome sans casser la session du fondateur.
            int leakCode = await AudioTools.LeakAsync(DefaultDdiFolder, Say);
            if (leakCode != 0) return leakCode;
            break;
        }

        case "audio-info":
        {
            // audio-info [secondes] [capture.rtp] [--variant=…] [--video]
            // [--direction=…] — le son du telephone : la negociation entiere, ce
            // que le RTP transporte, et une capture brute au meme format que la
            // video. Avec --video, l'audio partage l'identifiant de session du
            // flux video, comme le miroir de Xcode.
            int audioSeconds = 10;
            string? audioCapture = null;
            string audioVariant = "default";
            bool audioWithVideo = false;
            string audioDirection = "output";
            foreach (string argument in args[1..])
            {
                if (argument.StartsWith("--variant=", StringComparison.OrdinalIgnoreCase))
                    audioVariant = argument[10..];
                else if (argument.StartsWith("--direction=", StringComparison.OrdinalIgnoreCase))
                    audioDirection = argument[12..];
                else if (argument.Equals("--video", StringComparison.OrdinalIgnoreCase))
                    audioWithVideo = true;
                else if (int.TryParse(argument, out int parsed))
                    audioSeconds = parsed;
                else
                    audioCapture = argument;
            }
            if (audioSeconds is < 1 or > 300)
            {
                Say($"usage : audio-info [1..300] [capture.rtp] [--variant=<variante>] [--video] [--direction=<output|input>]"
                    + $"{Environment.NewLine}{AudioTools.VariantUsage}");
                return 2;
            }
            int audioCode = await AudioTools.RunAsync(audioSeconds, audioCapture, audioVariant, audioWithVideo,
                audioDirection, DefaultDdiFolder, Say);
            if (audioCode != 0) return audioCode;
            break;
        }

        case "mirror-test":
        {
            // mirror-test [secondes] [sortie.bmp] [--suite=<dossier>] — la
            // session complete sur le telephone, decodage en direct, et la
            // preuve que les rapports de reception tiennent le flux au-dela des
            // vingt secondes. Avec --suite, une image par seconde y est ecrite,
            // nommee par l'heure PC du dernier paquet qui l'a formee : c'est la
            // seule mesure du decalage absolu que cette machine sache produire,
            // et elle se lit contre le chronometre du telephone.
            int seconds = 60;
            string bmpPath = "mirror_last.bmp";
            string? stillFolder = null;
            string variant = "default";
            foreach (string argument in args[1..])
            {
                if (argument.StartsWith("--suite=", StringComparison.OrdinalIgnoreCase))
                    stillFolder = argument[8..];
                else if (argument.StartsWith("--variant=", StringComparison.OrdinalIgnoreCase))
                    variant = argument[10..];
                else if (int.TryParse(argument, out int parsed))
                    seconds = parsed;
                else
                    bmpPath = argument;
            }
            await VideoTools.MirrorTestAsync(seconds, bmpPath, DefaultDdiFolder, Say, stillFolder, variant);
            break;
        }

        case "clock-test":
        {
            // clock-test [secondes] [--variant=<variante>] [--out=<dossier>] —
            // le retard absolu du miroir, mesure contre l'horloge du telephone
            // lui-meme : la sonde detecte chaque changement de seconde a
            // l'ecran, ecrit l'image qui l'a montre et l'heure PC de son
            // dernier paquet. La latence se lit en soustrayant l'heure
            // affichee sur l'image de l'heure PC de la transition.
            int clockSeconds = 12;
            string clockVariant = "default";
            string clockFolder = "clock";
            foreach (string argument in args[1..])
            {
                if (argument.StartsWith("--variant=", StringComparison.OrdinalIgnoreCase))
                    clockVariant = argument[10..];
                else if (argument.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
                    clockFolder = argument[6..];
                else if (int.TryParse(argument, out int parsed))
                    clockSeconds = parsed;
                else
                {
                    Say($"usage : clock-test [5..120] [--variant=<variante>] [--out=<dossier>]{Environment.NewLine}{VideoTools.VariantUsage}");
                    return 2;
                }
            }
            if (clockSeconds is < 5 or > 120)
            {
                Say("usage : clock-test [5..120] [--variant=<variante>] [--out=<dossier>]");
                return 2;
            }
            int clockCode = await ClockTools.RunAsync(clockSeconds, clockVariant, clockFolder, DefaultDdiFolder, Say);
            if (clockCode != 0) return clockCode;
            break;
        }

        case "motion-test":
        {
            // motion-test [secondes] [default|half|bitrate:<kbit/s>|rctl] — le
            // miroir sous un doigt qui fait defiler sans s'arreter : trois
            // secondes de repos, le mouvement, trois secondes de repos, et une
            // ligne par seconde dont la derive est celle qui tranche. Ouvrir
            // Reglages sur le telephone avant de lancer.
            int motionSeconds = 20;
            string motionVariant = "default";
            foreach (string argument in args[1..])
            {
                if (int.TryParse(argument, out int parsed)) motionSeconds = parsed;
                else motionVariant = argument;
            }
            if (motionSeconds is < 5 or > 300)
            {
                Say("usage : motion-test [5..300] [default|half|bitrate:<kbit/s>|rctl]");
                return 2;
            }
            int motionCode = await VideoTools.MotionTestAsync(motionSeconds, motionVariant, DefaultDdiFolder, Say);
            if (motionCode != 0) return motionCode;
            break;
        }

        case "latency-test":
        {
            // latency-test [n] [move] — la latence de bout en bout, mesuree sur
            // les pixels : un bouton (ou un glisser court) part a un instant
            // connu, et l'on attend la premiere image decodee qui a change.
            int trials = 5;
            bool moveMode = false;
            foreach (string argument in args[1..])
            {
                if (argument.Equals("move", StringComparison.OrdinalIgnoreCase)) moveMode = true;
                else if (int.TryParse(argument, out int parsed)) trials = parsed;
                else { Say("usage : latency-test [n] [move]"); return 2; }
            }
            if (trials is < 1 or > 50) { Say("usage : latency-test [1..50] [move]"); return 2; }
            await InputTools.LatencyTestAsync(trials, moveMode, DefaultDdiFolder, Say);
            break;
        }

        case "flood":
        {
            // flood [secondes] [chained|coalesce] — un glisser circulaire a 1000
            // positions/s avec un ping toutes les 200 ms : ce que la charge du
            // chemin d'entree fait au reste du tunnel.
            int floodSeconds = 5;
            var floodMode = InputTools.FloodMode.Coalesced;
            foreach (string argument in args[1..])
            {
                if (argument.Equals("chained", StringComparison.OrdinalIgnoreCase)) floodMode = InputTools.FloodMode.Chained;
                else if (argument.Equals("coalesce", StringComparison.OrdinalIgnoreCase)) floodMode = InputTools.FloodMode.Coalesced;
                else if (int.TryParse(argument, out int parsed)) floodSeconds = parsed;
                else { Say("usage : flood [secondes] [chained|coalesce]"); return 2; }
            }
            if (floodSeconds is < 1 or > 60) { Say("usage : flood [1..60] [chained|coalesce]"); return 2; }
            await InputTools.FloodAsync(floodSeconds, floodMode, DefaultDdiFolder, Say);
            break;
        }

        case "button":
        {
            // button <home|lock|volume-up|volume-down|mute|siri> [...] — hardware
            // buttons through the daemon's Indigo door, one press each, in order.
            var names = args.Length > 1 ? args[1..] : ["volume-up"];
            await using var session = new DeviceSession(new DdiSource(DefaultDdiFolder), new ConsoleLog());
            session.UnlockRequired += message => Say(message);
            await session.ConnectAsync();

            foreach (string name in names)
            {
                Say($"Bouton {name}…");
                await session.Input.PressButtonAsync(name);
                await Task.Delay(800);
            }
            // The daemon dispatches asynchronously: closing at once can lose the last event.
            await Task.Delay(500);
            Say($"*** {names.Length} BOUTON(S) ENVOYE(S) *** (messages abandonnes : {session.Input.DroppedMessages})");
            break;
        }

        case "chassis-test":
        {
            // chassis-test [--out=<dossier>] — les quatre commandes dessinees
            // sur le chassis de la fenetre, pressees sur le vrai telephone, et
            // ce que le verrouillage fait a la session : debits paquet par
            // seconde et images decodees ecrites sur le disque, luminance
            // moyenne a cote, parce qu'un ecran eteint et un flux arrete se
            // ressemblent dans un journal et jamais dans une image.
            string chassisFolder = "chassis";
            int longLock = 9;
            foreach (string argument in args[1..])
            {
                if (argument.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
                    chassisFolder = argument[6..];
                else if (argument.StartsWith("--lock=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(argument[7..], out int parsed))
                    longLock = Math.Clamp(parsed, 0, 120);
                else { Say("usage : chassis-test [--out=<dossier>] [--lock=<secondes>]"); return 2; }
            }
            await ChassisTools.ChassisTestAsync(DefaultDdiFolder, Say, chassisFolder, longLock);
            break;
        }

        case "clipboard":
        {
            // clipboard [texte] — sans argument, ce que le presse-papiers du
            // telephone contient ; avec un argument, il l'y ecrit puis le
            // relit, parce qu'un envoi sans relecture ne prouve rien.
            string? written = args.Length > 1 ? string.Join(' ', args[1..]) : null;

            await using var session = new DeviceSession(new DdiSource(DefaultDdiFolder), new ConsoleLog());
            session.UnlockRequired += message => Say(message);
            await session.ConnectAsync();

            if (written is not null)
            {
                await session.WritePhoneClipboardAsync(written);
                Say($"*** ECRIT *** {written.Length} caractere(s) dans le presse-papiers du telephone.");
            }
            var content = await session.ReadPhoneClipboardSnapshotAsync();
            Say(content.Kind switch
            {
                ClipboardKind.Text => $"Presse-papiers du telephone : {content.Text!.Length} caractere(s),"
                    + $" {content.Bytes} octet(s) UTF-8.",
                ClipboardKind.Image => $"Presse-papiers du telephone : une image ({content.Type}, {content.Bytes} octets).",
                ClipboardKind.Data => $"Presse-papiers du telephone : des donnees ({content.Type}, {content.Bytes} octets).",
                _ => "Presse-papiers du telephone : vide.",
            });
            if (content.Kind == ClipboardKind.Text)
                Console.WriteLine(content.Text);
            if (written is not null && content.Text != written)
                Say("*** RELECTURE DIFFERENTE DE L'ECRITURE ***");
            break;
        }

        case "listen":
        {
            int seconds = args.Length > 1 ? int.Parse(args[1]) : 30;
            var ack = await mux.RequestAsync("Listen");
            Say($"Listen -> {Plist.Dump(ack).Trim()}");
            Say($"A l'ecoute des branchements pendant {seconds} s…");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var evt = await mux.ReceiveAsync().WaitAsync(cts.Token);
                    Say($"Evenement {evt.GetValueOrDefault("MessageType")} :");
                    Console.Write(Plist.Dump(evt, 1));
                }
            }
            catch (OperationCanceledException) { Say("Fin de l'ecoute."); }
            break;
        }

        case "pair":
        {
            if (args.Length < 2) { Say("usage : pair <udid>"); return 2; }
            var reply = await mux.RequestAsync("ReadPairRecord",
                new Dictionary<string, object> { ["PairRecordID"] = args[1] });
            if (reply.TryGetValue("PairRecordData", out var data) && data is byte[] bytes)
            {
                var record = Plist.Read(bytes) as Dictionary<string, object>;
                Say($"Enregistrement d'appairage trouve ({bytes.Length} octets). Cles :");
                if (record is not null)
                    foreach (var (k, v) in record)
                        Say($"  {k} = {(v is byte[] b ? $"<{b.Length} octets>" : v is string s && s.Length > 60 ? s[..60] + "…" : v)}");
            }
            else
            {
                Say("Pas d'enregistrement d'appairage :");
                Console.Write(Plist.Dump(reply, 1));
            }
            break;
        }

        case "buid":
        {
            var reply = await mux.RequestAsync("ReadBUID");
            Console.Write(Plist.Dump(reply, 1));
            break;
        }

        default:
            Say($"Commande inconnue : {command}");
            return 2;
    }
}
return 0;

/// <summary>A package Payload is a cpio wrapped in pbzx (modern) or gzip (older); pick by magic.</summary>
static Stream OpenPayload(Stream raw)
{
    byte[] magic = new byte[6];
    raw.Position = 0;
    int got = raw.Read(magic, 0, magic.Length);
    raw.Position = 0;
    if (got >= 4 && magic[0] == (byte)'p' && magic[1] == (byte)'b' && magic[2] == (byte)'z' && magic[3] == (byte)'x')
        return new LuminaMonitor.Formats.PbzxStream(raw);
    if (got >= 2 && magic[0] == 0x1F && magic[1] == 0x8B)
        return new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionMode.Decompress);
    throw new InvalidDataException($"charge utile inconnue, magie {Convert.ToHexString(magic, 0, Math.Max(0, got))}");
}

static async Task<List<object>> ListDevicesAsync(UsbmuxClient mux)
{
    var reply = await mux.RequestAsync("ListDevices");
    return reply.TryGetValue("DeviceList", out var dl) && dl is List<object> list ? list : [];
}

static async Task<(long DeviceId, string Udid)> FirstDeviceAsync(UsbmuxClient mux)
{
    var devices = await ListDevicesAsync(mux);
    if (devices.Count == 0)
        throw new InvalidOperationException("Aucun appareil attache.");
    var first = (Dictionary<string, object>)devices[0];
    long deviceId = (long)first["DeviceID"];
    var props = first.TryGetValue("Properties", out var pr) ? pr as Dictionary<string, object> : null;
    string udid = props?.GetValueOrDefault("SerialNumber")?.ToString()
        ?? throw new InvalidOperationException("Appareil sans UDID.");
    // Masked: this line prefixes almost every command's output, and that
    // output ends up pasted into bug reports. `list` and `info` still show the
    // whole thing, because showing it is what they are for.
    Say($"Appareil usbmux #{deviceId}, UDID {DeviceInfo.Mask(udid)}.");
    return (deviceId, udid);
}

static async Task<PairRecord> ReadPairRecordAsync(UsbmuxClient mux, string udid)
{
    var reply = await mux.RequestAsync("ReadPairRecord",
        new Dictionary<string, object> { ["PairRecordID"] = udid });
    if (!reply.TryGetValue("PairRecordData", out var data) || data is not byte[] bytes)
        throw new InvalidOperationException("Pas d'enregistrement d'appairage pour cet appareil.");
    var dict = Plist.Read(bytes) as Dictionary<string, object>
        ?? throw new InvalidDataException("enregistrement d'appairage illisible");
    Say($"Enregistrement d'appairage charge (HostID {dict["HostID"]}).");
    return PairRecord.Parse(dict);
}

static async Task ShowValues(LockdownClient lockdown, params string[] keys)
{
    foreach (var key in keys)
    {
        var reply = await lockdown.GetValueRawAsync(key);
        if (reply.TryGetValue("Value", out var value))
            Say($"  {key,-18} = {(value is byte[] b ? $"<{b.Length} octets>" : value)}");
        else
            Say($"  {key,-18} : {reply.GetValueOrDefault("Error") ?? "pas de valeur"}");
    }
}

/// <summary>
/// The blobs an XPC dump only counts: every byte[] in the reply, in full hex,
/// because the codec the phone chose is decided inside one of them.
/// </summary>
static void DumpBlobs(object? value, string path, Action<string> say)
{
    const int MaxHexBytes = 4096;
    switch (value)
    {
        case IDictionary<string, object?> dict:
            foreach (var (key, item) in dict)
                DumpBlobs(item, path.Length == 0 ? key : path + "." + key, say);
            break;
        case IList<object?> list:
            for (int i = 0; i < list.Count; i++)
                DumpBlobs(list[i], $"{path}[{i}]", say);
            break;
        case byte[] blob:
            say($"  blob {path} : {blob.Length} octets"
                + (blob.Length > MaxHexBytes ? $" (premiers {MaxHexBytes} en hexadecimal)" : " en hexadecimal"));
            int shown = Math.Min(blob.Length, MaxHexBytes);
            for (int at = 0; at < shown; at += 32)
                say($"    {at:X4}  {Convert.ToHexString(blob, at, Math.Min(32, shown - at))}");
            break;
    }
}

static void Say(string message) =>
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");

/// <summary>
/// Core's log and its progress channel, both on this console, in the format
/// <c>Say</c> uses.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="Progress{T}"/>: that one posts to the thread
/// pool, and a mount trace whose lines arrive out of order is worse than no
/// trace at all. Reporting on the calling thread keeps the transcript honest.
/// </remarks>
internal sealed class ConsoleLog : ILog, IProgress<string>
{
    public void Info(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {message}");

    public void Warn(string message) => Info(message);

    public void Report(string value) => Info(value);
}

/// <summary>
/// One streaming pass over a XIP's cpio payload, looking for ASCII needles in
/// the contents of every file. Same chain as list-xip and extract-xip — xar →
/// pbzx/xz → cpio — read once, forward only, because the payload is 8 GB and
/// the stream cannot seek.
/// </summary>
/// <remarks>
/// Kept in a type of its own: spans are not allowed inside Program's async
/// top-level body (CS8652), and a vectorised <c>IndexOf</c> over a span is the
/// whole point. Each file is read in 1 MiB blocks that keep a tail of
/// (longest needle - 1) bytes, so a match straddling two blocks is still seen;
/// absolute positions weed out the duplicates that tail would otherwise cause.
/// </remarks>
internal static class XipGrep
{
    private const int BlockSize = 1 << 20;
    private const int MaxToken = 200;        // longest token reported
    private const int MaxTokensPerNeedle = 20000;
    private const int ReportedTokens = 40;

    /// <summary>What one distinct token is worth: how many files hold it, and where.</summary>
    private sealed class TokenStat
    {
        public int FileCount;
        public string? LastFile;
        public readonly List<string> Samples = new();
    }

    private sealed class NeedleStat
    {
        public readonly Dictionary<string, TokenStat> Tokens = new(StringComparer.Ordinal);
        public long Occurrences;
        public long Dropped;                 // tokens past the distinct-token ceiling
    }

    public static int Run(string xipPath, string[] needleTexts, int maxPerFile, Action<string> say)
    {
        if (needleTexts.Length == 0) { say("Aucune aiguille fournie."); return 2; }
        if (maxPerFile < 1) maxPerFile = 1;

        byte[][] needles = needleTexts.Select(System.Text.Encoding.ASCII.GetBytes).ToArray();
        int overlap = needles.Max(n => n.Length) - 1;
        var stats = needleTexts.Select(_ => new NeedleStat()).ToArray();

        byte[] buffer = new byte[BlockSize + overlap];
        int[] hits = new int[needles.Length];
        long[] lastMatch = new long[needles.Length];

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long entries = 0, contentBytes = 0, scannedBytes = 0;

        using var xar = LuminaMonitor.Formats.Xar.Open(xipPath);
        var content = xar.Entries.FirstOrDefault(e => e.Name == "Content")
            ?? throw new InvalidDataException("pas d'entree Content dans le XIP");
        using var pbzx = new LuminaMonitor.Formats.PbzxStream(xar.Open(content));
        var cpio = new LuminaMonitor.Formats.CpioReader(pbzx);

        say($"Aiguilles ({needles.Length}) : {string.Join(" | ", needleTexts)}");
        say($"Bloc {BlockSize / 1024} Kio, recouvrement {overlap} octets, {maxPerFile} occurrence(s) max par aiguille et par fichier.");

        while (cpio.Next() is { } entry)
        {
            entries++;
            contentBytes += entry.Size;
            if (entries % 5000 == 0)
                say($"  {entries:N0} entrees, {contentBytes / (1L << 30)} Go parcourus, {pbzx.ChunksRead} blocs xz, {clock.Elapsed:mm\\:ss}");
            if (entry.Size == 0) continue;

            Array.Clear(hits);
            for (int i = 0; i < lastMatch.Length; i++) lastMatch[i] = -1;
            var stream = cpio.OpenCurrent();
            int carry = 0;                   // bytes kept from the previous block
            long baseOffset = 0;             // file offset of buffer[0]
            int live = needles.Length;       // needles not yet capped for this file

            while (live > 0)
            {
                int read = stream.Read(buffer, carry, BlockSize);
                if (read <= 0) break;
                scannedBytes += read;
                int available = carry + read;
                var window = buffer.AsSpan(0, available);

                for (int i = 0; i < needles.Length; i++)
                {
                    if (hits[i] >= maxPerFile) continue;
                    var needle = needles[i].AsSpan();
                    int from = 0;
                    while (hits[i] < maxPerFile && from <= available - needle.Length)
                    {
                        int at = window[from..].IndexOf(needle);
                        if (at < 0) break;
                        at += from;
                        from = at + 1;
                        long absolute = baseOffset + at;
                        if (absolute <= lastMatch[i]) continue;   // already seen in the kept tail
                        lastMatch[i] = absolute;
                        hits[i]++;
                        Record(stats[i], Token(buffer, available, at, needle.Length), entry.Name);
                    }
                    if (hits[i] >= maxPerFile) live--;
                }

                carry = Math.Min(overlap, available);
                if (carry > 0) Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
                baseOffset += available - carry;
            }
        }

        for (int i = 0; i < needleTexts.Length; i++)
        {
            var stat = stats[i];
            say("");
            say($"=== {needleTexts[i]} : {stat.Tokens.Count} jeton(s) distinct(s), {stat.Occurrences} occurrence(s) retenue(s) ===");
            if (stat.Tokens.Count == 0) { say("  (aucune occurrence)"); continue; }
            foreach (var pair in stat.Tokens
                         .OrderByDescending(x => x.Value.FileCount)
                         .ThenBy(x => x.Key, StringComparer.Ordinal)
                         .Take(ReportedTokens))
            {
                say($"  {pair.Value.FileCount,6} fichier(s)  {pair.Key}");
                foreach (string sample in pair.Value.Samples)
                    say($"                 <- {Tail(sample, 90)}");
            }
            if (stat.Tokens.Count > ReportedTokens)
                say($"  ... {stat.Tokens.Count - ReportedTokens} autre(s) jeton(s) non listes.");
            if (stat.Dropped > 0)
                say($"  ... {stat.Dropped} occurrence(s) ignorees : plafond de {MaxTokensPerNeedle} jetons distincts atteint.");
        }

        say("");
        say($"{entries:N0} entrees lues, {contentBytes:N0} octets de contenu dont {scannedBytes:N0} scannes, {pbzx.ChunksRead} blocs xz, duree {clock.Elapsed:hh\\:mm\\:ss}.");
        return 0;
    }

    /// <summary>Counts a token once per file, keeping the first two paths as samples.</summary>
    private static void Record(NeedleStat stat, string token, string file)
    {
        stat.Occurrences++;
        if (!stat.Tokens.TryGetValue(token, out var entry))
        {
            if (stat.Tokens.Count >= MaxTokensPerNeedle) { stat.Dropped++; return; }
            stat.Tokens[token] = entry = new TokenStat();
        }
        if (entry.LastFile == file) return;
        entry.LastFile = file;
        entry.FileCount++;
        if (entry.Samples.Count < 2) entry.Samples.Add(file);
    }

    /// <summary>The longest run of [A-Za-z0-9._/:-] around a match, at most 200 characters.</summary>
    private static string Token(byte[] buffer, int available, int at, int needleLength)
    {
        int start = at;
        while (start > 0 && IsTokenByte(buffer[start - 1]) && at - start < MaxToken / 2) start--;
        int end = Math.Min(available, at + needleLength);
        while (end < available && IsTokenByte(buffer[end]) && end - start < MaxToken) end++;
        if (end - start > MaxToken) end = start + MaxToken;
        return System.Text.Encoding.ASCII.GetString(buffer, start, end - start);
    }

    private static bool IsTokenByte(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b == (byte)'.' || b == (byte)'_' || b == (byte)'/' || b == (byte)':' || b == (byte)'-';

    /// <summary>Keeps a path's end, the telling part, within <paramref name="max"/> characters.</summary>
    private static string Tail(string path, int max) =>
        path.Length <= max ? path : "..." + path[^(max - 3)..];
}

/// <summary>
/// Reads the RTP headers of the display stream (RFC 3550) and, if asked,
/// writes every datagram to a capture file for offline decoding.
/// </summary>
/// <remarks>
/// A type of its own because spans are not allowed in Program's async
/// top-level body (CS8652), and header parsing wants them. Datagrams arrive
/// on the tunnel's receive thread, so every counter is behind one lock.
/// The capture format is the simplest thing a decoder can replay:
/// <c>[u32 LE length][u64 LE microseconds since arming][whole UDP datagram]</c>.
/// </remarks>
internal sealed class RtpTap : IDisposable
{
    private const int FirstBytesShown = 16;

    private sealed class PayloadTypeStat
    {
        public long Packets;
        public long Bytes;
        public long Markers;
        public int FirstPayloadSize = -1;
        public string FirstPayloadHex = "";
    }

    private sealed class SourceStat
    {
        public long Packets;
        public int LastSequence = -1;
        public long Lost;
        public long Duplicates;
        public long Reordered;
    }

    private readonly object _gate = new();
    private readonly Dictionary<int, PayloadTypeStat> _byPayloadType = new();
    private readonly Dictionary<uint, SourceStat> _bySource = new();
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly string? _capturePath;
    private FileStream? _capture;
    private bool _armed;
    private long _beforeArming, _packets, _bytes, _markers, _malformed, _badVersion, _captureBytes;

    // --- Timing ---------------------------------------------------------------
    // The RTP timestamp says when the phone sampled a picture; the marker bit
    // says which packet completes it. Comparing the gap between two timestamps
    // to the gap between the two arrivals separates a link that breathes —
    // jitter around zero — from one that buffers: a lag that grows across the
    // window is a queue filling up between the encoder and here.
    //
    // The rate of that timestamp clock is measured rather than assumed: the
    // stream is not the 90 kHz of a broadcast profile, and taking the nominal
    // figure on faith turns a plain scale error into an imaginary drift of
    // several seconds. One entry per access unit, so ten seconds of 60 Hz is
    // six hundred pairs — small enough to keep and reduce at the end.
    private const double NominalClockHz = 24_000.0;   // what H264Decoder now uses, measured here
    private const int MaxUnits = 200_000;
    private readonly List<(uint Stamp, double ArrivalMs)> _frames = new();

    public RtpTap(string? capturePath)
    {
        _capturePath = capturePath;
        if (capturePath is null) return;
        string? folder = Path.GetDirectoryName(Path.GetFullPath(capturePath));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        _capture = new FileStream(capturePath, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    /// <summary>
    /// Opens the measured window. The clock only starts at the first datagram,
    /// so arming before the climb — which is the only way to capture the key
    /// frame — does not put the setup time into the rates.
    /// </summary>
    public void Arm()
    {
        lock (_gate)
        {
            _armed = true;
            _clock.Reset();
        }
    }

    public void Add(ReadOnlyMemory<byte> datagram)
    {
        var span = datagram.Span;
        lock (_gate)
        {
            if (!_armed) { _beforeArming++; return; }
            if (!_clock.IsRunning) _clock.Start();
            long microseconds = _clock.ElapsedTicks * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
            _packets++;
            _bytes += span.Length;
            Write(span, microseconds);

            if (span.Length < 12) { _malformed++; return; }
            int version = span[0] >> 6;
            bool padding = (span[0] & 0x20) != 0;
            bool extension = (span[0] & 0x10) != 0;
            int csrcCount = span[0] & 0x0F;
            // RTCP is multiplexed on the same port and has no marker bit: its
            // packet type owns the whole second byte, so masking would file a
            // sender report (200) under a media payload type (72).
            bool control = span[1] is >= 200 and <= 206;
            bool marker = !control && (span[1] & 0x80) != 0;
            int payloadType = control ? span[1] : span[1] & 0x7F;
            int sequence = (span[2] << 8) | span[3];
            uint ssrc = (uint)((span[8] << 24) | (span[9] << 16) | (span[10] << 8) | span[11]);
            if (version != 2) _badVersion++;
            if (marker)
            {
                _markers++;
                if (_frames.Count < MaxUnits)
                    _frames.Add(((uint)((span[4] << 24) | (span[5] << 16) | (span[6] << 8) | span[7]),
                        microseconds / 1000.0));
            }

            // Payload start: fixed header, the CSRC list, then the extension header.
            int start = 12 + 4 * csrcCount;
            if (extension && span.Length >= start + 4)
                start += 4 + 4 * ((span[start + 2] << 8) | span[start + 3]);
            int end = span.Length;
            if (padding && end > start && span[end - 1] > 0 && end - span[end - 1] >= start)
                end -= span[end - 1];
            if (start > end) { _malformed++; start = end; }

            if (!_byPayloadType.TryGetValue(payloadType, out var pt))
                _byPayloadType[payloadType] = pt = new PayloadTypeStat();
            pt.Packets++;
            pt.Bytes += span.Length;
            if (marker) pt.Markers++;
            if (pt.FirstPayloadSize < 0)
            {
                pt.FirstPayloadSize = end - start;
                pt.FirstPayloadHex = Convert.ToHexString(span.Slice(start, Math.Min(FirstBytesShown, end - start)));
            }

            if (control)
                return;                                 // no sequence numbers to follow in a control packet
            if (!_bySource.TryGetValue(ssrc, out var source))
                _bySource[ssrc] = source = new SourceStat();
            source.Packets++;
            if (source.LastSequence >= 0)
            {
                int step = (sequence - source.LastSequence) & 0xFFFF;
                if (step == 0) source.Duplicates++;
                else if (step >= 0x8000) source.Reordered++;
                else source.Lost += step - 1;
            }
            if (source.LastSequence < 0 || ((sequence - source.LastSequence) & 0xFFFF) < 0x8000)
                source.LastSequence = sequence;
        }
    }

    /// <summary>
    /// Turns the access units into a timing verdict: the timestamp clock the
    /// phone actually used, the jitter between consecutive pictures, and how far
    /// the delivery wandered from that steady pace over the window.
    /// </summary>
    /// <remarks>
    /// Gaps are taken between consecutive units, unsigned, so the 32-bit wrap
    /// costs nothing; the clock is then the total of those gaps over the elapsed
    /// arrival time. The lag is the running difference between the two elapsed
    /// times, which starts at zero by construction: only its excursions mean
    /// anything, and its amplitude is how long a picture waited beyond the pace
    /// the phone set — buffering, wherever it happens.
    /// </remarks>
    private void ReportTiming(Action<string> say)
    {
        if (_frames.Count < 2)
        {
            say("  pas assez d'images completes pour une mesure de gigue.");
            return;
        }

        double span = _frames[^1].ArrivalMs - _frames[0].ArrivalMs;
        long units = 0;
        for (int i = 1; i < _frames.Count; i++)
            units += (uint)(_frames[i].Stamp - _frames[i - 1].Stamp);
        if (span <= 0 || units == 0)
        {
            say("  horodatage RTP immobile : pas de mesure de gigue.");
            return;
        }

        double clock = units * 1000.0 / span;
        int gaps = _frames.Count - 1;
        double jitterSum = 0, jitterMax = 0, lag = 0, lagMin = 0, lagMax = 0, elapsedStamp = 0;
        for (int i = 1; i < _frames.Count; i++)
        {
            double stampGap = (uint)(_frames[i].Stamp - _frames[i - 1].Stamp) * 1000.0 / clock;
            double arrivalGap = _frames[i].ArrivalMs - _frames[i - 1].ArrivalMs;
            double jitter = Math.Abs(arrivalGap - stampGap);
            jitterSum += jitter;
            if (jitter > jitterMax) jitterMax = jitter;

            elapsedStamp += stampGap;
            lag = _frames[i].ArrivalMs - _frames[0].ArrivalMs - elapsedStamp;
            if (lag < lagMin) lagMin = lag;
            if (lag > lagMax) lagMax = lag;
        }

        say($"  images completes : {_frames.Count} en {span / 1000.0:F2} s ({gaps * 1000.0 / span:F1} images/s),"
            + $" intervalle moyen {span / gaps:F2} ms");
        say($"  horloge RTP mesuree : {clock:N0} Hz"
            + (Math.Abs(clock - NominalClockHz) > NominalClockHz * 0.02
                ? $" — le decodeur en suppose {NominalClockHz:N0} : constante a revoir."
                : " (conforme a l'hypothese du decodeur)."));
        say($"  gigue (arrivee - horodatage, d'une image a la suivante) :"
            + $" moyenne {jitterSum / gaps:F2} ms, max {jitterMax:F2} ms");
        say($"  retard cumule (origine a la 1re image) : de {lagMin:F2} a {lagMax:F2} ms,"
            + $" amplitude {lagMax - lagMin:F2} ms, fin {lag:F2} ms.");
        say("  (amplitude = ce qu'une image a attendu au-dela du rythme du telephone ; une derive"
            + " qui ne redescend pas = mise en tampon.)");
    }

    public void Report(Action<string> say)
    {
        lock (_gate)
        {
            double elapsed = _clock.Elapsed.TotalSeconds;
            if (elapsed <= 0) elapsed = 1;
            _capture?.Flush();
            say($"=== RTP : {_packets} paquet(s), {_bytes:N0} octets en {elapsed:F2} s ===");
            say($"  debit : {_packets / elapsed:F1} paquets/s, {_bytes * 8 / elapsed / 1_000_000:F2} Mbit/s"
                + $" (paquet moyen {(_packets == 0 ? 0 : _bytes / _packets)} octets)");
            say($"  marqueurs (fin d'image) : {_markers} -> {_markers / elapsed:F1} images/s");
            if (_beforeArming > 0) say($"  {_beforeArming} paquet(s) recu(s) avant la fenetre de mesure, non comptes.");
            ReportTiming(say);
            if (_malformed > 0) say($"  {_malformed} datagramme(s) trop court(s) ou mal formes.");
            if (_badVersion > 0) say($"  {_badVersion} paquet(s) hors version RTP 2.");

            say(_byPayloadType.Count == 0 ? "  aucun payload type vu." : "  payload types vus :");
            foreach (var (type, stat) in _byPayloadType.OrderByDescending(x => x.Value.Packets))
            {
                string codec = type switch
                {
                    123 => "AVC", 100 => "HEVC", 101 => "AAC-ELD", >= 200 and <= 206 => "RTCP", _ => "?",
                };
                say($"    PT {type,3} ({codec,-6}) : {stat.Packets} paquet(s), {stat.Bytes:N0} octets, {stat.Markers} marqueur(s)");
                say($"      premiere charge utile : {stat.FirstPayloadSize} octets, {FirstBytesShown} premiers = {stat.FirstPayloadHex}");
            }

            say(_bySource.Count == 0 ? "  aucun SSRC vu." : "  SSRC vus :");
            foreach (var (ssrc, stat) in _bySource.OrderByDescending(x => x.Value.Packets))
                say($"    SSRC 0x{ssrc:X8} : {stat.Packets} paquet(s), pertes {stat.Lost}, doublons {stat.Duplicates},"
                    + $" desordres {stat.Reordered}, derniere sequence {stat.LastSequence}");

            if (_capturePath is not null)
            {
                _capture?.Dispose();
                _capture = null;
                say($"  capture : {_capturePath} — {_packets} enregistrement(s), {_captureBytes:N0} octets de datagrammes,"
                    + $" fichier {new FileInfo(_capturePath).Length:N0} octets.");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }

    /// <summary>One capture record: length, arrival time, then the datagram itself.</summary>
    private void Write(ReadOnlySpan<byte> datagram, long microseconds)
    {
        if (_capture is null) return;
        Span<byte> header = stackalloc byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)datagram.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(header[4..], (ulong)microseconds);
        _capture.Write(header);
        _capture.Write(datagram);
        _captureBytes += datagram.Length;
    }
}
