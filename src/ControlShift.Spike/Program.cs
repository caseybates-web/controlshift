// ControlShift — Phase 2 Hardware Validation Spike
//
// Validates that ViGEm + HidHide work correctly together on this machine
// BEFORE building the full forwarding stack into the app.
//
// What this spike does:
//   Phase 1  — Enumerate physical XInput controllers and XInput HID interfaces
//   Phase 2  — Create virtual Xbox 360 controllers via ViGEmBus; check assigned slots
//   Phase 3  — Hide physical controllers via HidHide; verify allowlist works for us
//   Phase 4  — Run P1↔P2 swap forwarding loop at ~125Hz for 10 seconds
//   Cleanup  — Disconnect virtual controllers, clear HidHide state
//
// Prerequisites:
//   • Run as Administrator (HidHide driver requires admin)
//   • ViGEmBus driver installed (https://github.com/nefarius/ViGEmBus/releases)
//   • HidHide driver installed  (https://github.com/nefarius/HidHide/releases)
//
// Record findings in CLAUDE.md under "Phase 2 ViGEm Hardware Validation Spike".

using System.Diagnostics;
using System.Security.Principal;
using HidSharp;
using Nefarius.Drivers.HidHide;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Vortice.XInput;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ── Admin check ───────────────────────────────────────────────────────────────

if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
{
    Fail("Must run as Administrator — HidHide driver requires admin.");
    Fail("Right-click the terminal → 'Run as administrator', then re-run.");
    return 1;
}

// ── State ─────────────────────────────────────────────────────────────────────

ViGEmClient?                  vigem        = null;
var                           virtualCtrls = new List<IXbox360Controller>();
HidHideControlService?        hidHide      = null;
bool                          hidHideWasEnabled = false;
string                        exePath      = Process.GetCurrentProcess().MainModule?.FileName
                                             ?? throw new InvalidOperationException("Cannot resolve process path");

// ── Cleanup handler (registered before anything that can fail) ────────────────

static void CleanupViGEm(List<IXbox360Controller> ctrls, ViGEmClient? client)
{
    foreach (var ctrl in ctrls)
    {
        try { ctrl.Disconnect(); } catch { /* best-effort */ }
    }
    ctrls.Clear();
    client?.Dispose();
}

static void CleanupHidHide(HidHideControlService? svc)
{
    if (svc is null) return;
    try
    {
        svc.IsActive = false;
        svc.ClearBlockedInstancesList();
        svc.ClearApplicationsList();
        Ok("  HidHide: cleared and disabled");
    }
    catch (Exception ex)
    {
        Warn($"  HidHide cleanup error: {ex.Message}");
        Warn("  Manual fix: open HidHide Configuration app and clear all lists");
    }
}

