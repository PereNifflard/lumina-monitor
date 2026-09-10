namespace LuminaMonitor.Core.Hid;

/// <summary>
/// What the input path counted, in one snapshot.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="Media.MediaStats"/> for the other direction.
/// A mouse produces five hundred to a thousand positions a second and the phone
/// can show sixty; the difference has to be thrown away somewhere, and these
/// figures say where. <see cref="MovesDropped"/> rising while
/// <see cref="QueueDepth"/> and <see cref="SendWaiters"/> stay at zero is the
/// healthy shape: positions are superseded before they are ever sent, so
/// nothing queues and the finger is always at the latest place the hand went.
/// A rising queue or waiter count is the opposite — the backlog that makes the
/// lag grow with the speed of the hand.
///
/// <para>The one figure that settles an argument about lag is
/// <see cref="SendMaxMs"/>: how long the worst report waited between being
/// handed to the pump and reaching the wire. Everything else says where the
/// time went; that says whether there was any.</para>
/// </remarks>
/// <param name="ReportsSent">HID reports handed to the channel since the session came up.</param>
/// <param name="MovesDropped">Positions superseded before they were sent, or evicted from a full queue.</param>
/// <param name="WindowDrops">Reports the HTTP/2 window was too small to carry.</param>
/// <param name="QueueDepth">Reports waiting for the pump, pending position included.</param>
/// <param name="SendWaiters">Callers parked on the channel's write lock right now.</param>
/// <param name="UnackedSegments">TCP segments sent and not acknowledged.</param>
/// <param name="UnackedBytes">Bytes in those segments.</param>
/// <param name="QueueOverflows">Times the queue was full of reports none of which could be dropped.</param>
/// <param name="SendTimeouts">Sends abandoned after <c>SendPatience</c>.</param>
/// <param name="SendMeanMs">Mean milliseconds from <c>Queue…</c> to the report reaching the wire.</param>
/// <param name="SendMaxMs">The worst of those, since the peaks were last reset.</param>
/// <param name="LastFault">What the last failed report failed with, or null.</param>
public readonly record struct InputStats(
    long ReportsSent,
    long MovesDropped,
    long WindowDrops,
    int QueueDepth,
    int SendWaiters,
    int UnackedSegments,
    long UnackedBytes,
    long QueueOverflows,
    long SendTimeouts,
    double SendMeanMs,
    double SendMaxMs,
    string? LastFault);
