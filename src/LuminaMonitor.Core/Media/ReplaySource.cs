using System.Buffers.Binary;
using System.Diagnostics;

namespace LuminaMonitor.Core.Media;

/// <summary>
/// A recorded stream played back into the live video path, without a phone.
/// </summary>
/// <remarks>
/// The window's picture pipeline — depacketizer, decode thread, staged frame —
/// is the part of the mirror whose cost has to be measured against a saturated
/// user interface, and it is also the part that cannot be exercised at two in
/// the morning with a locked phone on the desk. So the capture the probe writes
/// with <c>stream-info</c> is fed back in at the same door the tunnel's
/// datagrams use: <see cref="OnDatagram"/> is a copy of the media session's own
/// receive path, minus the RTCP it has nobody to answer.
///
/// <para>The capture is read into memory once and replayed in a loop at the
/// pace of its own timestamps. Each turn of the loop starts again from the
/// first record, which is where the parameter sets and the only IDR of the
/// recording are, so the decoder is flushed at the boundary and handed a key
/// frame straight away rather than a P picture whose reference it threw
/// away.</para>
/// </remarks>
public sealed class ReplaySource : IDisposable
{
    /// <summary>How many undecoded pictures may wait, as in the live session.</summary>
    private const int QueueDepth = 16;

    private readonly List<(byte[] Datagram, long Microseconds)> _records = [];
    private readonly H264Depacketizer _depacketizer = new();
    private readonly Queue<(AccessUnit Unit, bool Restart)> _queue = new();
    private readonly object _queueGate = new();
    private readonly CancellationTokenSource _stopping = new();

    private Thread? _feed;
    private Thread? _decoder;
    private int _restartWanted;
    private long _packets, _bytes, _framesDecoded, _lateDrops, _loops;
    private long _pendingUnits, _maxPendingUnits;
    private double _lastLatencyMs, _wireMs, _queueMs, _decodeMs, _maxQueueMs;

    /// <summary>Reads the whole capture; the file is not touched again afterwards.</summary>
    public ReplaySource(string path)
    {
        Path = path;
        foreach (var record in ReadCapture(path))
            _records.Add(record);
    }

    public string Path { get; }

    /// <summary>How many datagrams the capture holds, RTCP included.</summary>
    public int Records => _records.Count;

    /// <summary>How long one turn of the loop lasts, from the capture's own clock.</summary>
    public double SpanSeconds => _records.Count < 2
        ? 0
        : (_records[^1].Microseconds - _records[0].Microseconds) / 1_000_000.0;

    /// <summary>How many times the capture has been played from the top.</summary>
    public long Loops => Interlocked.Read(ref _loops);

    /// <summary>Every decoded picture; the pixels belong to the decoder until the handler returns.</summary>
    public event Action<VideoFrame>? FrameDecoded;

    /// <summary>Where the replay reports a fault; nothing else is thrown at the caller.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The same snapshot the live media session publishes, for the same journal line.</summary>
    public MediaStats Stats => new(
        Interlocked.Read(ref _packets), Interlocked.Read(ref _bytes), 0,
        _depacketizer.AccessUnits, _depacketizer.KeyFrames, _depacketizer.Lost, _depacketizer.Dropped,
        _depacketizer.Trailers, Interlocked.Read(ref _framesDecoded), Interlocked.Read(ref _lateDrops),
        _lastLatencyMs, 0, 0, 0,
        Interlocked.Read(ref _pendingUnits), Interlocked.Read(ref _maxPendingUnits),
        _wireMs, _queueMs, _decodeMs, _maxQueueMs,
        // A capture is played from a file at its own recorded pace: the drift
        // of a stream that no longer exists would be a measurement of this
        // loop's timer, not of anything on a phone.
        0, 0, 0, 0,
        _h264?.InFlight ?? 0, _h264?.LowLatencySet ?? false);

    /// <summary>The live decoder, for the pictures-held counter only.</summary>
    private volatile H264Decoder? _h264;

    /// <summary>Forgets the high-water marks, so the next window measures itself.</summary>
    public void ResetPeaks()
    {
        Interlocked.Exchange(ref _maxPendingUnits, Interlocked.Read(ref _pendingUnits));
        _maxQueueMs = 0;
    }

    /// <summary>Starts the feed and the decoder, each on a thread of its own.</summary>
    public void Start()
    {
        if (_records.Count == 0 || _feed is not null)
            return;

        _depacketizer.Completed = Enqueue;
        _decoder = new Thread(DecodeLoop) { IsBackground = true, Name = "LuminaReplayDecode" };
        _decoder.SetApartmentState(ApartmentState.MTA);
        _decoder.Start();

        _feed = new Thread(FeedLoop) { IsBackground = true, Name = "LuminaReplayFeed" };
        _feed.Start();
    }

    public void Dispose()
    {
        if (_stopping.IsCancellationRequested)
            return;
        _stopping.Cancel();
        lock (_queueGate) Monitor.PulseAll(_queueGate);
        _feed?.Join(TimeSpan.FromSeconds(2));
        _decoder?.Join(TimeSpan.FromSeconds(2));
        _feed = null;
        _decoder = null;
        _stopping.Dispose();
    }

    // --- The feed ----------------------------------------------------------------

