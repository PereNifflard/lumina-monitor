using System.Runtime.InteropServices;

namespace LuminaMonitor.Core.Media.MediaFoundation;

/// <summary>What <c>IMFTransform.GetOutputStreamInfo</c> says about the pictures it will hand back.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint Flags;
    public uint Size;
    public uint Alignment;
}

/// <summary>One output slot of <c>IMFTransform.ProcessOutput</c>.</summary>
/// <remarks>
/// The caller owns <c>Sample</c>: the Microsoft H.264 decoder does not set
/// MFT_OUTPUT_STREAM_PROVIDES_SAMPLES, so this project allocates the picture
/// buffer itself and hands the same sample back on every call.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public uint StreamId;
    [MarshalAs(UnmanagedType.Interface)] public IMFSample? Sample;
    public uint Status;
    public IntPtr Events;
}

/// <summary>The flat entry points and the constants they are called with.</summary>
internal static class MfNative
{
    // Media Foundation platform version: SDK 2, API 0x70.
    public const uint MfVersion = 0x00020070;
    public const uint MfStartupLite = 1;

    public const uint ClsCtxInprocServer = 1;
    public const uint CoinitMultithreaded = 0;

    public const uint MessageCommandFlush = 0;
    public const uint MessageCommandDrain = 1;
    public const uint MessageNotifyBeginStreaming = 0x10000000;
    public const uint MessageNotifyEndStreaming = 0x10000001;
    public const uint MessageNotifyEndOfStream = 0x10000002;
    public const uint MessageNotifyStartOfStream = 0x10000003;

    public const int Ok = 0;
    public const int NeedMoreInput = unchecked((int)0xC00D6D72);
    public const int StreamChange = unchecked((int)0xC00D6D61);
    public const int TransformTypeNotSet = unchecked((int)0xC00D6D3A);
    public const int NoMoreTypes = unchecked((int)0xC00D36B9);
    public const int InvalidMediaType = unchecked((int)0xC00D36B4);

    public const ushort VariantBool = 11;
    public const short VariantTrue = -1;

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType type);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoInitializeEx(IntPtr reserved, uint flags);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object instance);

    /// <summary>Throws with the HRESULT in hexadecimal, which is the only form worth reading.</summary>
    public static void Check(int hr, string what)
    {
        if (hr < 0)
            throw new InvalidOperationException($"{what} : HRESULT 0x{hr:X8}");
    }
}

/// <summary>The GUIDs, spelled out: no interop assembly ships them.</summary>
internal static class MfGuids
{
    public static Guid ClsidH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    public static Guid IidTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    public static Guid MajorTypeKey = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");   // MF_MT_MAJOR_TYPE
    public static Guid SubtypeKey = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");     // MF_MT_SUBTYPE
    public static Guid FrameSizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");   // MF_MT_FRAME_SIZE
    public static Guid DefaultStrideKey = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"); // MF_MT_DEFAULT_STRIDE
    public static Guid InterlaceModeKey = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"); // MF_MT_INTERLACE_MODE

    public static Guid MediaTypeVideo = new("73646976-0000-0010-8000-00AA00389B71"); // 'vids'
    public static Guid VideoFormatH264 = new("34363248-0000-0010-8000-00AA00389B71"); // 'H264'
    public static Guid VideoFormatNv12 = new("3231564E-0000-0010-8000-00AA00389B71"); // 'NV12'

    public static Guid CodecApiLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
}
