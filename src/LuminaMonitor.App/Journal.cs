using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using IoPath = System.IO.Path;

namespace LuminaMonitor.App;

/// <summary>
/// The journal: everything the application did, kept on disk, always.
/// </summary>
/// <remarks>
/// It used to be written only under <c>--diagnostic</c>, which meant that the
/// two failures worth understanding — a picture that stops without a word, and
/// a process that ended leaving nothing behind, not even an entry in the event
/// log — happened precisely where nothing was being recorded. So it is now on
/// for every run, and it is a real log: one file at
/// <c>%APPDATA%\LuminaMonitor\lumina.log</c>, rotated to <c>lumina.1.log</c> at
/// five megabytes, with the wall clock on every line.
///
/// <para>Writing happens on a thread of its own, behind a bounded queue. The
/// window's thread hands over a formatted line and returns; it never waits for
/// a disk. The bound matters more than it looks: a journal that blocks is a
/// journal that freezes the very thing it is watching, so a full queue drops
/// lines and counts them rather than holding anyone up. What is not negotiable
/// is the other end — <see cref="Flush"/> is called before the process leaves,
/// by the exception handlers as well as the window, because a buffer holding
/// the last ten seconds would lose exactly the part worth reading.</para>
/// </remarks>
internal sealed class Journal : IDisposable
{
    /// <summary>Where the file is rotated, in bytes.</summary>
    private const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>Lines that may wait for the disk before the journal starts dropping them.</summary>
    private const int QueueBound = 8192;

    /// <summary>The one journal of the process, so a crash handler can reach it.</summary>
    public static Journal Shared { get; } = new(IoPath.Combine(Settings.Folder, "lumina.log"));

    private readonly BlockingCollection<string> _lines = new(new ConcurrentQueue<string>(), QueueBound);
    private readonly object _fileGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string? _previous;
    private StreamWriter? _file;
    private long _written;
    private long _dropped;
    private bool _closed;

    public Journal(string? path)
    {
        Path = path;
        if (path is null)
            return;

        _previous = IoPath.ChangeExtension(path, null) + ".1.log";
        try
        {
            Directory.CreateDirectory(IoPath.GetDirectoryName(path)!);
            Open();
        }
        catch (Exception)
        {
            Path = null;
            return;
        }

        new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "LuminaJournal",
            Priority = ThreadPriority.BelowNormal,
        }.Start();

        Write($"--- Lumina Monitor {Environment.ProcessId} demarre : {Environment.CommandLine}");
    }

    public string? Path { get; private set; }

    /// <summary>Lines the queue had to throw away because the disk was too slow.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Puts one line in the journal, stamped now, and returns at once.</summary>
    public void Write(string line)
    {
        if (Path is null || _closed)
            return;
        string stamped = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}";
        if (!_lines.TryAdd(stamped))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>
    /// An exception nobody caught, with its stack, and where it came from.
    /// </summary>
    /// <remarks>
    /// Written whole, inner exceptions included: the message alone has never
    /// once been enough to place a fault, and this is the last chance to write
    /// anything at all.
    /// </remarks>
    public void Fatal(string source, Exception? exception)
    {
        var text = new StringBuilder($"EXCEPTION NON GEREE ({source}) : ");
        if (exception is null)
        {
            text.Append("objet non exploitable");
        }
        else
        {
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                text.Append(current.GetType().FullName).Append(" : ").Append(current.Message).AppendLine();
                text.Append(current.StackTrace ?? "  (pas de pile)").AppendLine();
                if (current.InnerException is not null)
                    text.Append("  --- cause : ");
            }
        }
        Write(text.ToString().TrimEnd());
    }

    /// <summary>Waits, briefly, for what is queued to reach the disk.</summary>
    public void Flush(TimeSpan patience)
    {
        if (Path is null)
            return;
        var deadline = _clock.Elapsed + patience;
        while (_lines.Count > 0 && _clock.Elapsed < deadline)
            Thread.Sleep(10);
        lock (_fileGate)
        {
            try { _file?.Flush(); }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        if (Path is null || _closed)
            return;
        Write("--- journal ferme" + (Dropped > 0 ? $" ({Dropped} ligne(s) perdue(s))" : ""));
        _closed = true;
        _lines.CompleteAdding();
        Flush(TimeSpan.FromSeconds(2));
        lock (_fileGate)
        {
            try { _file?.Dispose(); }
            catch (Exception) { }
            _file = null;
        }
    }

    private void WriteLoop()
    {
        foreach (string line in _lines.GetConsumingEnumerable())
        {
            lock (_fileGate)
            {
                try
                {
                    Rotate(line.Length + Environment.NewLine.Length);
                    _file?.WriteLine(line);
                    _written += line.Length + Environment.NewLine.Length;
                    if (_lines.Count == 0)
                        _file?.Flush();
                }
                catch (Exception)
                {
                    // A journal that cannot be written is not worth stopping for.
                }
            }
        }
    }

    /// <summary>Called with the file lock held.</summary>
    private void Rotate(int about)
    {
        if (_file is null || _written + about < MaxBytes || _previous is null)
            return;
        _file.Dispose();
        _file = null;
        try
        {
            if (File.Exists(_previous)) File.Delete(_previous);
            File.Move(Path!, _previous);
        }
        catch (Exception)
        {
            // Whoever holds the old file keeps it; the new one starts anyway.
        }
        Open();
    }

    /// <summary>
    /// Opens the file for appending, and lets anyone read it while we write:
    /// the whole point is that it can be watched live from another
    /// window.
    /// </summary>
    private void Open()
    {
        var stream = new FileStream(Path!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _written = stream.Length;
        _file = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = false };
    }
}
