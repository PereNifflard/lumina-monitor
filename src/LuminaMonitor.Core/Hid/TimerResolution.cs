using System.Runtime.InteropServices;

namespace LuminaMonitor.Core.Hid;

/// <summary>
/// Asks Windows for a one-millisecond timer while input is being sent.
/// </summary>
/// <remarks>
/// Every .NET timer — <see cref="PeriodicTimer"/> included — is driven by the
/// platform's timer, which idles at 15.6 ms. A pump asking for 120 ticks a
/// second therefore gets 75, measured: the reports still go out in order and
/// still never queue, but a position can be up to 13 ms old before it leaves,
/// which is a third of the delay this whole exercise exists to remove.
///
/// <para>Since Windows 10 2004 the request is scoped to the calling process, so
/// this raises no other program's timer and lowers nobody's battery life but
/// ours. It is held only while a session is actually sending input, and the
/// reference count is what makes two sessions in one process safe.</para>
/// </remarks>
internal static class TimerResolution
{
    private const uint OneMillisecond = 1;

    private static readonly object Gate = new();
    private static int _holders;

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    /// <summary>Holds the fine timer until the returned handle is disposed.</summary>
    public static IDisposable Hold() => new Holder();

    private sealed class Holder : IDisposable
    {
        private bool _released;

        public Holder()
        {
            lock (Gate)
                if (_holders++ == 0)
                    TimeBeginPeriod(OneMillisecond);
        }

        public void Dispose()
        {
            lock (Gate)
            {
                if (_released) return;
                _released = true;
                if (--_holders == 0)
                    TimeEndPeriod(OneMillisecond);
            }
        }
    }
}
