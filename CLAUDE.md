# ControlShift

A lightweight Windows app that lets users see all connected controllers, identify them via rumble, and reorder their XInput Player Index assignments on gaming handhelds. Solves the problem where the integrated gamepad permanently holds Player 1, preventing Bluetooth controllers from being recognized by games that only poll the first controller.

**Status:** Phase 1 Steps 1–5 complete. Step 6 (wire enumeration to UI + UI rework) is next.
**Owner:** Hardware PdM managing e2e  
**License:** MIT (open source, public release)  
**Target:** Windows 10 (19041) minimum, Windows 11 supported

---

## Recent Product Decisions (Supersede Earlier Instructions)

These decisions were made after initial scaffolding. They override anything in the original design.

### 1. Standalone Window — NOT a tray app
The app is a proper standalone windowed application, not a system tray popup.

**Why:** The Xbox Full Screen Experience (FSE) on Windows blocks access to the system tray entirely. ControlShift must be launchable from the Xbox FSE app launcher as a first-class app.

**What this means:**
- Single window, ~480×640px, fixed size, non-resizable
- App appears in Windows app list and Xbox FSE launcher
- User flow: launch app → identify controllers → reorder → close app → launch game
- No tray icon, no auto-hide, no tray click handler
- Window has a standard title bar with close button
- Closing the window exits the app (after HidHide/ViGEm cleanup)

### 2. Xbox Aesthetic
Match the Xbox visual design language as closely as possible.

**Design tokens:**
- Background: `#1A1A1A` (near-black)
- Surface/card: `#2D2D2D` (elevated dark)
- Accent: `#107C10` (Xbox signature green)
- Text primary: `#FFFFFF`
- Text secondary: `#ABABAB`
- Font: Segoe UI — system font, already on every Windows machine, no import needed
- Border radius: 4px on cards, 2px on buttons
- Button style: solid green fill, white text, no outline
- No gradients — flat, bold, confident

### 3. Rumble to Identify Controllers
When a user taps or clicks a controller card, send a short rumble pulse to that controller.

**Spec:**
- Trigger: single click/tap on a controller card
- Duration: 500ms (0.5 seconds)
- Strength: left motor = 65535, right motor = 65535 for 500ms, then XInputSetState to 0
- Implementation: `XInputSetState` via Vortice.XInput
- Only works for XInput controllers — HID-only devices silently skip rumble with no error
- Visual feedback: card border briefly highlights `#107C10` while rumbling, returns to normal after

### 4. Integrated Gamepad Always Shown First
The integrated gamepad is always displayed as the first card, regardless of Player Index.

