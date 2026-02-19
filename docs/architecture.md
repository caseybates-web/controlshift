# ControlShift Architecture

## Overview

ControlShift is a Windows desktop application that solves the XInput Player Index lock-in problem on gaming handhelds. The built-in gamepad permanently holds Player 1 (slot 0), preventing external Bluetooth/USB controllers from being recognized as the primary controller by games.

## How It Works

1. **ViGEmBus** creates a virtual XInput controller at the desired Player Index slot
2. **HidHide** hides the physical device from game processes (only ControlShift can see it)
3. ControlShift **forwards HID input** from the physical device to the virtual controller
4. Games see the virtual controller at the correct slot — unaware of the swap
5. On exit or revert: HidHide rules cleared, virtual controllers removed, physical devices restored

## Project Structure

```
ControlShift.sln / ControlShift.slnx
├── src/ControlShift.Core/              # Controller logic (no UI dependency)
│   ├── Enumeration/                    # XInput + HID device discovery
│   ├── Devices/                        # Fingerprinting, matching, vendor database
│   ├── Models/                         # Data models and profile schema
│   ├── Forwarding/                     # (Phase 2) Input forwarding via ViGEm
│   └── Profiles/                       # (Phase 2) Per-game profile management
├── src/ControlShift.App/               # WinUI 3 desktop application
│   ├── Controls/                       # SlotCard — per-controller card UI
│   ├── ViewModels/                     # MainViewModel, SlotViewModel (MVVM)
│   ├── Converters/                     # XAML value converters
│   ├── SplashWindow.xaml/.cs           # Xbox-themed boot animation
│   ├── MainWindow.xaml/.cs             # Standalone 480x600 window with 4 slot cards
│   └── App.xaml/.cs                    # Entry point: splash → main window
├── src/ControlShift.Installer/         # WiX/MSIX packaging (placeholder)
├── tests/ControlShift.Core.Tests/      # Unit tests (xUnit + Moq)
├── devices/known-devices.json          # VID/PID → device name database
├── devices/known-vendors.json          # VID → vendor brand database
├── profiles/examples/                  # Example per-game profiles
└── .github/workflows/                  # CI: build, test, app packaging
```

## App Architecture

### Startup Flow
`Program.Main` → `App.OnLaunched` → `SplashWindow` (2.5s Xbox animation) → `MainWindow`

### MainWindow
A standalone 480×600 window (DPI-scaled) displaying four `SlotCard` controls — one per XInput slot. Uses a `DispatcherTimer` (5s polling interval) to refresh controller state via `MainViewModel.RefreshAsync()`.

### SlotCard
A reusable `UserControl` that renders a single controller slot:
- Vendor brand badge and device name
- VID:PID identifier
- Battery level glyph (Segoe Fluent Icons)
- Connection type indicator (Bluetooth/USB/Integrated)
- Rumble-on-tap feedback via Vortice.XInput

### ViewModels
- **MainViewModel**: Owns `ObservableCollection<SlotViewModel>`, orchestrates refresh using `IXInputEnumerator` + `IHidEnumerator` + `IControllerMatcher`
- **SlotViewModel**: Per-slot state (device name, vendor, battery, VID/PID, connection type) with `INotifyPropertyChanged`. Includes `BatteryGlyph` computed property mapping battery percentage to Segoe Fluent Icons codepoints.

## Core Library

### Enumeration
- `IXInputEnumerator` / `XInputEnumerator`: Discovers connected XInput controllers (slots 0–3) via Vortice.XInput
- `IHidEnumerator` / `HidEnumerator`: Discovers HID gaming devices with VID/PID, battery, and connection info

### Devices
- `IControllerMatcher` / `ControllerMatcher`: Matches XInput slots to HID devices using heuristics
- `IDeviceFingerprinter` / `DeviceFingerprinter`: Identifies devices using VID/PID + known-devices database
- `IVendorDatabase` / `VendorDatabase`: Maps VID to vendor brand names (Xbox, PlayStation, etc.)
- `KnownDeviceDatabase`: Loads and queries `devices/known-devices.json`

### Models
Core data types: `ControllerInfo`, `XInputSlot`, `ConnectionType`, `Profile`, `SlotAssignment`, etc.

## Key Design Decisions

- **Unpackaged app** (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`): Avoids MSIX signing complexity; allows direct exe execution and admin elevation for driver operations
- **Standalone window** (not system tray): Provides a visible, always-accessible UI for controller management during gaming sessions
- **Interface-based enumeration**: `IXInputEnumerator`, `IHidEnumerator`, and `IControllerMatcher` enable mock-based unit testing
- **Heuristic XInput-to-HID matching**: XInput doesn't expose VID/PID, so the matcher uses known-devices database, battery type inference, and connection type correlation
- **Vortice.XInput**: Lightweight DirectX binding for XInput state queries and rumble motor control

## Color Theme

Xbox-inspired dark theme using `Xb*` resource tokens:
- `XbBackground` (#1A1A1A), `XbCard` (#2D2D2D), `XbAccent` (#107C10)
- `XbText` (#FFFFFF), `XbTextMuted` (#ABABAB), `XbBorder` (#404040)

## Phase Roadmap

- **Phase 1** (current): Enumeration, fingerprinting, matching, standalone window UI, splash screen
- **Phase 2**: ViGEm virtual controllers, HidHide suppression, input forwarding, drag-to-reorder
- **Phase 3**: Per-game profiles, WMI process watcher, anticheat auto-revert
