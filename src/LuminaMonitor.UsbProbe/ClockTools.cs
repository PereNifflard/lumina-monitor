using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using LuminaMonitor.Core;
using LuminaMonitor.Core.Ddi;
using LuminaMonitor.Core.Media;

/// <summary>
/// The absolute delay of the mirror, read off the phone's own clock.
/// </summary>
/// <remarks>
/// Every other figure this project produces is a difference: the drift says
/// how the delay <i>changes</i>, the decode timings say what happens after the
/// packets arrive, and none of them can see a picture the phone held before it
/// sent it. The only instrument that can is the phone's screen showing a clock
/// that is right — time.is, over NTP — because then one decoded picture carries
/// both readings at once: the time the phone believes it is, painted in the
/// pixels, and the time our own clock says the last packet of that picture
/// arrived.
///
/// <para>Reading a digit is left to a human, but finding the picture worth
/// reading is not. A second changes on the phone at an exact instant; the first
/// decoded picture that shows the new value is the one whose arrival time
/// matters, and it announces itself as a burst of luminance change confined to
/// the band the digits occupy. So the tool watches that band, notes every
/// transition, and writes only those pictures out. The interval between
/// consecutive transitions has to come out at a thousand milliseconds — that is
/// the check that the detector is looking at a clock and not at an animation.</para>
///
/// <para><b>The arithmetic.</b> When the display turns over to <c>X</c>, the
/// true time on the phone is <c>X</c>,000. So the delay is the PC time of the
/// transition minus <c>X</c>,000 — and <c>X</c> is read on the picture the tool
/// just wrote, not computed here.</para>
/// </remarks>
internal static class ClockTools
{
    /// <summary>How long the band and the threshold are calibrated for, in seconds.</summary>
    private const double CalibrationSeconds = 2.5;

    /// <summary>How many horizontal strips the picture is cut into while looking for the digits.</summary>
    private const int Strips = 48;

    /// <summary>The columns the scan looks at: the middle of the picture, margins dropped.</summary>
    private const double ScanLeft = 0.10, ScanRight = 0.90;

    /// <summary>One pixel in four, both ways: the digits are hundreds of pixels tall.</summary>
    private const int Decimation = 4;

    /// <summary>A strip joins the band while it still changes this much of the peak.</summary>
    private const double BandFloor = 0.25;

    /// <summary>Where between the quiet level and the loudest calibration frame the trigger sits.</summary>
    private const double TriggerFraction = 0.35;

    /// <summary>Two transitions closer than this are the same one, seen twice.</summary>
    private const double DebounceMs = 400;

    /// <summary>What the band is padded by, top and bottom, in the written PNG.</summary>
    private const double PngPadding = 0.05;

    /// <summary>One second turning over on the phone, and the picture that showed it.</summary>
    private sealed record Tick(int Index, VideoFrame Frame, DateTime Read, double BandDiff);

