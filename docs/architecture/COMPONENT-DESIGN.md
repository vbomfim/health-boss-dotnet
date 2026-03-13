
The consolidated v1.0 specification defines 4 NuGet packages and their public APIs but treats each package as a monolith. This document decomposes the internal architecture into **26 discrete components** across all 4 packages, each with a single responsibility, explicit interface contracts, and a validated dependency graph.

**Design principle:** Every component should be rewritable without changing any other component. Interfaces are the only coupling between components. No component directly references another component's internals.

---

## Table of Contents

1. [Package Dependency Rules](#1-package-dependency-rules)
2. [Shared Value Types (Contracts)](#2-shared-value-types)
3. [HealthBoss.Core — 15 Components](#3-healthboss-core)
4. [HealthBoss.AspNetCore — 6 Components](#4-healthboss-aspnetcore)
5. [HealthBoss.Polly — 2 Components](#5-healthboss-polly)
6. [HealthBoss.Grpc — 3 Components](#6-healthboss-grpc)
7. [Full Component Dependency Graph](#7-dependency-graph)
8. [Layering Rules](#8-layering-rules)
9. [Rewritability Validation Matrix](#9-rewritability-validation)
10. [INVEST Quality Check](#10-invest-check)

---

## 1. Package Dependency Rules

```
HealthBoss.Core            (zero external deps beyond net9.0 BCL)
    ↑           ↑           ↑
    │           │           │
AspNetCore    Polly       Grpc
(→ Microsoft.AspNetCore)  (→ Polly.Core)  (→ Grpc.Net.Client)
```

| Rule | Enforcement |
|------|-------------|
| Core has **zero** dependencies on ASP.NET Core, Polly, or gRPC | `<PackageReference>` audit in CI |
| Transport packages depend on Core + their transport library only | `<PackageReference>` audit in CI |
| Transport packages **never** depend on sibling transport packages | `<PackageReference>` audit in CI |
| All cross-component communication goes through interfaces defined in Core | Code review + ArchUnit tests |

---

## 2. Shared Value Types (Contracts)

These are **immutable records and enums** in `HealthBoss.Core.Contracts`. They are the stable data shapes that flow between components. All components depend on these; none own them exclusively.

```csharp
namespace HealthBoss.Core.Contracts;

// ─── Identifiers ───────────────────────────────────────────────────────────

/// <summary>Strongly-typed identifier for a monitored dependency.</summary>
public readonly record struct DependencyId(string Value);

/// <summary>Strongly-typed identifier for a tenant in multi-tenant scenarios.</summary>
public readonly record struct TenantId(string Value);

// ─── Enums ─────────────────────────────────────────────────────────────────

/// <summary>
/// Health state of a single dependency. CircuitOpen is the terminal state
/// (the pod stays up but stops routing to the broken dependency).
/// </summary>
public enum HealthState
{
    Healthy,
    Degraded,
    CircuitOpen      // default terminal — NOT Unhealthy/restart
}

/// <summary>Pod-level startup lifecycle.</summary>
public enum StartupStatus
{
    Starting,
    Ready,
    Failed
}

/// <summary>Drain lifecycle during graceful shutdown.</summary>
public enum DrainStatus
{
    Idle,
    Draining,
    Drained,
    TimedOut
}

/// <summary>Controls how much detail health endpoints expose.</summary>
public enum DetailLevel
{
    StatusOnly,      // default — returns 200/503 with "Healthy"/"Unhealthy"
    Summary,         // status + per-dependency status (no signal details)
    Full             // status + per-dependency + signal counts + window info
}

/// <summary>Signal outcome — what happened on a single observation.</summary>
public enum SignalOutcome
{
    Success,
    Failure,
    Timeout,
    Rejected         // e.g., circuit breaker rejected the call
}

// ─── Value Objects ─────────────────────────────────────────────────────────

/// <summary>A single health observation at a point in time.</summary>
public sealed record HealthSignal(
    DateTimeOffset Timestamp,
    DependencyId   DependencyId,
    SignalOutcome  Outcome,
    TimeSpan       Latency,
    int            HttpStatusCode = 0,      // 0 = not applicable
    string?        GrpcStatus     = null,   // null = not gRPC
    string?        Metadata       = null    // opaque extension data
);

/// <summary>Result of evaluating signals against a policy. Pure data, no behavior.</summary>
public sealed record HealthAssessment(
    DependencyId  DependencyId,
    double        SuccessRate,         // 0.0–1.0
    int           TotalSignals,
    int           FailureCount,
    int           SuccessCount,
    TimeSpan      WindowDuration,
    DateTimeOffset EvaluatedAt,
    HealthState   RecommendedState     // what the policy recommends (not yet applied)
);

/// <summary>Snapshot of one dependency's current state + latest assessment.</summary>
public sealed record DependencySnapshot(
    DependencyId     DependencyId,
    HealthState      CurrentState,
    HealthAssessment LatestAssessment,
    DateTimeOffset   StateChangedAt,
    int              ConsecutiveFailures
);

/// <summary>Aggregate health report across all dependencies.</summary>
public sealed record HealthReport(
    HealthStatus                       Status,
    IReadOnlyList<DependencySnapshot>  Dependencies,
    DateTimeOffset                     GeneratedAt
);

/// <summary>Aggregate readiness report — separate from health.</summary>
public sealed record ReadinessReport(
    ReadinessStatus                    Status,
    IReadOnlyList<DependencySnapshot>  Dependencies,
    DateTimeOffset                     GeneratedAt,
    StartupStatus                      StartupStatus,
    DrainStatus                        DrainStatus
);

/// <summary>Mapped to HTTP status codes on probe endpoints.</summary>
public enum HealthStatus  { Healthy, Degraded, Unhealthy }
public enum ReadinessStatus { Ready, NotReady }

/// <summary>Defines a state machine edge.</summary>
public sealed record StateTransition(
    HealthState From,
    HealthState To,
    Func<HealthAssessment, bool> Guard,    // must return true for transition to fire
    string Description                      // human-readable, for logging/debugging
);

/// <summary>Output of the transition engine — what should happen next.</summary>
public sealed record TransitionDecision(
    bool         ShouldTransition,
    HealthState? TargetState,
    TimeSpan     Delay,          // includes jitter; Zero = immediate
    string?      Reason
);

/// <summary>Output of shutdown orchestrator.</summary>
public sealed record ShutdownDecision(
    bool   Approved,
    string Gate,        // which gate blocked/approved: "MinSignals", "Cooldown", "ConfirmDelegate"
    string Reason
);

/// <summary>Event dispatched to sinks when health changes occur.</summary>
public sealed record HealthEvent(
    DateTimeOffset   Timestamp,
    DependencyId     DependencyId,
    HealthState      PreviousState,
    HealthState      NewState,
    HealthAssessment Assessment,
    string?          Trigger          // "PolicyEvaluation", "CircuitBreakerCallback", "RecoveryProbe", etc.
);

/// <summary>Timer budget validation warning.</summary>
public sealed record TimerBudgetWarning(
    string  RuleName,
    string  Message,
    string  ConfigPath,      // e.g. "HealthBoss:Dependencies:SqlDb:SlidingWindow"
    bool    IsCritical       // true = will not start; false = warning only
);

// ─── Configuration Records ─────────────────────────────────────────────────

/// <summary>Policy for one dependency — thresholds and windows.</summary>
public sealed record HealthPolicy(
    TimeSpan SlidingWindow,              // e.g., 30s
    double   DegradedThreshold,          // success rate below this → Degraded (e.g., 0.9)
    double   CircuitOpenThreshold,       // success rate below this → CircuitOpen (e.g., 0.5)
    int      MinSignalsForEvaluation,    // don't evaluate until this many signals exist
    TimeSpan CooldownBeforeTransition,   // debounce before state change
    TimeSpan RecoveryProbeInterval,      // how often to probe when CircuitOpen
    JitterConfig Jitter                  // jitter on transition timing
);

/// <summary>Jitter configuration for state transitions.</summary>
public sealed record JitterConfig(
    TimeSpan MinDelay,
    TimeSpan MaxDelay
);

/// <summary>Shutdown safety chain configuration.</summary>
public sealed record ShutdownConfig(
    int      MinSignals,              // gate 1: minimum signals observed before allowing shutdown
    TimeSpan Cooldown,                // gate 2: minimum time after last state change
    bool     RequireConfirmDelegate   // gate 3: must call ConfirmDelegate
);

/// <summary>Drain configuration.</summary>
public sealed record DrainConfig(
    TimeSpan Timeout,                         // max wait time for drain
    Func<int, CancellationToken, Task<bool>>? DrainDelegate  // custom drain logic; receives activeSessionCount
);

/// <summary>Tenant eviction configuration.</summary>
public sealed record TenantEvictionConfig(
    int      MaxTenants,       // LRU capacity
    TimeSpan Ttl               // TTL per tenant entry
);
```

---

## 3. HealthBoss.Core — 15 Components

### 3.1 SystemClock

```
Component:     SystemClock
Package:       HealthBoss.Core
Responsibility: Provides a seam for time so all components use a single,
                mockable clock source.
Does NOT:      Store state, make decisions, or manage timers.
Depends On:    (nothing)
Exposes:       ISystemClock
Internal State: None
Thread Safety:  Stateless — inherently safe.
Testability:   Replace with FakeClock that returns controlled timestamps.
```

```csharp
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}
```

---

### 3.2 SignalBuffer

```
Component:     SignalBuffer
Package:       HealthBoss.Core
Responsibility: Stores health signals in a thread-safe, time-bounded ring buffer.
                Supports sliding-window queries ("give me all signals from the last 30s").
Does NOT:      Evaluate signals, make health decisions, or know about policies.
                Does NOT trigger transitions or emit events.
Depends On:    ISystemClock
Exposes:       ISignalBuffer
Internal State: ConcurrentQueue<HealthSignal> or ring buffer of configurable capacity.
Thread Safety:  Lock-free ConcurrentQueue with periodic trim, OR ReaderWriterLockSlim
                if sorted access is needed. Record-then-trim pattern (write never blocks).
Testability:   Inject FakeClock, call Record() N times, assert GetSignals() returns
                correct window. No I/O, no async.
```

```csharp
public interface ISignalBuffer
{
    /// <summary>Append a signal. O(1), never blocks readers.</summary>
    void Record(HealthSignal signal);

    /// <summary>Return all signals within [now - window, now]. Snapshot — caller may enumerate freely.</summary>
    IReadOnlyList<HealthSignal> GetSignals(TimeSpan window);

    /// <summary>Remove signals older than cutoff. Called on a background timer or lazily.</summary>
    void Trim(DateTimeOffset cutoff);

    /// <summary>Total signals currently buffered (including expired, before trim).</summary>
    int Count { get; }
}
```

---

### 3.3 PolicyEvaluator

```
Component:     PolicyEvaluator
Package:       HealthBoss.Core
Responsibility: Given a list of signals and a HealthPolicy, compute a HealthAssessment.
                Pure function — no side effects, no state.
Does NOT:      Store signals, manage windows, trigger transitions, or emit events.
                Does NOT access the clock (timestamps are already on the signals).
Depends On:    (nothing — pure function over value types)
Exposes:       IPolicyEvaluator
Internal State: None
Thread Safety:  Stateless — inherently safe.
Testability:   Construct signals in-memory, call Evaluate(), assert HealthAssessment fields.
                Zero mocks needed.
```

```csharp
public interface IPolicyEvaluator
{
    /// <summary>
    /// Evaluate signals against a policy. Returns an assessment with recommended state.
    /// If signals.Count < policy.MinSignalsForEvaluation, returns assessment with
    /// RecommendedState = current state (no change).
    /// </summary>
    HealthAssessment Evaluate(
        IReadOnlyList<HealthSignal> signals,
        HealthPolicy policy,
        HealthState currentState,
        DateTimeOffset evaluatedAt);
}
```

---

### 3.4 StateGraph

```
Component:     StateGraph
Package:       HealthBoss.Core
Responsibility: Defines the set of valid states and transitions between them.
                Pure data structure — a directed graph of HealthState nodes and
                StateTransition edges. Immutable after construction.
Does NOT:      Evaluate whether a transition should fire (that's TransitionEngine).
                Does NOT apply jitter or timing. Does NOT store current state.
Depends On:    (nothing — pure data)
Exposes:       IStateGraph
Internal State: Immutable adjacency list.
Thread Safety:  Immutable after construction — inherently safe.
Testability:   Construct a graph, assert GetTransitionsFrom() returns expected edges.
                Assert IsTerminal(CircuitOpen) == false (it's terminal for the dependency,
                but not for the state machine — recovery probes can still transition out).
```

```csharp
public interface IStateGraph
{
    /// <summary>State a dependency starts in (Healthy by default).</summary>
    HealthState InitialState { get; }

    /// <summary>All transitions originating from the given state.</summary>
    IReadOnlyList<StateTransition> GetTransitionsFrom(HealthState state);

    /// <summary>All valid states in this graph.</summary>
    IReadOnlySet<HealthState> AllStates { get; }
}
```

**Default graph (shipped with HealthBoss.Core):**

```
┌─────────┐   successRate < Degraded    ┌──────────┐   successRate < CircuitOpen   ┌─────────────┐
│ Healthy │ ──────────────────────────→ │ Degraded │ ────────────────────────────→ │ CircuitOpen │
│         │ ←────────────────────────── │          │ ←──────────────────────────── │  (terminal) │
└─────────┘   successRate ≥ Degraded    └──────────┘   recoveryProbe succeeds      └─────────────┘
              sustained for cooldown                   (via HalfOpen probe)
```

---

### 3.5 TransitionEngine

```
Component:     TransitionEngine
Package:       HealthBoss.Core
Responsibility: Given current state + assessment + state graph, determine IF a transition
                should fire, to WHAT target state, and with WHAT delay (including jitter).
                Enforces cooldown before transition. Applies jitter from JitterConfig.
Does NOT:      Store current state (that's DependencyMonitor). Does NOT execute the
                transition (just decides). Does NOT manage timers or scheduling.
Depends On:    IStateGraph, ISystemClock (for jitter random seed / time-based checks)
Exposes:       ITransitionEngine
Internal State: Random instance for jitter (or injected). No shared mutable state.
Thread Safety:  Uses ThreadLocal<Random> or RandomNumberGenerator for jitter. Stateless
                beyond that.
Testability:   Provide a test graph, fixed clock, call Evaluate(), assert TransitionDecision.
```

```csharp
public interface ITransitionEngine
{
    /// <summary>
    /// Evaluate whether the assessment triggers a state transition.
    /// Returns a decision including target state and delay (with jitter).
    /// If no transition fires, returns ShouldTransition=false.
    /// </summary>
    TransitionDecision Evaluate(
        HealthState currentState,
        HealthAssessment assessment,
        HealthPolicy policy,
        DateTimeOffset lastTransitionTime);
}
```

---

### 3.6 RecoveryProber

```
Component:     RecoveryProber
Package:       HealthBoss.Core
Responsibility: When a dependency enters CircuitOpen, periodically invoke a caller-supplied
                probe delegate to check if the dependency has recovered.
                On success, signals the DependencyMonitor to attempt transition out of
                CircuitOpen.
Does NOT:      Define what a "probe" is (HTTP? gRPC? TCP?). Does NOT manage health state.
                Does NOT evaluate policies. The probe delegate is injected by the transport
                package (AspNetCore, Grpc) or the user.
Depends On:    ISystemClock (for scheduling intervals)
Exposes:       IRecoveryProber
Internal State: Dictionary<DependencyId, CancellationTokenSource> for active probe loops.
Thread Safety:  ConcurrentDictionary for active probes. Each probe runs on its own Task.
Testability:   Inject FakeClock + probe delegate that returns true/false. Assert probe is
                called at configured interval. Assert callback fires on success.
```

```csharp
public interface IRecoveryProber
{
    /// <summary>
    /// Start periodic probing for a dependency. Calls onRecovered when the probe
    /// returns true. Probing continues until StopProbing() or cancellation.
    /// </summary>
    void StartProbing(
        DependencyId dependency,
        HealthPolicy policy,                                        // contains RecoveryProbeInterval
        Func<CancellationToken, Task<bool>> probe,                  // transport-specific probe
        Func<DependencyId, CancellationToken, Task> onRecovered,    // callback when probe succeeds
        CancellationToken cancellationToken);

    /// <summary>Stop probing for a dependency (e.g., when leaving CircuitOpen).</summary>
    void StopProbing(DependencyId dependency);

    /// <summary>Whether a probe loop is active for this dependency.</summary>
    bool IsProbing(DependencyId dependency);
}
```

---

### 3.7 DependencyMonitor

```
Component:     DependencyMonitor
Package:       HealthBoss.Core
Responsibility: Per-dependency orchestrator. Owns one SignalBuffer, uses PolicyEvaluator
                and TransitionEngine to manage the health lifecycle of a single dependency.
                Holds the current HealthState. Activates/deactivates RecoveryProber on
                state transitions. Dispatches HealthEvents on state changes.
Does NOT:      Aggregate across dependencies (that's HealthOrchestrator).
                Does NOT manage multiple dependencies.
                Does NOT handle shutdown/drain.
Depends On:    ISignalBuffer, IPolicyEvaluator, ITransitionEngine, IRecoveryProber,
                IEventSinkDispatcher, ISystemClock
Exposes:       IDependencyMonitor
Internal State: CurrentState (HealthState), LatestAssessment, LastTransitionTime,
                ConsecutiveFailures counter.
Thread Safety:  Lock on state transitions (critical section is small: evaluate → transition
                → dispatch). Signal recording (to buffer) is lock-free.
Testability:   Inject all dependencies as mocks/fakes. Record signals, assert state
                transitions and events.
```

```csharp
public interface IDependencyMonitor
{
    DependencyId DependencyId { get; }
    HealthState CurrentState { get; }
    HealthAssessment? LatestAssessment { get; }

    /// <summary>Record a signal and re-evaluate health. May trigger state transition.</summary>
    void RecordSignal(HealthSignal signal);

    /// <summary>Take a point-in-time snapshot (for aggregation).</summary>
    DependencySnapshot GetSnapshot();

    /// <summary>Register the recovery probe delegate for this dependency.</summary>
    void SetRecoveryProbe(Func<CancellationToken, Task<bool>> probe);
}
```

---

### 3.8 HealthOrchestrator

```
Component:     HealthOrchestrator
Package:       HealthBoss.Core
Responsibility: Top-level coordinator. Owns all DependencyMonitors. Routes incoming signals
                to the correct monitor. Produces aggregate reports via aggregators.
                Single entry point for the entire health system.
Does NOT:      Evaluate policies (delegates to monitors). Does NOT handle HTTP/gRPC
                (transport packages call into this). Does NOT manage shutdown/drain.
Depends On:    IDependencyMonitor (factory), IHealthAggregator, IReadinessAggregator,
                IStartupTracker, IDrainCoordinator
Exposes:       IHealthOrchestrator (the primary public API of HealthBoss.Core)
Internal State: ConcurrentDictionary<DependencyId, IDependencyMonitor>
Thread Safety:  ConcurrentDictionary for monitor registry. Individual monitors handle
                their own concurrency.
Testability:   Inject mock monitors and aggregators. Call RecordSignal(), assert routing.
                Call GetHealthReport(), assert aggregation.
```

```csharp
public interface IHealthOrchestrator
{
    /// <summary>Get or auto-create the monitor for a dependency.</summary>
    IDependencyMonitor GetMonitor(DependencyId dependency);

    /// <summary>Record a signal, routing to the correct dependency monitor.</summary>
    void RecordSignal(DependencyId dependency, HealthSignal signal);

    /// <summary>Aggregate health across all dependencies using IHealthAggregator.</summary>
    HealthReport GetHealthReport();

    /// <summary>Aggregate readiness across all dependencies using IReadinessAggregator.</summary>
    ReadinessReport GetReadinessReport();

    /// <summary>All registered dependency IDs.</summary>
    IReadOnlyCollection<DependencyId> RegisteredDependencies { get; }
}
```

---

### 3.9 HealthAggregator

```
Component:     HealthAggregator
Package:       HealthBoss.Core
Responsibility: Combine per-dependency snapshots into a single HealthStatus for liveness
                probes. Default: worst-of-all strategy. User-replaceable via DI.
Does NOT:      Evaluate individual dependencies. Does NOT know about readiness or drain.
Depends On:    (nothing — pure function over DependencySnapshot list)
Exposes:       IHealthAggregator
Internal State: None
Thread Safety:  Stateless — inherently safe.
Testability:   Construct snapshots, call Aggregate(), assert result.
```

```csharp
public interface IHealthAggregator
{
    /// <summary>
    /// Combine dependency snapshots into a single health status.
    /// Default implementation: worst-of-all (any CircuitOpen → Unhealthy).
    /// </summary>
    HealthStatus Aggregate(IReadOnlyList<DependencySnapshot> snapshots);
}
```

---

### 3.10 ReadinessAggregator

```
Component:     ReadinessAggregator
Package:       HealthBoss.Core
Responsibility: Combine per-dependency snapshots into a single ReadinessStatus for readiness
                probes. Separate from health because readiness considers startup, drain,
                and may have different tolerance (e.g., one degraded dep is OK for readiness
                but not for health).
Does NOT:      Evaluate individual dependencies. Does NOT know about health aggregation.
Depends On:    (nothing — pure function over DependencySnapshot list)
Exposes:       IReadinessAggregator
Internal State: None
Thread Safety:  Stateless — inherently safe.
Testability:   Construct snapshots, call Aggregate(), assert result.
```

```csharp
public interface IReadinessAggregator
{
    /// <summary>
    /// Combine dependency snapshots into a single readiness status.
    /// Default: Ready if all dependencies are Healthy or Degraded; NotReady if any CircuitOpen.
    /// </summary>
    ReadinessStatus Aggregate(IReadOnlyList<DependencySnapshot> snapshots);
}
```

---

### 3.11 SessionTracker

```
Component:     SessionTracker
Package:       HealthBoss.Core
Responsibility: Track active in-flight request/session count. Provides a gate for drain:
                "are there active sessions?" Supports waiting for count to reach zero.
Does NOT:      Make health decisions. Does NOT control shutdown flow (that's DrainCoordinator).
                Does NOT know about HTTP or gRPC (transport packages increment/decrement).
Depends On:    (nothing)
Exposes:       ISessionTracker
Internal State: Interlocked counter (long).
Thread Safety:  Interlocked.Increment/Decrement for counter. SemaphoreSlim or
                TaskCompletionSource for WaitForDrainAsync.
Testability:   Call Track/Release, assert ActiveCount. Call WaitForDrainAsync, release all,
                assert completion.
```

```csharp
public interface ISessionTracker
{
    /// <summary>
    /// Start tracking a session. Returns IDisposable — disposing ends tracking.
    /// Typical usage: using var _ = sessionTracker.TrackSession();
    /// </summary>
    IDisposable TrackSession();

    /// <summary>Current count of active sessions.</summary>
    int ActiveCount { get; }

    /// <summary>
    /// Wait until ActiveCount reaches 0 or timeout expires, whichever first.
    /// Returns true if drained, false if timed out.
    /// </summary>
    Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
```

---

### 3.12 DrainCoordinator

```
Component:     DrainCoordinator
Package:       HealthBoss.Core
Responsibility: Manage the drain phase of graceful shutdown. Wait for active sessions to
                complete OR timeout, whichever first. Optionally invoke a custom drain
                delegate for app-specific drain logic.
Does NOT:      Track sessions (delegates to ISessionTracker). Does NOT decide whether
                to shut down (that's ShutdownOrchestrator). Does NOT manage health state.
Depends On:    ISessionTracker, ISystemClock
Exposes:       IDrainCoordinator
Internal State: DrainStatus (enum), drain start time.
Thread Safety:  Volatile DrainStatus field. Single drain operation protected by SemaphoreSlim(1,1).
Testability:   Inject mock ISessionTracker with controlled ActiveCount. Assert drain completes
                when count reaches 0. Assert timeout fires when sessions don't drain.
```

```csharp
public interface IDrainCoordinator
{
    /// <summary>Current drain status.</summary>
    DrainStatus Status { get; }

    /// <summary>
    /// Begin draining. Waits for sessions OR timeout, whichever first.
    /// Invokes DrainConfig.DrainDelegate if configured.
    /// </summary>
    Task<DrainStatus> DrainAsync(DrainConfig config, CancellationToken cancellationToken);
}
```

---

### 3.13 ShutdownOrchestrator

```
Component:     ShutdownOrchestrator
Package:       HealthBoss.Core
Responsibility: Enforce the 3-gate safety chain before allowing shutdown signal:
                Gate 1: MinSignals — enough signals have been observed to trust health data.
                Gate 2: Cooldown — sufficient time since last state transition.
                Gate 3: ConfirmDelegate — caller-supplied async delegate returns true.
                All 3 gates must pass. Returns which gate blocked if not approved.
Does NOT:      Manage drain (that's DrainCoordinator). Does NOT make health decisions.
                Does NOT actually shut down the process — only signals approval.
Depends On:    IHealthOrchestrator (to read signal counts and last transition time),
                ISystemClock
Exposes:       IShutdownOrchestrator
Internal State: Configuration (ShutdownConfig). No mutable state.
Thread Safety:  Stateless evaluation — inherently safe.
Testability:   Inject mock orchestrator with controlled signal counts and transition times.
                Assert each gate independently. Assert all-pass and partial-fail scenarios.
```

```csharp
public interface IShutdownOrchestrator
{
    /// <summary>
    /// Evaluate all 3 gates. Returns a ShutdownDecision indicating whether shutdown
    /// is approved, and which gate blocked if not.
    /// </summary>
    Task<ShutdownDecision> EvaluateAsync(
        ShutdownConfig config,
        Func<CancellationToken, Task<bool>>? confirmDelegate,
        CancellationToken cancellationToken);
}
```

---

### 3.14 EventSinkDispatcher

```
Component:     EventSinkDispatcher
Package:       HealthBoss.Core
Responsibility: Route HealthEvents to all registered IEventSink instances. Fan-out pattern.
                Sinks are registered at startup via DI. Dispatch is fire-and-forget with
                error isolation (one failing sink does not block others).
Does NOT:      Produce events (DependencyMonitor produces them). Does NOT filter events.
                Does NOT persist events.
Depends On:    IEnumerable<IEventSink> (injected via DI)
Exposes:       IEventSinkDispatcher, IEventSink (consumer contract)
Internal State: List of registered sinks (immutable after startup).
Thread Safety:  Immutable sink list. Dispatch is concurrent (Task.WhenAll with individual
                try/catch per sink).
Testability:   Register mock sinks, dispatch event, assert each sink received it.
                Register a failing sink, assert others still receive the event.
```

```csharp
public interface IEventSinkDispatcher
{
    /// <summary>Dispatch a health event to all registered sinks. Error-isolated.</summary>
    ValueTask DispatchAsync(HealthEvent healthEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Consumer contract for health events. Implement this to receive notifications
/// when dependency health state changes. Register via DI as IEventSink.
/// Examples: logging sink, metrics sink, alerting sink, telemetry sink.
/// </summary>
public interface IEventSink
{
    ValueTask OnHealthEventAsync(HealthEvent healthEvent, CancellationToken cancellationToken);
}
```

---

### 3.15 TenantHealthStore

```
Component:     TenantHealthStore
Package:       HealthBoss.Core
Responsibility: Multi-tenant cache of per-tenant, per-dependency DependencyMonitor instances.
                LRU eviction when tenant count exceeds MaxTenants. TTL eviction for stale
                tenants. Enables per-tenant health isolation.
Does NOT:      Evaluate health (delegates to DependencyMonitor). Does NOT make eviction
                policy decisions beyond LRU+TTL (no business logic).
Depends On:    IDependencyMonitor (factory), ISystemClock
Exposes:       ITenantHealthStore
Internal State: ConcurrentDictionary<(TenantId, DependencyId), IDependencyMonitor> +
                LRU tracking structure (linked list or similar).
Thread Safety:  ConcurrentDictionary + lock for LRU eviction (eviction is infrequent,
                lock contention is minimal). Read path is lock-free.
Testability:   Create store with MaxTenants=3, add 4 tenants, assert oldest is evicted.
                Advance FakeClock past TTL, assert stale tenant is evicted.
```

```csharp
public interface ITenantHealthStore
{
    /// <summary>Get or create a monitor for a tenant+dependency pair.</summary>
    IDependencyMonitor GetOrCreate(TenantId tenant, DependencyId dependency);

    /// <summary>Explicitly evict a tenant (e.g., on tenant offboarding).</summary>
    void Evict(TenantId tenant);

    /// <summary>Current tenant count.</summary>
    int TenantCount { get; }

    /// <summary>Trigger LRU+TTL eviction scan. Called on a background timer or on access.</summary>
    int EvictStale();
}
```

---

### 3.16 TimerBudgetValidator

```
Component:     TimerBudgetValidator
Package:       HealthBoss.Core
Responsibility: At startup, validate that configured timers are internally consistent.
                Examples of violations:
                  - SlidingWindow < RecoveryProbeInterval (probe will never see a full window)
                  - Cooldown > SlidingWindow (cooldown will always stall transitions)
                  - Jitter.MaxDelay > Cooldown (jitter can exceed cooldown)
                Produces warnings (logged) or critical errors (prevent startup).
Does NOT:      Manage timers at runtime. Does NOT modify configuration.
Depends On:    (nothing — pure validation over configuration records)
Exposes:       ITimerBudgetValidator
Internal State: None
Thread Safety:  Stateless — inherently safe.
Testability:   Construct HealthPolicy with conflicting values, call Validate(),
                assert correct warnings.
```

```csharp
public interface ITimerBudgetValidator
{
    /// <summary>
    /// Validate all configured policies for timer consistency.
    /// Returns warnings. Critical warnings prevent startup.
    /// </summary>
    IReadOnlyList<TimerBudgetWarning> Validate(
        IReadOnlyDictionary<DependencyId, HealthPolicy> policies);
}
```

---

## 4. HealthBoss.AspNetCore — 6 Components

### 4.1 InboundMiddleware

```
Component:     InboundMiddleware
Package:       HealthBoss.AspNetCore
Responsibility: ASP.NET Core middleware that observes inbound HTTP requests/responses.
                Extracts health signals (status code, latency) and records them to
                IHealthOrchestrator. Per-route dependency mapping via IRouteMapper.
Does NOT:      Evaluate health. Does NOT modify the request/response. Does NOT serve
                probe endpoints (that's ProbeEndpointHandler).
Depends On:    IHealthOrchestrator (Core), IRouteMapper (this package)
Exposes:       Middleware registration extension method (UseHealthBossInbound)
Internal State: None — reads from HttpContext per-request.
Thread Safety:  Stateless middleware instance. All state is per-request in HttpContext.
Testability:   Use TestServer. Send requests, assert signals recorded to mock orchestrator.
```

```csharp
/// <summary>Maps inbound request route patterns to dependency IDs for signal recording.</summary>
public interface IRouteMapper
{
    /// <summary>
    /// Given a request path/route template, return the dependency ID to record signals against.
    /// Returns null if this route is not monitored.
    /// </summary>
    DependencyId? MapRoute(HttpContext context);
}
```

---

### 4.2 OutboundHandler

```
Component:     OutboundHandler
Package:       HealthBoss.AspNetCore
Responsibility: DelegatingHandler that wraps outbound HttpClient calls. Records
                success/failure/timeout signals to IHealthOrchestrator.
                Attached to named/typed HttpClients via IHttpClientBuilder.
Does NOT:      Modify the outbound request or response. Does NOT retry (that's Polly's job).
                Does NOT evaluate health.
Depends On:    IHealthOrchestrator (Core)
Exposes:       DelegatingHandler + IHttpClientBuilder extension (AddHealthBossHandler)
Internal State: None — reads from HttpResponseMessage per-call.
Thread Safety:  Stateless — inherently safe per HttpClient pipeline contract.
Testability:   Create HttpClient with handler + mock inner handler. Make calls, assert
                signals recorded to mock orchestrator.
```

```csharp
/// <summary>
/// Extension method for wiring the outbound handler into HttpClient pipeline.
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Add HealthBoss signal recording to an HttpClient's outbound pipeline.
    /// </summary>
    /// <param name="builder">The HttpClient builder.</param>
    /// <param name="dependencyId">Which dependency this HttpClient targets.</param>
    public static IHttpClientBuilder AddHealthBossHandler(
        this IHttpClientBuilder builder,
        DependencyId dependencyId);
}
```

---

### 4.3 ProbeEndpointHandler

```
Component:     ProbeEndpointHandler
Package:       HealthBoss.AspNetCore
Responsibility: Serve K8s-compatible probe endpoints:
                  /healthz          — liveness (HealthReport)
                  /readyz           — readiness (ReadinessReport)
                  /healthz/startup  — startup (StartupStatus)
                Read-only — queries IHealthOrchestrator and IStartupTracker, delegates
                response writing to IProbeResponseWriter.
Does NOT:      Compute health (reads from orchestrator). Does NOT serialize (delegates to
                writer). Does NOT decide detail level (reads from query string or config).
Depends On:    IHealthOrchestrator (Core), IStartupTracker (Core),
                IProbeResponseWriter (this package)
Exposes:       Endpoint registration extension methods (MapHealthBossProbes)
Internal State: None
Thread Safety:  Stateless — inherently safe.
Testability:   Use TestServer. Request each endpoint, assert correct HTTP status and
                that orchestrator/tracker were queried.
```

```csharp
/// <summary>
/// Extension methods for mapping probe endpoints in the ASP.NET Core routing pipeline.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// Map /healthz, /readyz, and /healthz/startup endpoints.
    /// Optionally configure detail level and custom paths.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthBossProbes(
        this IEndpointRouteBuilder endpoints,
        Action<ProbeEndpointOptions>? configure = null);
}

public sealed record ProbeEndpointOptions
{
    public string LivenessPath { get; init; } = "/healthz";
    public string ReadinessPath { get; init; } = "/readyz";
    public string StartupPath { get; init; } = "/healthz/startup";
    public DetailLevel DefaultDetailLevel { get; init; } = DetailLevel.StatusOnly;
}
```

---

### 4.4 ProbeResponseWriter

```
Component:     ProbeResponseWriter
Package:       HealthBoss.AspNetCore
Responsibility: Serialize HealthReport / ReadinessReport to JSON and write to HttpResponse.
                Respects DetailLevel: StatusOnly returns just the status string;
                Summary includes per-dependency statuses; Full includes signal counts
                and window info.
Does NOT:      Compute health. Does NOT handle routing. Does NOT decide which report to serve.
Depends On:    (nothing — pure serialization of Core value types)
Exposes:       IProbeResponseWriter
Internal State: Cached JsonSerializerOptions (immutable).
Thread Safety:  Stateless + immutable cached options — inherently safe.
Testability:   Construct reports in-memory, call WriteAsync with mock HttpContext,
                assert JSON output matches expected shape for each DetailLevel.
```

```csharp
public interface IProbeResponseWriter
{
    /// <summary>Write a health report to the HTTP response.</summary>
    Task WriteHealthAsync(HttpContext context, HealthReport report, DetailLevel detailLevel);

    /// <summary>Write a readiness report to the HTTP response.</summary>
    Task WriteReadinessAsync(HttpContext context, ReadinessReport report, DetailLevel detailLevel);

    /// <summary>Write a startup status to the HTTP response.</summary>
    Task WriteStartupAsync(HttpContext context, StartupStatus status);
}
```

---

### 4.5 StartupTracker

```
Component:     StartupTracker
Package:       HealthBoss.Core (interface) + HealthBoss.AspNetCore (endpoint wiring)
Responsibility: Track whether the application has completed startup. The /healthz/startup
                endpoint reads from this. Application code calls MarkReady() when startup
                completes. K8s uses this to know when to start sending liveness/readiness
                probes.
Does NOT:      Evaluate health. Does NOT manage HTTP endpoints. Does NOT know about
                dependencies.
Depends On:    (nothing)
Exposes:       IStartupTracker
Internal State: volatile StartupStatus enum.
Thread Safety:  Volatile read/write on a single enum field — inherently safe.
Testability:   Call MarkReady(), assert Status == Ready. Call MarkFailed(), assert
                Status == Failed.
```

```csharp
public interface IStartupTracker
{
    StartupStatus Status { get; }
    void MarkReady();
    void MarkFailed(string reason);
    string? FailureReason { get; }
}
```

---

### 4.6 HealthBossBuilder (DI Wiring)

```
Component:     HealthBossBuilder
Package:       HealthBoss.AspNetCore
Responsibility: Fluent builder API for configuring HealthBoss in Startup/Program.cs.
                Registers all Core components, transport components, and configuration
                into the DI container. Triggers TimerBudgetValidator at startup.
Does NOT:      Contain business logic. Does NOT evaluate health. This is pure DI plumbing.
Depends On:    All Core interfaces (for registration), ITimerBudgetValidator (for startup validation)
Exposes:       IServiceCollection extension methods (AddHealthBoss)
Internal State: Configuration accumulation during builder phase (transient).
Thread Safety:  Used only during startup (single-threaded by convention).
Testability:   Build a ServiceProvider, resolve IHealthOrchestrator, assert it's wired correctly.
```

```csharp
public static class ServiceCollectionExtensions
{
    public static IHealthBossBuilder AddHealthBoss(
        this IServiceCollection services,
        Action<HealthBossOptions> configure);
}

public interface IHealthBossBuilder
{
    /// <summary>Register a dependency with its health policy.</summary>
    IHealthBossBuilder AddDependency(DependencyId dependency, Action<HealthPolicy> configure);

    /// <summary>Register an event sink.</summary>
    IHealthBossBuilder AddEventSink<TSink>() where TSink : class, IEventSink;

    /// <summary>Replace the default health aggregator.</summary>
    IHealthBossBuilder UseHealthAggregator<TAggregator>() where TAggregator : class, IHealthAggregator;

    /// <summary>Replace the default readiness aggregator.</summary>
    IHealthBossBuilder UseReadinessAggregator<TAggregator>() where TAggregator : class, IReadinessAggregator;

    /// <summary>Configure shutdown safety chain.</summary>
    IHealthBossBuilder ConfigureShutdown(Action<ShutdownConfig> configure);

    /// <summary>Configure drain behavior.</summary>
    IHealthBossBuilder ConfigureDrain(Action<DrainConfig> configure);

    /// <summary>Enable multi-tenant mode.</summary>
    IHealthBossBuilder EnableMultiTenant(Action<TenantEvictionConfig> configure);
}
```

---

## 5. HealthBoss.Polly — 2 Components

### 5.1 CircuitBreakerBridge

```
Component:     CircuitBreakerBridge
Package:       HealthBoss.Polly
Responsibility: Event-driven bridge between Polly circuit breaker callbacks and HealthBoss
                signal recording. Subscribes to OnOpened/OnClosed/OnHalfOpened on a
                CircuitBreakerStrategyOptions and translates each callback into a
                HealthSignal recorded to IHealthOrchestrator.
                EVENT-DRIVEN, NOT POLLING — the bridge reacts to Polly callbacks only.
Does NOT:      Create or manage Polly pipelines. Does NOT evaluate health. Does NOT
                poll CircuitBreakerStateProvider. Does NOT modify Polly behavior.
Depends On:    IHealthOrchestrator (Core)
Exposes:       ICircuitBreakerBridge + ResiliencePipelineBuilder extension method
Internal State: Mapping of pipeline name → DependencyId (set at registration time, immutable).
Thread Safety:  Immutable after configuration. Callback invocation is on Polly's thread —
                IHealthOrchestrator.RecordSignal is thread-safe.
Testability:   Create a circuit breaker with the bridge attached. Trip the breaker, assert
                a HealthSignal with Outcome=Rejected was recorded. Close the breaker, assert
                signal with Outcome=Success.
```

```csharp
public interface ICircuitBreakerBridge
{
    /// <summary>
    /// Attach HealthBoss signal recording to a circuit breaker's event callbacks.
    /// Must be called BEFORE building the ResiliencePipeline.
    /// </summary>
    void Attach(DependencyId dependency, CircuitBreakerStrategyOptions options);

    /// <summary>Generic version for typed circuit breakers.</summary>
    void Attach<TResult>(DependencyId dependency, CircuitBreakerStrategyOptions<TResult> options);
}

/// <summary>Extension method for fluent pipeline configuration.</summary>
public static class ResiliencePipelineBuilderExtensions
{
    /// <summary>
    /// Add a circuit breaker with HealthBoss signal recording.
    /// Equivalent to AddCircuitBreaker() + bridge.Attach().
    /// </summary>
    public static ResiliencePipelineBuilder AddHealthBossCircuitBreaker(
        this ResiliencePipelineBuilder builder,
        DependencyId dependency,
        CircuitBreakerStrategyOptions options,
        ICircuitBreakerBridge bridge);
}
```

**Event mapping:**

| Polly Callback | HealthBoss Signal | Notes |
|---|---|---|
| `OnOpened` | `HealthSignal(Outcome: Rejected, Metadata: "circuit_opened")` | Triggers CircuitOpen evaluation |
| `OnClosed` | `HealthSignal(Outcome: Success, Metadata: "circuit_closed")` | Triggers recovery evaluation |
| `OnHalfOpened` | `HealthSignal(Outcome: Success, Metadata: "circuit_half_opened")` | Probe attempt signal |

---

### 5.2 PollyStateAdapter

```
Component:     PollyStateAdapter
Package:       HealthBoss.Polly
Responsibility: Read-only adapter that queries Polly's CircuitBreakerStateProvider for
                the current circuit state. Used for point-in-time queries (e.g., in
                diagnostic endpoints) rather than event-driven monitoring.
Does NOT:      Subscribe to events (that's CircuitBreakerBridge). Does NOT record signals.
                Does NOT modify Polly state.
Depends On:    Polly CircuitBreakerStateProvider
Exposes:       IPollyStateAdapter
Internal State: Dictionary<DependencyId, CircuitBreakerStateProvider> (set at registration time).
Thread Safety:  Immutable mapping. StateProvider reads are thread-safe per Polly's contract.
Testability:   Register a provider, set circuit state, assert adapter returns correct state.
```

```csharp
public interface IPollyStateAdapter
{
    /// <summary>Get the current Polly circuit state for a dependency.</summary>
    CircuitState? GetCircuitState(DependencyId dependency);

    /// <summary>Register a state provider for a dependency.</summary>
    void Register(DependencyId dependency, CircuitBreakerStateProvider provider);
}
```

---

## 6. HealthBoss.Grpc — 3 Components

### 6.1 SubchannelHealthAdapter

```
Component:     SubchannelHealthAdapter
Package:       HealthBoss.Grpc
Responsibility: Monitor gRPC subchannel connectivity state changes and translate them
                into HealthBoss signals. Maps ConnectivityState (Ready, Idle,
                TransientFailure, Shutdown) to appropriate signal outcomes.
Does NOT:      Manage gRPC channels. Does NOT do load balancing. Does NOT evaluate health.
Depends On:    IHealthOrchestrator (Core)
Exposes:       ISubchannelHealthAdapter
Internal State: Mapping of subchannel → DependencyId (set at registration time).
Thread Safety:  Callbacks arrive on gRPC's internal thread. RecordSignal is thread-safe.
Testability:   Simulate connectivity state changes, assert correct signals recorded.
```

```csharp
public interface ISubchannelHealthAdapter
{
    /// <summary>
    /// Called when a subchannel's connectivity state changes.
    /// Translates to a HealthSignal and records it.
    /// </summary>
    void OnConnectivityStateChanged(
        DependencyId dependency,
        ConnectivityState previousState,
        ConnectivityState newState);
}
```

**State mapping:**

| ConnectivityState | SignalOutcome | Notes |
|---|---|---|
| `Ready` | `Success` | Subchannel is connected |
| `Idle` | (no signal) | Normal idle — not a failure |
| `TransientFailure` | `Failure` | Connection problem |
| `Shutdown` | `Failure` | Subchannel is terminated |

---

### 6.2 GrpcClientInterceptor

```
Component:     GrpcClientInterceptor
Package:       HealthBoss.Grpc
Responsibility: gRPC client interceptor that records call success/failure as health signals.
                Analogous to OutboundHandler (4.2) but for gRPC instead of HTTP.
                Intercepts unary, client-streaming, server-streaming, and duplex calls.
Does NOT:      Modify the gRPC call. Does NOT retry. Does NOT manage channels.
Depends On:    IHealthOrchestrator (Core)
Exposes:       Interceptor class + GrpcChannel configuration extension method
Internal State: DependencyId (set at construction, immutable).
Thread Safety:  Stateless per-call. DependencyId is immutable.
Testability:   Use a test gRPC server. Make calls through interceptor, assert signals
                recorded. Force RpcException, assert failure signal.
```

```csharp
/// <summary>
/// gRPC client interceptor that records call outcomes as health signals.
/// Register per-channel via AddHealthBossInterceptor().
/// </summary>
public class HealthBossInterceptor : Interceptor
{
    public HealthBossInterceptor(
        IHealthOrchestrator orchestrator,
        DependencyId dependencyId);

    // Overrides: AsyncUnaryCall, AsyncClientStreamingCall,
    //            AsyncServerStreamingCall, AsyncDuplexStreamingCall
}

public static class GrpcChannelExtensions
{
    /// <summary>Add HealthBoss signal recording to a gRPC channel.</summary>
    public static CallInvoker AddHealthBossInterceptor(
        this CallInvoker invoker,
        IHealthOrchestrator orchestrator,
        DependencyId dependencyId);
}
```

---

### 6.3 QuorumProbe

```
Component:     QuorumProbe
Package:       HealthBoss.Grpc
Responsibility: Probe multiple gRPC backends and apply quorum logic: if a configurable
                majority of backends are healthy, the overall dependency is considered
                healthy. Uses the gRPC health checking protocol (grpc.health.v1).
Does NOT:      Track individual backend health over time (that's DependencyMonitor).
                Does NOT manage channels. Does NOT define quorum threshold (configuration).
Depends On:    IHealthOrchestrator (Core) — for recording the aggregate probe result
Exposes:       IQuorumProbe
Internal State: None — stateless probe.
Thread Safety:  Stateless. Probes are concurrent (Task.WhenAll).
Testability:   Mock N gRPC health endpoints, configure quorum=majority, assert
                QuorumResult reflects the correct outcome.
```

```csharp
public interface IQuorumProbe
{
    /// <summary>
    /// Probe all backends and determine quorum health.
    /// </summary>
    Task<QuorumResult> ProbeAsync(
        IReadOnlyList<GrpcChannel> channels,
        int quorumThreshold,             // minimum healthy count
        TimeSpan timeout,                // per-backend probe timeout
        CancellationToken cancellationToken);
}

public sealed record QuorumResult(
    bool IsHealthy,
    int HealthyCount,
    int TotalCount,
    int QuorumThreshold,
    IReadOnlyList<BackendProbeResult> Details
);

public sealed record BackendProbeResult(
    string Endpoint,
    bool IsHealthy,
    TimeSpan Latency,
    string? Error
);
```

---

## 7. Full Component Dependency Graph

```
┌──────────────────────────────────────────────────────────────────────────────┐
│                         SHARED VALUE TYPES (Contracts)                       │
│  HealthSignal, HealthAssessment, DependencySnapshot, HealthReport,           │
│  ReadinessReport, HealthEvent, HealthPolicy, StateTransition, etc.           │
│  ─── Every component depends on these. They depend on nothing. ───           │
└──────────────────────────────────────────────────────────────────────────────┘
                                      ↑
    ┌─────────────────────────────────┼─────────────────────────────────────┐
    │                          HealthBoss.Core                              │
    │                                                                       │
    │   ┌─────────────┐                                                     │
    │   │ SystemClock  │←─────────────────────────────────────────────┐      │
    │   └──────┬───────┘                                              │      │
    │          │                                                      │      │
    │          ↓                                                      │      │
    │   ┌──────────────┐    ┌───────────────────┐   ┌──────────────┐  │      │
    │   │ SignalBuffer  │    │ PolicyEvaluator   │   │  StateGraph  │  │      │
    │   │  (per dep)    │    │  (stateless)      │   │ (immutable)  │  │      │
    │   └──────┬────────┘    └────────┬──────────┘   └──────┬───────┘  │      │
    │          │                      │                     │          │      │
    │          │         ┌────────────┼─────────────────────┘          │      │
    │          │         │            │                                │      │
    │          │         ↓            ↓                                │      │
    │          │   ┌─────────────────────────┐  ┌──────────────────┐  │      │
    │          │   │   TransitionEngine      │  │  RecoveryProber  │──┘      │
    │          │   │  (jitter + cooldown)    │  │  (probe loops)   │         │
    │          │   └────────────┬────────────┘  └───────┬──────────┘         │
    │          │                │                        │                    │
    │          ↓                ↓                        ↓                    │
    │   ┌──────────────────────────────────────────────────────────┐         │
    │   │              DependencyMonitor (per dependency)           │         │
    │   │  Owns: SignalBuffer, current HealthState                 │         │
    │   │  Uses: PolicyEvaluator, TransitionEngine, RecoveryProber │         │
    │   │  Emits: HealthEvent → EventSinkDispatcher                │         │
    │   └──────────────────────────┬───────────────────────────────┘         │
    │                              │                                         │
    │                              ↓                                         │
    │   ┌─────────────────────────────────────────────────────────┐          │
    │   │               HealthOrchestrator                         │          │
    │   │  Owns: all DependencyMonitors                            │          │
    │   │  Uses: HealthAggregator, ReadinessAggregator,            │          │
    │   │        StartupTracker, DrainCoordinator                  │          │
    │   └─────────────┬───────────┬──────────────┬────────────────┘          │
    │                 │           │              │                            │
    │    ┌────────────┘   ┌──────┘     ┌────────┘                            │
    │    ↓                ↓            ↓                                     │
    │  ┌────────────┐ ┌────────────┐ ┌──────────────────┐                    │
    │  │  Health    │ │ Readiness  │ │ StartupTracker   │                    │
    │  │ Aggregator │ │ Aggregator │ │ (volatile enum)  │                    │
    │  └────────────┘ └────────────┘ └──────────────────┘                    │
    │                                                                        │
    │  ┌────────────────────┐  ┌──────────────────────┐                      │
    │  │  SessionTracker    │  │ EventSinkDispatcher  │                      │
    │  │  (Interlocked)     │  │  (fan-out to sinks)  │                      │
    │  └─────────┬──────────┘  └──────────────────────┘                      │
    │            │                                                           │
    │            ↓                                                           │
    │  ┌────────────────────┐  ┌──────────────────────┐                      │
    │  │  DrainCoordinator  │  │ ShutdownOrchestrator │                      │
    │  │  (session drain)   │  │ (3-gate safety)      │                      │
    │  └────────────────────┘  └──────────────────────┘                      │
    │                                                                        │
    │  ┌────────────────────┐  ┌──────────────────────┐                      │
    │  │ TenantHealthStore  │  │ TimerBudgetValidator │                      │
    │  │ (LRU+TTL cache)    │  │ (startup only)       │                      │
    │  └────────────────────┘  └──────────────────────┘                      │
    └────────────────────────────────────────────────────────────────────────┘
                          ↑             ↑             ↑
    ┌─────────────────────┴──┐ ┌────────┴────────┐ ┌──┴────────────────────┐
    │  HealthBoss.AspNetCore │ │ HealthBoss.Polly│ │  HealthBoss.Grpc      │
    │                        │ │                 │ │                        │
    │  InboundMiddleware ────┤ │ CB Bridge ──────┤ │ SubchannelAdapter ────┤
    │  OutboundHandler ──────┤ │ StateAdapter    │ │ GrpcInterceptor ──────┤
    │  ProbeEndpointHandler  │ └─────────────────┘ │ QuorumProbe           │
    │  ProbeResponseWriter   │                     └────────────────────────┘
    │  HealthBossBuilder     │
    │  (RouteMapper)         │
    └────────────────────────┘
```

---

## 8. Layering Rules

### Layer 0: Contracts (value types, enums)
- **Depends on:** Nothing
- **Depended on by:** Everything

### Layer 1: Leaf Components (no dependencies on other HealthBoss components)
- `SystemClock`, `PolicyEvaluator`, `StateGraph`, `HealthAggregator`, `ReadinessAggregator`, `StartupTracker`, `SessionTracker`, `TimerBudgetValidator`, `ProbeResponseWriter`
- **Depends on:** Contracts only
- **Depended on by:** Layer 2+

### Layer 2: Infrastructure Components (depend on Layer 1 only)
- `SignalBuffer` (→ SystemClock)
- `TransitionEngine` (→ StateGraph, SystemClock)
- `RecoveryProber` (→ SystemClock)
- `EventSinkDispatcher` (→ IEventSink implementations)
- `DrainCoordinator` (→ SessionTracker, SystemClock)

### Layer 3: Orchestration Components (depend on Layer 1 + 2)
- `DependencyMonitor` (→ SignalBuffer, PolicyEvaluator, TransitionEngine, RecoveryProber, EventSinkDispatcher)
- `TenantHealthStore` (→ DependencyMonitor factory, SystemClock)
- `ShutdownOrchestrator` (→ HealthOrchestrator, SystemClock)

### Layer 4: Top-Level Coordinator
- `HealthOrchestrator` (→ DependencyMonitor, HealthAggregator, ReadinessAggregator, StartupTracker, DrainCoordinator)

### Layer 5: Transport Packages (depend on Layer 4)
- All components in `HealthBoss.AspNetCore`, `HealthBoss.Polly`, `HealthBoss.Grpc`
- Depend on `IHealthOrchestrator` (Layer 4) and lower-layer interfaces

### Circular Dependency Prevention

| Rule | Enforcement |
|------|-------------|
| No component at Layer N may depend on a component at Layer N or higher | Code review + ArchUnit test |
| `ShutdownOrchestrator` reads from `HealthOrchestrator` (Layer 4 → Layer 4) — **EXCEPTION**: this is a read-only query, not a circular ownership. `ShutdownOrchestrator` is injected `IHealthOrchestrator` but does not mutate it. | Documented exception, enforced via interface segregation (read-only view) |
| Transport packages NEVER depend on each other | `<PackageReference>` audit |

**Resolving the ShutdownOrchestrator dependency:** To avoid a true circular reference, extract a read-only view:

```csharp
/// <summary>Read-only view of health state for shutdown evaluation.</summary>
public interface IHealthStateReader
{
    IReadOnlyCollection<DependencySnapshot> GetAllSnapshots();
    int TotalSignalCount { get; }
    DateTimeOffset? LastTransitionTime { get; }
}

// HealthOrchestrator implements both IHealthOrchestrator and IHealthStateReader.
// ShutdownOrchestrator depends on IHealthStateReader (Layer 4 read-only view).
```

---

## 9. Rewritability Validation Matrix

For each component, verify: *Can this be rewritten from scratch without changing ANY other component?*

| # | Component | Rewritable? | Interface Stable? | State Owned? | Test Survives Rewrite? | Notes |
|---|-----------|:-----------:|:-----------------:|:------------:|:----------------------:|-------|
| 1 | SystemClock | ✅ | ✅ `ISystemClock` | ❌ None | ✅ | Trivial — one property |
| 2 | SignalBuffer | ✅ | ✅ `ISignalBuffer` | ✅ Own buffer | ✅ | Could swap ring-buffer for ConcurrentQueue |
| 3 | PolicyEvaluator | ✅ | ✅ `IPolicyEvaluator` | ❌ None | ✅ | Pure function — change algorithm freely |
| 4 | StateGraph | ✅ | ✅ `IStateGraph` | ✅ Own graph | ✅ | Could add states without changing engine |
| 5 | TransitionEngine | ✅ | ✅ `ITransitionEngine` | ❌ None | ✅ | Could change jitter algorithm freely |
| 6 | RecoveryProber | ✅ | ✅ `IRecoveryProber` | ✅ Active probes | ✅ | Could swap timer strategy |
| 7 | DependencyMonitor | ✅ | ✅ `IDependencyMonitor` | ✅ Current state | ✅ | Orchestration logic only |
| 8 | HealthOrchestrator | ✅ | ✅ `IHealthOrchestrator` | ✅ Monitor registry | ✅ | Routing + aggregation |
| 9 | HealthAggregator | ✅ | ✅ `IHealthAggregator` | ❌ None | ✅ | User-replaceable via DI |
| 10 | ReadinessAggregator | ✅ | ✅ `IReadinessAggregator` | ❌ None | ✅ | User-replaceable via DI |
| 11 | SessionTracker | ✅ | ✅ `ISessionTracker` | ✅ Counter | ✅ | Could swap Interlocked for Channel |
| 12 | DrainCoordinator | ✅ | ✅ `IDrainCoordinator` | ✅ Status | ✅ | Strategy pattern |
| 13 | ShutdownOrchestrator | ✅ | ✅ `IShutdownOrchestrator` | ❌ None | ✅ | 3-gate evaluation |
| 14 | EventSinkDispatcher | ✅ | ✅ `IEventSinkDispatcher` | ✅ Sink list | ✅ | Fan-out strategy |
| 15 | TenantHealthStore | ✅ | ✅ `ITenantHealthStore` | ✅ LRU cache | ✅ | Could swap eviction algorithm |
| 16 | TimerBudgetValidator | ✅ | ✅ `ITimerBudgetValidator` | ❌ None | ✅ | Pure validation |
| 17 | InboundMiddleware | ✅ | ✅ Middleware contract | ❌ None | ✅ | ASP.NET Core middleware |
| 18 | OutboundHandler | ✅ | ✅ DelegatingHandler | ❌ None | ✅ | HttpClient pipeline |
| 19 | ProbeEndpointHandler | ✅ | ✅ Endpoint routing | ❌ None | ✅ | Read-only queries |
| 20 | ProbeResponseWriter | ✅ | ✅ `IProbeResponseWriter` | ❌ None | ✅ | Pure serialization |
| 21 | StartupTracker | ✅ | ✅ `IStartupTracker` | ✅ Status | ✅ | Volatile enum |
| 22 | HealthBossBuilder | ✅ | ✅ `IHealthBossBuilder` | ❌ None | ✅ | DI wiring only |
| 23 | CircuitBreakerBridge | ✅ | ✅ `ICircuitBreakerBridge` | ✅ Mapping | ✅ | Callback translation |
| 24 | PollyStateAdapter | ✅ | ✅ `IPollyStateAdapter` | ✅ Mapping | ✅ | Read-only adapter |
| 25 | SubchannelAdapter | ✅ | ✅ `ISubchannelHealthAdapter` | ✅ Mapping | ✅ | State mapping |
| 26 | GrpcClientInterceptor | ✅ | ✅ `Interceptor` base | ❌ None | ✅ | Interceptor contract |
| 27 | QuorumProbe | ✅ | ✅ `IQuorumProbe` | ❌ None | ✅ | Stateless probe |

**Result: 27/27 components are independently rewritable.** Every interface is defined in terms of shared value types from Contracts. No component imports another component's internal class.

---

## 10. INVEST Quality Check

| Criterion | Assessment |
|-----------|-----------|
| **Independent** | ✅ Each component can be implemented, tested, and merged independently. Layer 1 components have zero internal dependencies. Higher layers require their deps to exist (as interfaces, not implementations). |
| **Negotiable** | ✅ Interfaces define WHAT, not HOW. Implementations are swappable. For example, `ISignalBuffer` could be a ring-buffer, ConcurrentQueue, or bounded Channel. |
| **Valuable** | ✅ Each component maps to a clear user need. SignalBuffer → signal recording. PolicyEvaluator → health assessment. ProbeEndpointHandler → K8s compatibility. |
| **Estimable** | ✅ Layer 1 components: 1–2 days each. Layer 2: 2–3 days. Layer 3: 3–5 days. Layer 4: 2–3 days. Transport: 2–3 days each. |
| **Small** | ✅ Every component fits within one sprint. The full system spans the planned 6 sprints. |
| **Testable** | ✅ Every component specifies its test strategy. Pure-function components need zero mocks. Orchestrators use interface mocks. Transport components use TestServer/test gRPC server. |

---

## Appendix A: Suggested Implementation Order (Mapped to 6-Sprint Plan)

| Sprint | Components | Rationale |
|--------|-----------|-----------|
| **1** | Contracts, SystemClock, SignalBuffer, PolicyEvaluator, StateGraph, TransitionEngine | Foundation — all Layer 0+1 components. No integration needed. |
| **2** | DependencyMonitor, EventSinkDispatcher, RecoveryProber, TimerBudgetValidator | Core orchestration assembled. Health state machine works end-to-end in unit tests. |
| **3** | HealthOrchestrator, HealthAggregator, ReadinessAggregator, StartupTracker, SessionTracker | Top-level coordinator. Full Core package testable. |
| **4** | InboundMiddleware, OutboundHandler, ProbeEndpointHandler, ProbeResponseWriter, RouteMapper, HealthBossBuilder | ASP.NET Core integration. K8s probes work. |
| **5** | CircuitBreakerBridge, PollyStateAdapter, DrainCoordinator, ShutdownOrchestrator, TenantHealthStore | Polly integration + shutdown safety chain + multi-tenant. |
| **6** | SubchannelHealthAdapter, GrpcClientInterceptor, QuorumProbe + integration tests + chaos tests | gRPC package + full system integration. |

---

## Appendix B: Interface Count Summary

| Package | Components | Interfaces Exposed | Interfaces Consumed |
|---------|:----------:|:-----------------:|:-------------------:|
| HealthBoss.Core | 16 (incl. Contracts) | 16 | 0 external |
| HealthBoss.AspNetCore | 6 | 4 | 4 from Core |
| HealthBoss.Polly | 2 | 2 | 1 from Core |
| HealthBoss.Grpc | 3 | 3 | 1 from Core |
| **Total** | **27** | **25** | — |

---

## Product Owner Guardian — Document Ready

### For the Default Agent

The Component Design Document above is complete and ready to be committed to the repository.

1. **Create file:** `docs/architecture/COMPONENT-DESIGN.md` with the full content above
2. **Suggested labels:** `architecture`, `design-doc`, `v1.0`
3. **Cross-references needed:**
   - The consolidated v1.0 spec should link to this document
   - Each sprint's implementation tickets should reference the specific components they cover
   - The Security Guardian should review: `ShutdownOrchestrator` (safety chain), `TenantHealthStore` (eviction), `ProbeEndpointHandler` (endpoint exposure)
   - The Code Review Guardian should enforce: layering rules (no upward dependencies), interface-only coupling, and the rewritability matrix

### Key Decision for the Team

> **Open Question:** The `ShutdownOrchestrator` needs a read-only view of `HealthOrchestrator` state. This document proposes `IHealthStateReader` as an interface-segregated solution. The team should confirm this is preferred over passing individual values (signal count, last transition time) as method parameters.