\# ControlShift



A lightweight Windows system tray app that lets users reorder XInput controller Player Index assignments on gaming handhelds. Solves the problem where the integrated gamepad permanently holds Player 1, preventing Bluetooth controllers from being recognized by games that only poll the first controller.



\*\*Status:\*\* Phase 1 complete. Enumeration, fingerprinting, and tray UI are implemented and passing CI. Phase 2 (ViGEm + HidHide controller reordering) is next.

\*\*Owner:\*\* Hardware PdM managing e2e

\*\*License:\*\* MIT (open source, public release)

\*\*Target:\*\* Windows 10 (19041) minimum, Windows 11 supported



---



\## How This App Works (Read Before Writing Any Code)



Windows XInput Player Index is NOT reassignable via public API. The solution:



1\. \*\*ViGEmBus\*\* creates a virtual XInput controller at the desired Player Index slot

2\. \*\*HidHide\*\* hides the physical device from all game processes (only ControlShift can see it)

3\. ControlShift \*\*forwards HID input\*\* from the physical device to the virtual ViGEm controller

4\. Games see the virtual controller at the correct slot — unaware anything changed

5\. On exit or revert: HidHide rules cleared, virtual controllers removed, physical devices restored



This is the same approach used by DS4Windows and NVIDIA GameStream. ViGEmBus and HidHide are signed, trusted, widely deployed drivers by Nefarius Software Solutions.



---



\## Stack



| Layer | Technology |

|---|---|

| App | WinUI 3, C#, .NET 8 |

| XInput enumeration | Vortice.XInput |

| HID enumeration | HidSharp |

| Virtual controller bus | Nefarius.ViGEm.Client |

| Input suppression | Nefarius.HidHide.Client |

| Profile storage | System.Text.Json → %APPDATA%\\ControlShift\\profiles\\ |

| Process watching | Win32 WMI event subscription |

| Packaging | MSIX + WiX bundler |

| CI | GitHub Actions |



---



\## Repository Structure



```

/src/ControlShift.App/          # WinUI 3 application (UI layer only)
&nbsp; /ViewModels/                   # MainViewModel, SlotViewModel (INPC)
&nbsp; /Controls/                     # SlotCard custom control
&nbsp; /Services/                     # TrayIconService
&nbsp; /Converters/                   # Value converters for XAML binding

/src/ControlShift.Core/         # All controller logic — no UI dependency
&nbsp; /Enumeration/                 # XInput + HID device discovery
&nbsp; /Devices/                     # DeviceFingerprinter, KnownDeviceDatabase
&nbsp; /Models/                      # Data models (XInputSlot, Profile, etc.)

/src/ControlShift.Installer/    # Placeholder for Phase 2 packaging

/tests/ControlShift.Core.Tests/ # xUnit + Moq + FluentAssertions

/devices/known-devices.json     # VID/PID database for integrated gamepads

/profiles/examples/             # Example per-game profile JSON files

/docs/                          # Architecture notes

/.github/workflows/             # CI: build + test (dotnet), build-app (MSBuild)

```



---



\## NuGet Packages (currently installed)



Core:
```
Vortice.XInput                  3.6.2
HidSharp                        2.6.4
System.Text.Json                8.0.5
Serilog                         4.2.0
Serilog.Sinks.File              6.0.0
Microsoft.Extensions.Hosting.Abstractions  8.0.1
```

App:
```
Microsoft.WindowsAppSDK         1.6.x
H.NotifyIcon.WinUI              2.1.0
CommunityToolkit.WinUI.Controls.Primitives  8.1.x
Microsoft.Extensions.Hosting    8.0.1
Serilog.Extensions.Hosting      8.0.0
```

Phase 2 (not yet added):
```
Nefarius.ViGEm.Client           >= 1.21.442.0
Nefarius.HidHide.Client         latest
```



---



\## Build \& Run Commands



```bash

\# Build and test Core library (works with dotnet CLI)
dotnet build src/ControlShift.Core/ControlShift.Core.csproj
dotnet test tests/ControlShift.Core.Tests/ControlShift.Core.Tests.csproj

\# Build App (requires MSBuild — WinUI 3 XAML compiler needs it)
msbuild src/ControlShift.App/ControlShift.App.csproj /t:Restore /p:Configuration=Release /p:Platform=x64
msbuild src/ControlShift.App/ControlShift.App.csproj /p:Configuration=Release /p:Platform=x64

\# Run the app (requires Windows, no admin needed for Phase 1)
dotnet run --project src/ControlShift.App

```



---



\## Phase 1 — COMPLETE

All Phase 1 steps are implemented and passing CI:

\- Solution scaffolded (App, Core, Installer placeholder, Tests)
\- XInput enumeration (Vortice.XInput, battery normalization, wired level suppression)
\- HID enumeration (HidSharp, gamepad/joystick usage page filter)
\- Device fingerprinting (VID/PID lookup, case-insensitive battery type matching)
\- Tray popup UI (H.NotifyIcon.WinUI, 320x480 dark theme, 4 slot cards)
\- Async enumeration wired to UI with INotifyPropertyChanged
\- CI: dotnet build+test (Debug/Release) + MSBuild app build (Release/x64)



