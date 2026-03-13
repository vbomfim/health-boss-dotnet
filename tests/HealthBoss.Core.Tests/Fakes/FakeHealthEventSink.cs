using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests.Fakes;

/// <summary>
/// Test double that records all published health events for assertion in tests.
/// Thread-safe for concurrent dispatch testing.
/// </summary>
internal sealed class FakeHealthEventSink : IHealthEventSink
{
    private readonly List<TenantHealthEvent> _events = [];
    private readonly List<HealthEvent> _healthEvents = [];
    private readonly object _lock = new();

    public IReadOnlyList<TenantHealthEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return _events.ToList();
            }
        }
    }

    public IReadOnlyList<HealthEvent> HealthEvents
    {
        get
        {
            lock (_lock)
            {
                return _healthEvents.ToList();
            }
        }
    }

    public int EventCount
    {
        get
        {
            lock (_lock)
            {
                return _events.Count;
            }
        }
    }

    public Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _healthEvents.Add(healthEvent);
        }

        return Task.CompletedTask;
    }

    public Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _events.Add(tenantEvent);
        }

        return Task.CompletedTask;
    }
}
