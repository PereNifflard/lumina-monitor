using System.Runtime.InteropServices;

namespace LuminaMonitor.Core.Media.MediaFoundation;

/// <summary>
/// The Media Foundation COM interfaces this project calls, declared by hand.
/// </summary>
/// <remarks>
/// No interop assembly, no NuGet: a COM interface is a vtable, so what matters
/// is that every slot is declared in the right order with the right stack
/// shape. Slots this project never calls are still declared — a missing one
/// would shift everything below it — as bare <c>Unused</c> entries the runtime
/// never has to marshal because they are never invoked.
///
/// <para>The interfaces are flattened, not inherited: in COM, IMFMediaType and
/// IMFSample begin with the whole IMFAttributes vtable, and declaring that in
/// managed code as C# interface inheritance would put the derived methods at
/// the base's slot numbers. So each interface repeats the thirty attribute
/// slots it really has.</para>
///
/// <para>Every method keeps its HRESULT (<see cref="PreserveSigAttribute"/>):
/// the decoder answers "need more input" and "the format changed" as failure
/// codes, and those are ordinary events here, not exceptions.</para>
/// </remarks>
[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    // --- IMFAttributes, slots 0..29 ---
    [PreserveSig] int Unused00(); [PreserveSig] int Unused01(); [PreserveSig] int Unused02(); [PreserveSig] int Unused03();
    [PreserveSig] int GetUINT32(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int Unused06();
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int Unused08(); [PreserveSig] int Unused09(); [PreserveSig] int Unused10(); [PreserveSig] int Unused11();
    [PreserveSig] int Unused12(); [PreserveSig] int Unused13(); [PreserveSig] int Unused14(); [PreserveSig] int Unused15();
    [PreserveSig] int Unused16(); [PreserveSig] int Unused17();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int Unused20();
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int Unused22(); [PreserveSig] int Unused23(); [PreserveSig] int Unused24(); [PreserveSig] int Unused25();
    [PreserveSig] int Unused26(); [PreserveSig] int Unused27(); [PreserveSig] int Unused28(); [PreserveSig] int Unused29();
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    // --- IMFAttributes, slots 0..29 ---
    [PreserveSig] int Unused00(); [PreserveSig] int Unused01(); [PreserveSig] int Unused02(); [PreserveSig] int Unused03();
    [PreserveSig] int Unused04(); [PreserveSig] int Unused05(); [PreserveSig] int Unused06(); [PreserveSig] int Unused07();
    [PreserveSig] int Unused08(); [PreserveSig] int Unused09(); [PreserveSig] int Unused10(); [PreserveSig] int Unused11();
    [PreserveSig] int Unused12(); [PreserveSig] int Unused13(); [PreserveSig] int Unused14(); [PreserveSig] int Unused15();
    [PreserveSig] int Unused16(); [PreserveSig] int Unused17(); [PreserveSig] int Unused18(); [PreserveSig] int Unused19();
    [PreserveSig] int Unused20(); [PreserveSig] int Unused21(); [PreserveSig] int Unused22(); [PreserveSig] int Unused23();
    [PreserveSig] int Unused24(); [PreserveSig] int Unused25(); [PreserveSig] int Unused26(); [PreserveSig] int Unused27();
    [PreserveSig] int Unused28(); [PreserveSig] int Unused29();
    // --- IMFSample's own, slots 30..43 ---
    [PreserveSig] int Unused30(); [PreserveSig] int Unused31();
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    [PreserveSig] int Unused34();
    [PreserveSig] int SetSampleDuration(long duration);
    [PreserveSig] int Unused36();
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    [PreserveSig] int Unused40();
    [PreserveSig] int RemoveAllBuffers();
    [PreserveSig] int Unused42(); [PreserveSig] int Unused43();
}

/// <summary>
/// An attribute store. Only <see cref="SetUINT32"/> is called, to put the
/// transform into low-latency mode, which is the difference between a decoder
/// that answers with the picture it was just given and one that holds four
/// dozen of them waiting to reorder pictures this stream never reorders.
/// </summary>
[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int Unused00(); [PreserveSig] int Unused01(); [PreserveSig] int Unused02(); [PreserveSig] int Unused03();
    [PreserveSig] int GetUINT32(ref Guid key, out uint value);
    [PreserveSig] int Unused05(); [PreserveSig] int Unused06(); [PreserveSig] int Unused07();
    [PreserveSig] int Unused08(); [PreserveSig] int Unused09(); [PreserveSig] int Unused10(); [PreserveSig] int Unused11();
    [PreserveSig] int Unused12(); [PreserveSig] int Unused13(); [PreserveSig] int Unused14(); [PreserveSig] int Unused15();
    [PreserveSig] int Unused16(); [PreserveSig] int Unused17();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    [PreserveSig] int Unused00();
    [PreserveSig] int GetStreamCount(out uint inputs, out uint outputs);
    [PreserveSig] int Unused02(); [PreserveSig] int Unused03();
    [PreserveSig] int GetOutputStreamInfo(uint id, out MftOutputStreamInfo info);
    [PreserveSig] int GetAttributes(out IMFAttributes? attributes);
    [PreserveSig] int Unused06(); [PreserveSig] int Unused07();
    [PreserveSig] int Unused08(); [PreserveSig] int Unused09(); [PreserveSig] int Unused10();
    [PreserveSig] int GetOutputAvailableType(uint id, uint index, out IMFMediaType? type);
    [PreserveSig] int SetInputType(uint id, IMFMediaType? type, uint flags);
    [PreserveSig] int SetOutputType(uint id, IMFMediaType? type, uint flags);
    [PreserveSig] int Unused14();
    [PreserveSig] int GetOutputCurrentType(uint id, out IMFMediaType? type);
    [PreserveSig] int Unused16(); [PreserveSig] int Unused17(); [PreserveSig] int Unused18(); [PreserveSig] int Unused19();
    [PreserveSig] int ProcessMessage(uint message, IntPtr parameter);
    [PreserveSig] int ProcessInput(uint id, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint count, ref MftOutputDataBuffer buffers, out uint status);
}

/// <summary>The codec knobs; only <see cref="SetValue"/> is used, for low-latency mode.</summary>
[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int Unused01(); [PreserveSig] int Unused02(); [PreserveSig] int Unused03();
    [PreserveSig] int Unused04(); [PreserveSig] int Unused05();
    [PreserveSig] int SetValue(ref Guid api, IntPtr variant);
}