---



\## Phase 2 — Controller Reordering (after Phase 1 passes manual testing)



\- Implement `ViGEmController` wrapper in Core/Devices/ — creates virtual XInput at a given slot

\- Implement `HidHideService` wrapper in Core/Devices/ — add/remove device whitelist rules

\- Implement `InputForwardingService` as IHostedService — reads HID reports, writes to ViGEm at 125Hz

\- Build drag-to-reorder in the UI (drag slot cards)

\- Confirm dialog before applying any reorder

\- "Revert All" button — always visible when forwarding is active



\### CRITICAL: HidHide crash safety

On startup, ALWAYS call `HidHideService.ClearAllRules()` before applying any profile. Never assume previous state is clean.



Register cleanup in:

\- `AppDomain.CurrentDomain.UnhandledException`

\- `Application.Current.UnhandledException`

\- Windows Job Object termination callback



If cleanup does not run and user's devices stay hidden, they lose all controller input until ControlShift is restarted. This is a P0 safety issue.



---



\## Phase 3 — Per-Game Profiles (after Phase 2 is stable)



\- Profile JSON schema: see `/profiles/examples/` for reference format

\- WMI process watcher: `SELECT \* FROM Win32\_ProcessStartTrace WHERE ProcessName = ?`

\- On game launch: silently apply matching profile

\- On game exit: silently revert to default state

\- "Save Profile" button: auto-detect foreground EXE, save current slot layout



---



\## Known Devices (known-devices.json format)



```json

{

&nbsp; "devices": \[

&nbsp;   { "name": "ASUS ROG Ally MCU Gamepad", "vid": "0B05", "pid": "1ABE", "confirmed": true },

&nbsp;   { "name": "Lenovo Legion Go", "vid": "17EF", "pid": "6178", "confirmed": false },

&nbsp;   { "name": "GPD Win 4", "vid": "2833", "pid": "0004", "confirmed": false }

&nbsp; ]

}

```



`confirmed: false` means VID/PID needs validation on real hardware. Run HidEnumerator on each device and update this file before Phase 1 is complete.



---



\## Profile JSON Schema



Stored at `%APPDATA%\\ControlShift\\profiles\\{name}.json`



```json

{

&nbsp; "profileName": "Elden Ring - BT Controller as P1",

&nbsp; "gameExe": "eldenring.exe",

&nbsp; "gamePath": null,

&nbsp; "slotAssignments": \["bt-controller-device-path", null, null, null],

&nbsp; "suppressIntegrated": true,

&nbsp; "antiCheatGame": false,

&nbsp; "createdAt": "2026-02-18T00:00:00Z"

}

```



---



\## Anticheat — Critical Constraint



Games using Easy Anti-Cheat (EAC) or BattlEye may block launch when virtual XInput devices are active.



\*\*Required behavior:\*\*

\- Ship a bundled `anticheat-games.json` list of known protected executables

\- When process watcher detects an anticheat game is launching, call `RevertAll()` BEFORE the process fully starts

\- When saving a profile for an anticheat game, show an in-app warning



Do NOT silently apply profiles to anticheat games. This will get flagged and could result in bans.



---



\## UI Design Rules



\- Dark theme only for v1 (#0F172A background, #00E5FF accent)

\- Tray popup: 320×480px, no taskbar presence

\- No accessibility requirements for v1 (keyboard nav, screen reader out of scope)

\- Reversible always: any active reorder shows a prominent "Revert All" button

\- Confirm before applying any HidHide or ViGEm changes



---



\## Out of Scope for v1



\- Button remapping / key rebinding

\- Linux / Steam Deck

\- Accessibility (a11y)

\- Cloud profile sync

\- Paid or freemium features

\- Auto-update (defer to v1.1)



---



\## Manual Testing Checklist (run before any release)



\- \[ ] Plug/unplug USB and BT controllers — list updates correctly

\- \[ ] Reorder: promote BT to P1 — verify slot 0 in x360ce shows BT input

\- \[ ] Launch a XInput-only game (Forza Horizon 5) — BT controller recognized as P1

\- \[ ] Kill ControlShift via Task Manager while forwarding active — physical devices reappear on relaunch

\- \[ ] If anticheat game available — verify auto-revert fires before launch

\- \[ ] Cold boot with startup enabled — app loads cleanly, controllers detected

\- \[ ] Uninstall — HidHide rules cleared (drivers remain, rules removed)

\- \[ ] Validate VID/PID for each handheld model — update known-devices.json



---



\## Questions? Decisions Already Made



All major decisions are locked. Do not ask about:

\- App name (ControlShift — final)

\- Linux support (out of scope)

\- Accessibility (out of scope for v1)

\- External collaboration (building solo first)

\- Minimum Windows version (10, build 19041)



If you encounter an ambiguous implementation decision not covered here, make a reasonable choice and add a `// DECISION:` comment explaining your reasoning. The PdM will review.

