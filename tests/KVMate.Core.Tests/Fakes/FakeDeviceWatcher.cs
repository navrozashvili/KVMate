using KVMate.Core.Devices;

namespace KVMate.Core.Tests.Fakes;

/// <summary>A watcher whose device comes and goes when the test says so.</summary>
internal sealed class FakeDeviceWatcher : IDeviceWatcher
{
    public event EventHandler? Arrived;

    public event EventHandler? Removed;

    /// <summary>
    /// The presence the watcher reports. Setting it directly changes the state without an event,
    /// which is how a test makes the device vanish between two checks.
    /// </summary>
    public bool IsPresent { get; set; }

    public string? WatchedInstanceId { get; private set; }

    public int StartCount { get; private set; }

    public Task StartAsync(string instanceId, CancellationToken cancellationToken)
    {
        WatchedInstanceId = instanceId;
        StartCount++;
        return Task.CompletedTask;
    }

    public void Stop() => WatchedInstanceId = null;

    public Task<IReadOnlyList<UsbDeviceInfo>> ListUsbDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UsbDeviceInfo>>([]);

    /// <summary>The device arrives, as Windows would report it.</summary>
    public void Arrive()
    {
        IsPresent = true;
        Arrived?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The device leaves, as Windows would report it.</summary>
    public void Remove()
    {
        IsPresent = false;
        Removed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
    }
}
