namespace KVMate.App;

/// <summary>
/// Makes sure only one copy of the app runs, and lets a second copy ask the first to open its
/// settings window instead of starting.
/// </summary>
/// <remarks>
/// <para>
/// Two copies would each run an engine against the same speaker, so one could disconnect what the
/// other had just connected.
/// </para>
/// <para>
/// Both names are in the <c>Local\</c> namespace, so the scope is one logon session.
/// </para>
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\KVMate.SingleInstance";
    private const string ActivationEventName = @"Local\KVMate.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activation;
    private readonly CancellationTokenSource _stopping = new();

    private Thread? _listener;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activation)
    {
        _mutex = mutex;
        _activation = activation;
    }

    /// <summary>Claim the right to be the running copy, or return null if another copy holds it.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        // Auto-reset, so one request opens the window once rather than on every later wait.
        var activation = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivationEventName);

        return new SingleInstance(mutex, activation);
    }

    /// <summary>Ask the running copy to open its settings window.</summary>
    /// <returns>False if signalling failed. The second copy exits either way.</returns>
    public static bool SignalRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivationEventName, out var activation))
            {
                return false;
            }

            using (activation)
            {
                return activation.Set();
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Run <paramref name="onActivationRequested"/> whenever another copy asks.</summary>
    /// <param name="onActivationRequested">
    /// Invoked on a background thread, so it must marshal to the UI thread itself.
    /// </param>
    public void ListenForActivation(Action onActivationRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivationRequested);

        // A dedicated thread rather than a task: it spends its whole life blocked in a wait, which
        // is what the thread pool must not be used for.
        _listener = new Thread(() => Listen(onActivationRequested))
        {
            IsBackground = true,
            Name = "KVMate activation listener",
        };

        _listener.Start();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stopping.Cancel();
        _listener?.Join(TimeSpan.FromSeconds(1));

        // Released before the handle closes, so a copy started straight afterwards sees the name
        // as free rather than abandoned.
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread. Nothing to undo.
        }

        _mutex.Dispose();
        _activation.Dispose();
        _stopping.Dispose();
    }

    private void Listen(Action onActivationRequested)
    {
        var handles = new WaitHandle[] { _activation, _stopping.Token.WaitHandle };

        while (true)
        {
            int signalled;
            try
            {
                signalled = WaitHandle.WaitAny(handles);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Index 1 is the shutdown signal.
            if (signalled != 0)
            {
                return;
            }

            onActivationRequested();
        }
    }
}
