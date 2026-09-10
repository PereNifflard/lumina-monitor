namespace LuminaMonitor.Core.Tunnel;

/// <summary>
/// One window of the tunnel's read pump, as it was actually spent.
/// </summary>
/// <remarks>
/// The counters upstream of everything else. A delay seen on screen is either
/// made below this line — in the cable, the multiplexer, the phone — or above
/// it, in what we do with each packet once we have it; nothing else measures
/// the join. <see cref="SocketAvailable"/> is the byte backlog Windows is
/// holding for us at the instant of the reading: a few kilobytes means the
/// pump keeps up and the delay is not ours, hundreds of kilobytes means it
/// does not and the delay is exactly ours. The turn is split into the part
/// spent waiting for bytes and the part spent in the handlers, because only
/// the second one is ours to fix.
///
/// <para>A window, not a running total: the sums and the peaks are taken and
/// cleared by <see cref="TunnelNet.TakeStats"/>, so every line describes its
/// own second rather than the average since the cable was plugged in.</para>
/// </remarks>
public readonly record struct TunnelStats(
    double Seconds,
    long Packets,
    long Bytes,
    int SocketAvailable,
    double TurnMeanMs,
    double TurnMaxMs,
    double ReadMeanMs,
    double ReadMaxMs,
    double HandlerMeanMs,
    double HandlerMaxMs,
    double UdpMs,
    double TcpMs)
{
    public double PacketsPerSecond => Seconds > 0 ? Packets / Seconds : 0;

    public double KilobytesPerSecond => Seconds > 0 ? Bytes / 1024.0 / Seconds : 0;
}
