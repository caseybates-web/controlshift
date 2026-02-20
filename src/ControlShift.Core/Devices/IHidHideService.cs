namespace ControlShift.Core.Devices;

/// <summary>
/// Abstracts the HidHide driver for device suppression.
/// </summary>
public interface IHidHideService
{
    /// <summary>Whether the HidHide driver is installed and operational.</summary>
    bool IsDriverInstalled { get; }

    /// <summary>Whitelist an application so it can still see hidden devices.</summary>
    void AddApplicationRule(string executablePath);

    /// <summary>Add a device instance ID to HidHide's block list.</summary>
    void HideDevice(string deviceInstanceId);

    /// <summary>Remove a device instance ID from HidHide's block list.</summary>
    void UnhideDevice(string deviceInstanceId);

    /// <summary>Remove ALL application and device rules and deactivate HidHide.</summary>
    void ClearAllRules();

    /// <summary>Enable or disable HidHide globally.</summary>
    void SetActive(bool active);
}
