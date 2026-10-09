# KVMate

A Windows tray app that brings a Bluetooth speaker along when a KVM switch moves to another PC.
The KVM carries keyboard, mouse and display over, but a Bluetooth pairing stays with the machine
that made it. KVMate notices the switch and hands the speaker to the PC that is now active.

A sibling of Dongled, built on the same stack.

## Layout

| Path | What |
| --- | --- |
| `src/KVMate.Core` | Switch detection and the Bluetooth handoff. No UI. |
| `src/KVMate.App` | WinUI 3 tray app (unpackaged, self-contained). Produces `KVMate.exe`. |
| `tests/KVMate.Core.Tests` | xUnit v3 tests for Core. |

## Build

```
dotnet build KVMate.slnx
dotnet test KVMate.slnx
```

The SDK is pinned in `global.json`. Package versions live in `Directory.Packages.props`, and the
generated `packages.lock.json` files are committed.