try
{
    Header("ControlShift — ViGEm/HidHide Hardware Validation Spike");

    // ── Phase 1: Physical XInput discovery ───────────────────────────────────

    Section("Phase 1: Physical XInput state");

    var physicalSlots = new List<int>();
    for (int i = 0; i < 4; i++)
    {
        if (XInput.GetState((uint)i, out State s))
        {
            physicalSlots.Add(i);
            Ok($"  Slot {i}: CONNECTED  btns=0x{(ushort)s.Gamepad.Buttons:X4}  " +
               $"LX={s.Gamepad.LeftThumbX,6}  LY={s.Gamepad.LeftThumbY,6}");
        }
        else
        {
            Info($"  Slot {i}: disconnected");
        }
    }
    Info($"  → {physicalSlots.Count} physical controller(s) found");

    Section("Phase 1b: XInput HID interfaces (IG_ paths)");

    // HidSharp enumerates HID devices; filter for those with IG_ in the path.
    // These are the XInput-capable HID interfaces we'll hide via HidHide.
    var xinputHidDevices = DeviceList.Local.GetHidDevices()
        .Where(d => d.DevicePath.IndexOf("ig_", StringComparison.OrdinalIgnoreCase) >= 0)
        .ToList();

    foreach (var hid in xinputHidDevices)
        Info($"  VID={hid.VendorID:X4}  PID={hid.ProductID:X4}  path={hid.DevicePath}");

    if (xinputHidDevices.Count == 0)
        Warn("  No XInput HID interfaces found — HidHide hiding phase will be skipped");
    else
        Info($"  → {xinputHidDevices.Count} XInput HID interface(s) found");

    // ── Phase 2: ViGEm virtual controller creation ────────────────────────────

    Section("Phase 2: ViGEm virtual controller creation");

    try
    {
        vigem = new ViGEmClient();
        Ok("  ViGEmClient created — ViGEmBus driver is installed and responsive");
    }
    catch (Exception ex)
    {
        Fail($"  ViGEmClient failed: {ex.Message}");
        Fail("  → Install ViGEmBus from https://github.com/nefarius/ViGEmBus/releases");
        return 1;
    }

    // Create at least 2 virtual controllers (for swap test); match physical count if more.
    int targetCount = Math.Max(2, physicalSlots.Count);
    for (int i = 0; i < targetCount; i++)
    {
        try
        {
            var ctrl = vigem.CreateXbox360Controller();
            // DECISION: Disable auto-submit so we can batch all axis/button updates
            // into a single SubmitReport() call per forwarding cycle (125Hz target).
            ctrl.AutoSubmitReport = false;
            ctrl.Connect();
            virtualCtrls.Add(ctrl);
            Ok($"  Virtual controller [{i}]: connected → XInput slot {ctrl.UserIndex}");
        }
        catch (Exception ex)
        {
            Fail($"  Virtual controller [{i}]: FAILED — {ex.Message}");
        }
    }

    if (virtualCtrls.Count == 0)
    {
        Fail("  No virtual controllers created — aborting");
        return 1;
    }

    // Let Windows propagate slot assignments before reading state.
    Thread.Sleep(300);

    Info("");
    Info("  XInput slots after connecting virtual controllers:");
    for (int i = 0; i < 4; i++)
    {
        bool occupied = XInput.GetState((uint)i, out _);
        // Identify if it's one of our virtual controllers by UserIndex.
        var match = virtualCtrls.FirstOrDefault(c => c.UserIndex == i);
        string who = match is not null ? $"virtual[{virtualCtrls.IndexOf(match)}]" : "physical or other";
        Info($"    Slot {i}: {(occupied ? $"OCCUPIED ({who})" : "empty")}");
    }

    // ── Phase 3: HidHide ──────────────────────────────────────────────────────

    Section("Phase 3: HidHide device hiding");

    try
    {
        hidHide = new HidHideControlService();

        if (!hidHide.IsInstalled)
        {
            Warn("  HidHide driver not installed — skipping Phase 3");
            Warn("  Install from https://github.com/nefarius/HidHide/releases");
            hidHide = null;
        }
        else
        {
            Ok($"  HidHide driver found  — version {hidHide.LocalDriverVersion}");

            // Safety: always clear stale state before applying our rules.
            hidHide.IsActive = false;
            hidHide.ClearBlockedInstancesList();
            hidHide.ClearApplicationsList();
            Ok("  HidHide: cleared stale state");

            // Add spike process to the allowlist so WE can still read physical controllers.
            hidHide.AddApplicationPath(exePath, throwIfInvalid: false);
            Ok($"  HidHide: allowlist → {exePath}");

            // Block each physical XInput HID interface by its device instance ID.
            // Converts \\?\hid#vid_...#...#{guid} → HID\VID_...\... format.
            var blockedInstanceIds = xinputHidDevices
                .Select(d => HidPathToInstanceId(d.DevicePath))
                .ToList();

            foreach (string id in blockedInstanceIds)
            {
                hidHide.AddBlockedInstanceId(id);
                Info($"  HidHide: blocking → {id}");
            }

            hidHide.IsActive = true;
            hidHideWasEnabled = true;
            Ok("  HidHide: ACTIVE — physical controllers hidden from all other processes");

            // Verify: our process (on the allowlist) must still see physical controllers.
            Info("");
            Info("  XInput state from OUR process after HidHide enable:");
            Info("  (Physical slots should still appear — allowlist exempts us.)");
            for (int i = 0; i < 4; i++)
            {
                bool occupied = XInput.GetState((uint)i, out _);
                Info($"    Slot {i}: {(occupied ? "OCCUPIED" : "empty")}");
            }
        }
    }
    catch (Exception ex)
    {
        Warn($"  HidHide error: {ex.Message}");
        Warn("  Continuing with ViGEm-only forwarding (physical controllers not hidden)");
        hidHide = null;
    }

    // ── Phase 4: Forwarding loop ──────────────────────────────────────────────

    Section("Phase 4: P1↔P2 swap forwarding (~125Hz, 10 seconds)");

    if (physicalSlots.Count >= 2 && virtualCtrls.Count >= 2)
    {
        int vSlot0 = virtualCtrls[0].UserIndex;
        int vSlot1 = virtualCtrls[1].UserIndex;

        Info($"  Routing: physical slot 0 → virtual[1] (XInput slot {vSlot1})");
        Info($"  Routing: physical slot 1 → virtual[0] (XInput slot {vSlot0})");
        Info($"  Games will see: physical P1 input on slot {vSlot1}, P2 input on slot {vSlot0}");
        Info("  Press buttons on each physical controller to verify the swap.");
        Info("");

        var sw = Stopwatch.StartNew();
        int lastSec = -1;
        long frameCount = 0;

        while (sw.Elapsed.TotalSeconds < 10)
        {
            XInput.GetState(0, out State s0);
            XInput.GetState(1, out State s1);

            // Swap: physical 0 → virtual[1], physical 1 → virtual[0]
            ForwardToController(virtualCtrls[1], s0.Gamepad);
            ForwardToController(virtualCtrls[0], s1.Gamepad);

            virtualCtrls[0].SubmitReport();
            virtualCtrls[1].SubmitReport();

            frameCount++;

            int sec = (int)sw.Elapsed.TotalSeconds;
            if (sec != lastSec)
            {
                lastSec = sec;
                Console.Write($"\r  [{sec:D2}s] P1 btns=0x{(ushort)s0.Gamepad.Buttons:X4}  " +
                              $"P2 btns=0x{(ushort)s1.Gamepad.Buttons:X4}   ");
            }

            Thread.Sleep(8); // ~125Hz
        }

        Console.WriteLine();
        double actualHz = frameCount / sw.Elapsed.TotalSeconds;
        Ok($"  Forwarding loop complete — {frameCount} frames in {sw.Elapsed.TotalSeconds:F1}s (~{actualHz:F0}Hz)");
    }
    else if (physicalSlots.Count >= 1 && virtualCtrls.Count >= 1)
    {
        Info("  Only 1 physical controller — running identity forwarding (5 seconds)");
        Info("  Press buttons to confirm virtual controller mirrors physical input.");

        var sw = Stopwatch.StartNew();
        int lastSec = -1;
        long frameCount = 0;

        while (sw.Elapsed.TotalSeconds < 5)
        {
            XInput.GetState(0, out State s0);
            ForwardToController(virtualCtrls[0], s0.Gamepad);
            virtualCtrls[0].SubmitReport();
            frameCount++;

            int sec = (int)sw.Elapsed.TotalSeconds;
            if (sec != lastSec)
            {
                lastSec = sec;
                Console.Write($"\r  [{sec:D2}s] btns=0x{(ushort)s0.Gamepad.Buttons:X4}  LX={s0.Gamepad.LeftThumbX,6}   ");
            }
            Thread.Sleep(8);
        }

        Console.WriteLine();
        Ok($"  Forwarding loop complete — {frameCount} frames in {sw.Elapsed.TotalSeconds:F1}s");
    }
    else
    {
        Warn("  No physical controllers connected — skipping forwarding loop");
        Warn("  Connect at least one Xbox controller and re-run to test forwarding");
    }
}
catch (Exception ex)
{
    Fail($"Unhandled exception: {ex}");
}
finally
{
    // ── Cleanup — always runs, even on exception ──────────────────────────────

    Section("Cleanup");

    CleanupViGEm(virtualCtrls, vigem);
    Ok("  ViGEm: all virtual controllers disconnected");

    CleanupHidHide(hidHide);

    // Verify physical controllers are visible again.
    Thread.Sleep(200);
    Info("");
    Info("  XInput state after cleanup (physical controllers should be visible again):");
    for (int i = 0; i < 4; i++)
    {
        bool occupied = XInput.GetState((uint)i, out _);
        Info($"    Slot {i}: {(occupied ? "OCCUPIED" : "empty")}");
    }
}

