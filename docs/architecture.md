# ControlShift Architecture

## Overview

ControlShift is a Windows system tray application that solves the XInput Player Index lock-in problem on gaming handhelds. The built-in gamepad permanently holds Player 1 (slot 0), preventing external Bluetooth/USB controllers from being recognized as the primary controller by games.

## How It Works

1. **ViGEmBus** creates a virtual XInput controller at the desired Player Index slot
2. **HidHide** hides the physical device from game processes (only ControlShift can see it)
3. ControlShift **forwards HID input** from the physical device to the virtual controller
4. Games see the virtual controller at the correct slot — unaware of the swap
5. On exit or revert: HidHide rules cleared, virtual controllers removed, physical devices restored

## Project Structure

```
ControlShift.sln
├── src/ControlShift.Core/          # Controller logic (no UI dependency)
│   ├── Enumeration/                # XInput + HID device discovery
│   ├── Devices/                    # Fingerprinting, ViGEm, HidHide wrappers
│   └── Models/                     # Data models and profile schema
├── src/ControlShift.App/           # WinUI 3 tray application
│   ├── Controls/                   # Reusable UI controls (SlotCard)
│   ├── ViewModels/                 # MVVM view models
│   ├── Services/                   # Tray icon, etc.
│   └── Converters/                 # XAML value converters
├── src/ControlShift.Installer/     # WiX/MSIX packaging (placeholder)
├── tests/ControlShift.Core.Tests/  # Unit tests (xUnit + Moq)
├── devices/known-devices.json      # VID/PID database
└── profiles/examples/              # Example per-game profiles
```

## Key Design Decisions

- **Unpackaged app** (`WindowsPackageType=None`): Avoids MSIX signing complexity; allows direct exe execution and admin elevation for driver operations
- **H.NotifyIcon.WinUI**: WinUI 3 has no native tray icon support; this is the standard community library
- **Interface-based enumeration**: `IXInputEnumerator` and `IHidEnumerator` enable mock-based unit testing
- **Heuristic XInput-to-HID matching**: XInput doesn't expose VID/PID, so the fingerprinter uses known-devices database and battery type inference

## Phase Roadmap

- **Phase 1** (current): Enumeration, fingerprinting, tray popup UI
- **Phase 2**: ViGEm virtual controllers, HidHide suppression, input forwarding, drag-to-reorder
- **Phase 3**: Per-game profiles, WMI process watcher, anticheat auto-revert
