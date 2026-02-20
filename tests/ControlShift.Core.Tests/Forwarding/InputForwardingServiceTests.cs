using FluentAssertions;
using ControlShift.Core.Devices;
using ControlShift.Core.Forwarding;
using ControlShift.Core.Models;

namespace ControlShift.Core.Tests.Forwarding;

public class InputForwardingServiceTests
{
    private readonly Mock<IHidHideService> _hidHideMock = new();

    [Fact]
    public void IsForwarding_Initially_IsFalse()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);
        svc.IsForwarding.Should().BeFalse();
    }

    [Fact]
    public void ActiveAssignments_Initially_IsEmpty()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);
        svc.ActiveAssignments.Should().BeEmpty();
    }

    [Fact]
    public async Task StopForwardingAsync_WhenNotForwarding_DoesNotThrow()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);
        await svc.Invoking(s => s.StopForwardingAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StopForwardingAsync_ClearsHidHideRules()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);
        await svc.StopForwardingAsync();

        _hidHideMock.Verify(h => h.ClearAllRules(), Times.Once);
    }

    [Fact]
    public async Task StartForwardingAsync_EmptyAssignments_DoesNotThrow()
    {
        // With no assignments that have a SourceDevicePath, nothing to forward.
        var assignments = new[]
        {
            new SlotAssignment { TargetSlot = 0 },
            new SlotAssignment { TargetSlot = 1 },
        };

        using var svc = new InputForwardingService(_hidHideMock.Object);

        // This will try to create a ViGEmClient — which requires the driver.
        // On dev machines without ViGEmBus, this throws. That's expected.
        // We test the orchestration logic, not the driver.
        try
        {
            await svc.StartForwardingAsync(assignments);
            // If we got here, ViGEm is installed and the service started successfully
            // with no active pairs (all assignments had null SourceDevicePath).
            svc.IsForwarding.Should().BeFalse(); // No pairs created for null device paths
        }
        catch (Nefarius.ViGEm.Client.Exceptions.VigemBusNotFoundException)
        {
            // Expected on machines without ViGEmBus driver.
        }
    }

    [Fact]
    public void CrashSafetyGuard_Install_ClearsRulesImmediately()
    {
        var mock = new Mock<IHidHideService>();
        CrashSafetyGuard.Install(mock.Object);

        mock.Verify(h => h.ClearAllRules(), Times.Once);
    }

    [Fact]
    public void NullHidHideService_IsDriverInstalled_ReturnsFalse()
    {
        var svc = new NullHidHideService();
        svc.IsDriverInstalled.Should().BeFalse();
    }

    [Fact]
    public void NullHidHideService_AllMethods_DoNotThrow()
    {
        var svc = new NullHidHideService();

        svc.Invoking(s => s.AddApplicationRule("test.exe")).Should().NotThrow();
        svc.Invoking(s => s.HideDevice("instance")).Should().NotThrow();
        svc.Invoking(s => s.UnhideDevice("instance")).Should().NotThrow();
        svc.Invoking(s => s.ClearAllRules()).Should().NotThrow();
        svc.Invoking(s => s.SetActive(true)).Should().NotThrow();
    }
}
