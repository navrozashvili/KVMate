using System.Diagnostics.CodeAnalysis;
using KVMate.App.Startup;
using KVMate.App.Tray;
using KVMate.Core;
using KVMate.Core.Devices;
using KVMate.Core.Handoff;
using KVMate.Core.Logging;
using KVMate.Core.Power;
using KVMate.Core.Settings;
using KVMate.Core.Speakers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace KVMate.App;

/// <summary>
/// The application object: builds the host, puts up the tray icon, and wires the engine to it.
/// </summary>
/// <remarks>
/// <para>
/// There is no main window. The app lives in the notification area and opens the settings window
/// on first run, from the tray menu, or when a second copy is launched.
/// </para>
/// <para>
/// The engine raises its events on whatever thread it is on. Everything that touches the tray or
/// a window is marshalled to the UI thread here, and calls into the engine from the UI go to the
/// thread pool, because an engine step can run inline in its caller.
/// </para>
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "An Application is created by the XAML runtime and nothing ever disposes one. What it owns is disposed on the one path that ends the process, in ExitApp.")]
public partial class App : Application
{
    private readonly SingleInstance? _instance;
    private readonly StartupRegistration _startup = StartupRegistration.ForThisProcess();

    private IHost? _host;
    private ILogger<App>? _logger;
    private DispatcherQueue? _dispatcher;
    private HandoffEngine? _engine;
    private PowerEvents? _power;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _exiting;

    /// <summary>
    /// Used only by the XAML-generated entry point, which this build replaces and never runs.
    /// </summary>
    public App()
        : this(null)
    {
    }

    /// <param name="instance">The single-instance claim taken in <see cref="Program"/>.</param>
    internal App(SingleInstance? instance)
    {
        _instance = instance;
        InitializeComponent();

        // Closing the settings window must not end the app; only Exit does.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _host = BuildHost();

        var services = _host.Services;
        _logger = services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("KVMate starting.");

        _engine = services.GetRequiredService<HandoffEngine>();
        _engine.StatusChanged += (_, _) => OnUi(ShowStatus);
        _engine.ConnectAttemptsExhausted += (_, _) => OnUi(() =>
            _tray?.Notify("KVMate", "Couldn't connect the speaker. It may still be connected to the other PC."));

        _power = services.GetRequiredService<PowerEvents>();
        _power.Resumed += (_, _) => _engine.NotifyResumed();
        _power.Start();

        _tray = new TrayIcon(
            Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico"),
            connectNow: () => Task.Run(() => _engine.ConnectNow()),
            disconnectNow: () => Task.Run(() => _engine.DisconnectNow()),
            openSettings: ShowSettings,
            exit: ExitApp);

        var trayUp = _tray.TryCreate();
        ShowStatus();

        _instance?.ListenForActivation(() => OnUi(ShowSettings));

        var settings = services.GetRequiredService<SettingsStore>().Load();
        _ = ApplyAsync(settings);

        // First run, or a tray that could not be created: a process with nothing on screen cannot
        // be found or stopped, so the window opens.
        if (!settings.IsConfigured || !trayUp)
        {
            ShowSettings();
        }
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "AddProvider hands the provider to the logger factory, which disposes it with the host.")]
    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Services.AddKvMateCore();

        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddProvider(new FileLoggerProvider(StoragePaths.LogDirectory, TimeProvider.System));

        return builder.Build();
    }

    /// <summary>Hand the engine a configuration. Never throws.</summary>
    private Task ApplyAsync(KvmSettings settings) => Task.Run(async () =>
    {
        try
        {
            await _engine!.ConfigureAsync(settings.DeviceInstanceId, settings.SpeakerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not start watching the configured device.");
        }
    });

    private void ShowStatus()
    {
        if (_tray is null || _engine is null)
        {
            return;
        }

        _tray.ShowStatus(_engine.Status switch
        {
            HandoffStatus.NotConfigured => "Not configured: open Settings",
            HandoffStatus.Connecting => "Connecting…",
            HandoffStatus.Connected => "Connected",
            HandoffStatus.CouldNotConnect => "Couldn't connect",
            HandoffStatus.Disconnecting => "Disconnecting…",
            HandoffStatus.OtherPcActive => "Disconnected (other PC active)",
            HandoffStatus.Disconnected => "Disconnected",
            _ => _engine.Status.ToString(),
        });
    }

    private void ShowSettings()
    {
        if (_exiting || _host is null)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var services = _host.Services;

        try
        {
            _settingsWindow = new SettingsWindow(
                services.GetRequiredService<IDeviceWatcher>(),
                services.GetRequiredService<ISpeakerLink>(),
                services.GetRequiredService<SettingsStore>(),
                _startup,
                ApplyAsync,
                services.GetRequiredService<ILogger<SettingsWindow>>());

            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Could not open the settings window.");
            _settingsWindow = null;
        }
    }

    private void OnUi(Action action)
    {
        _dispatcher?.TryEnqueue(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "A UI update failed.");
            }
        });
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _logger?.LogInformation("KVMate exiting.");

        try
        {
            _settingsWindow?.Close();
            _power?.Dispose();
            _engine?.Dispose();
            _tray?.Dispose();
            _host?.Dispose();
        }
        catch (Exception)
        {
            // Exiting untidily is better than refusing to exit.
        }
        finally
        {
            Exit();
        }
    }
}
