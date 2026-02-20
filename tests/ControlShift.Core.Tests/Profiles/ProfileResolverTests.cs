using FluentAssertions;
using ControlShift.Core.Models;
using ControlShift.Core.Profiles;

namespace ControlShift.Core.Tests.Profiles;

public class ProfileResolverTests
{
    private static Profile CreateProfile(params string?[] vidPids)
    {
        return new Profile
        {
            ProfileName = "Test",
            SlotAssignments = vidPids,
        };
    }

    private static List<(string VidPid, string? DevicePath)> CreateConnected(
        params (string vidPid, string devicePath)[] controllers)
    {
        return controllers.Select(c => (c.vidPid, (string?)c.devicePath)).ToList();
    }

    [Fact]
    public void Resolve_MatchingVidPid_ReturnsDevicePath()
    {
        var profile = CreateProfile("045E:02FD", null, null, null);
        var connected = CreateConnected(
            ("045E:02FD", @"\\?\hid#vid_045e&pid_02fd#abc"));

        var result = ProfileResolver.Resolve(profile, connected);

        result.Should().HaveCount(4);
        result[0].TargetSlot.Should().Be(0);
        result[0].SourceDevicePath.Should().Be(@"\\?\hid#vid_045e&pid_02fd#abc");
    }

    [Fact]
    public void Resolve_NoMatchingController_ReturnsNullDevicePath()
    {
        var profile = CreateProfile("DEAD:BEEF", null, null, null);
        var connected = CreateConnected(
            ("045E:02FD", @"\\?\hid#vid_045e&pid_02fd#abc"));

        var result = ProfileResolver.Resolve(profile, connected);

        result[0].SourceDevicePath.Should().BeNull();
    }

    [Fact]
    public void Resolve_NullSlotAssignment_SkipsSlot()
    {
        var profile = CreateProfile(null, "045E:02FD", null, null);
        var connected = CreateConnected(
            ("045E:02FD", @"\\?\hid#vid_045e&pid_02fd#abc"));

        var result = ProfileResolver.Resolve(profile, connected);

        result[0].SourceDevicePath.Should().BeNull();
        result[1].SourceDevicePath.Should().Be(@"\\?\hid#vid_045e&pid_02fd#abc");
    }

    [Fact]
    public void Resolve_DuplicateVidPid_ClaimsFirstMatchOnly()
    {
        // Two slots want the same VID:PID, but only one physical controller is connected.
        var profile = CreateProfile("045E:02FD", "045E:02FD", null, null);
        var connected = CreateConnected(
            ("045E:02FD", @"\\?\hid#path1"),
            ("045E:02FD", @"\\?\hid#path2"));

        var result = ProfileResolver.Resolve(profile, connected);

        // First slot gets first match, second slot gets second match.
        result[0].SourceDevicePath.Should().Be(@"\\?\hid#path1");
        result[1].SourceDevicePath.Should().Be(@"\\?\hid#path2");
    }

    [Fact]
    public void Resolve_DuplicateVidPid_SingleController_SecondSlotGetsNull()
    {
        // Two slots want the same VID:PID, but only one physical controller exists.
        var profile = CreateProfile("045E:02FD", "045E:02FD", null, null);
        var connected = CreateConnected(
            ("045E:02FD", @"\\?\hid#path1"));

        var result = ProfileResolver.Resolve(profile, connected);

        result[0].SourceDevicePath.Should().Be(@"\\?\hid#path1");
        result[1].SourceDevicePath.Should().BeNull();
    }

    [Fact]
    public void Resolve_CaseInsensitiveVidPid()
    {
        var profile = CreateProfile("045e:02fd", null, null, null);
        var connected = CreateConnected(
            ("045E:02FD", @"\\?\hid#path1"));

        var result = ProfileResolver.Resolve(profile, connected);

        result[0].SourceDevicePath.Should().Be(@"\\?\hid#path1");
    }

    [Fact]
    public void Resolve_EmptyConnectedList_AllSlotsNull()
    {
        var profile = CreateProfile("045E:02FD", "054C:0CE6", null, null);
        var connected = new List<(string VidPid, string? DevicePath)>();

        var result = ProfileResolver.Resolve(profile, connected);

        result.Should().HaveCount(4);
        result.Should().AllSatisfy(a => a.SourceDevicePath.Should().BeNull());
    }
}
