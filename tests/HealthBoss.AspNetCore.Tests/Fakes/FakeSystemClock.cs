using HealthBoss.Core;

namespace HealthBoss.AspNetCore.Tests.Fakes;

/// <summary>
/// Controllable clock for deterministic latency testing.
/// </summary>
internal sealed class FakeSystemClock : ISystemClock
{
    private DateTimeOffset _now = new(2024, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan duration) => _now += duration;

    public void SetUtcNow(DateTimeOffset value) => _now = value;
}
