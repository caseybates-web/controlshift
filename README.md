# ControlShift

[![CI](https://github.com/caseybates-web/controlshift/actions/workflows/ci.yml/badge.svg)](https://github.com/caseybates-web/controlshift/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)
![Windows](https://img.shields.io/badge/Windows-10%2B-0078D6)

**Reorder XInput controller Player Index on Windows gaming handhelds.**

## The Problem

Gaming handhelds like the ROG Ally, Legion Go, and Steam Deck have a built-in gamepad that permanently claims Player 1 (XInput slot 0). When you connect an external Bluetooth or USB controller, it gets assigned Player 2, 3, or 4 — and most games only respond to Player 1. There's no built-in way to swap slots in Windows.

ControlShift fixes this.

## How It Works

1. **ViGEmBus** creates a virtual XInput controller at the desired Player Index slot
2. **HidHide** hides the physical device from game processes (only ControlShift can see it)
3. ControlShift **forwards HID input** from the physical device to the virtual controller
4. Games see the virtual controller at the correct slot — unaware of the swap
5. On exit: HidHide rules are cleared, virtual controllers removed, physical devices restored

## Features

- **Xbox-themed UI** — Dark theme with Xbox green accents, professional splash screen animation
- **Controller cards** — Each XInput slot displayed as a rich card with device name, vendor badge, and VID:PID
- **Battery glyphs** — Real-time battery level icons for wireless controllers
- **Rumble feedback** — Tap a controller card to identify it with haptic vibration
- **Connection detection** — Distinguishes Bluetooth, USB, and integrated gamepads
- **Vendor recognition** — Identifies Xbox, PlayStation, Nintendo, ASUS, Lenovo, Valve, and more
- **Device database** — Pre-loaded fingerprints for popular gaming handhelds

## Supported Devices

| Device | VID:PID |
|--------|---------|
| ASUS ROG Ally | `0B05:1ABE` |
| ASUS ROG Ally X | `0B05:1B4C` |
| Lenovo Legion Go | `17EF:6178` |
| GPD Win 4 | `2833:0004` |
| GPD Win Max 2 | `2833:0003` |
| Steam Deck (XInput) | `28DE:11FF` |
| Ayaneo 2 | `2810:2003` |
| OneXPlayer Mini | `045E:028E` |

Additional controllers are identified via vendor database (12 brands) and HID enumeration.

## Screenshots

> Coming soon — the app is in active development.

## Getting Started

### Prerequisites

- **Windows 10** build 19041 or later
- [**.NET 8 SDK**](https://dotnet.microsoft.com/download/dotnet/8.0) (8.0.400+)
- [**ViGEmBus**](https://github.com/nefarius/ViGEmBus/releases) driver (virtual controller creation)
- [**HidHide**](https://github.com/nefarius/HidHide/releases) driver (device suppression)

### Build & Run

```bash
# Restore dependencies
dotnet restore

# Build everything
dotnet build

# Run the app
dotnet run --project src/ControlShift.App

# Run tests
dotnet test
```

### CI

All builds run on GitHub Actions (`windows-latest`). The CI pipeline builds Core + App, runs xUnit tests in both Debug and Release, and packages the WinUI 3 app for x64.

## Project Structure

```
ControlShift.sln
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
│   ├── SplashWindow.xaml               # Xbox-themed boot animation
│   └── MainWindow.xaml                 # Standalone 480×600 window with 4 slot cards
├── src/ControlShift.Installer/         # Installer packaging (placeholder)
├── tests/ControlShift.Core.Tests/      # Unit tests (xUnit + Moq)
├── devices/known-devices.json          # VID/PID → device name database
├── devices/known-vendors.json          # VID → vendor brand database
└── profiles/examples/                  # Example per-game profiles
```

## Roadmap

### Phase 1 — Enumeration & UI ✅
- [x] XInput slot enumeration (0–3)
- [x] HID device discovery with VID/PID
- [x] Heuristic XInput-to-HID matching
- [x] Device fingerprinting via known-devices database
- [x] Vendor brand identification
- [x] Standalone MainWindow with SlotCard UI
- [x] Xbox-themed splash screen
- [x] Battery level glyphs
- [x] Rumble-on-tap feedback

### Phase 2 — Controller Reordering
- [ ] ViGEm virtual controller creation
- [ ] HidHide device suppression
- [ ] Input forwarding (125Hz HID → ViGEm)
- [ ] Drag-to-reorder UI
- [ ] "Revert All" safety button

### Phase 3 — Per-Game Profiles
- [ ] Profile save/load (JSON)
- [ ] WMI process watcher for auto-apply
- [ ] Anticheat-safe auto-revert
- [ ] WiX installer

## Tech Stack

| Component | Technology |
|-----------|-----------|
| UI Framework | WinUI 3 (Windows App SDK 1.6) |
| Runtime | .NET 8, C# 12 |
| XInput | Vortice.XInput 3.8 |
| Virtual Controllers | ViGEmBus (Nefarius.ViGEm.Client) |
| Device Suppression | HidHide (Nefarius.Drivers.HidHide) |
| HID Access | HidSharp |
| Testing | xUnit, Moq, FluentAssertions |
| CI/CD | GitHub Actions |
| Packaging | Unpackaged (self-contained, no MSIX) |

## License

[MIT](LICENSE) — Copyright 2026 caseybates-web
