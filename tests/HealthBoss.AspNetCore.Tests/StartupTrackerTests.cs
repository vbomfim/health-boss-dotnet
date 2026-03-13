using FluentAssertions;
using HealthBoss.Core.Contracts;

namespace HealthBoss.AspNetCore.Tests;

public sealed class StartupTrackerTests
{
    [Fact]
    public void Initial_status_is_starting()
    {
        var tracker = new StartupTracker();

        tracker.Status.Should().Be(StartupStatus.Starting);
    }

    [Fact]
    public void MarkReady_sets_status_to_ready()
    {
        var tracker = new StartupTracker();

        tracker.MarkReady();

        tracker.Status.Should().Be(StartupStatus.Ready);
    }

    [Fact]
    public void MarkFailed_sets_status_to_failed()
    {
        var tracker = new StartupTracker();

        tracker.MarkFailed();

        tracker.Status.Should().Be(StartupStatus.Failed);
    }

    [Fact]
    public void MarkReady_then_MarkFailed_results_in_failed()
    {
        var tracker = new StartupTracker();

        tracker.MarkReady();
        tracker.MarkFailed();

        tracker.Status.Should().Be(StartupStatus.Failed);
    }

    [Fact]
    public void Multiple_MarkReady_calls_are_idempotent()
    {
        var tracker = new StartupTracker();

        tracker.MarkReady();
        tracker.MarkReady();
        tracker.MarkReady();

        tracker.Status.Should().Be(StartupStatus.Ready);
    }
}