**Rules:**
- Sort order: integrated gamepad first, all others sorted by Player Index
- Integrated gamepad card shows a distinct "INTEGRATED" badge/chip
- Label: "Built-in Controller" (or the device's product name if available)
- Its Player Index can still be changed via reordering — visual pinning only, not functional
- If no integrated gamepad is detected, this rule has no effect

---

## How This App Works (Read Before Writing Any Code)

Windows XInput Player Index is NOT reassignable via public API. The solution:

1. **ViGEmBus** creates a virtual XInput controller at the desired Player Index slot
2. **HidHide** hides the physical device from all game processes (only ControlShift can see it)
3. ControlShift **forwards HID input** from the physical device to the virtual ViGEm controller
4. Games see the virtual controller at the correct slot — unaware anything changed
5. On exit or revert: HidHide rules cleared, virtual controllers removed, physical devices restored

This is the same approach used by DS4Windows and NVIDIA GameStream. ViGEmBus and HidHide are signed, trusted, widely deployed drivers by Nefarius Software Solutions.

---

## Stack

| Layer | Technology |
|---|---|
| App | WinUI 3, C#, .NET 8 |
| XInput enumeration + rumble | Vortice.XInput |
| HID enumeration | HidSharp |
| Virtual controller bus | Nefarius.ViGEm.Client |
| Input suppression | Nefarius.Drivers.HidHide |
| Profile storage | System.Text.Json → %APPDATA%\ControlShift\profiles\ |
| Process watching | Win32 WMI event subscription |
| Packaging | MSIX + WiX bundler |
| CI | GitHub Actions |

**Actual NuGet versions (corrected during scaffold — use these):**
- `Nefarius.ViGEm.Client` 1.21.256
- `Nefarius.Drivers.HidHide` 3.3.0 (was renamed from Nefarius.HidHide.Client)
- `Vortice.XInput` 3.8.2
- `CommunityToolkit.WinUI.Extensions` 8.2.x

---

## Repository Structure

```
/src/ControlShift.App/          # WinUI 3 application (UI layer only)
/src/ControlShift.Core/         # All controller logic — no UI dependency
  /Enumeration/                 # XInput + HID device discovery
  /Forwarding/                  # Input forwarding loop (BackgroundService)
  /Profiles/                    # Profile model + JSON persistence
  /Devices/                     # ViGEm + HidHide wrappers
/src/ControlShift.Installer/    # WiX installer / MSIX packaging
/devices/known-devices.json     # VID/PID database for integrated gamepads
/profiles/examples/             # Example per-game profile JSON files
/docs/                          # Architecture notes
/.github/workflows/             # CI: build, sign, release
```

---

## Build & Run Commands

```bash
dotnet restore
dotnet build
dotnet run --project src/ControlShift.App
dotnet test
```

---

## Phase 1 — Status

- [x] Step 1 — Solution scaffolded
- [x] Step 2 — XInput enumeration (14 tests passing)
- [x] Step 3 — HID enumeration (25 tests passing)
- [x] Step 4 — Device fingerprinting (43 tests passing)
- [x] Step 5 — UI window built (needs rework per decisions above)
- [ ] Step 6 — Wire enumeration to UI + rework UI to new design

### Step 6 — Wire Enumeration to UI (includes UI rework)

Do these together in one step:

1. Rename `PopupWindow` → `MainWindow` — it is no longer a popup
2. Redesign window to Xbox aesthetic using design tokens above
3. Remove all tray icon code: `TrayIconService`, tray sections of `NativeMethods`, tray event wiring in `App.xaml.cs`
4. On window open: call `XInputEnumerator` + `HidEnumerator` + `DeviceFingerprinter`
5. Build controller card list: integrated gamepad first (with "INTEGRATED" badge), then others by Player Index
6. Each card shows: controller name, connection type, Player Index badge, battery % if available
7. On card click: `XInputSetState` full rumble (L=65535, R=65535) for 500ms then stop; highlight card border green during rumble
8. Subscribe to `WM_DEVICECHANGE` — refresh list on connect/disconnect
9. Window close → HidHide/ViGEm cleanup → app exit

---

## Phase 2 — Controller Reordering (after Phase 1 passes manual testing)

- `ViGEmController` wrapper in Core/Devices/
- `HidHideService` wrapper in Core/Devices/
- `InputForwardingService` as IHostedService — reads HID reports, writes to ViGEm at 125Hz
- Drag-to-reorder UI (drag controller cards between Player Index slots)
- Confirm dialog before applying any reorder
- "Revert All" button — always visible when forwarding is active

### CRITICAL: HidHide crash safety
On startup, ALWAYS call `HidHideService.ClearAllRules()` first. Never assume previous state is clean.

Register cleanup in:
- `AppDomain.CurrentDomain.UnhandledException`
- `Application.Current.UnhandledException`
- Windows Job Object termination callback

If devices stay hidden after a crash, the user loses all controller input until ControlShift restarts. This is a P0 safety issue.

---

## Phase 3 — Per-Game Profiles (after Phase 2 is stable)

- WMI process watcher: `SELECT * FROM Win32_ProcessStartTrace WHERE ProcessName = ?`
- On game launch: silently apply matching profile
- On game exit: silently revert
- "Save Profile" button: auto-detect foreground EXE, save current slot layout

---

## Anticheat — Critical Constraint

EAC and BattlEye may block launch when virtual XInput devices are active.

- Ship bundled `anticheat-games.json`
- Call `RevertAll()` BEFORE anticheat game process fully starts
- Show warning when saving a profile for a known anticheat game

---

## Out of Scope for v1

- Button remapping / key rebinding
- Linux / Steam Deck
- Accessibility (a11y)
- Cloud profile sync
- System tray presence
- Auto-update

---

## Manual Testing Checklist

- [ ] App launches as standalone window (not tray)
- [ ] App appears in Windows app list and Xbox FSE launcher
- [ ] Integrated gamepad always shown first with INTEGRATED badge
- [ ] Click controller card — rumble fires 500ms, card border highlights green
- [ ] Plug/unplug controllers — list updates correctly
- [ ] Reorder: promote BT to P1 — verify in x360ce that slot 0 shows BT input
- [ ] Launch XInput-only game (Forza Horizon 5) — BT controller seen as P1
- [ ] Kill app via Task Manager while forwarding active — devices reappear on relaunch
- [ ] Anticheat game — verify auto-revert fires before launch
- [ ] Uninstall — HidHide rules cleared, drivers remain

---

## Decisions Already Made — Do Not Revisit

- App name: ControlShift (final)
- App model: standalone window, NOT system tray
- Xbox aesthetic: confirmed, use design tokens above
- Rumble: 500ms full strength on card click
- Integrated gamepad: always shown first visually, Player Index still changeable
- Linux: out of scope
- Accessibility: out of scope for v1
- Min Windows version: 10, build 19041

If you hit an ambiguous decision not covered here, make a reasonable choice and add a `// DECISION:` comment. The PdM will review.
