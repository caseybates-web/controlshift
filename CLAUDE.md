# ControlShift

A lightweight Windows app that lets users see all connected controllers, identify them via rumble, and reorder their XInput Player Index assignments on gaming handhelds. Solves the problem where the integrated gamepad permanently holds Player 1, preventing Bluetooth controllers from being recognized by games that only poll the first controller.

**Status:** Phase 3 complete — profiles, process watcher, anticheat, WiX installer.
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
- Single window, ~480×600px, fixed size, non-resizable
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
- Duration: 200ms (0.2 seconds)
- Strength: left motor = 16383, right motor = 16383 (25% of max) for 200ms, then stop
- Implementation: `XInput.SetVibration` via Vortice.XInput (method renamed in v3.x — NOT SetState)
- Only works for XInput controllers — HID-only devices silently skip rumble with no error
- Visual feedback: card border briefly highlights `#107C10` while rumbling, returns to normal after

### 4. Integrated Gamepad Always Shown First
The integrated gamepad is always displayed as the first card, regardless of Player Index.

**Rules:**
- Sort order: integrated gamepad first, all others sorted by Player Index
- Integrated gamepad card shows a distinct "INTEGRATED" badge/chip
- Label: product string from HID descriptor (or known device name, or VID:PID fallback)
- Its Player Index can still be changed via reordering — visual pinning only, not functional
- If no integrated gamepad is detected, this rule has no effect

---

## Code Quality — DRY Principle

DRY is a core guideline. Apply pragmatically: extract shared logic into helpers when the same pattern appears in 2+ places. Use `// TODO: DRY` comments to flag duplication mid-task. Don't over-abstract single-use code. Don't DRY test setup — tests repeat intentionally. Run a cleanup pass at the end of each Phase before moving on.

---

## GitHub-Native Development (Hard Requirement)

All build, test, and release steps must run entirely inside GitHub Actions. No local Visual Studio or SDK required to produce a release. Casey must be able to go from code to a downloadable installer by pushing a git tag — nothing else.

- All builds use `dotnet build` / `dotnet test` on `windows-latest` GitHub Actions runners
- WiX installer built in CI — no local WiX installation required
- ViGEmBus and HidHide installers downloaded from their official GitHub Releases URLs at build time and bundled into the bootstrapper
- GitHub Releases page is the distribution channel — tag push triggers a release with `ControlShift-Setup.exe` attached
- No code signing for v1 — users click through SmartScreen "Unknown Publisher" warning
- **Never require:** Visual Studio, local WiX, manual signing steps, any paid tooling

---

## Packaging & Distribution

Ship as a single `ControlShift-Setup.exe`. WiX Burn bootstrapper chains: (1) ViGEmBus silent install, (2) HidHide silent install, (3) ControlShift app. Users run one file, click through UAC, done.

Uninstaller: remove ControlShift and clear HidHide rules. Leave ViGEmBus and HidHide drivers installed (DS4Windows and other tools may depend on them).

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
| Packaging | WiX Burn bootstrapper (CI-built) |
| CI | GitHub Actions — dotnet build/test + WiX + GitHub Release |

**Actual NuGet versions (corrected during scaffold — use these):**
- `Nefarius.ViGEm.Client` 1.21.256
- `Nefarius.Drivers.HidHide` 3.3.0 (was renamed from Nefarius.HidHide.Client)
- `Vortice.XInput` 3.8.2
- `HidSharp` 2.6.4

---

## Repository Structure

