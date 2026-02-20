namespace ControlShift.Core.Devices;

/// <summary>
/// No-op implementation for when the HidHide driver is not installed.
/// Allows the app to run with enumeration/identification features intact
/// while disabling controller reordering.
/// </summary>
public sealed class NullHidHideService : IHidHideService
{
    public bool IsDriverInstalled => false;

    public void AddApplicationRule(string executablePath) { }
    public void HideDevice(string deviceInstanceId) { }
    public void UnhideDevice(string deviceInstanceId) { }
    public void ClearAllRules() { }
    public void SetActive(bool active) { }
}
