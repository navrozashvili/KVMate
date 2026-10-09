using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace KVMate.Core.Interop;

/// <summary>
/// Creates COM wrappers and releases them deterministically rather than leaving them to the
/// finalizer.
/// </summary>
/// <remarks>
/// Wrappers are always created with <see cref="CreateObjectFlags.UniqueInstance"/>, because
/// <see cref="Release"/> calls <see cref="ComObject.FinalRelease"/>; on a cached wrapper that would
/// drop a reference some other holder still expects to own.
/// </remarks>
internal static class ComInterop
{
    /// <summary><c>CLSCTX_ALL</c>, which is what <c>IMMDevice::Activate</c> callers pass.</summary>
    internal const uint ClsCtxAll = 0x17;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary><c>CLSID_MMDeviceEnumerator</c>.</summary>
    private static readonly Guid MMDeviceEnumeratorClsid = new("bcde0395-e52f-467c-8e3d-c4579291692e");

    /// <summary>Create an endpoint enumerator. The caller releases it.</summary>
    /// <exception cref="COMException">The object could not be created.</exception>
    internal static IMMDeviceEnumerator CreateDeviceEnumerator()
    {
        var hr = Ole32.CoCreateInstance(
            in MMDeviceEnumeratorClsid,
            IntPtr.Zero,
            Ole32.ClsCtxInprocServer,
            in Ole32.IidIUnknown,
            out var unknown);
        Marshal.ThrowExceptionForHR(hr);

        return Wrap<IMMDeviceEnumerator>(unknown);
    }

    /// <summary>
    /// Turn a raw interface pointer into a wrapper, taking over the caller's reference.
    /// </summary>
    internal static T Wrap<T>(IntPtr unknown)
        where T : class
    {
        try
        {
            // The wrapper takes a reference of its own, so the one the pointer carried is still
            // ours to drop.
            return (T)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// Release a wrapper now. Safe with null and with anything that is not a wrapper, so it can be
    /// used unguarded in a <c>finally</c>.
    /// </summary>
    internal static void Release(object? rcw)
    {
        if (rcw is ComObject com)
        {
            com.FinalRelease();
        }
    }

    /// <summary>Read and free a string the callee allocated with <c>CoTaskMemAlloc</c>.</summary>
    internal static string? ReadCoTaskMemString(IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(value);
        }
        finally
        {
            Marshal.FreeCoTaskMem(value);
        }
    }
}

/// <summary>Collects wrappers as they are obtained and releases them all at the end of a walk.</summary>
/// <remarks>
/// A topology walk touches a dozen objects per endpoint and can fail at any step. Tracking them in
/// one place keeps the release in one <c>finally</c> instead of one per step.
/// </remarks>
internal sealed class ComScope : IDisposable
{
    private readonly List<object> _owned = [];

    /// <summary>Remember <paramref name="rcw"/> for release and hand it back.</summary>
    public T Own<T>(T rcw)
        where T : class
    {
        _owned.Add(rcw);
        return rcw;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Reverse order, so children go before the objects that produced them.
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            ComInterop.Release(_owned[i]);
        }

        _owned.Clear();
    }
}
