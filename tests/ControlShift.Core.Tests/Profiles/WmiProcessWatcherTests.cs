using ControlShift.Core.Profiles;
using FluentAssertions;

namespace ControlShift.Core.Tests.Profiles;

/// <summary>
/// Unit tests for WmiProcessWatcher — no WMI integration tests in CI
/// (WMI may not be available in GitHub Actions runners).
/// Tests validate the public API contract and edge cases.
/// </summary>
public sealed class WmiProcessWatcherTests : IDisposable
{
    private readonly WmiProcessWatcher _watcher = new();

    [Fact]
    public void IsWatching_InitiallyFalse()
    {
        _watcher.IsWatching.Should().BeFalse();
    }

    [Fact]
    public void StartWatching_EmptyList_RemainsNotWatching()
    {
        _watcher.StartWatching(Array.Empty<string>());
        _watcher.IsWatching.Should().BeFalse();
    }

    [Fact]
    public void StopWatching_WhenNotWatching_DoesNotThrow()
    {
        Action act = () => _watcher.StopWatching();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_WhenNotWatching_DoesNotThrow()
    {
        Action act = () => _watcher.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        _watcher.Dispose();
        Action act = () => _watcher.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void EscapeWql_EscapesSingleQuotes()
    {
        WmiProcessWatcher.EscapeWql("game's.exe").Should().Be("game''s.exe");
    }

    [Fact]
    public void EscapeWql_PlainString_Unchanged()
    {
        WmiProcessWatcher.EscapeWql("eldenring.exe").Should().Be("eldenring.exe");
    }

    public void Dispose() => _watcher.Dispose();
}
