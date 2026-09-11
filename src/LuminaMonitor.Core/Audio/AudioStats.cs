namespace LuminaMonitor.Core.Audio;

/// <summary>
/// Everything the sound path counted, in one snapshot.
/// </summary>
/// <remarks>
/// The counters exist because the two ways this path fails are silent. A frame
/// the decoder refused produces a fading tail rather than a noise, and a queue
/// that runs dry produces ten milliseconds of nothing: neither of them is
/// audible on its own, and both of them are obvious in a number. So every step
/// of the chain reports how many frames it passed and how many it lost, and the
/// queue reports how full it is against the target it was asked for — the one
/// figure that says whether the delay someone set is the delay they got.
///
/// <para><see cref="SkewMs"/> is the only one that is not a count: how far the
/// sound being played is ahead of or behind the picture being shown, from the
/// two streams' own sender reports. It is a measurement, not a correction —
/// nothing in this version acts on it.</para>
/// </remarks>
/// <param name="Streaming">Whether the phone is sending sound right now.</param>
/// <param name="Failure">Why it is not, when it is not: the daemon's refusal or the output's.</param>
/// <param name="Packets">RTP datagrams of the audio stream that reached the chain.</param>
/// <param name="FramesDecoded">Access units the decoder read whole.</param>
/// <param name="FramesFailed">Access units it refused; each one leaves the overlap's fading tail.</param>
/// <param name="FramesLost">Silent frames put in for packets that never arrived, to keep the time.</param>
/// <param name="PacketsOutOfOrder">Packets that arrived after a later one and were dropped.</param>
/// <param name="Underruns">Times the output asked for samples the queue did not have.</param>
/// <param name="FramesSkipped">Frames dropped because the queue had drifted past its target.</param>
/// <param name="QueuedMs">How much sound is waiting in the jitter buffer.</param>
/// <param name="TargetMs">How much it is supposed to be waiting.</param>
/// <param name="OutputLatencyMs">What the endpoint says its own stream latency is.</param>
/// <param name="Device">The output actually in use, as Windows names it.</param>
/// <param name="Format">The format it is being fed in.</param>
/// <param name="SkewMs">Sound minus picture, in milliseconds; NaN until both streams have reported.</param>
public readonly record struct AudioStats(
    bool Streaming,
    string? Failure,
    long Packets,
    long FramesDecoded,
    long FramesFailed,
    long FramesLost,
    long PacketsOutOfOrder,
    long Underruns,
    long FramesSkipped,
    double QueuedMs,
    double TargetMs,
    double OutputLatencyMs,
    string Device,
    string Format,
    double SkewMs)
{
    /// <summary>A one-line summary for the journal, French like every other log line.</summary>
    public override string ToString() =>
        $"{(Streaming ? "ouvert" : Failure is null ? "coupe" : "refuse")}"
        + (Failure is null ? "" : $" ({Failure})")
        + $"  {Packets} paq  {FramesDecoded} trames  echecs {FramesFailed}"
        + $"  pertes {FramesLost}  desordre {PacketsOutOfOrder}"
        + $"  sous-alim {Underruns}  sauts {FramesSkipped}"
        + $"  file {QueuedMs:F0} / cible {TargetMs:F0} ms"
        + $"  latence sortie {OutputLatencyMs:F1} ms"
        + $"  peripherique « {Device} » {Format}"
        + (double.IsNaN(SkewMs) ? "  desynchro n/a" : $"  desynchro son-image {SkewMs:+0;-0;0} ms");
}
