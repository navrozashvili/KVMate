using KVMate.Core.Devices;
using KVMate.Core.Speakers;
using KVMate.Probe;
using Microsoft.Extensions.Logging;

// A thin console front end over the same UsbDeviceWatcher and KsSpeakerLink the app uses, so a run
// of this on real hardware checks exactly the code the app depends on.

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Debug);
    builder.AddProvider(new ConsoleLoggerProvider());
});

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Let watch end tidily instead of killing the process mid-callback.
    e.Cancel = true;
    cancel.Cancel();
};

var command = args.Length > 0 ? args[0].ToUpperInvariant() : string.Empty;

try
{
    return command switch
    {
        "LIST-USB" => await ListUsbAsync(),
        "LIST-SPEAKERS" => await ListSpeakersAsync(),
        "CONNECT" when args.Length == 2 => await SendAsync(args[1], connect: true),
        "DISCONNECT" when args.Length == 2 => await SendAsync(args[1], connect: false),
        "WATCH" when args.Length == 2 => await WatchAsync(args[1]),
        _ => Usage(),
    };
}
catch (OperationCanceledException)
{
    return 0;
}

async Task<int> ListUsbAsync()
{
    using var watcher = new UsbDeviceWatcher(loggerFactory.CreateLogger<UsbDeviceWatcher>());
    var devices = await watcher.ListUsbDevicesAsync(cancel.Token);

    foreach (var device in devices)
    {
        Console.WriteLine($"{device.Name}");
        Console.WriteLine($"    {device.InstanceId}");
    }

    Console.WriteLine($"{devices.Count} present USB device(s).");
    return 0;
}

async Task<int> ListSpeakersAsync()
{
    var link = new KsSpeakerLink(loggerFactory.CreateLogger<KsSpeakerLink>());
    var speakers = await link.ListSpeakersAsync(cancel.Token);

    foreach (var speaker in speakers)
    {
        Console.WriteLine($"{speaker.Id}  {(speaker.IsConnected ? "connected   " : "disconnected")}  {speaker.Name}");
    }

    Console.WriteLine($"{speakers.Count} paired Bluetooth audio device(s).");
    return 0;
}

async Task<int> SendAsync(string speakerId, bool connect)
{
    var link = new KsSpeakerLink(loggerFactory.CreateLogger<KsSpeakerLink>());
    var started = TimeProvider.System.GetTimestamp();

    var result = connect
        ? await link.ConnectAsync(speakerId, cancel.Token)
        : await link.DisconnectAsync(speakerId, cancel.Token);

    var elapsed = TimeProvider.System.GetElapsedTime(started);
    var connected = await link.IsConnectedAsync(speakerId, cancel.Token);

    Console.WriteLine($"{(connect ? "connect" : "disconnect")}: {result} after {elapsed.TotalMilliseconds:F0} ms; now {(connected ? "connected" : "disconnected")}.");
    return result == SpeakerResult.Success ? 0 : 1;
}

async Task<int> WatchAsync(string instanceId)
{
    using var watcher = new UsbDeviceWatcher(loggerFactory.CreateLogger<UsbDeviceWatcher>());

    watcher.Arrived += (_, _) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  arrived");
    watcher.Removed += (_, _) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  removed");

    await watcher.StartAsync(instanceId, cancel.Token);
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  initially {(watcher.IsPresent ? "present" : "absent")}. Ctrl+C to stop.");

    await Task.Delay(Timeout.Infinite, cancel.Token);
    return 0;
}

static int Usage()
{
    Console.WriteLine("""
        KVMate.Probe: checks KVMate's device watcher and speaker link on real hardware.

          list-usb                    Present USB devices: name and instance id.
          list-speakers               Paired Bluetooth audio devices: id, state and name.
          connect <speaker-id>        Ask the speaker to connect to this PC and report the outcome.
          disconnect <speaker-id>     Ask the speaker to disconnect from this PC and report the outcome.
          watch <usb-instance-id>     Print arrive and remove events for one USB device until Ctrl+C.

        Quote instance ids: they contain '&'.
        """);
    return 2;
}