Section("Summary — record these findings in CLAUDE.md");
Info("  ViGEm driver present:     YES (would have exited early otherwise)");
Info($"  HidHide active:           {hidHideWasEnabled}");
Info("  Record: slot assignment behavior, observed Hz, any errors");
Info("  Record: which virtual slot each ViGEm controller was assigned");
Info("  Record: whether allowlist correctly let us read physical XInput state");
return 0;

// ── Forwarding helper ─────────────────────────────────────────────────────────

// Copies Vortice.XInput Gamepad state into a ViGEm Xbox360 controller.
// DECISION: AutoSubmitReport = false so caller controls when SubmitReport() fires.
// This allows batching all 7 axis/button updates into one kernel round-trip.
static void ForwardToController(IXbox360Controller ctrl, Gamepad gp)
{
    // Button bitmask: XInput GamepadButtons enum values match the XUSB_REPORT
    // button bitmask exactly, so a direct cast is correct.
    ctrl.SetButtonsFull((ushort)gp.Buttons);

    ctrl.SetAxisValue(Xbox360Axis.LeftThumbX,  gp.LeftThumbX);
    ctrl.SetAxisValue(Xbox360Axis.LeftThumbY,  gp.LeftThumbY);
    ctrl.SetAxisValue(Xbox360Axis.RightThumbX, gp.RightThumbX);
    ctrl.SetAxisValue(Xbox360Axis.RightThumbY, gp.RightThumbY);

    // Triggers are "sliders" in the ViGEm API (byte, 0–255).
    ctrl.SetSliderValue(Xbox360Slider.LeftTrigger,  gp.LeftTrigger);
    ctrl.SetSliderValue(Xbox360Slider.RightTrigger, gp.RightTrigger);
}

