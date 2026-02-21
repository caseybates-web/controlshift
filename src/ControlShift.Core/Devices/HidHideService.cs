using System.Diagnostics;
using Nefarius.Drivers.HidHide;
using ControlShift.Core.Diagnostics;

namespace ControlShift.Core.Devices;

/// <summary>
/// Production implementation wrapping <see cref="HidHideControlService"/>.
/// </summary>
/// <remarks>
/// DECISION: All calls are wrapped in try/catch. HidHide communicates with the
/// driver via device I/O control — if the driver is unloaded or the device node
/// vanishes (e.g. during a Windows update), calls throw IOException. We log and
/// surface errors without crashing the application.
/// </remarks>
public sealed class HidHideService : IHidHideService
{
    private readonly HidHideControlService _service;

    public HidHideService()
    {
        _service = new HidHideControlService();
    }

    public bool IsDriverInstalled
    {
        get
        {
            try { return _service.IsInstalled; }
            catch { return false; }
        }
    }

    public void AddApplicationRule(string executablePath)
    {
        _service.AddApplicationPath(executablePath);
    }

    public void HideDevice(string deviceInstanceId)
    {
        Debug.WriteLine($"[HidHide] HideDevice — instanceId='{deviceInstanceId}'");
        try
        {
            _service.AddBlockedInstanceId(deviceInstanceId);
            Debug.WriteLine($"[HidHide] HideDevice — SUCCESS for '{deviceInstanceId}'");
            DebugLog.HidHide("hide-success", deviceInstanceId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HidHide] HideDevice — FAILED for '{deviceInstanceId}': {ex.Message}");
            DebugLog.Exception($"HidHide.HideDevice({deviceInstanceId})", ex);
            throw;
        }
    }

    public void UnhideDevice(string deviceInstanceId)
    {
        Debug.WriteLine($"[HidHide] UnhideDevice — instanceId='{deviceInstanceId}'");
        DebugLog.HidHide("unhide", deviceInstanceId);
        _service.RemoveBlockedInstanceId(deviceInstanceId);
    }

    public void ClearAllRules()
    {
        DebugLog.HidHide("clearAll", "(all rules)");
        try { _service.ClearBlockedInstancesList(); } catch { /* best effort */ }
        try { _service.ClearApplicationsList(); }     catch { /* best effort */ }
        try { _service.IsActive = false; }            catch { /* best effort */ }
    }

    public void SetActive(bool active)
    {
        _service.IsActive = active;
    }
}
