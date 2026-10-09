# KVMate

A Windows tray app that brings a Bluetooth speaker along when a KVM switch moves to another PC.
The KVM carries keyboard, mouse and display over, but a Bluetooth speaker stays connected to
whichever PC last held it and refuses the other. KVMate watches one USB device that travels with
the KVM (normally the keyboard): when it appears, KVMate connects the speaker to this PC; when it
leaves, KVMate lets the speaker go so the other PC can take it.

Run it on both PCs. They never talk to each other; each reacts only to its own USB events.

A sibling of Dongled, built on the same stack. Dongled can then make the speaker the default
output once it appears.

## Usage

1. Pair the speaker with both PCs in Windows Bluetooth settings. KVMate never pairs.
2. Unzip a release on each PC and run `KVMate.exe`. The settings window opens on first run.
3. With the KVM pointing at this PC, pick the keyboard (or another USB device that moves with the
   KVM) and the speaker, tick *Start with Windows* if wanted, and save.

From then on KVMate starts in the notification area. Its menu shows the current status and has
*Connect now*, *Disconnect now*, *Settings…* and *Exit*. A manual action holds until the next
switch. Launching `KVMate.exe` again opens the running copy's settings.

KVMate also checks on startup and after the PC wakes from sleep. If every connect attempt fails it
shows one notification; the usual reason is that the other PC still holds the speaker.

Settings are in `%LOCALAPPDATA%\KVMate\settings.json` and logs in `%LOCALAPPDATA%\KVMate\logs`
(two files of at most 1 MB each).

### How it works

- The switch is detected with a `Windows.Devices.Enumeration` device watcher on one USB device
  instance id. A change must hold for 300 ms before KVMate acts on it.
- The speaker is connected and disconnected the way the Connect button in Sound settings does it:
  the Bluetooth audio driver's one-shot reconnect and disconnect properties
  (`KSPROPSETID_BtAudio`), sent through `IKsControl` on the speaker's kernel-streaming filter, found
  by walking each playback endpoint's device topology (the approach of
  [ToothTray](https://github.com/m2jean/ToothTray)). No elevation is needed and the audio endpoint
  survives, so Dongled's rules keep matching it.
- A connect is tried at once and retried after 0.5 s, 1 s and 2 s, which covers the old PC taking a
  moment to let go. A disconnect is retried once after 1 s.

## Layout

| Path | What |
| --- | --- |
| `src/KVMate.Core` | Device watcher, speaker link, handoff engine, settings, power events, logging. No UI. |
| `src/KVMate.App` | WinUI 3 tray app (unpackaged, self-contained). Produces `KVMate.exe`. |
| `tests/KVMate.Core.Tests` | xUnit v3 tests for the engine and the settings store. |
| `tools/KVMate.Probe` | Console tool over Core's real watcher and speaker link, for checking hardware. |
| `build/release.ps1` | Publishes and zips a release, locally or in CI. |

## Build

```
dotnet build KVMate.slnx
dotnet test KVMate.slnx
```

The SDK is pinned in `global.json`. Package versions live in `Directory.Packages.props`, and the
generated `packages.lock.json` files are committed.

## Probe tool

`tools/KVMate.Probe` is in the solution and uses the same `UsbDeviceWatcher` and `KsSpeakerLink`
as the app, so it checks exactly the code the app relies on. Use it to try a new speaker or USB
device before trusting the app with it.

```
dotnet run --project tools/KVMate.Probe -- list-usb
dotnet run --project tools/KVMate.Probe -- list-speakers
dotnet run --project tools/KVMate.Probe -- connect "{speaker-id}"
dotnet run --project tools/KVMate.Probe -- disconnect "{speaker-id}"
dotnet run --project tools/KVMate.Probe -- watch "USB\VID_xxxx&PID_xxxx\..."
```

| Command | What it does |
| --- | --- |
| `list-usb` | Present USB devices: name and instance id. |
| `list-speakers` | Paired Bluetooth audio devices: id, connected state and name. |
| `connect <id>` | Sends the reconnect request, waits up to 3 s for the speaker, prints the outcome. |
| `disconnect <id>` | Same for disconnect. |
| `watch <instance-id>` | Prints arrive, remove and resume events until Ctrl+C. |

Diagnostic lines, including the driver's answer to each request, go to standard error. Quote
instance ids, since they contain `&`.

## Release

Every push to `master` that passes CI publishes a GitHub Release: the version is `version.json`'s
`major.minor` plus the workflow run number (for example `1.0.37`), the asset is
`KVMate-<version>-win-x64.zip` (self-contained), and the notes are generated from the commits.
To build the same zip locally:

```
./build/release.ps1 -Version 1.0.0
```

Not signed, no installer, no auto-update.
