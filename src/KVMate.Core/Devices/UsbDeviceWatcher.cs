using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;

namespace KVMate.Core.Devices;

/// <summary>
/// <see cref="IDeviceWatcher"/> over <see cref="DeviceWatcher"/>, filtered to one device instance.
/// </summary>
/// <remarks>
/// <para>
/// Watches device nodes (<see cref="DeviceInformationKind.Device"/>) rather than device interfaces,
/// because the instance id is what Device Manager shows and what stays the same across reboots. A
/// device node outlives an unplug, so presence is read from <c>System.Devices.Present</c>, which is
/// part of the filter: a node that stops being present stops matching and is reported as removed.
/// </para>
/// <para>
/// The Windows watcher can stop by itself (it reports <see cref="DeviceWatcherStatus.Aborted"/>
/// after some driver failures). It is then started again, and any presence change it missed shows
/// up as an addition or a removal during its new initial enumeration.
/// </para>
/// </remarks>
public sealed class UsbDeviceWatcher : IDeviceWatcher
{
    private const string InstanceIdProperty = "System.Devices.DeviceInstanceId";
    private const string PresentProperty = "System.Devices.Present";
    private const string PresentFilter = PresentProperty + ":=System.StructuredQueryType.Boolean#True";

    private static readonly TimeSpan EnumerationTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly ILogger<UsbDeviceWatcher> _logger;

    private DeviceWatcher? _watcher;
    private string? _instanceId;
    private TaskCompletionSource? _enumerated;
    private bool _present;
    private bool _initialDone;
    private bool _seenInPass;
    private bool _disposed;

    public UsbDeviceWatcher(ILogger<UsbDeviceWatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public event EventHandler? Arrived;

    /// <inheritdoc />
    public event EventHandler? Removed;

    /// <inheritdoc />
    public bool IsPresent
    {
        get
        {
            lock (_gate)
            {
                return _present;
            }
        }
    }

    /// <summary>The AQS filter matching one present device node.</summary>
    /// <remarks>
    /// The id goes inside double quotes, where AQS reads backslashes and ampersands literally. A
    /// double quote cannot occur in a device instance id.
    /// </remarks>
    public static string FilterFor(string instanceId) =>
        $"{InstanceIdProperty}:=\"{instanceId}\" AND {PresentFilter}";

    /// <inheritdoc />
    public async Task StartAsync(string instanceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Stop();

        TaskCompletionSource enumerated;
        lock (_gate)
        {
            _instanceId = instanceId;
            _present = false;
            _initialDone = false;
            enumerated = StartWatcherLocked();
        }

        try
        {
            await enumerated.Task.WaitAsync(EnumerationTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Carry on with whatever was seen so far. A watcher that is slow to finish its first
            // pass still reports later changes.
            _logger.LogWarning("Device enumeration did not finish within {Timeout}; continuing.", EnumerationTimeout);
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Watching {InstanceId}: {State}.", instanceId, IsPresent ? "present" : "absent");
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            _instanceId = null;
            _present = false;
            _initialDone = false;
            _enumerated?.TrySetResult();
            _enumerated = null;
        }

        StopWatcher(watcher);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UsbDeviceInfo>> ListUsbDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await DeviceInformation
                .FindAllAsync(PresentFilter, [InstanceIdProperty], DeviceInformationKind.Device)
                .AsTask(cancellationToken);

            return
            [
                .. devices
                    .Select(device => new UsbDeviceInfo(InstanceIdOf(device), device.Name))
                    .Where(device => device.InstanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate USB devices.");
            return [];
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private static string InstanceIdOf(DeviceInformation device) =>
        device.Properties.TryGetValue(InstanceIdProperty, out var value) && value is string id ? id : device.Id;

    private TaskCompletionSource StartWatcherLocked()
    {
        var watcher = DeviceInformation.CreateWatcher(
            FilterFor(_instanceId!),
            [InstanceIdProperty, PresentProperty],
            DeviceInformationKind.Device);

        // Continuations run elsewhere, so whoever awaits the first pass never runs inside the
        // watcher's own callback.
        var enumerated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        watcher.Added += OnAdded;
        watcher.Updated += OnUpdated;
        watcher.Removed += OnRemoved;
        watcher.EnumerationCompleted += OnEnumerationCompleted;
        watcher.Stopped += OnStopped;

        _watcher = watcher;
        _enumerated = enumerated;
        _seenInPass = false;

        watcher.Start();
        return enumerated;
    }

    private static void StopWatcher(DeviceWatcher? watcher)
    {
        if (watcher is null)
        {
            return;
        }

        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch (Exception)
        {
            // Already stopping or stopped; either way it is no longer ours.
        }
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation args) => SetPresent(sender, present: true);

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate args) => SetPresent(sender, present: false);

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        // Normally a node that stops being present leaves the filter and is reported as removed.
        // An update that carries the flag is honoured as well, in case a driver reports it that way.
        if (args.Properties.TryGetValue(PresentProperty, out var value) && value is bool present)
        {
            SetPresent(sender, present);
        }
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        bool missedRemoval;

        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher))
            {
                return;
            }

            // Only after a restart: the device was present before the watcher stopped and the new
            // pass did not find it, so it left while nobody was listening.
            missedRemoval = _initialDone && _present && !_seenInPass;

            _initialDone = true;
            _enumerated?.TrySetResult();
        }

        if (missedRemoval)
        {
            SetPresent(sender, present: false);
        }
    }

    private void OnStopped(DeviceWatcher sender, object args)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher) || _disposed)
            {
                return;
            }

            // Stopped without being asked to. Start a fresh one for the same device.
            _logger.LogWarning("The device watcher stopped unexpectedly ({Status}); restarting it.", sender.Status);
            _ = StartWatcherLocked();
        }
    }

    private void SetPresent(DeviceWatcher sender, bool present)
    {
        EventHandler? handler = null;
        var raise = false;

        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher))
            {
                return;
            }

            _seenInPass |= present;

            if (_present == present)
            {
                return;
            }

            _present = present;

            // During the first pass the value is the initial state, which the caller reads from
            // IsPresent rather than hearing as an event.
            if (_initialDone)
            {
                raise = true;
                handler = present ? Arrived : Removed;
            }
        }

        if (!raise)
        {
            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Watched device {State}.", present ? "arrived" : "removed");
        }

        handler?.Invoke(this, EventArgs.Empty);
    }
}
