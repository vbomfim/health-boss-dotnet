# ADR-001: ObservableGauge Push Model

> **Status:** Accepted
> **Date:** 2025-01-01
> **Applies to:** `HealthBossMetrics` (all 7 observable gauges)
> **See also:** [COMPONENT-DESIGN.md](COMPONENT-DESIGN.md) · [METRICS-CARDINALITY.md](../METRICS-CARDINALITY.md)

## Context

OpenTelemetry's standard pattern for `ObservableGauge` instruments uses a **callback (pull) model**: the meter invokes a registered callback at collection time, and the callback returns current measurements by reading them directly from the source.

```csharp
// Standard OTel callback model (NOT what HealthBoss uses)
meter.CreateObservableGauge("my.gauge", () =>
{
    // Callback reads directly from the source component
    var value = someService.GetCurrentValue();
    return new Measurement<int>(value);
});
```

HealthBoss has **7 observable gauges** (health state, active sessions, drain status, quorum healthy/total/met, tenant count). The standard callback model would require the gauge callbacks inside `HealthBossMetrics` to hold references to the source components (e.g., `IHealthStateMachine`, `ISessionHealthTracker`, `IDrainCoordinator`) so they can read current values at collection time.

## Problem

Using the standard OTel callback model creates a **circular dependency in the DI container**:

```
HealthBossMetrics
    → needs IHealthStateMachine (to read health_state in callback)
    → needs ISessionHealthTracker (to read active_sessions in callback)
    → needs IDrainCoordinator (to read drain_status in callback)

But:

HealthStateMachine → needs IHealthBossMetrics (to record state transitions)
SessionHealthTracker → needs IHealthBossMetrics (to record session metrics)
DrainCoordinator → needs IHealthBossMetrics (to record drain status)
```

This is a **circular DI dependency**: `HealthBossMetrics ↔ HealthStateMachine`. .NET's built-in DI container does not support circular dependencies and would throw at resolution time.

### Alternatives considered

| Alternative | Why rejected |
|---|---|
| **Lazy<T> injection** | Defers resolution but doesn't eliminate the cycle. `Lazy<IHealthStateMachine>` still requires the service to be resolvable, and both services are singletons created at startup. |
| **Service Locator** | Anti-pattern. Callbacks would call `IServiceProvider.GetRequiredService<T>()` at collection time — hides dependencies, untestable, violates DI principles. |
| **Split HealthBossMetrics into read/write interfaces** | Would work but adds significant interface complexity (separate `IMetricsWriter` and `IMetricsReader`) for a problem with a simpler solution. |
| **Event-based decoupling** | Components publish events, metrics subscribes — adds unnecessary indirection and allocation for simple gauge updates. |

## Decision

**HealthBoss uses a push model for ObservableGauge state.** Components push their current values into `HealthBossMetrics` via `SetXxx()` methods, and the gauge callbacks read from internal concurrent state.

```csharp
// Push model: component writes, gauge callback reads
public void SetHealthState(string component, HealthState state)
{
    _healthStates[component] = (int)state;  // ConcurrentDictionary
}

public void SetActiveSessionCount(int count)
{
    Volatile.Write(ref _activeSessionCount, count);  // volatile int
}

// Gauge callback reads the stored state — no external dependency needed
_meter.CreateObservableGauge("healthboss.health_state",
    observeValues: () => _healthStates.Select(kvp =>
        new Measurement<int>(kvp.Value, new TagList { { "component", kvp.Key } })));
```

### Dependency direction (push model)

```
HealthStateMachine → IStateMachineMetrics.SetHealthState()
SessionHealthTracker → ISessionMetrics.SetActiveSessionCount()
DrainCoordinator → ISessionMetrics.SetDrainStatus()

HealthBossMetrics implements all metric interfaces
    → gauge callbacks read internal ConcurrentDictionary / Volatile fields
    → NO references to source components
```

**No circular dependency.** The dependency arrow flows one way: components → metrics.

## Consequences

### Positive

- **No circular DI dependency** — the primary motivation.
- **Simple DI graph** — `HealthBossMetrics` has a single constructor dependency (`IMeterFactory`).
- **Thread-safe by construction** — `ConcurrentDictionary` and `Volatile` are proven primitives.
- **Testable** — tests call `SetXxx()` directly and verify gauge output without wiring up full component graphs.
- **Interface Segregation** — components depend on narrow `IStateMachineMetrics`, `ISessionMetrics`, etc., not the full `IHealthBossMetrics`.

### Negative

- **Stale reads possible** — if a component fails to call `SetXxx()`, the gauge reports the last written value. In practice, this is equivalent to the callback model (which would also return the last known value from the source).
- **Not idiomatic OTel** — deviates from the standard callback pattern. New contributors may expect the callback model. This ADR serves as the explanation.

### Neutral

- **Memory overhead** — one `ConcurrentDictionary` entry per component per gauge dimension. With ≤ 100 components (the recommended cardinality limit), this is negligible.

## References

- [OpenTelemetry .NET — ObservableGauge](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation#observablegauge)
- [.NET Dependency Injection — Circular Dependencies](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection-guidelines#disposal-of-services)
- GitHub Issue #67 — Low-priority docs + info fixes
