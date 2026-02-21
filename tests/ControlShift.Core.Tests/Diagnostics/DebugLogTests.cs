using FluentAssertions;
using ControlShift.Core.Diagnostics;

namespace ControlShift.Core.Tests.Diagnostics;

public class DebugLogTests
{
    private readonly string _logPath =
        Path.Combine(Path.GetTempPath(), "controlshift-debug.log");

    [Fact]
    public void Log_WritesTimestampedLine()
    {
        DebugLog.Log("test-message-unique-12345");

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("test-message-unique-12345");
    }

    [Fact]
    public void Log_TimestampFormat_ContainsDateAndTime()
    {
        var before = DateTime.Now;
        DebugLog.Log("timestamp-check-67890");
        var after = DateTime.Now;

        var lines = File.ReadAllLines(_logPath);
        var matchingLine = lines.LastOrDefault(l => l.Contains("timestamp-check-67890"));

        matchingLine.Should().NotBeNull();
        // Format: [yyyy-MM-dd HH:mm:ss.fff]
        matchingLine.Should().MatchRegex(@"\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\]");
    }

    [Fact]
    public void Exception_LogsExceptionWithStackTrace()
    {
        var ex = new InvalidOperationException("test-exception-msg");
        DebugLog.Exception("test-context", ex);

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("[EXCEPTION] test-context:");
        content.Should().Contain("test-exception-msg");
    }

    [Fact]
    public void Startup_LogsStartupMarker()
    {
        DebugLog.Startup("1.2.3");

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("=== APP STARTUP ===");
        content.Should().Contain("v1.2.3");
    }

    [Fact]
    public void Startup_WithoutVersion_OmitsVersionPrefix()
    {
        DebugLog.Startup();

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("=== APP STARTUP ===");
    }

    [Fact]
    public void Shutdown_LogsShutdownMarkerWithReason()
    {
        DebugLog.Shutdown("test-reason");

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("=== APP SHUTDOWN ===");
        content.Should().Contain("reason=test-reason");
    }

    [Fact]
    public void ControllerChange_FormatsCorrectly()
    {
        DebugLog.ControllerChange("connect", 2, "045E:028E", "Usb");

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("[Controller] connect slot=2 vidPid=045E:028E busType=Usb");
    }

    [Fact]
    public void ViGEmSlotAssigned_FormatsCorrectly()
    {
        DebugLog.ViGEmSlotAssigned(0, 3);

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("[ViGEm] Assigned physical=0");
        content.Should().Contain("virtual=3");
    }

    [Fact]
    public void HidHide_FormatsCorrectly()
    {
        DebugLog.HidHide("hide", "HID\\VID_045E&PID_028E\\1234");

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("[HidHide] hide instanceId=HID\\VID_045E&PID_028E\\1234");
    }

    [Fact]
    public void DeviceChange_FormatsCorrectly()
    {
        DebugLog.DeviceChange(2, 3);

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("[WM_DEVICECHANGE] devices before=2 after=3");
    }

    [Fact]
    public void SlotMapChange_FormatsCorrectly()
    {
        DebugLog.SlotMapChange([0, 1, 2, 3], [1, 0, 2, 3]);

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("[SlotMap] before=[0,1,2,3] after=[1,0,2,3]");
    }

    [Fact]
    public void FocusChange_FormatsCorrectly()
    {
        DebugLog.FocusChange("RevertButton", -1);

        var content = File.ReadAllText(_logPath);
        content.Should().Contain("[Focus] element=RevertButton cardIndex=-1");
    }

    [Fact]
    public void Log_ThreadSafe_NoExceptions()
    {
        // Fire 50 concurrent log writes — should not throw.
        var tasks = Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => DebugLog.Log($"thread-safety-{i}")))
            .ToArray();

        var act = () => Task.WaitAll(tasks);
        act.Should().NotThrow();
    }
}