```
/src/ControlShift.App/          # WinUI 3 application (UI layer only)
/src/ControlShift.Core/         # All controller logic — no UI dependency
  /Enumeration/                 # XInput + HID device discovery
  /Devices/                     # Fingerprinting, vendor lookup, controller matching
  /Forwarding/                  # Input forwarding loop (Phase 2)
  /Profiles/                    # Profile model + JSON persistence (Phase 3)
/src/ControlShift.Installer/    # WiX Burn bootstrapper (Phase 3)
/devices/known-devices.json     # VID/PID database for integrated gamepads
/devices/known-vendors.json     # VID → brand name (Xbox, PlayStation, Nintendo, ...)
/profiles/examples/             # Example per-game profile JSON files
/docs/                          # Architecture notes
/.github/workflows/             # CI: build, test, release
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

## Phase 1 — Complete

- [x] Step 1 — Solution scaffolded
- [x] Step 2 — XInput enumeration (14 tests passing)
- [x] Step 3 — HID enumeration (25 tests passing)
- [x] Step 4 — Device fingerprinting (43 tests passing)
- [x] Step 5 — UI window built
- [x] Step 6 — Wire enumeration to UI + rework to Xbox aesthetic standalone window

---

## Phase 2 — Controller Reordering

### Step 7 — Rich Controller Identity ✓ (complete)

Surface rich identity on each card:
- Name: HID product string, or known-devices.json name, or VID:PID fallback
- Brand badge: Xbox / PlayStation / Nintendo / vendor name by VID (known-vendors.json)
- VID:PID in small hex text
- Connection type: USB or Bluetooth (detected from HID device path `BTHENUM` / BT service UUID)
- Player Index badge (P1–P4)
- Battery % if available (XInput wireless only)

XInput ↔ HID matching: XInput slot N exposes an HID interface with `IG_0N` in its device path.
Use this marker to link `XInputSlotInfo` to the right `HidDeviceInfo`.

### Controller Nicknames (Phase 3 feature — implement after reordering is stable)

Let users rename any controller card with a custom nickname (e.g. "Casey's Xbox Elite").

- Double-click a card's name label to enter inline edit mode
- TextBox replaces the TextBlock; Escape cancels, Enter/focus-loss saves
- Nickname persisted to `%APPDATA%\ControlShift\nicknames.json` keyed by `VID:PID:SerialOrPath`
- If serial is unavailable, fall back to the HID device path (good enough for a fixed handheld gamepad)
- Nickname takes highest display priority: `nickname → HID product string → known-device name → VID:PID`

### Step 8 — Controller Navigation ✓ (complete)

Allow the user to navigate cards and initiate reordering without a mouse.

**Focus movement (normal mode):**
- D-pad up/down or left thumbstick up/down moves focus between cards
- Tab / Shift+Tab moves focus between cards (keyboard)
- WinUI 3 `XYFocusUp` / `XYFocusDown` set on each card for built-in gamepad navigation

**A button — tap vs. hold (gamepad only):**
- **Tap A** (released in < 500ms) → rumble the focused controller to identify it; no reorder
- **Hold A** (≥ 500ms) → enter reorder mode on the focused card
  - While held: a green `ProgressBar` fills over 500ms at the bottom of the card
  - At 500ms: progress bar hides, card enters Selected state, reorder begins
- Keyboard Enter/Space → immediate reorder (no hold required; keyboard users skip the tap/hold distinction)

**Reorder mode (one card selected):**
- D-pad/thumbstick (or arrow keys) moves the selected card up/down in the list — live preview
- A again (or Enter/Space on keyboard) confirms the new position
- B (or Escape) cancels and snaps the card back to its original position

**Visual states:**
- Focused: 1px `#107C10` border + scale 1.03 swell (Composition animation, 100ms ease-in-out)
- Selected: 3px `#107C10` border + `#404040` background + scale 1.03
- Dimmed: 0.5 opacity (all other cards while one is selected)
- Normal: 1px `#303030` border, full opacity, scale 1.0

**Implementation notes:**
- Poll XInput at **16ms** (~60 fps) via `DispatcherTimer` (fires on UI thread — no `TryEnqueue` needed)
- Watchdog timer at 5s checks `_navTimer.IsEnabled` and restarts if stopped
- `newReleases = _prevGamepadButtons & ~current` detects A button release for tap/hold distinction
- `_aHoldStart` (DateTime?) tracks when A was pressed; `_aHoldEnteredReorder` prevents double-trigger
- `SlotCard.ShowHoldProgress(double)` / `HideHoldProgress()` control the in-card progress bar
- `SlotCard.TriggerRumble()` is public — called by MainWindow on tap, by mouse Tapped event directly
- Gate XInput polling on window active state (`Activated`/`Deactivated` events)
- After any card reorder, update `XYFocusUp`/`XYFocusDown` links to match new visual order
- Tab order (`TabIndex`) must match visual top-to-bottom order; rebuild after reorder
- Cards have `Margin=8,4,8,4` so swell overflows into margin without hitting ScrollContentPresenter clip

