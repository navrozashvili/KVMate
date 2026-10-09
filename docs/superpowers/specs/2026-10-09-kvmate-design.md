# KVMate design

Date: 2026-10-09
Status: approved in conversation, pending review of this document

## Problem

A KVM switch moves keyboard, mouse and display between two Windows PCs. A Bluetooth speaker does
not follow: it stays connected to whichever PC last held it. The speaker is paired with both PCs
but accepts only one host at a time; while connected to one, a connection attempt from the other is
rejected.

## Goal

Press the KVM button and, within a few seconds and without further input, the speaker is
connected to the PC that now has the keyboard.

## Decisions

| Topic | Decision |
| --- | --- |
| Switch signal | A user-picked USB device (normally the keyboard). Present means this PC is active. |
| Topology | KVMate runs on both PCs. The PCs never talk to each other; each reacts only to its own USB events. |
| Pairing | Already done on both PCs. KVMate only connects and disconnects; it never pairs. |
| Default audio output | Out of scope. Dongled handles it once the speaker appears. |
| Connect/disconnect mechanism | The Bluetooth audio driver's one-shot reconnect/disconnect kernel-streaming properties via `IKsControl`, the same path as Sound settings' Connect button (as in ToothTray). No elevation; the audio endpoint survives, so Dongled's rules keep matching it. |
| Device detection | `Windows.Devices.Enumeration.DeviceWatcher`, filtered to one device instance ID. |
| UI | Tray icon plus a small settings window. |
| Startup and wake | Reconcile with the current device state (connect if present, disconnect if absent). |
| Release | GitHub Actions publishes a self-contained win-x64 zip as a GitHub Release on every push to `master`. |

## Components

### KVMate.Core

No UI. Every Windows-facing piece sits behind an interface so the policy is testable with fakes.

- **`IDeviceWatcher` / `UsbDeviceWatcher`**: reports presence of the configured device as an
  initial value plus `Arrived` and `Removed` events, and enumerates present USB devices
  (instance ID and friendly name) for the settings picker.
- **`ISpeakerLink` / `KsSpeakerLink`**: `ConnectAsync`, `DisconnectAsync`, `IsConnected`, and
  enumeration of paired Bluetooth audio devices (ID and name). Connect returns a result that
  distinguishes success from rejection or failure; it never throws to the caller.
- **`HandoffEngine`**: the policy (see Behaviour). Depends only on the two interfaces and
  `TimeProvider`. Publishes a `Status` value and a change event.
- **`SettingsStore`**: `%LOCALAPPDATA%\KVMate\settings.json` holding the device instance ID, the
  speaker ID and the start-with-Windows flag. Writes go to a temporary file that then replaces the
  real one. A missing or corrupt file yields defaults (not configured).
- **`PowerEvents`**: raises `Resumed` after wake from sleep or hibernate.

### KVMate.App

- Tray icon (H.NotifyIcon.WinUI) with a status line and the menu items *Connect now*,
  *Disconnect now*, *Settings…* and *Exit*.
- Settings window: a USB device picker, a speaker picker and a *Start with Windows* checkbox.
  Start with Windows writes or removes a value under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` pointing at the running executable.
- A single-instance guard (named mutex) before the XAML runtime starts, as in Dongled.
- Logging to `%LOCALAPPDATA%\KVMate\logs`, rolled by size with a small cap.

## Behaviour

The engine runs one operation at a time. Every new trigger cancels the operation in flight.

**Debounce.** A device-state change must be stable for 300 ms before the engine acts. A keyboard
often enumerates as several devices and can flicker during a switch.

**Device present** (arrival, or present at startup or resume):

1. Cancel any operation in flight.
2. If the speaker is already connected here: status *Connected*, done.
3. Attempt to connect. On rejection or failure, retry after 0.5 s, then 1 s, then 2 s: four
   attempts over about 3.5 s.
4. Before each retry, confirm the device is still present; if not, stop without changing status.
5. Success: status *Connected*. All attempts failed: status *Couldn't connect* and one tray
   notification.

**Device absent** (removal, or absent at startup or resume):

1. Cancel any operation in flight, including a connect loop still retrying.
2. If the speaker is connected here, disconnect. On failure, retry once after 1 s, then log and
   give up.
3. Status *Disconnected (other PC active)*.

Only the active PC ever attempts to connect, so the two PCs never compete. The one-host rejection
only shows up as the new PC's first attempts failing until the old PC lets go, which the retry
schedule covers.

**Manual actions.** *Connect now* runs the same connect loop regardless of device presence: it
skips the step 4 presence check and is not debounced.
*Disconnect now* disconnects once. A manual action holds until the next real device event, which
resumes automatic behaviour.

**Not configured.** With no device or no speaker chosen, the engine is idle, the status is
*Not configured: open Settings*, and the settings window opens on first run.

**Errors.** Every Windows call is wrapped. Failures become a status and a log line, never a crash.

### Known limitation

Some speakers reconnect by themselves to their last host when powered on. KVMate corrects this on
the next trigger (switch, startup or wake) but does not watch for or fight it continuously.

## Testing

- **`HandoffEngine` unit tests** with a fake watcher, a fake speaker link and
  `FakeTimeProvider`: debounce, the exact retry schedule and attempt count, cancellation when the
  device leaves mid-retry, the already-connected short circuit, disconnect with its single retry,
  startup and resume reconciliation in both states, manual actions holding until the next event,
  and the not-configured idle state.
- **`SettingsStore` tests**: round trip, missing file, corrupt file, and replacement without a
  partial write.
- **Hardware proof of concept first.** Before the engine is built on it, `KsSpeakerLink` is
  checked against the real speaker in a small throwaway console run: connect, disconnect, and the
  rejection seen when the other PC holds the speaker. If the `IKsControl` path does not work with
  this speaker, the design is revisited before continuing.
- `UsbDeviceWatcher`, `KsSpeakerLink` and the tray UI are checked by hand on the real hardware.
  They are thin wrappers and are not unit-tested.

## Release pipeline

One workflow, `.github/workflows/ci.yml`, with read-only permissions by default.

- **Pull request:** locked restore, Release build, tests, `dotnet format --verify-no-changes`.
- **Push to `master`:** the same checks, then a release job with `contents: write`:
  1. Version `major.minor` from `version.json` (initially `1.0`), patch from the run number,
     giving for example `1.0.37`.
  2. `dotnet publish` of `KVMate.App`, Release, self-contained, win-x64, zipped as
     `KVMate-<version>-win-x64.zip`.
  3. Tag `v<version>` and a GitHub Release with the zip attached and generated notes.

Out of scope: code signing, an installer, auto-update.

## Out of scope

- Setting the default audio output (Dongled does this).
- Pairing, or more than one speaker or watched device.
- Any communication between the two PCs.
