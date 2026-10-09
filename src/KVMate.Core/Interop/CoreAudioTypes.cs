using System.Runtime.InteropServices;

namespace KVMate.Core.Interop;

/// <summary>Direction of an audio endpoint. Native <c>EDataFlow</c>.</summary>
internal enum EDataFlow
{
    /// <summary>Playback. The only direction a speaker has.</summary>
    Render = 0,

    /// <summary>Recording.</summary>
    Capture = 1,

    /// <summary>Both.</summary>
    All = 2,
}

/// <summary>Native <c>ERole</c>. Declared only because the enumerator's vtable mentions it.</summary>
internal enum ERole
{
    /// <summary>Games and system notifications.</summary>
    Console = 0,

    /// <summary>General playback.</summary>
    Multimedia = 1,

    /// <summary>Voice chat.</summary>
    Communications = 2,
}

/// <summary>Endpoint availability. Native <c>DEVICE_STATE_*</c>.</summary>
[Flags]
internal enum DeviceState : uint
{
    /// <summary>Present and usable. For a Bluetooth speaker this means connected to this PC.</summary>
    Active = 0x00000001,

    /// <summary>Disabled in Windows sound settings.</summary>
    Disabled = 0x00000002,

    /// <summary>The device is gone.</summary>
    NotPresent = 0x00000004,

    /// <summary>
    /// The jack is empty. A paired Bluetooth speaker that is not connected reads as this.
    /// </summary>
    Unplugged = 0x00000008,

    /// <summary>Every state, so that paired but disconnected speakers are enumerated too.</summary>
    All = Active | Disabled | NotPresent | Unplugged,
}

/// <summary>Property store access mode. Native <c>STGM_*</c>.</summary>
internal enum StorageAccessMode : uint
{
    /// <summary>Read only, which is all this app ever needs.</summary>
    Read = 0,
}

/// <summary>Native <c>PROPERTYKEY</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    /// <summary>Property set identifier.</summary>
    public Guid Fmtid;

    /// <summary>Property identifier within the set.</summary>
    public uint Pid;
}

/// <summary>The property keys this app reads.</summary>
internal static class PropertyKeys
{
    /// <summary><c>PKEY_Device_FriendlyName</c>: the endpoint name Windows sound settings shows.</summary>
    public static readonly PropertyKey DeviceFriendlyName = new()
    {
        Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        Pid = 14,
    };

    /// <summary>
    /// <c>PKEY_Device_ContainerId</c>: the physical device an endpoint belongs to. A speaker has
    /// one endpoint per Bluetooth profile, and they share this.
    /// </summary>
    public static readonly PropertyKey DeviceContainerId = new()
    {
        Fmtid = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        Pid = 2,
    };
}

/// <summary>
/// Enough of native <c>PROPVARIANT</c> to read a wide string or a GUID, the only two variant
/// types this app asks for.
/// </summary>
/// <remarks>
/// The pointer sits at offset 8 in the 64-bit native union: two bytes of <c>vt</c>, six reserved,
/// then the union. This app is x64 only.
/// </remarks>
[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    private const ushort VtLpwstr = 31;
    private const ushort VtClsid = 72;

    /// <summary>Variant type tag.</summary>
    [FieldOffset(0)]
    public ushort Vt;

    /// <summary>The union, read only as a pointer.</summary>
    [FieldOffset(8)]
    public IntPtr Pointer;

    /// <summary>
    /// Never read. Pads the struct to the native 24 bytes: the widest union members (a
    /// <c>BLOB</c>, a <c>CA*</c> vector) are a count plus a pointer, and the callee writes the
    /// whole struct, so a 16-byte one would have its neighbour on the stack overwritten.
    /// </summary>
    [FieldOffset(16)]
    public IntPtr Reserved;

    /// <summary>The string this variant holds, or null if it holds something else.</summary>
    public readonly string? AsString() =>
        Vt == VtLpwstr && Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(Pointer) : null;

    /// <summary>The GUID this variant points at, or null if it holds something else.</summary>
    public readonly Guid? AsGuid() =>
        Vt == VtClsid && Pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(Pointer) : null;
}

/// <summary>Native <c>KSIDENTIFIER</c> (alias <c>KSPROPERTY</c>).</summary>
/// <remarks>
/// The native union also holds a <c>LONGLONG</c>, which pads it to 24 bytes on x64. A GUID and two
/// 32-bit values are already 24 bytes, so the sequential layout matches without it.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct KsIdentifier
{
    /// <summary>Property set.</summary>
    public Guid Set;

    /// <summary>Property within the set.</summary>
    public uint Id;

    /// <summary><c>KSPROPERTY_TYPE_*</c>.</summary>
    public uint Flags;
}

/// <summary>The Bluetooth audio driver's one-shot commands.</summary>
/// <remarks>
/// <para>
/// <c>KSPROPSETID_BtAudio</c> from <c>ksmedia.h</c>. Both properties are "get" requests with no
/// data: the request itself is the command. This is what the Connect and Disconnect buttons in
/// Sound settings send, and it needs no elevation.
/// </para>
/// <para>
/// The driver acknowledges the request before the radio link is up or down, so the result says
/// only that the command was accepted. Whether the speaker actually came is read from the
/// endpoint state afterwards.
/// </para>
/// </remarks>
internal static class BtAudioProperties
{
    /// <summary><c>KSPROPSETID_BtAudio</c>.</summary>
    public static readonly Guid PropertySet = new("7fa06c40-b8f6-4c7e-8556-e8c33a12e54d");

    /// <summary><c>KSPROPERTY_ONESHOT_RECONNECT</c>.</summary>
    public const uint OneShotReconnect = 0;

    /// <summary><c>KSPROPERTY_ONESHOT_DISCONNECT</c>.</summary>
    public const uint OneShotDisconnect = 1;

    /// <summary><c>KSPROPERTY_TYPE_GET</c>.</summary>
    public const uint TypeGet = 0x00000001;
}