// ── HID path → device instance ID ────────────────────────────────────────────

// HidSharp gives us a symbolic link (device path):
//   \\?\hid#vid_045e&pid_02ff&ig_00#7&286a539d&1&0000#{4d1e55b2-...}
//
// HidHide AddBlockedInstanceId wants the device instance ID:
//   HID\VID_045E&PID_02FF&IG_00\7&286A539D&1&0000
//
// Conversion: strip \\?\ prefix, strip #{guid} suffix, replace # with \, uppercase.
static string HidPathToInstanceId(string hidPath)
{
    string s = hidPath;
    if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
    int guidStart = s.LastIndexOf("#{", StringComparison.Ordinal);
    if (guidStart >= 0) s = s[..guidStart];
    return s.Replace('#', '\\').ToUpperInvariant();
}

// ── Output helpers ────────────────────────────────────────────────────────────

static void Header(string msg)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine();
    Console.WriteLine(msg);
    Console.WriteLine(new string('═', Math.Min(msg.Length, 72)));
    Console.ResetColor();
}

static void Section(string msg)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine();
    Console.WriteLine($"── {msg}");
    Console.ResetColor();
}

static void Ok(string msg)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(msg);
    Console.ResetColor();
}

static void Info(string msg) => Console.WriteLine(msg);

static void Warn(string msg)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine(msg);
    Console.ResetColor();
}

static void Fail(string msg)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(msg);
    Console.ResetColor();
}
