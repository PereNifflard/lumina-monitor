namespace LuminaMonitor.Core;

/// <summary>
/// Where Core writes what it is doing. The console implementation lives in the
/// probe; the desktop app binds it to its own trace pane.
/// </summary>
public interface ILog
{
    void Info(string message);

    void Warn(string message);
}