### Step 9 — ViGEm + HidHide Controller Reordering ✓ (complete)

Core/Forwarding/ stack:
- `ViGEmControllerPool` — creates and holds 4 virtual Xbox360 controllers on startup, keeps them connected, `AutoSubmitReport = false`
- `HidHideService` — wraps HidHide COM/driver API: `AddToAllowlist(exePath)`, `HideDevice(instanceId)`, `ClearAll()`
- `InputForwardingService` — runs a Stopwatch-based 125Hz loop (8ms target), reads XInput state for physical slots 0–3, writes to virtual slots according to `int[4] slotMap` where `slotMap[physicalSlot] = virtualSlot`
- `IReorderService` — interface: `ApplyOrder(int[] newOrder)`, `RevertAll()`

App wiring:
- On app start, initialize forwarding with identity map (0→0, 1→1, 2→2, 3→3)
- Reorder UI updates the slotMap
- "Revert All" button resets to identity
- No confirm dialog yet — just get forwarding working

### CRITICAL: HidHide crash safety
On startup, ALWAYS call `HidHideService.ClearAllRules()` first. Never assume previous state is clean.

Register cleanup in:
- `AppDomain.CurrentDomain.UnhandledException`
- `Application.Current.UnhandledException`
- Windows Job Object termination callback

If devices stay hidden after a crash, the user loses all controller input until ControlShift restarts. This is a P0 safety issue.

---

## Phase 2 ViGEm Hardware Validation Spike

**Spike location:** `src/ControlShift.Spike/Program.cs`
**Purpose:** Validate ViGEm + HidHide APIs work correctly on this machine before building the full forwarding stack.

**To run:** Build and execute as Administrator:
```
dotnet build src/ControlShift.Spike
.\src\ControlShift.Spike\bin\Debug\net8.0-windows10.0.19041.0\ControlShift.Spike.exe
```

### Confirmed API shapes (from ilspy decompilation of NuGet packages)

**ViGEm (`Nefarius.ViGEm.Client` 1.21.256):**
- No `Xbox360Report` class — mutation is done via methods on `IXbox360Controller`:
  - `SetButtonsFull(ushort buttons)` — pass raw XUSB bitmask (matches `GamepadButtons` directly)
  - `SetAxisValue(Xbox360Axis axis, short value)` — `Xbox360Axis.LeftThumbX / LeftThumbY / RightThumbX / RightThumbY`
  - `SetSliderValue(Xbox360Slider slider, byte value)` — `Xbox360Slider.LeftTrigger / RightTrigger`
  - `SubmitReport()` — send accumulated state to the driver
- `AutoSubmitReport` defaults to `true` (each Set* call auto-submits) — set to `false` for batched 125Hz forwarding
- `UserIndex` (int) — which XInput slot (0–3) Windows assigned to this virtual controller
- `ViGEmClient.CreateXbox360Controller()` — factory; `Connect()` assigns a slot; `Disconnect()` releases it

**HidHide (`Nefarius.Drivers.HidHide` 3.3.0):**
- `IsInstalled` / `IsOperational` — check before using; throws `HidHideDriverAccessFailedException` if driver absent
- `IsActive` — enable/disable device hiding globally
- `AddBlockedInstanceId(string instanceId)` — takes device instance ID (NOT the HID symbolic link path)
- `ClearBlockedInstancesList()` — removes all blocks
- `AddApplicationPath(string path, bool throwIfInvalid)` — add process to allowlist
- `ClearApplicationsList()` — removes all allowed apps

**HID path → instance ID conversion:**
```
\\?\hid#vid_045e&pid_02ff&ig_00#7&286a539d&1&0000#{4d1e55b2-...}
→ HID\VID_045E&PID_02FF&IG_00\7&286A539D&1&0000
Strip \\?\, strip #{guid} suffix, replace # with \, uppercase.
```

### Findings

