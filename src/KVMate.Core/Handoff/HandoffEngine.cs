using KVMate.Core.Devices;
using KVMate.Core.Speakers;
using Microsoft.Extensions.Logging;

namespace KVMate.Core.Handoff;

/// <summary>
/// The policy: connect the speaker when the watched device is here, disconnect it when the device
/// is gone.
/// </summary>
/// <remarks>
/// <para>
/// One operation runs at a time, and every trigger (a device event, startup, resume, or a manual
/// action) cancels the one in flight and queues behind it. The new one starts only once the old one
/// has unwound, so two operations never talk to the speaker at once.
/// </para>
/// <para>
/// Automatic triggers wait out <see cref="Debounce"/> and then act on the device state at that
/// moment, so a flicker during a switch resolves to its final state. Manual actions act at once.
/// A manual action holds until the next trigger simply because nothing else starts an operation.
/// </para>
/// <para>
/// Delays complete their awaiters inline on the timer's thread rather than queueing them, so with
/// a fake clock every step a test advances past has already run when <c>Advance</c> returns.
/// </para>
/// </remarks>
public sealed class HandoffEngine : IDisposable
{
    /// <summary>How long a device state must hold before the engine acts on it.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// The waits before the second, third and fourth connect attempts. The first attempt is
    /// immediate.
    /// </summary>
    /// <remarks>
    /// Sized for the one-host rejection: the other PC lets go once its own debounce and disconnect
    /// have run, and these attempts span that.
    /// </remarks>
    public static readonly IReadOnlyList<TimeSpan> ConnectRetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    /// <summary>The wait before the single disconnect retry.</summary>
    public static readonly TimeSpan DisconnectRetryDelay = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly IDeviceWatcher _watcher;
    private readonly ISpeakerLink _speaker;
    private readonly TimeProvider _time;
    private readonly ILogger<HandoffEngine> _logger;

    private CancellationTokenSource _operation = new();
    private Task _current = Task.CompletedTask;
    private string? _speakerId;
    private HandoffStatus _status = HandoffStatus.NotConfigured;
    private bool _disposed;

    public HandoffEngine(IDeviceWatcher watcher, ISpeakerLink speaker, TimeProvider time, ILogger<HandoffEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(speaker);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);

        _watcher = watcher;
        _speaker = speaker;
        _time = time;
        _logger = logger;

