namespace KVMate.Core.Devices;

/// <summary>A USB device present on this PC.</summary>
/// <param name="InstanceId">The device instance id, such as <c>USB\VID_046D&amp;PID_C52B\...</c>.</param>
/// <param name="Name">The name Device Manager shows.</param>
public sealed record UsbDeviceInfo(string InstanceId, string Name);

/// <summary>Watches one device and says whether it is present.</summary>
/// <remarks>
/// Events are raised on whatever thread Windows reports the change on, never on a UI thread.
/// </remarks>
public interface IDeviceWatcher : IDisposable
{
    /// <summary>Whether the watched device is present right now. False when nothing is watched.</summary>
    bool IsPresent { get; }

    /// <summary>The watched device arrived.</summary>
    event EventHandler? Arrived;

    /// <summary>The watched device went away.</summary>
    event EventHandler? Removed;

    /// <summary>
    /// Watch <paramref name="instanceId"/>, replacing whatever was watched before. Completes once
    /// the initial presence is known, which <see cref="IsPresent"/> then holds. The initial value
    /// raises no event.
    /// </summary>
    Task StartAsync(string instanceId, CancellationToken cancellationToken);

    /// <summary>Stop watching. <see cref="IsPresent"/> reads false afterwards.</summary>
    void Stop();

    /// <summary>Every USB device present now, for the settings picker.</summary>
    Task<IReadOnlyList<UsbDeviceInfo>> ListUsbDevicesAsync(CancellationToken cancellationToken);
}
