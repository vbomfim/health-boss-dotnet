using FluentAssertions;
using HealthBoss.Core.Components;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

public sealed class SystemClockTests
{
    [Fact]
    public void UtcNow_returns_time_from_provider()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        var expected = new DateTimeOffset(2024, 6, 15, 10, 30, 0, TimeSpan.Zero);
        fakeTimeProvider.SetUtcNow(expected);

        var clock = new SystemClock(fakeTimeProvider);

        clock.UtcNow.Should().Be(expected);
    }

    [Fact]
    public void UtcNow_advances_with_provider()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        fakeTimeProvider.SetUtcNow(TestFixtures.BaseTime);
        var clock = new SystemClock(fakeTimeProvider);

        var before = clock.UtcNow;
        fakeTimeProvider.Advance(TimeSpan.FromMinutes(5));
        var after = clock.UtcNow;

        after.Should().Be(before.AddMinutes(5));
    }
}
