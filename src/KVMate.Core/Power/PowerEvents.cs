using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace KVMate.Core.Power;

/// <summary>Raises <see cref="Resumed"/> after the PC wakes from sleep or hibernation.</summary>
/// <remarks>
/// <para>
/// Uses <c>PowerRegisterSuspendResumeNotification</c> with a callback rather than
/// <c>WM_POWERBROADCAST</c>, so it needs no window and works the same in the tray app and the probe.
/// </para>
/// <para>
/// Only <c>PBT_APMRESUMEAUTOMATIC</c> counts. Windows sends it on every resume, including one with
/// nobody at the keyboard, which is exactly when the KVM may have been switched while asleep.
/// </para>
/// </remarks>
public sealed partial class PowerEvents : IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint PbtApmResumeAutomatic = 0x12;

    private readonly ILogger<PowerEvents> _logger;

    private GCHandle _self;
    private IntPtr _parameters;
    private IntPtr _registration;

    public PowerEvents(ILogger<PowerEvents> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>The PC resumed. Raised on a thread-pool thread.</summary>
    public event EventHandler? Resumed;

    /// <summary>Start listening. A failure is logged and leaves resume reconciliation off.</summary>
    public unsafe void Start()
    {
        if (_registration != IntPtr.Zero)
        {
            return;
        }

        _self = GCHandle.Alloc(this);

        // Native memory, so the block the system reads cannot move or be collected.
        _parameters = Marshal.AllocHGlobal(sizeof(SubscribeParameters));
        *(SubscribeParameters*)_parameters = new SubscribeParameters
        {
            Callback = &OnPowerEvent,
            Context = GCHandle.ToIntPtr(_self),
        };

        var error = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, _parameters, out _registration);
        if (error != 0)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning("Could not register for resume notifications (error {Error}); wake will not trigger a check.", error);
            }

            Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            _ = PowerUnregisterSuspendResumeNotification(_registration);
            _registration = IntPtr.Zero;
        }

        Release();
    }

    private void Release()
    {
        _registration = IntPtr.Zero;

        if (_parameters != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_parameters);
            _parameters = IntPtr.Zero;
        }

        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnPowerEvent(IntPtr context, uint type, IntPtr setting)
    {
        // Nothing may escape into the native caller, and the system waits for this to return, so
        // the work is handed to the thread pool.
        try
        {
            if (type == PbtApmResumeAutomatic && GCHandle.FromIntPtr(context).Target is PowerEvents events)
            {
                ThreadPool.QueueUserWorkItem(static e => e.RaiseResumed(), events, preferLocal: false);
            }
        }
        catch (Exception)
        {
            // A notification lost is a reconcile skipped; the next device event still corrects it.
        }

        return 0;
    }

    private void RaiseResumed()
    {
        try
        {
            _logger.LogInformation("Resumed from sleep.");
            Resumed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handling resume failed.");
        }
    }

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr registrationHandle);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerUnregisterSuspendResumeNotification(IntPtr registrationHandle);

    /// <summary>Native <c>DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SubscribeParameters
    {
        public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint> Callback;
        public IntPtr Context;
    }
}