        _watcher.Arrived += OnDeviceEvent;
        _watcher.Removed += OnDeviceEvent;
    }

    /// <summary>
    /// <see cref="Status"/> changed. Raised on whichever thread made the change, never on a UI
    /// thread by design.
    /// </summary>
    public event EventHandler? StatusChanged;

    /// <summary>A connect loop used up every attempt. Raised once per loop, for a notification.</summary>
    public event EventHandler? ConnectAttemptsExhausted;

    /// <summary>What the engine last did or is doing.</summary>
    public HandoffStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    private bool IsConfigured
    {
        get
        {
            lock (_gate)
            {
                return _speakerId is not null;
            }
        }
    }

    /// <summary>
    /// Watch <paramref name="deviceInstanceId"/> and hand over <paramref name="speakerId"/>, then
    /// reconcile with the device's current state. With either missing the engine goes idle.
    /// </summary>
    public async Task ConfigureAsync(string? deviceInstanceId, string? speakerId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Whatever was running belongs to the old configuration.
        lock (_gate)
        {
            _speakerId = null;
        }

        Start(_ => Task.CompletedTask);
        _watcher.Stop();

        if (string.IsNullOrWhiteSpace(deviceInstanceId) || string.IsNullOrWhiteSpace(speakerId))
        {
            _logger.LogInformation("Not configured; idle.");
            SetStatus(HandoffStatus.NotConfigured);
            return;
        }

        await _watcher.StartAsync(deviceInstanceId, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _speakerId = speakerId;
        }

        StartAutomatic("startup");
    }

    /// <summary>The PC woke from sleep or hibernation. Reconcile with the device's state.</summary>
    public void NotifyResumed()
    {
        if (IsConfigured)
        {
            StartAutomatic("resume");
        }
    }

    /// <summary>Connect now, whether or not the device is here.</summary>
    public void ConnectNow()
    {
        if (IsConfigured)
        {
            _logger.LogInformation("Connect requested by hand.");
            Start(token => ConnectLoopAsync(checkPresence: false, token));
        }
    }

    /// <summary>Disconnect now, once, whether or not the device is here.</summary>
    public void DisconnectNow()
    {
        if (IsConfigured)
        {
            _logger.LogInformation("Disconnect requested by hand.");
            Start(ManualDisconnectAsync);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource operation;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _speakerId = null;
            operation = _operation;
        }

        _watcher.Arrived -= OnDeviceEvent;
        _watcher.Removed -= OnDeviceEvent;

        operation.Cancel();
        operation.Dispose();
    }

    private void OnDeviceEvent(object? sender, EventArgs e)
    {
        if (IsConfigured)
        {
            StartAutomatic("device event");
        }
    }

    private void StartAutomatic(string reason)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Reconciling after {Reason}.", reason);
        }

        Start(ReconcileAsync);
    }

    /// <summary>Cancel the operation in flight and queue <paramref name="operation"/> behind it.</summary>
    private void Start(Func<CancellationToken, Task> operation)
    {
        CancellationTokenSource previousSource;
        var source = new CancellationTokenSource();

        lock (_gate)
        {
            if (_disposed)
            {
                source.Dispose();
                return;
            }

            previousSource = _operation;
            _operation = source;

            // Suspends at once on the previous operation, which is cancelled just below, outside
            // the lock: its unwinding runs inline and may raise events.
            _current = RunAfterAsync(_current, operation, source.Token);
        }

        // Not disposed: the operation that owns it may still be about to read its token, and a
        // token source without a timer holds nothing that needs releasing.
        previousSource.Cancel();
    }

    private async Task RunAfterAsync(Task previous, Func<CancellationToken, Task> operation, CancellationToken token)
    {
        // Never faults: every operation's failures end here.
        await previous.ConfigureAwait(false);

        if (token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await operation(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Superseded by a newer trigger, which now owns the status.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The handoff operation failed.");
        }
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        await DelayAsync(Debounce, token).ConfigureAwait(false);

        if (_watcher.IsPresent)
        {
            _logger.LogInformation("Device present: this PC is active.");
            await ConnectLoopAsync(checkPresence: true, token).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation("Device absent: the other PC is active.");
            await AutomaticDisconnectAsync(token).ConfigureAwait(false);
        }
    }

    private async Task ConnectLoopAsync(bool checkPresence, CancellationToken token)
    {
        var speakerId = SpeakerId();

        if (await _speaker.IsConnectedAsync(speakerId, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            SetStatus(HandoffStatus.Connected);
            return;
        }

        token.ThrowIfCancellationRequested();
        SetStatus(HandoffStatus.Connecting);

        for (var attempt = 0; attempt <= ConnectRetryDelays.Count; attempt++)
        {
            if (attempt > 0)
            {
                await DelayAsync(ConnectRetryDelays[attempt - 1], token).ConfigureAwait(false);

                // Gone between attempts and the removal not delivered yet. The removal will start
                // its own operation and set the status, so this one only stops.
                if (checkPresence && !_watcher.IsPresent)
                {
                    if (_logger.IsEnabled(LogLevel.Information))
                    {
                        _logger.LogInformation("Device gone before connect attempt {Attempt}; stopping.", attempt + 1);
                    }

                    return;
                }
            }

            var result = await _speaker.ConnectAsync(speakerId, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (result == SpeakerResult.Success)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Connected on attempt {Attempt}.", attempt + 1);
                }

                SetStatus(HandoffStatus.Connected);
                return;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Connect attempt {Attempt} of {Attempts}: {Result}.", attempt + 1, ConnectRetryDelays.Count + 1, result);
            }
        }

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning("Could not connect the speaker after {Attempts} attempts.", ConnectRetryDelays.Count + 1);
        }

        SetStatus(HandoffStatus.CouldNotConnect);
        ConnectAttemptsExhausted?.Invoke(this, EventArgs.Empty);
    }

    private async Task AutomaticDisconnectAsync(CancellationToken token)
    {
        var speakerId = SpeakerId();

        if (await _speaker.IsConnectedAsync(speakerId, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            SetStatus(HandoffStatus.Disconnecting);

            var result = await _speaker.DisconnectAsync(speakerId, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (result != SpeakerResult.Success)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Disconnect failed ({Result}); retrying once.", result);
                }

                await DelayAsync(DisconnectRetryDelay, token).ConfigureAwait(false);

                result = await _speaker.DisconnectAsync(speakerId, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (result != SpeakerResult.Success)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning("Disconnect failed again ({Result}); giving up until the next trigger.", result);
                    }
                }
            }
        }

        token.ThrowIfCancellationRequested();
        SetStatus(HandoffStatus.OtherPcActive);
    }

    private async Task ManualDisconnectAsync(CancellationToken token)
    {
        var speakerId = SpeakerId();

        if (await _speaker.IsConnectedAsync(speakerId, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            SetStatus(HandoffStatus.Disconnecting);

            var result = await _speaker.DisconnectAsync(speakerId, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            if (result != SpeakerResult.Success)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning("Disconnect failed ({Result}).", result);
                }


                // Say what is actually true rather than what was asked for.
                var connected = await _speaker.IsConnectedAsync(speakerId, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                SetStatus(connected ? HandoffStatus.Connected : HandoffStatus.Disconnected);
                return;
            }
        }

        token.ThrowIfCancellationRequested();
        SetStatus(HandoffStatus.Disconnected);
    }

    private string SpeakerId()
    {
        lock (_gate)
        {
            // Configuration clears this only after cancelling, so a running operation that reaches
            // here without being cancelled still has one.
            return _speakerId ?? throw new OperationCanceledException();
        }
    }

    /// <summary>
    /// Wait on <see cref="_time"/>. Unlike <c>Task.Delay</c>, the awaiter resumes inline on the
    /// timer's callback (or on whoever cancels), not on a queued continuation.
    /// </summary>
    private async Task DelayAsync(TimeSpan delay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var done = new TaskCompletionSource();
        using var timer = _time.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            done,
            delay,
            Timeout.InfiniteTimeSpan);
        using var registration = token.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            done);

        await done.Task.ConfigureAwait(false);
    }

    private void SetStatus(HandoffStatus status)
    {
        lock (_gate)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Status: {Status}.", status);
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }
}
