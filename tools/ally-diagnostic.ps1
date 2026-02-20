# ControlShift ROG Ally X Diagnostic Dump
$ErrorActionPreference = "Continue"
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
$allyDumpPath = Join-Path $env:TEMP "controlshift-ally-dump.txt"
$hidDumpPath = Join-Path $env:TEMP "controlshift-hid-dump.txt"

$output = [System.Collections.Generic.List[string]]::new()
$hidOutput = [System.Collections.Generic.List[string]]::new()

function Log($msg) {
    $output.Add($msg)
    Write-Host $msg
}

function HidLog($msg) {
    $hidOutput.Add($msg)
}

# CfgMgr32 P/Invoke for parent chain walk
$csharpCode = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;

public static class CfgMgr32Diag {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Locate_DevNodeW(
        out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    public static extern int CM_Get_Parent(
        out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    public static extern int CM_Get_Device_IDW(
        uint dnDevInst, StringBuilder Buffer, uint BufferLen, uint ulFlags);

    public static string GetDeviceId(uint devNode) {
        var sb = new StringBuilder(400);
        int cr = CM_Get_Device_IDW(devNode, sb, (uint)sb.Capacity, 0);
        return cr == 0 ? sb.ToString() : "(error cr=0x" + cr.ToString("X") + ")";
    }

    public static string[] WalkParentChain(string instanceId, int maxLevels) {
        var chain = new List<string>();
        uint devNode;
        int cr = CM_Locate_DevNodeW(out devNode, instanceId, 0);
        if (cr != 0) {
            chain.Add("CM_Locate_DevNodeW FAILED for '" + instanceId + "' cr=0x" + cr.ToString("X"));
            return chain.ToArray();
        }
        chain.Add("[self] " + GetDeviceId(devNode));
        for (int i = 0; i < maxLevels; i++) {
            uint parent;
            cr = CM_Get_Parent(out parent, devNode, 0);
            if (cr != 0) {
                chain.Add("[level " + (i+1) + "] CM_Get_Parent ended (cr=0x" + cr.ToString("X") + ")");
                break;
            }
            chain.Add("[level " + (i+1) + "] " + GetDeviceId(parent));
            devNode = parent;
        }
        return chain.ToArray();
    }
}
'@
Add-Type -TypeDefinition $csharpCode -Language CSharp

# Helper: replicate DevicePathConverter.ToInstanceId from C#
function Convert-DevicePathToInstanceId($path) {
    $s = $path
    if ($s.StartsWith('\\?\')) { $s = $s.Substring(4) }
    $lastHashBrace = $s.LastIndexOf('#{')
    if ($lastHashBrace -ge 0) { $s = $s.Substring(0, $lastHashBrace) }
    $s = $s.Replace('#', '\')
    return $s.ToUpperInvariant()
}

Log "============================================================"
Log "ControlShift ROG Ally X Diagnostic Dump"
Log "Timestamp: $timestamp"
Log "Machine: $env:COMPUTERNAME"
$osName = (Get-CimInstance Win32_OperatingSystem).Caption
Log "OS: $osName"
Log "============================================================"
Log ""

# ══════════════════════════════════════════════════════════════════════════════
# SECTION 1: Full HID Device Dump
# ══════════════════════════════════════════════════════════════════════════════
Log "== SECTION 1: Full HID Device Dump =="
Log ""

HidLog "ControlShift HID Device Dump - $timestamp"
HidLog "============================================================"
HidLog ""

$hidDevices = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Sort-Object InstanceId
$hidCount = 0

foreach ($dev in $hidDevices) {
    $hidCount++
    $hwIds = ''
    if ($dev.HardwareID) { $hwIds = $dev.HardwareID -join '; ' }
    HidLog "[$hidCount] $($dev.FriendlyName)"
    HidLog "  InstanceId:  $($dev.InstanceId)"
    HidLog "  Status:      $($dev.Status)"
    HidLog "  Class:       $($dev.Class)"
    HidLog "  Manufacturer:$($dev.Manufacturer)"
    HidLog "  HardwareID:  $hwIds"
    HidLog ""

    # Also log ASUS/gamepad-related ones to main dump
    $isAsus = $dev.InstanceId -match '0B05'
    $isGameRelated = $dev.FriendlyName -match 'ASUS|ROG|Ally|game|controller|gamepad|xbox|xinput'
    if ($isAsus -or $isGameRelated) {
        Log "  [HID #$hidCount] $($dev.FriendlyName)"
        Log "    InstanceId: $($dev.InstanceId)"
        Log "    Status:     $($dev.Status)"
        Log "    HardwareID: $hwIds"
        Log ""
    }
}

Log "Total HID devices enumerated: $hidCount"
Log "(Full list written to $hidDumpPath)"
Log ""

# ══════════════════════════════════════════════════════════════════════════════
# SECTION 2: ASUS / ROG / Ally / Controller PnP Devices (all classes)
# ══════════════════════════════════════════════════════════════════════════════
Log "== SECTION 2: Get-PnpDevice ASUS/ROG/Ally/Controller filter =="
Log ""

$pnpDevices = Get-PnpDevice | Where-Object {
    $_.FriendlyName -like '*ASUS*' -or
    $_.FriendlyName -like '*ROG*' -or
    $_.FriendlyName -like '*Ally*' -or
    $_.FriendlyName -like '*controller*' -or
    $_.FriendlyName -like '*gamepad*' -or
    $_.FriendlyName -like '*Xbox*' -or
    $_.FriendlyName -like '*XInput*'
} | Sort-Object Class, InstanceId

if ($pnpDevices) {
    foreach ($dev in $pnpDevices) {
        $hwIds = ''
        if ($dev.HardwareID) { $hwIds = $dev.HardwareID -join '; ' }
        Log "  [$($dev.Class)] $($dev.FriendlyName)"
        Log "    InstanceId:   $($dev.InstanceId)"
        Log "    Status:       $($dev.Status)"
        Log "    Manufacturer: $($dev.Manufacturer)"
        Log "    HardwareID:   $hwIds"
        Log ""
    }
    Log "Total matching devices: $($pnpDevices.Count)"
}
else {
    Log "  (No matching devices found)"
}
Log ""

# Search by VID 0B05 (ASUS vendor ID) across ALL device classes
Log "-- Additional: All PnP devices with VID 0B05 (ASUS) --"
Log ""

$asusVidDevices = Get-PnpDevice | Where-Object {
    $_.InstanceId -match 'VID_0B05' -or
    $_.InstanceId -match '0B05'
} | Sort-Object Class, InstanceId

if ($asusVidDevices) {
    foreach ($dev in $asusVidDevices) {
        $hwIds = ''
        if ($dev.HardwareID) { $hwIds = $dev.HardwareID -join '; ' }
        Log "  [$($dev.Class)] $($dev.FriendlyName)"
        Log "    InstanceId:   $($dev.InstanceId)"
        Log "    Status:       $($dev.Status)"
        Log "    HardwareID:   $hwIds"
        Log ""
    }
    Log "Total ASUS VID devices: $($asusVidDevices.Count)"
}
else {
    Log "  (No devices with VID 0B05 found)"
}
Log ""

# ══════════════════════════════════════════════════════════════════════════════
# SECTION 3: PnP Parent Chain Walk for ASUS Integrated Controller
# ══════════════════════════════════════════════════════════════════════════════
Log "== SECTION 3: PnP Parent Chain for ASUS Controller =="
Log ""

# Find all HID devices that look like the ASUS integrated gamepad
$allyHidDevices = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object {
    $_.InstanceId -match '0B05'
}

# Also check for IG_ (XInput interface) devices with ASUS VID
$allyXInputDevices = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    ($_.InstanceId -match '0B05') -and ($_.InstanceId -match 'IG_')
}

$allAllyDevices = @()
if ($allyHidDevices) { $allAllyDevices += $allyHidDevices }
if ($allyXInputDevices) { $allAllyDevices += $allyXInputDevices }
$allAllyDevices = $allAllyDevices | Sort-Object InstanceId -Unique

$allyCount = 0
if ($allAllyDevices) { $allyCount = @($allAllyDevices).Count }

if ($allyCount -eq 0) {
    Log "  No ASUS (VID 0B05) HID/XInput devices found."
    Log "  Searching broader: any HID device with gamepad/game/controller in name..."
    $allAllyDevices = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object {
        $_.FriendlyName -match 'game|controller|gamepad'
    }
}

foreach ($dev in $allAllyDevices) {
    Log "-- Parent chain for: $($dev.FriendlyName) --"
    Log "   InstanceId: $($dev.InstanceId)"
    Log "   Status:     $($dev.Status)"
    Log ""

    $chain = [CfgMgr32Diag]::WalkParentChain($dev.InstanceId, 15)
    foreach ($entry in $chain) {
        Log "   $entry"
    }
    Log ""
}

# ══════════════════════════════════════════════════════════════════════════════
# SECTION 4: DevicePathConverter Instance ID Simulation
# ══════════════════════════════════════════════════════════════════════════════
Log "== SECTION 4: What ControlShift passes to HidHide =="
Log ""
Log "DevicePathConverter.ToInstanceId() logic:"
Log "  1. Strip \\?\ prefix"
Log '  2. Remove trailing #{GUID}'
Log '  3. Replace # with \'
Log "  4. ToUpperInvariant()"
Log ""

# Get device interface paths for ASUS HID devices
$asusInterfaces = Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object {
    $_.InstanceId -match '0B05'
}

foreach ($dev in $asusInterfaces) {
    Log "-- Device: $($dev.FriendlyName) --"
    Log "   PnP InstanceId (from Get-PnpDevice): $($dev.InstanceId)"

    # Reconstruct the HidSharp-style device path from the instance ID
    $instId = $dev.InstanceId
    $hidGuid = '{4d1e55b2-f16f-11cf-88cb-001111000030}'
    $simulatedPath = '\\?\' + $instId.Replace('\', '#').ToLower() + '#' + $hidGuid

    Log "   Simulated HidSharp path: $simulatedPath"

    $convertedId = Convert-DevicePathToInstanceId $simulatedPath
    Log "   DevicePathConverter output: $convertedId"
    $upperInstId = $instId.ToUpperInvariant()
    $match = ($convertedId -eq $upperInstId)
    Log "   Matches PnP InstanceId:     $match"
    Log ""
}

# Enumerate actual device interfaces from registry
Log "-- Actual HID Device Interface Paths (from registry) --"
Log ""

$hidInterfaceGuid = '{4d1e55b2-f16f-11cf-88cb-001111000030}'
$regBase = "HKLM:\SYSTEM\CurrentControlSet\Control\DeviceClasses\$hidInterfaceGuid"

if (Test-Path $regBase) {
    $interfaces = Get-ChildItem $regBase -ErrorAction SilentlyContinue
    $asusInterfacePaths = $interfaces | Where-Object { $_.PSChildName -match '0[Bb]05' }

    foreach ($iface in $asusInterfacePaths) {
        $symLink = $iface.PSChildName
        Log "   Registry key name: $symLink"

        # Convert registry format back to device path
        # Registry uses ## for \\
        $pathFromReg = $symLink -replace '^##\?#', '\\?\'
        Log "   Reconstructed path: $pathFromReg"

        $convertedId = Convert-DevicePathToInstanceId $pathFromReg
        Log "   DevicePathConverter output: $convertedId"
        Log ""
    }
}
else {
    Log "   (HID device interface registry key not found)"
}

# ══════════════════════════════════════════════════════════════════════════════
# SECTION 5: XInput Slot Info
# ══════════════════════════════════════════════════════════════════════════════
Log "== SECTION 5: XInput / Game Controller Status =="
Log ""

$xInputNodes = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    $_.InstanceId -match 'IG_'
} | Sort-Object InstanceId

if ($xInputNodes) {
    foreach ($dev in $xInputNodes) {
        Log "  [$($dev.Class)] $($dev.FriendlyName)"
        Log "    InstanceId: $($dev.InstanceId)"
        Log "    Status:     $($dev.Status)"

        if ($dev.InstanceId -match '(IG_\d+)') {
            Log "    XInput Interface: $($Matches[1])"
        }
        Log ""
    }
    Log "Total IG_ nodes: $($xInputNodes.Count)"
}
else {
    Log "  (No IG_ XInput interface nodes found)"
}
Log ""

# ══════════════════════════════════════════════════════════════════════════════
# SECTION 6: HidHide Current State
# ══════════════════════════════════════════════════════════════════════════════
Log "== SECTION 6: HidHide Driver Current State =="
Log ""

$hidHideRegPath = 'HKLM:\SYSTEM\CurrentControlSet\Services\HidHide\Parameters'

if (Test-Path $hidHideRegPath) {
    Log "  HidHide driver registry found at: $hidHideRegPath"

    $params = Get-ItemProperty -Path $hidHideRegPath -ErrorAction SilentlyContinue
    if ($params) {
        if ($null -ne $params.Active) {
            Log "  Active (hiding enabled): $($params.Active)"
        }

        if ($params.BlockedDevices) {
            Log ""
            Log "  Currently blocked instance IDs:"
            foreach ($blocked in $params.BlockedDevices) {
                if ($blocked) {
                    Log "    - $blocked"
                }
            }
        }
        else {
            Log "  Blocked devices list: (empty)"
        }

        if ($params.AllowedApplications) {
            Log ""
            Log "  Allowed applications:"
            foreach ($app in $params.AllowedApplications) {
                if ($app) {
                    Log "    - $app"
                }
            }
        }
        else {
            Log "  Allowed applications list: (empty)"
        }
    }
}
else {
    Log "  HidHide parameters registry key NOT found"
    Log "  (Driver may not be installed or may use a different registry location)"
}

$hidHideDevice = Get-PnpDevice -FriendlyName '*HidHide*' -ErrorAction SilentlyContinue
if ($hidHideDevice) {
    Log ""
    Log "  HidHide PnP device:"
    foreach ($dev in $hidHideDevice) {
        Log "    Name: $($dev.FriendlyName)"
        Log "    InstanceId: $($dev.InstanceId)"
        Log "    Status: $($dev.Status)"
    }
}
Log ""

# ══════════════════════════════════════════════════════════════════════════════
# SECTION 7: ViGEmBus Status
# ══════════════════════════════════════════════════════════════════════════════
Log "== SECTION 7: ViGEmBus Status =="
Log ""

$vigemDevices = Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object {
    $_.FriendlyName -like '*ViGEm*' -or $_.FriendlyName -like '*Virtual Gamepad*'
}

if ($vigemDevices) {
    foreach ($dev in $vigemDevices) {
        Log "  $($dev.FriendlyName)"
        Log "    InstanceId: $($dev.InstanceId)"
        Log "    Status:     $($dev.Status)"
    }
}
else {
    Log "  (No ViGEm devices found)"
}
Log ""

# ══════════════════════════════════════════════════════════════════════════════
# Write output files
# ══════════════════════════════════════════════════════════════════════════════
Log "============================================================"
Log "Diagnostic dump complete."
Log "  Main dump: $allyDumpPath"
Log "  HID dump:  $hidDumpPath"
Log "============================================================"

$output -join "`n" | Out-File -FilePath $allyDumpPath -Encoding UTF8
$hidOutput -join "`n" | Out-File -FilePath $hidDumpPath -Encoding UTF8

Write-Host ""
Write-Host "Files written successfully." -ForegroundColor Green
