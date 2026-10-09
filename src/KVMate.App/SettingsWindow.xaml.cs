using System.Runtime.InteropServices;
using KVMate.App.Startup;
using KVMate.Core.Devices;
using KVMate.Core.Settings;
using KVMate.Core.Speakers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace KVMate.App;

/// <summary>One line in a picker.</summary>
/// <param name="Id">What is saved.</param>
/// <param name="Name">The plain name, saved alongside for display.</param>
/// <param name="Label">What the picker shows.</param>
internal sealed record Choice(string Id, string Name, string Label);

/// <summary>Picks the USB device and the speaker, and sets start with Windows.</summary>
/// <remarks>
/// A new window per opening, closed for good when dismissed. It is opened rarely, and a fresh one
/// lists devices as they are now rather than as they were the first time.
/// </remarks>
internal sealed partial class SettingsWindow : Window
{
    private const int DefaultDpi = 96;

    private readonly IDeviceWatcher _devices;
    private readonly ISpeakerLink _speakers;
    private readonly SettingsStore _store;
    private readonly StartupRegistration _startup;
    private readonly Func<KvmSettings, Task> _apply;
    private readonly ILogger _logger;

    private KvmSettings _settings;

    /// <param name="devices">Lists the present USB devices.</param>
    /// <param name="speakers">Lists the paired speakers.</param>
    /// <param name="store">Where the choice is saved.</param>
    /// <param name="startup">The start-with-Windows entry.</param>
    /// <param name="apply">Hands saved settings to the engine.</param>
    /// <param name="logger">Where failures are reported.</param>
    public SettingsWindow(
        IDeviceWatcher devices,
        ISpeakerLink speakers,
        SettingsStore store,
        StartupRegistration startup,
        Func<KvmSettings, Task> apply,
        ILogger logger)
    {
        _devices = devices;
        _speakers = speakers;
        _store = store;
        _startup = startup;
        _apply = apply;
        _logger = logger;
        _settings = store.Load();

        InitializeComponent();

        StartWithWindowsBox.IsChecked = _startup.IsEnabledForThisCopy();

        SetIconAndSize();

        _ = RefreshAsync();
    }

    private void SetIconAndSize()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }

            // AppWindow sizes are physical pixels; scale so the window is the same size on a
            // high-DPI screen as on a normal one.
            var dpi = NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            var scale = dpi == 0 ? 1.0 : dpi / (double)DefaultDpi;
            AppWindow.Resize(new SizeInt32((int)(620 * scale), (int)(600 * scale)));
        }
        catch (Exception)
        {
            // Decoration and layout only.
        }
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        Problem.IsOpen = false;

        try
        {
            // Off the UI thread: both walks talk to drivers and can take a moment.
            var devicesTask = Task.Run(() => _devices.ListUsbDevicesAsync(CancellationToken.None));
            var speakersTask = Task.Run(() => _speakers.ListSpeakersAsync(CancellationToken.None));

            var devices = await devicesTask;
            var speakers = await speakersTask;

            var deviceChoices = devices
                .Select(device => new Choice(device.InstanceId, device.Name, $"{device.Name}  ·  {device.InstanceId}"))
                .ToList();
            var speakerChoices = speakers
                .Select(speaker => new Choice(speaker.Id, speaker.Name, speaker.IsConnected ? $"{speaker.Name}  (connected here)" : speaker.Name))
                .ToList();

            // The saved device is normally absent on the inactive PC. Keep it on the list so opening
            // the window there does not quietly lose the choice.
            KeepSaved(deviceChoices, _settings.DeviceInstanceId, _settings.DeviceName, "not plugged in now");
            KeepSaved(speakerChoices, _settings.SpeakerId, _settings.SpeakerName, "not found");

            Bind(DevicePicker, deviceChoices, DevicePicker.SelectedItem is Choice device ? device.Id : _settings.DeviceInstanceId);
            Bind(SpeakerPicker, speakerChoices, SpeakerPicker.SelectedItem is Choice speaker ? speaker.Id : _settings.SpeakerId);

            if (speakerChoices.Count == 0)
            {
                ShowProblem("No paired Bluetooth speaker was found. Pair it in Windows Bluetooth settings first.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list devices for the settings window.");
            ShowProblem("Could not list devices: " + ex.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
    }

    private static void KeepSaved(List<Choice> choices, string? id, string? name, string note)
    {
        if (string.IsNullOrWhiteSpace(id) || choices.Exists(choice => string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var plain = string.IsNullOrWhiteSpace(name) ? id : name;
        choices.Insert(0, new Choice(id, plain, $"{plain}  ({note})"));
    }

    private static void Bind(Microsoft.UI.Xaml.Controls.ComboBox picker, List<Choice> choices, string? selectedId)
    {
        picker.ItemsSource = choices;
        picker.SelectedItem = choices.Find(choice => string.Equals(choice.Id, selectedId, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowProblem(string message)
    {
        Problem.Message = message;
        Problem.IsOpen = true;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var device = DevicePicker.SelectedItem as Choice;
        var speaker = SpeakerPicker.SelectedItem as Choice;
        var startWithWindows = StartWithWindowsBox.IsChecked == true;

        var updated = _settings with
        {
            DeviceInstanceId = device?.Id,
            DeviceName = device?.Name,
            SpeakerId = speaker?.Id,
            SpeakerName = speaker?.Name,
            StartWithWindows = startWithWindows,
        };

        try
        {
            _store.Save(updated);
            _settings = updated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save settings.");
            ShowProblem("Could not save settings: " + ex.Message);
            return;
        }

        try
        {
            _startup.SetEnabled(startWithWindows);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not change start with Windows.");
            ShowProblem("Settings saved, but start with Windows could not be changed: " + ex.Message);
            return;
        }

        try
        {
            await _apply(updated);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not apply settings.");
        }

        Close();
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static partial uint GetDpiForWindow(IntPtr hwnd);
    }
}
