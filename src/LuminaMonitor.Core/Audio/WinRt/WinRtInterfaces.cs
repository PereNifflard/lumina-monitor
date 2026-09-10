using System.Runtime.InteropServices;

namespace LuminaMonitor.Core.Audio.WinRt;

// The Windows Runtime interfaces behind "let the phone play on this PC",
// declared by hand, slot by slot.
//
// Where the IIDs come from. The Windows SDK is not installed on the machine
// this was written on, so windows.media.audio.h and windows.devices.enumeration.h
// were not at hand. The source used instead is the one those headers are
// generated from: the Windows metadata files that ship with Windows itself,
// C:\Windows\System32\WinMetadata\Windows.Media.winmd, Windows.Devices.winmd and
// Windows.Foundation.winmd. Each interface there carries a
// Windows.Foundation.Metadata.GuidAttribute, and its methods are listed in
// vtable order; both were read with System.Reflection.Metadata (in the box,
// no package). Every IID below was then checked by the experiment: the runtime
// QueryInterfaces for it the moment a pointer is cast, and a wrong one comes
// back as E_NOINTERFACE (InvalidCastException), not as a quiet misbehaviour.
//
// The three parameterized interfaces (IAsyncOperation<T>, IVectorView<T>) have
// no GuidAttribute of their own: their IID is derived from a type signature by
// the Windows Runtime rule (a version 5 UUID: SHA-1 over the namespace
// 11f47ad5-7b73-42c0-abae-878b1e16adee followed by the UTF-8 signature).
// WinRtIid.FromSignature does that computation and the probe's winrt-selftest
// recomputes each of them against the constants here; the result for
// IAsyncOperation<DeviceInformationCollection>, 45180254-082e-5274-b2e7-ac0517f44d07,
// is the value published in the SDK headers, which is the check on the rule.
//
// Why InterfaceIsIUnknown and not InterfaceIsIInspectable. The latter is what a
// WinRT interface is, but .NET 5 removed the runtime's WinRT support and .NET 8
// answers any call through such a declaration with PlatformNotSupportedException
// ("Marshalling as IInspectable is not supported in the .NET runtime") —
// measured. An IInspectable vtable is IUnknown's three slots, then GetIids,
// GetRuntimeClassName and GetTrustLevel, then the interface's own methods; so
// each interface here is declared IUnknown-based and opens with those three
// slots as never-called placeholders, exactly as the Media Foundation
// declarations repeat the thirty IMFAttributes slots they really have.
//
// ABI shapes: an HSTRING is an IntPtr (created and freed with WindowsCreateString
// / WindowsDeleteString); a WinRT Boolean is one byte; an enumeration is an
// Int32; an HRESULT property (ExtendedError, ErrorCode) is an Int32; an event
// token is an Int64. Every method keeps its HRESULT.