    /// <summary>
    /// Replays the records at the pace of their own timestamps, for ever.
    /// </summary>
    /// <remarks>
    /// The wait is a sleep down to a millisecond of the due time and a spin for
    /// the rest: the platform timer is held at one millisecond for the duration,
    /// exactly as the input pump holds it, because a sixty-hertz stream whose
    /// packets arrive in fifteen-millisecond lumps would measure the lumps
    /// rather than the pipeline.
    /// </remarks>
    private void FeedLoop()
    {
        using var fineTimer = Hid.TimerResolution.Hold();
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                long origin = Stopwatch.GetTimestamp();
                long first = _records[0].Microseconds;
                Interlocked.Exchange(ref _restartWanted, 1);
                _depacketizer.Rewind();

                foreach (var (datagram, microseconds) in _records)
                {
                    if (_stopping.IsCancellationRequested)
                        return;
                    WaitUntil(origin + (microseconds - first) * Stopwatch.Frequency / 1_000_000);
                    OnDatagram(datagram);
                }
                Interlocked.Increment(ref _loops);
            }
        }
        catch (Exception exception)
        {
            Log?.Invoke($"rejeu arrete : {exception.Message}");
        }
    }

    private void WaitUntil(long dueTicks)
    {
        while (true)
        {
            long left = dueTicks - Stopwatch.GetTimestamp();
            if (left <= 0)
                return;
            double milliseconds = left * 1000.0 / Stopwatch.Frequency;
            if (milliseconds > 2)
                _stopping.Token.WaitHandle.WaitOne((int)(milliseconds - 1));
            else
                Thread.SpinWait(200);
            if (_stopping.IsCancellationRequested)
                return;
        }
    }

    /// <summary>The media session's receive path, word for word, minus the RTCP answer.</summary>
    private void OnDatagram(ReadOnlySpan<byte> datagram)
    {
        Interlocked.Increment(ref _packets);
        Interlocked.Add(ref _bytes, datagram.Length);
        if (RtpPacket.LooksLikeRtcp(datagram))
            return;
        var packet = RtpPacket.Parse(datagram);
        if (!packet.Valid)
            return;
        _depacketizer.Add(packet, Stopwatch.GetTimestamp());
    }

    // --- The decoder -------------------------------------------------------------

    private void Enqueue(AccessUnit unit)
    {
        bool restart = Interlocked.Exchange(ref _restartWanted, 0) == 1;
        lock (_queueGate)
        {
            if (_queue.Count >= QueueDepth)
            {
                Interlocked.Add(ref _lateDrops, _queue.Count);
                _queue.Clear();
            }
            _queue.Enqueue((unit, restart));
            long depth = _queue.Count;
            Interlocked.Exchange(ref _pendingUnits, depth);
            if (depth > Interlocked.Read(ref _maxPendingUnits))
                Interlocked.Exchange(ref _maxPendingUnits, depth);
            Monitor.Pulse(_queueGate);
        }
    }

    private void DecodeLoop()
    {
        H264Decoder? decoder = null;
        try
        {
            decoder = new H264Decoder();
            _h264 = decoder;
            while (true)
            {
                (AccessUnit Unit, bool Restart) item;
                lock (_queueGate)
                {
                    while (_queue.Count == 0)
                    {
                        if (_stopping.IsCancellationRequested)
                            return;
                        Monitor.Wait(_queueGate, 200);
                    }
                    item = _queue.Dequeue();
                    Interlocked.Exchange(ref _pendingUnits, _queue.Count);
                }

                // A new turn of the loop rewinds the RTP clock, and the decoder
                // reads its presentation times from that clock: flushed here, it
                // takes the capture's opening IDR as a fresh start.
                if (item.Restart)
                    decoder.Flush();
                decoder.Decode(item.Unit, OnFrame);
            }
        }
        catch (Exception exception)
        {
            Log?.Invoke($"decodeur de rejeu arrete : {exception.Message}");
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    private void OnFrame(VideoFrame frame)
    {
        Interlocked.Increment(ref _framesDecoded);
        _lastLatencyMs = frame.LatencyMs;
        _wireMs = frame.WireMs;
        _queueMs = frame.QueueMs;
        _decodeMs = frame.DecodeMs;
        if (frame.QueueMs > _maxQueueMs)
            _maxQueueMs = frame.QueueMs;
        FrameDecoded?.Invoke(frame);
    }

    // --- The capture file --------------------------------------------------------

    /// <summary>The probe's capture format: [u32 size][u64 microseconds][datagram].</summary>
    private static IEnumerable<(byte[] Datagram, long Microseconds)> ReadCapture(string path)
    {
        using var file = File.OpenRead(path);
        byte[] header = new byte[12];
        while (true)
        {
            if (!Fill(file, header, 12)) yield break;
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header);
            long microseconds = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(4));
            if (size is <= 0 or > 65535) yield break;
            byte[] datagram = new byte[size];
            if (!Fill(file, datagram, size)) yield break;
            yield return (datagram, microseconds);
        }
    }

    private static bool Fill(Stream stream, byte[] buffer, int count)
    {
        int at = 0;
        while (at < count)
        {
            int read = stream.Read(buffer, at, count - at);
            if (read <= 0) return false;
            at += read;
        }
        return true;
    }
}
