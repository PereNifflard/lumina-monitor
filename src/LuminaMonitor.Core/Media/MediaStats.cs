namespace LuminaMonitor.Core.Media;

/// <summary>
/// Everything the video path counted, in one snapshot.
/// </summary>
/// <remarks>
/// Read atomically enough to be printed side by side: the counters are all
/// interlocked, so a snapshot can be one packet stale but never
/// self-contradictory. The three durations are the reason this record exists at
/// all — a picture's journey split into the part spent on the wire, the part
/// spent waiting for the decoder and the part spent inside it, so a growing
/// delay can be blamed on the right stage instead of guessed at.
///
/// <para>The drift is the fourth of them and the only one that measures what
/// happens <i>before</i> the first three: how far the picture's own timestamp
/// has fallen behind our clock since the stream's first picture. The other
/// three can all read as zero — nothing waiting, nothing slow — while the
/// mirror is two seconds late, because they only start counting once we hold
/// the packet. The drift starts counting when the phone took the picture.</para>
/// </remarks>
public readonly record struct MediaStats(
    long Packets,
    long Bytes,
    long RtcpPackets,
    long AccessUnits,
    long KeyFrames,
    long PacketsLost,
    long UnitsDropped,
    long Trailers,
    long FramesDecoded,
    long LateDrops,
    double LastLatencyMs,
    long ReportsSent,
    long SenderReports,
    long KeyFrameRequests,
    long PendingUnits,
    long MaxPendingUnits,
    double WireMs,
    double QueueMs,
    double DecodeMs,
    double MaxQueueMs,
    double DriftMeanMs,
    double DriftMaxMs,
    double DriftLastMs,
    double RtpClockKhz,
    long DecoderInFlight,
    bool DecoderLowLatency);
