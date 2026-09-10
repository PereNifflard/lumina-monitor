using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace LuminaMonitor.Core.Audio.WinRt;

/// <summary>
/// The flat combase entry points the Windows Runtime is reached through, and
/// the three things this project does with them: make a string, get a class's
/// static half, wait for an asynchronous operation.
/// </summary>
/// <remarks>
/// <para><b>Apartments.</b> Every call is made from thread-pool threads, never
/// from the window's STA thread. <see cref="EnsureMta"/> calls
/// <c>CoIncrementMTAUsage</c> once for the process: from then on any thread that
/// has not initialized COM itself — a thread-pool thread — belongs to the
/// implicit multithreaded apartment and may call <c>RoGetActivationFactory</c>
/// without a <c>RoInitialize</c> of its own (which would otherwise answer
/// CO_E_NOTINITIALIZED).</para>
///
/// <para><b>Waiting.</b> An operation is waited for by reading
/// <c>IAsyncInfo.Status</c> every few milliseconds rather than by giving it a
/// completion handler: a handler would be a managed object Windows calls back
/// into, with its own IID and its own reference counting to get right, for the
/// sake of saving a few polls on an operation that runs once.</para>
/// </remarks>
internal static class WinRtNative
{
    public const int AsyncStarted = 0;
    public const int AsyncCompleted = 1;
    public const int AsyncCanceled = 2;
    public const int AsyncError = 3;

    /// <summary>REGDB_E_CLASSNOTREG: the class does not exist on this Windows (older than 2004 for AudioPlaybackConnection).</summary>
    public const int ClassNotRegistered = unchecked((int)0x80040154);

    private const int PollMs = 25;
    private static readonly object Gate = new();
    private static IntPtr _mtaCookie;

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoIncrementMTAUsage(out IntPtr cookie);

    /// <summary>Keeps the process's multithreaded apartment alive, once.</summary>
    public static void EnsureMta()
    {
        lock (Gate)
        {
            if (_mtaCookie != IntPtr.Zero) return;
            Check(CoIncrementMTAUsage(out _mtaCookie), "CoIncrementMTAUsage");
        }
    }

    /// <summary>A new HSTRING; the caller frees it with <see cref="DeleteString"/>.</summary>
    public static IntPtr CreateString(string text)
    {
        Check(WindowsCreateString(text, text.Length, out IntPtr hstring), "WindowsCreateString");
        return hstring;
    }

    public static void DeleteString(IntPtr hstring)
    {
        if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
    }

    /// <summary>Reads an HSTRING the callee handed over, and frees it. A null HSTRING is the empty string.</summary>
    public static string TakeString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return "";
        try
        {
            IntPtr buffer = WindowsGetStringRawBuffer(hstring, out uint length);
            return Marshal.PtrToStringUni(buffer, (int)length);
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    /// <summary>
    /// The activation factory of a runtime class, cast to the statics interface
    /// <typeparamref name="T"/> — whose IID the factory is asked for directly.
    /// </summary>
    /// <returns>The HRESULT; <paramref name="factory"/> is null unless it is a success.</returns>
    public static int TryGetFactory<T>(string runtimeClass, out T? factory) where T : class
    {
        EnsureMta();
        factory = null;
        IntPtr name = CreateString(runtimeClass);
        try
        {
            Guid iid = typeof(T).GUID;
            int hr = RoGetActivationFactory(name, ref iid, out IntPtr raw);
            if (hr < 0) return hr;
            try
            {
                factory = (T)Marshal.GetObjectForIUnknown(raw);
            }
            finally
            {
                Marshal.Release(raw);
            }
            return hr;
        }
        finally
        {
            DeleteString(name);
        }
    }

    /// <summary>
    /// Waits for an asynchronous operation by polling its status; throws on
    /// error with the operation's own HRESULT, cancels it on timeout or when
    /// <paramref name="cancel"/> fires.
    /// </summary>
    /// <returns>True when it completed; false when it timed out (and was cancelled).</returns>
    public static async Task<bool> WaitAsync(object operation, TimeSpan timeout, string what, CancellationToken cancel)
    {
        var info = (IAsyncInfo)operation;
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Check(info.GetStatus(out int status), what + " / IAsyncInfo.Status");
            switch (status)
            {
                case AsyncCompleted:
                    return true;
                case AsyncError:
                    info.GetErrorCode(out int error);
                    throw new COMException($"{what} : HRESULT 0x{error:X8}", error);
                case AsyncCanceled:
                    throw new OperationCanceledException(what);
            }
            if (cancel.IsCancellationRequested)
            {
                info.Cancel();
                cancel.ThrowIfCancellationRequested();
            }
            if (clock.Elapsed > timeout)
            {
                info.Cancel();
                return false;
            }
            await Task.Delay(PollMs, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Releases what the operation holds once its result has been read (<c>IAsyncInfo.Close</c>).</summary>
    public static void CloseOperation(object? operation)
    {
        if (operation is IAsyncInfo info) info.Close();
    }

    /// <summary>Throws with the HRESULT in hexadecimal.</summary>
    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new COMException($"{what} : HRESULT 0x{hr:X8}", hr);
    }
}

/// <summary>
/// The Windows Runtime rule for the IID of a parameterized interface
/// instance, so that the constants in <see cref="IAsyncOperationOfOpenResult"/>
/// and its kind can be recomputed rather than trusted.
/// </summary>
internal static class WinRtIid
{
    /// <summary>The namespace GUID of the rule, 11f47ad5-7b73-42c0-abae-878b1e16adee, in network order.</summary>
    private static readonly byte[] Namespace =
        [0x11, 0xf4, 0x7a, 0xd5, 0x7b, 0x73, 0x42, 0xc0, 0xab, 0xae, 0x87, 0x8b, 0x1e, 0x16, 0xad, 0xee];

    /// <summary>Signature of IAsyncOperation&lt;AudioPlaybackConnectionOpenResult&gt;.</summary>
    public const string OpenResultOperation =
        "pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};rc(Windows.Media.Audio.AudioPlaybackConnectionOpenResult;{4e656aef-39f9-5fc9-a519-a5bbfd9fe921}))";

    /// <summary>Signature of IVectorView&lt;DeviceInformation&gt;.</summary>
    public const string DeviceInformationVector =
        "pinterface({bbe1fa4c-b0e3-4583-baef-1f1b2e483e56};rc(Windows.Devices.Enumeration.DeviceInformation;{aba0fb95-4398-489d-8e44-e6130927011f}))";

    /// <summary>Signature of IAsyncOperation&lt;DeviceInformationCollection&gt;.</summary>
    public const string DeviceInformationCollectionOperation =
        "pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};rc(Windows.Devices.Enumeration.DeviceInformationCollection;"
        + DeviceInformationVector + "))";

    /// <summary>A version 5 UUID of <paramref name="signature"/> under the WinRT namespace.</summary>
    public static Guid FromSignature(string signature)
    {
        byte[] data = [.. Namespace, .. Encoding.UTF8.GetBytes(signature)];
        byte[] hash = SHA1.HashData(data);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        // The first three fields are big-endian in the hash, little-endian in System.Guid.
        return new Guid(
            (hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3],
            (short)((hash[4] << 8) | hash[5]),
            (short)((hash[6] << 8) | hash[7]),
            hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15]);
    }
}
