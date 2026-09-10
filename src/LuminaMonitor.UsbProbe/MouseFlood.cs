using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// A synthetic hand on a very fast mouse, aimed at the window.
/// </summary>
/// <remarks>
/// The delay this exists to measure is proportional to how fast the mouse is
/// moved — a gaming mouse sends around two thousand <c>WM_MOUSEMOVE</c> a
/// second — and that is not something a person can reproduce on demand, twice,
/// with the same numbers. So it is synthesized: <c>SendInput</c> with relative
/// deltas around a small circle, at a cadence held by a spin rather than by a
/// timer, because the platform timer's millisecond floor is half the period
/// being asked for.
///
/// <para>Relative rather than absolute, deliberately: that is what a mouse
/// sends, and it is the path Windows' own pointer handling takes. The cursor is
/// re-anchored on the middle of the window once per revolution, which keeps
/// pointer acceleration from walking the circle off the picture over fifteen
/// seconds without changing what each individual event is.</para>
///
/// <para>The window has to be engaged before any of it means anything — it
/// takes the mouse only after a click inside the picture, and ignores moves
/// while it is not the active window. A real click does both: it activates and
/// it engages.</para>
/// </remarks>
internal static class MouseFlood
{
    private const uint InputMouse = 0;
    private const uint MoveRelative = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const int RestoreWindow = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int count);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    /// <summary>The window's title, which is the only handle the probe has on it.</summary>
    private const string WindowTitle = "Lumina Monitor";

    /// <summary>
    /// Floods the window with mouse movement for a while.
    /// </summary>
    /// <param name="seconds">How long to keep it up.</param>
    /// <param name="hz">Events a second; a fast gaming mouse sits near 2000.</param>
    /// <param name="hold">
    /// Whether the left button is held for the duration. Held, the window has a
    /// finger down and every move becomes a report; free, only the pointer
    /// moves. Both are worth measuring and the first is the worse case.
    /// </param>
    public static int Run(int seconds, int hz, bool hold, Action<string> say)
    {
        IntPtr window = FindWindowW(null, WindowTitle);
        if (window == IntPtr.Zero)
        {
            say($"Fenetre « {WindowTitle} » introuvable : lancer l'app d'abord.");
            return 3;
        }

        ShowWindow(window, RestoreWindow);
        SetForegroundWindow(window);
        Thread.Sleep(300);

        if (!GetClientRect(window, out var client))
        {
            say("GetClientRect a echoue.");
            return 4;
        }
        var origin = new NativePoint { X = 0, Y = 0 };
        ClientToScreen(window, ref origin);
        int centreX = origin.X + (client.Right - client.Left) / 2;
        int centreY = origin.Y + (client.Bottom - client.Top) / 2;
        double radius = Math.Max(30, Math.Min(120, (client.Right - client.Left) / 4.0));

        // The click that activates the window and takes the mouse. The app
        // treats the first click as engagement only, so a second one is needed
        // before a press means anything on the picture.
        SetCursorPos(centreX, centreY);
        Thread.Sleep(80);
        Button(LeftDown);
        Thread.Sleep(40);
        Button(LeftUp);
        Thread.Sleep(300);

        IntPtr active = GetForegroundWindow();
        var title = new StringBuilder(128);
        GetWindowTextW(active, title, title.Capacity);
        say($"Fenetre active : « {title} » ({(active == window ? "la bonne" : "PAS la bonne")}).");
        say($"Centre {centreX},{centreY}, rayon {radius:F0} px, {hz} evenements/s pendant {seconds} s,"
            + $" bouton {(hold ? "maintenu" : "libre")}.");

        if (hold)
        {
            SetCursorPos(centreX, centreY);
            Button(LeftDown);
            Thread.Sleep(250);        // past the app's tap-defer window: this is a drag
        }

        TimeBeginPeriod(1);
        long sent = 0, skipped = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            // Four revolutions a second whatever the rate, so the step is about
            // two pixels at two kilohertz — the same order as a hand moving
            // fast, rather than a cursor teleporting round a circle.
            double perRevolution = Math.Max(8, hz / 4.0);
            long period = Stopwatch.Frequency / Math.Max(1, hz);
            long start = Stopwatch.GetTimestamp();
            long deadline = start + (long)seconds * Stopwatch.Frequency;
            double angle = 0;
            double lastX = radius, lastY = 0;

            for (long step = 0; ; step++)
            {
                long due = start + step * period;
                if (due > deadline)
                    break;
                Wait(due);

                angle += 2 * Math.PI / perRevolution;
                double x = radius * Math.Cos(angle);
                double y = radius * Math.Sin(angle);
                int dx = (int)Math.Round(x - lastX);
                int dy = (int)Math.Round(y - lastY);
                lastX += dx;
                lastY += dy;

                if (dx == 0 && dy == 0) { skipped++; continue; }
                Move(dx, dy);
                sent++;

                // Once a revolution, put the cursor back where the circle says
                // it should be: pointer acceleration turns a circle of relative
                // deltas into a spiral otherwise. Corrected to the computed
                // point rather than to the centre, so nothing jumps.
                if (step % (long)perRevolution == 0)
                    SetCursorPos(centreX + (int)Math.Round(lastX), centreY + (int)Math.Round(lastY));
            }
        }
        finally
        {
            if (hold)
                Button(LeftUp);
            TimeEndPeriod(1);
        }

        double elapsed = clock.Elapsed.TotalSeconds;
        say($"*** FLOT SOURIS *** {sent} mouvements en {elapsed:F1} s -> {sent / Math.Max(0.001, elapsed):F0}/s"
            + $" ({skipped} pas nul(s) omis).");
        return 0;
    }

    /// <summary>Spins to the due time; a sleep cannot resolve half a millisecond.</summary>
    private static void Wait(long dueTicks)
    {
        while (true)
        {
            long left = dueTicks - Stopwatch.GetTimestamp();
            if (left <= 0)
                return;
            if (left * 1000.0 / Stopwatch.Frequency > 2)
                Thread.Sleep(1);
            else
                Thread.SpinWait(40);
        }
    }

    /// <summary>One reused buffer: two thousand allocations a second would be the noise.</summary>
    private static readonly Input[] One = new Input[1];

    private static void Move(int dx, int dy)
    {
        One[0].Type = InputMouse;
        One[0].Mouse.Dx = dx;
        One[0].Mouse.Dy = dy;
        One[0].Mouse.MouseData = 0;
        One[0].Mouse.Flags = MoveRelative;
        SendInput(1, One, Marshal.SizeOf<Input>());
    }

    private static void Button(uint flag)
    {
        One[0].Type = InputMouse;
        One[0].Mouse.Dx = 0;
        One[0].Mouse.Dy = 0;
        One[0].Mouse.MouseData = 0;
        One[0].Mouse.Flags = flag;
        SendInput(1, One, Marshal.SizeOf<Input>());
    }
}