    /// <summary>
    /// clock-test [secondes] [--variant=…] [--out=&lt;dossier&gt;] — the absolute
    /// delay, one transition at a time.
    /// </summary>
    public static async Task<int> RunAsync(int seconds, string variant, string outFolder, string ddiFolder,
        Action<string> say)
    {
        if (!VideoTools.ParseVariant(variant, out var tuning, out string label))
        {
            say($"usage : clock-test [secondes] [--variant=<variante>] [--out=<dossier>]{Environment.NewLine}{VideoTools.VariantUsage}");
            return 2;
        }

        Directory.CreateDirectory(outFolder);
        say($"Variante « {label} » : {tuning}");
        say($"Sur le telephone : Safari sur time.is, grande horloge visible, verrouillage automatique desactive.");

        await using var session = new DeviceSession(new DdiSource(ddiFolder), new ConsoleLog())
        {
            VideoCodecs = VideoCodecs.AvcOnly,
            DecodeVideo = true,
            VideoTuning = tuning,
        };
        session.UnlockRequired += message => say(message);

        var detector = new TickDetector();
        long frames = 0;
        session.FrameDecoded += frame =>
        {
            Interlocked.Increment(ref frames);
            detector.Add(frame);
        };

        await session.ConnectAsync();
        var media = session.Media;
        if (media is null || !media.Streaming)
        {
            say($"*** FLUX REFUSE ({label}) *** {media?.Failure ?? "(aucun motif rapporte)"}");
            return 5;
        }

        // The answer's own words about what it is going to send us: the jitter
        // buffer above all, since a jitter buffer on the phone's side is one of
        // the two places a whole second could be hiding.
        foreach (string key in new[]
        {
            "JitterBufferMode", "VideoStreamMode", "TXMinBitrate", "TXMaxBitrate",
            "KeyFrameInterval", "RateAdaptationEnabled", "RTCPTimeoutInterval", "RxPayloadType",
        })
            if (media.ConfigText(key) is { } value)
                say($"  streamConfig {key} = {value}");

        var clock = Stopwatch.StartNew();
        var previous = media.Stats;
        long previousFrames = 0;
        double previousElapsed = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(4, seconds - clock.Elapsed.TotalSeconds + 0.01)));
            var now = media.Stats;
            long nowFrames = Interlocked.Read(ref frames);
            double elapsed = clock.Elapsed.TotalSeconds;
            double window = Math.Max(0.001, elapsed - previousElapsed);
            say($"t={elapsed,5:F1} s : {(nowFrames - previousFrames) / window,5:F1} images/s,"
                + $" {(now.Bytes - previous.Bytes) * 8 / 1000.0 / window,7:F0} kbit/s,"
                + $" derive {now.DriftLastMs,6:F0} ms, pertes {now.PacketsLost},"
                + $" horodatage->arrivee {media.PipelineMs,6:F0} ms,"
                + $" transitions {detector.Count}");
            previous = now;
            previousFrames = nowFrames;
            previousElapsed = elapsed;
        }

        var final = media.Stats;
        var (receipts, reports) = media.RctlCounts;
        say($"*** HORLOGE {clock.Elapsed.TotalSeconds:F1} s ({label}) *** {final.FramesDecoded} images decodees"
            + $" -> {final.FramesDecoded / clock.Elapsed.TotalSeconds:F1} images/s ;"
            + $" derive finale {final.DriftLastMs:F0} ms ; horloge RTP {final.RtpClockKhz:F2} kHz"
            + (tuning.Rctl ? $" ; RCTL {receipts} recus / {reports} rapports." : "."));
        // The sender report's own arithmetic, printed beside the pixels: the
        // phone's NTP clock against ours says how much of the delay is after
        // the picture was timestamped, and the pixels say how much there is in
        // total. The difference is what happens before the timestamp.
        say($"  horloge du telephone (dernier rapport d'emission) : {media.SenderClock?.ToLocalTime():HH:mm:ss.fff} ;"
            + $" horodatage -> arrivee {media.PipelineMs:F0} ms.");

        var (ticks, bandFrom, bandTo, trigger, quiet, loud, preview) = detector.Result;
        if (preview is not null)
        {
            string previewName = Path.Combine(outFolder, "apercu.png");
            WritePng(previewName, preview, 0, 0, preview.Width, preview.Height, 3);
            say($"  apercu de l'ecran (1 pixel sur 3) : {Path.GetFullPath(previewName)}");
        }
        say($"  bande de l'horloge : y de {bandFrom * 100.0 / Strips:F0} % a {bandTo * 100.0 / Strips:F0} % de la hauteur ;"
            + $" seuil {trigger:F2} (repos {quiet:F2}, plus fort calibrage {loud:F2}).");

        if (ticks.Count == 0)
        {
            say("  *** AUCUNE TRANSITION *** — la bande n'a rien vu changer : l'horloge est-elle a l'ecran ?");
            return 4;
        }

        say($"  {ticks.Count} transition(s) de seconde, dans {Path.GetFullPath(outFolder)} :");
        DateTime? last = null;
        foreach (var tick in ticks)
        {
            string stem = $"tick_{tick.Index:D2}_{tick.Read:HH-mm-ss.fff}";
            VideoTools.WriteBmp(Path.Combine(outFolder, stem + ".bmp"), tick.Frame);
            int padding = (int)(tick.Frame.Height * PngPadding);
            int top = Math.Max(0, bandFrom * tick.Frame.Height / Strips - padding);
            int bottom = Math.Min(tick.Frame.Height, (bandTo + 1) * tick.Frame.Height / Strips + padding);
            WritePng(Path.Combine(outFolder, stem + ".png"), tick.Frame, 0, top, tick.Frame.Width, bottom, 2);
            string gap = last is { } before
                ? $", {(tick.Read - before).TotalMilliseconds,6:F0} ms depuis la precedente"
                : "";
            say($"    transition {tick.Index:D2} : PC {tick.Read:HH:mm:ss.fff}{gap}"
                + $" — lire l'heure X sur {stem}.png, latence = {tick.Read:HH:mm:ss.fff} - X,000");
            last = tick.Read;
        }

        // The control: a detector that is looking at a clock produces one
        // transition a second and nothing else. Anything far from a thousand
        // milliseconds means it latched onto something that is not the digits,
        // and every latency below it is worthless.
        var gaps = new List<double>();
        for (int index = 1; index < ticks.Count; index++)
            gaps.Add((ticks[index].Read - ticks[index - 1].Read).TotalMilliseconds);
        if (gaps.Count > 0)
        {
            double mean = gaps.Average(), worst = gaps.Max(g => Math.Abs(g - 1000));
            say($"  intervalles : moyenne {mean:F0} ms, ecart maximal a 1000 ms : {worst:F0} ms"
                + (worst <= 120 ? " — le detecteur suit bien la seconde." : " — *** DETECTION SUSPECTE ***"));
        }
        return 0;
    }

    // --- Detection ---------------------------------------------------------------

    /// <summary>
    /// Finds the band the digits live in, then every moment it changes.
    /// </summary>
    /// <remarks>
    /// Nothing about the page is assumed. For the first seconds every strip of
    /// the picture is measured against the previous picture and the changes are
    /// added up; the digits of a clock are the only thing on a still page that
    /// changes once a second, so the strips that accumulate the most are the
    /// digits. The trigger is then set between the quiet level of those same
    /// calibration frames and their loudest — the loudest being, by
    /// construction, a transition.
    ///
    /// <para>Pictures are decimated four to one on both axes before anything is
    /// compared: sixty pictures a second of 1328x2896 is more than this
    /// measurement needs, and an instrument that costs the machine real time is
    /// an instrument that changes what it measures.</para>
    /// </remarks>
    private sealed class TickDetector
    {
        private readonly object _gate = new();
        private readonly List<double[]> _calibration = [];
        private readonly List<Tick> _ticks = [];

        private byte[]? _previous;
        private int _small, _tall;
        private long _firstTicks;
        private bool _calibrated;
        private int _bandFrom, _bandTo;
        private double _trigger, _quiet, _loud;
        private long _lastTickTicks;
        private VideoFrame? _preview;
        private long _anchorTicks;
        private DateTime _anchorTime;

        public TickDetector()
        {
            _anchorTicks = Stopwatch.GetTimestamp();
            _anchorTime = DateTime.Now;
        }

        public int Count { get { lock (_gate) return _ticks.Count; } }

        public (List<Tick> Ticks, int BandFrom, int BandTo, double Trigger, double Quiet, double Loud, VideoFrame? Preview) Result
        {
            get { lock (_gate) return ([.. _ticks], _bandFrom, _bandTo, _trigger, _quiet, _loud, _preview); }
        }

        public void Add(VideoFrame frame)
        {
            int width = frame.Width / Decimation, height = frame.Height / Decimation;
            if (width <= 0 || height < Strips)
                return;
            byte[] small = new byte[width * height];
            byte[] luma = frame.Nv12;
            for (int y = 0; y < height; y++)
            {
                int source = y * Decimation * frame.Stride;
                int target = y * width;
                for (int x = 0; x < width; x++)
                    small[target + x] = luma[source + x * Decimation];
            }

            lock (_gate)
            {
                if (_previous is null || _small != width || _tall != height)
                {
                    // The first picture, or a picture the phone resized under
                    // us: there is nothing to compare it against, and the
                    // calibration has to start again from here.
                    _previous = small;
                    _small = width;
                    _tall = height;
                    _firstTicks = frame.ArrivalTicks;
                    _calibration.Clear();
                    _calibrated = false;
                    return;
                }

                double[] strips = StripDiffs(small, _previous, width, height);
                _previous = small;

                if (!_calibrated)
                {
                    _calibration.Add(strips);
                    double elapsed = (frame.ArrivalTicks - _firstTicks) * 1000.0 / Stopwatch.Frequency;
                    if (elapsed < CalibrationSeconds * 1000 || _calibration.Count < 30)
                        return;
                    Calibrate();
                    _preview = frame.Copy();
                    return;
                }

                double band = Band(strips);
                if (band < _trigger)
                    return;
                double since = (frame.ArrivalTicks - _lastTickTicks) * 1000.0 / Stopwatch.Frequency;
                if (_lastTickTicks != 0 && since < DebounceMs)
                    return;
                _lastTickTicks = frame.ArrivalTicks;
                _ticks.Add(new Tick(_ticks.Count + 1, frame.Copy(),
                    _anchorTime.AddTicks((frame.ArrivalTicks - _anchorTicks) * TimeSpan.TicksPerSecond / Stopwatch.Frequency),
                    band));
            }
        }

        private static double[] StripDiffs(byte[] now, byte[] before, int width, int height)
        {
            int from = (int)(width * ScanLeft), to = (int)(width * ScanRight);
            double[] strips = new double[Strips];
            for (int strip = 0; strip < Strips; strip++)
            {
                int top = strip * height / Strips, bottom = (strip + 1) * height / Strips;
                long sum = 0;
                int count = 0;
                for (int y = top; y < bottom; y++)
                {
                    int row = y * width;
                    for (int x = from; x < to; x++)
                    {
                        sum += Math.Abs(now[row + x] - before[row + x]);
                        count++;
                    }
                }
                strips[strip] = count > 0 ? (double)sum / count : 0;
            }
            return strips;
        }

        private void Calibrate()
        {
            double[] total = new double[Strips];
            foreach (double[] strips in _calibration)
                for (int strip = 0; strip < Strips; strip++)
                    total[strip] += strips[strip];

            int peak = 0;
            for (int strip = 1; strip < Strips; strip++)
                if (total[strip] > total[peak]) peak = strip;
            _bandFrom = _bandTo = peak;
            while (_bandFrom > 0 && total[_bandFrom - 1] >= BandFloor * total[peak]) _bandFrom--;
            while (_bandTo < Strips - 1 && total[_bandTo + 1] >= BandFloor * total[peak]) _bandTo++;

            double[] band = [.. _calibration.Select(Band).OrderBy(value => value)];
            _quiet = band[band.Length / 2];
            _loud = band[^1];
            _trigger = _quiet + TriggerFraction * (_loud - _quiet);
            _calibrated = true;
            _calibration.Clear();
        }

        private double Band(double[] strips)
        {
            double sum = 0;
            for (int strip = _bandFrom; strip <= _bandTo; strip++)
                sum += strips[strip];
            return sum / (_bandTo - _bandFrom + 1);
        }
    }

    // --- PNG ---------------------------------------------------------------------

    /// <summary>
    /// Writes a crop of a decoded picture as a PNG, decimated by <paramref name="step"/>.
    /// </summary>
    /// <remarks>
    /// A PNG by hand rather than a dependency, and it is barely more than a BMP:
    /// a signature, an IHDR, the rows each prefixed with a filter byte and run
    /// through zlib — which the framework already writes, header and Adler
    /// checksum included — and an IEND. The only thing missing from the box is
    /// the CRC of each chunk, thirty lines below. The BMPs beside these files
    /// are the measurement; the PNGs exist because a BMP of eleven megabytes is
    /// not something anyone can look at from here.
    /// </remarks>
    public static void WritePng(string path, VideoFrame frame, int x0, int y0, int x1, int y1, int step)
    {
        x0 = Math.Clamp(x0, 0, frame.Width);
        x1 = Math.Clamp(x1, x0 + 1, frame.Width);
        y0 = Math.Clamp(y0, 0, frame.Height);
        y1 = Math.Clamp(y1, y0 + 1, frame.Height);
        step = Math.Max(1, step);
        int width = (x1 - x0 + step - 1) / step, height = (y1 - y0 + step - 1) / step;
        byte[] bgra = frame.Bgra;

        var raw = new MemoryStream(height * (1 + width * 3));
        byte[] row = new byte[1 + width * 3];
        for (int y = 0; y < height; y++)
        {
            int source = (y0 + y * step) * frame.Width * 4;
            int at = 1;                                     // row[0] stays 0: filter "None"
            for (int x = 0; x < width; x++)
            {
                int pixel = source + (x0 + x * step) * 4;
                row[at++] = bgra[pixel + 2];
                row[at++] = bgra[pixel + 1];
                row[at++] = bgra[pixel];
            }
            raw.Write(row);
        }

        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw.GetBuffer(), 0, (int)raw.Length);

        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;                                      // eight bits per sample
        header[9] = 2;                                      // truecolour, no alpha
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream file, string name, byte[] body)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        file.Write(length);
        byte[] tagged = new byte[4 + body.Length];
        for (int at = 0; at < 4; at++) tagged[at] = (byte)name[at];
        body.CopyTo(tagged, 4);
        file.Write(tagged);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(tagged));
        file.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint entry = 0; entry < 256; entry++)
        {
            uint value = entry;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[entry] = value;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
