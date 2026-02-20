namespace ControlShift.Core.Devices;

/// <summary>
/// Registers HidHide cleanup handlers for crash safety.
/// Call <see cref="Install"/> as early as possible during startup.
/// </summary>
/// <remarks>
/// CRITICAL (P0): If devices stay hidden after a crash, the user loses all controller
/// input until ControlShift restarts. Defense-in-depth:
/// 1. Startup always calls ClearAllRules() (handles previous-crash leftovers).
/// 2. UnhandledException + ProcessExit handlers clear rules on crash/exit.
/// 3. Next launch repeats step 1 as final safety net.
/// </remarks>
public static class CrashSafetyGuard
{
    /// <summary>
    /// Immediately clears stale HidHide rules and registers crash/exit handlers.
    /// Safe to call multiple times — idempotent.
    /// </summary>
    public static void Install(IHidHideService hidHide)
    {
        // 1. Immediately clear any leftover rules from a previous crash.
        try { hidHide.ClearAllRules(); }
        catch { /* Driver may not be installed — NullHidHideService handles this. */ }

        // 2. Register AppDomain-level handlers (fires on unhandled exceptions and process exit).
        AppDomain.CurrentDomain.UnhandledException += (_, _) => SafeClear(hidHide);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeClear(hidHide);
    }

    private static void SafeClear(IHidHideService hidHide)
    {
        try { hidHide.ClearAllRules(); }
        catch { /* Best effort — never throw in a crash handler. */ }
    }
}