- [x] ViGEm driver present on machine: **YES** — ViGEmBus present and responsive
- [x] Virtual controllers created successfully: **YES**
- [x] Slot assignment: Windows assigns slots in connection order (ViGEm cannot request a specific XInput slot — `Xbox360UserIndexNotReportedException` confirms this)
- [x] HidHide driver present: **YES** — v1.4.181.0
- [x] HidHide allowlist: **working correctly** — our process still reads physical controllers after hiding
- [x] Forwarding loop actual Hz achieved: **~61Hz** (target 125Hz — `Thread.Sleep(8)` is too coarse; loop needs Stopwatch-based sleep)
- [x] Any errors or unexpected behavior: `Xbox360UserIndexNotReportedException` — cannot request a specific slot
- [x] Cleanup: **works correctly** — physical controllers restored after disconnect

**Verdict: SPIKE PASSED — proceed to Step 9**

### Architecture implication from spike

ViGEm cannot request a specific XInput slot — Windows assigns slots in connection order. Therefore the architecture must be:
1. Hide ALL physical controllers via HidHide
2. Create 4 virtual ViGEm controllers (they get slots 0–3 in connection order)
3. Forward physical XInput input to virtual slots in user's preferred order

### Implications for Step 9 implementation

- **`InputForwardingService`**: set `AutoSubmitReport = false`; call `SubmitReport()` once per loop tick
- **Slot assignment**: ViGEm assigns slots in order from lowest available. Hide all physical first, then connect virtual to get deterministic slot 0, 1, 2, 3.
- **HidHide safety**: `ClearBlockedInstancesList()` + `IsActive = false` must run in all exit paths (normal, crash, exception)
- **Instance ID source**: derive from `HidDeviceInfo.DevicePath` using the conversion above (already in HidEnumerator output)

---

## Phase 3 — Per-Game Profiles ✓ (complete)

### Profile Save/Load (PR #16)
- ProfileStore: `%APPDATA%\ControlShift\profiles\{name}.json`
- Profiles store VID:PID strings (stable across reconnects), resolved to device paths at apply time
- ProfileResolver: VID:PID → current device paths, handles duplicates via claim tracking
- Save Profile button: auto-detects foreground EXE, ContentDialog for name/exe
- Profiles button: list/load/delete saved profiles

### WMI Process Watcher + Anticheat Auto-Revert (PR B)
- WMI: `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace` for game launch/exit detection
- Watches all exe names from saved profiles, fires events on WMI worker thread
- Auto-apply: non-anticheat game launches → load matching profile → start forwarding
- Auto-revert: game exits → stop forwarding
- Anticheat safety: known anticheat game → STOP forwarding before game fully starts
- `AntiCheatDatabase`: loads `devices/anticheat-games.json`, case-insensitive O(1) lookup
- Warning dialog when saving profile for known anticheat game

### WiX Installer (PR C)
- WiX 4 Burn bootstrapper: ViGEmBus → HidHide → ControlShift MSI
- `Permanent=yes` on drivers (not removed on uninstall)
- Custom action on uninstall: `ControlShift.App.exe --cleanup` to clear HidHide rules
- `release.yml`: tag push → build → GitHub Release with `ControlShift-Setup.exe`

---

## Anticheat — Critical Constraint ✓ (implemented)

EAC and BattlEye may block launch when virtual XInput devices are active.

- Ship bundled `anticheat-games.json` ✓
- Call `StopForwardingAsync()` BEFORE anticheat game process fully starts ✓
- Show warning when saving a profile for a known anticheat game ✓

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
- [ ] Controller cards show brand badge (Xbox/PlayStation/etc.) and VID:PID
- [ ] USB vs Bluetooth connection type shown correctly on each card
- [ ] Click controller card — rumble fires 200ms at 25% strength, card border highlights green
- [ ] Plug/unplug controllers — list updates within 5 seconds
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
- Rumble: 200ms, 25% strength (16383) on card click — confirmed
- Integrated gamepad: always shown first visually, Player Index still changeable
- CI/CD: GitHub Actions only, no local tooling required
- Distribution: single ControlShift-Setup.exe from GitHub Releases
- Linux: out of scope
- Accessibility: out of scope for v1
- Min Windows version: 10, build 19041

If you hit an ambiguous decision not covered here, make a reasonable choice and add a `// DECISION:` comment. The PdM will review.