/// <summary>
/// <c>Windows.Foundation.IAsyncInfo</c>, the non-generic half of every
/// asynchronous operation: how this project waits, by polling <see cref="GetStatus"/>.
/// </summary>
/// <remarks>
/// IID {00000036-0000-0000-C000-000000000046} — Windows.Foundation.winmd.
/// AsyncStatus: 0 Started, 1 Completed, 2 Canceled, 3 Error.
/// </remarks>
[ComImport, Guid("00000036-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAsyncInfo
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int GetId(out uint id);
    [PreserveSig] int GetStatus(out int status);
    [PreserveSig] int GetErrorCode(out int hresult);
    [PreserveSig] int Cancel();
    [PreserveSig] int Close();
}

/// <summary><c>Windows.Foundation.IAsyncAction</c>. IID {5a648006-843a-4da9-865b-9d26e5dfad7b} — Windows.Foundation.winmd.</summary>
[ComImport, Guid("5a648006-843a-4da9-865b-9d26e5dfad7b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAsyncAction
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int PutCompleted(IntPtr handler);
    [PreserveSig] int GetCompleted(out IntPtr handler);
    [PreserveSig] int GetResults();
}

/// <summary><c>Windows.Foundation.IClosable</c>, what C# projects as Dispose. IID {30d5a829-7fa4-4026-83bb-d75bae4ea99e} — Windows.Foundation.winmd.</summary>
[ComImport, Guid("30d5a829-7fa4-4026-83bb-d75bae4ea99e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClosable
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int Close();
}

// --- Windows.Media.Audio.AudioPlaybackConnection (Windows 10 2004, build 19041) ---

/// <summary>
/// <c>Windows.Media.Audio.IAudioPlaybackConnectionStatics</c>: the class's
/// static half, reached through <c>RoGetActivationFactory</c>.
/// </summary>
/// <remarks>IID {e60963a2-69e6-5ffc-9e13-824a85213daf} — Windows.Media.winmd.</remarks>
[ComImport, Guid("e60963a2-69e6-5ffc-9e13-824a85213daf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPlaybackConnectionStatics
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    /// <summary>Slot 6: the AQS selector of the Bluetooth A2DP sources Windows can receive from (HSTRING out).</summary>
    [PreserveSig] int GetDeviceSelector(out IntPtr selector);
    /// <summary>Slot 7: a connection object for one device interface id, or null if the id is not one.</summary>
    [PreserveSig] int TryCreateFromId(IntPtr deviceId, out IAudioPlaybackConnection? connection);
}

/// <summary><c>Windows.Media.Audio.IAudioPlaybackConnection</c>, the default interface of the class.</summary>
/// <remarks>
/// IID {1a4c1dea-cafc-50e7-8718-ea3f81cbfa51} — Windows.Media.winmd.
/// AudioPlaybackConnectionState: 0 Closed, 1 Opened. The StateChanged event
/// (slots 12-13) is declared but not used: the state is read by polling.
/// </remarks>
[ComImport, Guid("1a4c1dea-cafc-50e7-8718-ea3f81cbfa51"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPlaybackConnection
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int Start();
    [PreserveSig] int StartAsync(out IAsyncAction? operation);
    [PreserveSig] int GetDeviceId(out IntPtr deviceId);
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int Open(out IAudioPlaybackConnectionOpenResult? result);
    [PreserveSig] int OpenAsync(out IAsyncOperationOfOpenResult? operation);
    [PreserveSig] int AddStateChanged(IntPtr handler, out long token);
    [PreserveSig] int RemoveStateChanged(long token);
}

/// <summary><c>Windows.Media.Audio.IAudioPlaybackConnectionOpenResult</c>.</summary>
/// <remarks>
/// IID {4e656aef-39f9-5fc9-a519-a5bbfd9fe921} — Windows.Media.winmd.
/// AudioPlaybackConnectionOpenResultStatus: 0 Success, 1 RequestTimedOut,
/// 2 DeniedBySystem, 3 UnknownFailure. ExtendedError is an HRESULT.
/// </remarks>
[ComImport, Guid("4e656aef-39f9-5fc9-a519-a5bbfd9fe921"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPlaybackConnectionOpenResult
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int GetStatus(out int status);
    [PreserveSig] int GetExtendedError(out int hresult);
}

/// <summary><c>IAsyncOperation&lt;AudioPlaybackConnectionOpenResult&gt;</c>, what OpenAsync returns.</summary>
/// <remarks>
/// IID {f5245f8a-3dd1-56b2-829b-9888251d689c}, computed from the signature
/// <c>pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};rc(Windows.Media.Audio.AudioPlaybackConnectionOpenResult;{4e656aef-39f9-5fc9-a519-a5bbfd9fe921}))</c>
/// (IAsyncOperation`1's own GUID, then the runtime class and its default interface).
/// </remarks>
[ComImport, Guid("f5245f8a-3dd1-56b2-829b-9888251d689c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAsyncOperationOfOpenResult
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int PutCompleted(IntPtr handler);
    [PreserveSig] int GetCompleted(out IntPtr handler);
    [PreserveSig] int GetResults(out IAudioPlaybackConnectionOpenResult? result);
}

// --- Windows.Devices.Enumeration.DeviceInformation --------------------------------

/// <summary><c>Windows.Devices.Enumeration.IDeviceInformationStatics</c>; only FindAllAsync(selector) is called.</summary>
/// <remarks>IID {c17f100e-3a46-4a78-8013-769dc9b97390} — Windows.Devices.winmd.</remarks>
[ComImport, Guid("c17f100e-3a46-4a78-8013-769dc9b97390"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceInformationStatics
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int Unused06();   // CreateFromIdAsync(String)
    [PreserveSig] int Unused07();   // CreateFromIdAsync(String, IIterable<String>)
    [PreserveSig] int Unused08();   // FindAllAsync()
    [PreserveSig] int Unused09();   // FindAllAsync(DeviceClass)
    /// <summary>Slot 10: FindAllAsync(String aqsFilter).</summary>
    [PreserveSig] int FindAllAsync(IntPtr aqsFilter, out IAsyncOperationOfDeviceInformationCollection? operation);
}

/// <summary><c>Windows.Devices.Enumeration.IDeviceInformation</c>, the default interface of DeviceInformation.</summary>
/// <remarks>IID {aba0fb95-4398-489d-8e44-e6130927011f} — Windows.Devices.winmd.</remarks>
[ComImport, Guid("aba0fb95-4398-489d-8e44-e6130927011f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceInformation
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int GetId(out IntPtr id);
    [PreserveSig] int GetName(out IntPtr name);
    [PreserveSig] int GetIsEnabled(out byte enabled);
    [PreserveSig] int GetIsDefault(out byte isDefault);
}

/// <summary><c>IVectorView&lt;DeviceInformation&gt;</c>, the default interface of DeviceInformationCollection.</summary>
/// <remarks>
/// IID {e170688f-3495-5bf6-aab5-9cac17e0f10f}, computed from the signature
/// <c>pinterface({bbe1fa4c-b0e3-4583-baef-1f1b2e483e56};rc(Windows.Devices.Enumeration.DeviceInformation;{aba0fb95-4398-489d-8e44-e6130927011f}))</c>.
/// </remarks>
[ComImport, Guid("e170688f-3495-5bf6-aab5-9cac17e0f10f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceInformationVectorView
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int GetAt(uint index, out IDeviceInformation? item);
    [PreserveSig] int GetSize(out uint size);
}

/// <summary><c>IAsyncOperation&lt;DeviceInformationCollection&gt;</c>, what FindAllAsync returns.</summary>
/// <remarks>
/// IID {45180254-082e-5274-b2e7-ac0517f44d07}, computed from the signature
/// <c>pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};rc(Windows.Devices.Enumeration.DeviceInformationCollection;pinterface({bbe1fa4c-b0e3-4583-baef-1f1b2e483e56};rc(Windows.Devices.Enumeration.DeviceInformation;{aba0fb95-4398-489d-8e44-e6130927011f}))))</c>
/// — and the value the SDK headers publish for
/// <c>__FIAsyncOperation_1_Windows__CDevices__CEnumeration__CDeviceInformationCollection</c>.
/// </remarks>
[ComImport, Guid("45180254-082e-5274-b2e7-ac0517f44d07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAsyncOperationOfDeviceInformationCollection
{
    [PreserveSig] int GetIids(); [PreserveSig] int GetRuntimeClassName(); [PreserveSig] int GetTrustLevel();
    [PreserveSig] int PutCompleted(IntPtr handler);
    [PreserveSig] int GetCompleted(out IntPtr handler);
    [PreserveSig] int GetResults(out IDeviceInformationVectorView? result);
}
