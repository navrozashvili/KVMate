using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace KVMate.Core.Interop;

// Only the leading methods each caller needs are declared. A source-generated COM interface may stop
// early, because the vtable slots are laid out in declaration order and nothing after the last one
// declared is ever called.

/// <summary>Native <c>IPropertyStore</c>. Only <see cref="GetValue"/> is used.</summary>
[GeneratedComInterface]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PropertyKey pkey);

    [PreserveSig]
    int GetValue(in PropertyKey key, out PropVariant pv);
}

/// <summary>Native <c>IMMDevice</c>.</summary>
[GeneratedComInterface]
[Guid("d666063f-1587-4e43-81f1-b948e807363f")]
internal partial interface IMMDevice
{
    /// <param name="iid">Interface to activate.</param>
    /// <param name="dwClsCtx">Execution context.</param>
    /// <param name="pActivationParams">Activation parameters, or zero.</param>
    /// <param name="ppInterface">
    /// Raw pointer, because the same method hands out both <see cref="IDeviceTopology"/> and
    /// <see cref="IKsControl"/>. <see cref="ComInterop.Wrap{T}"/> turns it into a wrapper.
    /// </param>
    [PreserveSig]
    int Activate(in Guid iid, uint dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);

    [PreserveSig]
    int OpenPropertyStore(StorageAccessMode stgmAccess, out IPropertyStore ppProperties);

    [PreserveSig]
    int GetId(out IntPtr ppstrId);

    [PreserveSig]
    int GetState(out DeviceState pdwState);
}

/// <summary>Native <c>IMMDeviceCollection</c>.</summary>
[GeneratedComInterface]
[Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint pcDevices);

    [PreserveSig]
    int Item(uint nDevice, out IMMDevice ppDevice);
}

/// <summary>Native <c>IMMDeviceEnumerator</c>. The notification methods are not declared.</summary>
[GeneratedComInterface]
[Guid("a95664d2-9614-4f35-a746-de8db63617e6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState dwStateMask, out IMMDeviceCollection ppDevices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice? ppEndpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice? ppDevice);
}

/// <summary>Native <c>IDeviceTopology</c>: the graph of parts behind an audio endpoint.</summary>
[GeneratedComInterface]
[Guid("2a07407e-6497-4a18-9787-32f79bd0d98f")]
internal partial interface IDeviceTopology
{
    [PreserveSig]
    int GetConnectorCount(out uint pCount);

    [PreserveSig]
    int GetConnector(uint nIndex, out IConnector ppConnector);
}

/// <summary>Native <c>IConnector</c>: one end of a link between two devices in the topology.</summary>
/// <remarks>
/// The first method is <c>GetType</c> natively. It is renamed here because only the slot matters,
/// and the native name would read as <see cref="object.GetType"/>.
/// </remarks>
[GeneratedComInterface]
[Guid("9c2c4058-23f5-41de-877a-df3af236a09e")]
internal partial interface IConnector
{
    [PreserveSig]
    int GetConnectorType(out int pType);

    [PreserveSig]
    int GetDataFlow(out int pFlow);

    [PreserveSig]
    int ConnectTo(IntPtr pConnectTo);

    [PreserveSig]
    int Disconnect();

    [PreserveSig]
    int IsConnected(out int pbConnected);

    [PreserveSig]
    int GetConnectedTo(out IntPtr ppConTo);

    [PreserveSig]
    int GetConnectorIdConnectedTo(out IntPtr ppwstrConnectorId);

    /// <param name="ppwstrDeviceId">
    /// The device on the other side, allocated with <c>CoTaskMemAlloc</c>. For a Bluetooth audio
    /// endpoint that is the driver's kernel-streaming filter, whose id starts with
    /// <c>{2}.\\?\bth</c>.
    /// </param>
    [PreserveSig]
    int GetDeviceIdConnectedTo(out IntPtr ppwstrDeviceId);
}

/// <summary>Native <c>IKsControl</c>: sends kernel-streaming property requests to a driver.</summary>
[GeneratedComInterface]
[Guid("28f54685-06fd-11d2-b27a-00a0c9223196")]
internal partial interface IKsControl
{
    [PreserveSig]
    int KsProperty(in KsIdentifier property, uint propertyLength, IntPtr propertyData, uint dataLength, out uint bytesReturned);
}
