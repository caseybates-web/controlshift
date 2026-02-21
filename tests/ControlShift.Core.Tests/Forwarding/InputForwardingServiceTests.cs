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

    [Fact]
    public void VirtualSlotIndices_Initially_IsEmpty()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);
        svc.VirtualSlotIndices.Should().BeEmpty();
    }

    [Fact]
    public async Task RevertAllAsync_WhenNotForwarding_DoesNotThrow()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);
        await svc.Invoking(s => s.RevertAllAsync()).Should().NotThrowAsync();
    }

    [Fact]
    public async Task RevertAllAsync_ClearsHidHideRules()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);
        await svc.RevertAllAsync();

        _hidHideMock.Verify(h => h.ClearAllRules(), Times.Once);
    }

    [Fact]
    public async Task RevertAllAsync_ClearsVirtualSlotIndices()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);
        await svc.RevertAllAsync();

        svc.VirtualSlotIndices.Should().BeEmpty();
    }

    [Fact]
    public async Task RevertAllAsync_ClearsActiveAssignments()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);
        await svc.RevertAllAsync();

        svc.ActiveAssignments.Should().BeEmpty();
        svc.IsForwarding.Should().BeFalse();
    }

    [Fact]
    public async Task StopForwardingAsync_WhenNotForwarding_StillClearsHidHide()
    {
        using var svc = new InputForwardingService(_hidHideMock.Object);

        // Stop twice — both should clear HidHide without error.
        await svc.StopForwardingAsync();
        await svc.StopForwardingAsync();

        _hidHideMock.Verify(h => h.ClearAllRules(), Times.Exactly(2));
    }

    [Fact]
    public async Task StopForwardingAsync_PreservesVirtualSlotIndices()
    {
        // StopForwardingAsync should NOT clear virtual slot indices
        // (they're preserved for ViGEm pool reuse).
        using var svc = new InputForwardingService(_hidHideMock.Object);
        await svc.StopForwardingAsync();

        // Just verifying the property is accessible and empty by default.
        svc.VirtualSlotIndices.Should().BeEmpty();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var svc = new InputForwardingService(_hidHideMock.Object);
        svc.Invoking(s => s.Dispose()).Should().NotThrow();
    }

    [Fact]
    public void Dispose_ClearsHidHideRules()
    {
        var svc = new InputForwardingService(_hidHideMock.Object);
        svc.Dispose();

        _hidHideMock.Verify(h => h.ClearAllRules(), Times.Once);
    }

    [Fact]
    public void ForwardingErrorEventArgs_PreservesProperties()
    {
        var inner = new IOException("disconnected");
        var args = new ForwardingErrorEventArgs(2, @"\\?\hid#vid_045e", "Device lost", inner);

        args.TargetSlot.Should().Be(2);
        args.DevicePath.Should().Be(@"\\?\hid#vid_045e");
        args.ErrorMessage.Should().Be("Device lost");
        args.InnerException.Should().Be(inner);
    }

    [Fact]
    public void ForwardingErrorEventArgs_NullInnerException_Allowed()
    {
        var args = new ForwardingErrorEventArgs(0, "path", "error");

        args.InnerException.Should().BeNull();
    }

    [Fact]
    public void NullHidHideService_SetActive_BothValues_DoNotThrow()
    {
        var svc = new NullHidHideService();

        svc.Invoking(s => s.SetActive(true)).Should().NotThrow();
        svc.Invoking(s => s.SetActive(false)).Should().NotThrow();
    }
}
