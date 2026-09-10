using System.Windows;
using System.Windows.Threading;

namespace LuminaMonitor.App;

/// <summary>
/// The application object: the palette behind App.xaml, and the three nets
/// under everything else.
/// </summary>
/// <remarks>
/// Almost everything happens in the window. What lives here is the one thing a
/// window cannot do for itself — catch what nobody caught. The application has
/// ended at least once with no picture, no message and nothing in the event
/// log, which is the worst possible outcome: a fault that leaves no trace
/// cannot be fixed. So all three doors an exception can leave by are watched,
/// each writes the whole exception with its stack into the journal, and each
/// waits for the journal to reach the disk before the process is allowed to go.
/// </remarks>
public partial class App : Application
{
    /// <summary>How long the journal is given to reach the disk before we leave.</summary>
    private static readonly TimeSpan FlushPatience = TimeSpan.FromSeconds(2);

    protected override void OnStartup(StartupEventArgs e)
    {
        // A thread that throws: the process is going down whatever we do, so
        // there is exactly one useful thing to do, and it is this.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Journal.Shared.Fatal("AppDomain", args.ExceptionObject as Exception);
            Journal.Shared.Write($"terminaison du processus : {args.IsTerminating}");
            Journal.Shared.Flush(FlushPatience);
        };

        // The UI thread: recoverable in principle, not in practice — whatever
        // the window was in the middle of is now half done. Written down, then
        // closed properly, which at least runs the session's own shutdown.
        DispatcherUnhandledException += (_, args) =>
        {
            Journal.Shared.Fatal("Dispatcher", args.Exception);
            Journal.Shared.Flush(FlushPatience);
            args.Handled = true;
            Shutdown(1);
        };

        // A task nobody awaited. Harmless by itself — the finalizer is what
        // surfaces it — but it names the path that gave up, which is usually
        // the tunnel or a channel that closed under a fire-and-forget send.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Journal.Shared.Fatal("Task", args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Journal.Shared.Dispose();

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Journal.Shared.Write($"sortie de l'application (code {e.ApplicationExitCode})");
        Journal.Shared.Dispose();
        base.OnExit(e);
    }
}
