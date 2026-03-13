# HealthBoss v1.0 — Consolidated Specification

## 1. Title and Summary

**Title:** HealthBoss v1.0 — Stateful Health Intelligence Layer for ASP.NET Core

**Summary:** HealthBoss is a set of NuGet packages that add a **stateful, policy-driven health intelligence layer** on top of the existing ASP.NET Core `IHealthCheck` infrastructure. Instead of polling dependencies on each probe request, HealthBoss continuously tracks success/failure signals from real traffic, applies configurable policies (rate-based, latency-based, quorum-based, and critical-dependency lifecycle), and resolves health + readiness status from accumulated state. It supports per-component tracking, per-tenant health isolation, session-aware lifecycle, event-driven Polly circuit breaker bridging, gRPC subchannel health integration, and extensible alarm/notification sinks.

**Packages (4):**

| # | Package | Purpose |
|---|---------|---------|
| 1 | `HealthBoss.Core` | State machine, all policies, all trackers, event sinks |
| 2 | `HealthBoss.AspNetCore` | HTTP middleware, WebSocket tracker adapter, K8s probe endpoints |
| 3 | `HealthBoss.Polly` | Polly v8 circuit breaker event-driven bridge |
| 4 | `HealthBoss.Grpc` | gRPC subchannel adapter, client interceptor, quorum probe impl |

---

## 2. Problem Statement

ASP.NET Core's built-in health check system (`Microsoft.Extensions.Diagnostics.HealthChecks`) has fundamental limitations for production microservices:

| Gap | Built-in Behavior | HealthBoss Solution |
|-----|-------------------|---------------------|
| **Stateless polling** | Each `/healthz` request executes `IHealthCheck.CheckHealthAsync` — a point-in-time probe with no memory | Continuous signal ingestion from real traffic; sliding-window state |
| **No rate-based evaluation** | A single failed check = unhealthy; no concept of "5% failure rate is acceptable" | `HealthPolicy` with configurable `DegradedThreshold` / `UnhealthyThreshold` over a sliding window |
| **No latency awareness** | No way to detect that a dependency is responding but with degraded latency | `ResponseTimePolicy` with percentile-based thresholds (p50/p95/p99) |
| **No dual-probe semantics** | Health and readiness share the same evaluation model; Kubernetes needs different thresholds | Separate `AggregateHealthResolver` and `AggregateReadinessResolver` delegates |
| **No circuit breaker integration** | Polly circuit breakers track failures internally but don't feed into health probes | Event-driven bridge via `OnOpened`/`OnClosed`/`OnHalfOpened` Polly callbacks |
| **No critical dependency lifecycle** | No state machine for dependencies where failure means "drain and shutdown" (e.g., CosmosDB) | `HealthStateMachine` with `Ready → NotReady → Draining → Unhealthy → Shutdown` graph |
| **No per-tenant health** | No way to track per-customer health in multi-tenant systems (e.g., per-tenant Storage Accounts) | `ITenantHealthTracker` with isolated per-(component, tenantId) sliding windows |
| **No quorum awareness** | No "N of M instances must be healthy" policy for pooled backends | `QuorumHealthPolicy` with `IInstanceHealthProbe` for instance discovery + probing |
| **No session lifecycle** | No awareness of active WebSocket/streaming sessions during drain | `ISessionHealthTracker` with active count gauge + completion success rate |
| **No alarm integration** | Health state changes aren't routed to external notification systems | `IHealthEventSink` with built-in structured log + OpenTelemetry sinks |
| **No gRPC health** | gRPC subchannel connectivity state doesn't feed into health assessment | `HealthBoss.Grpc` package with subchannel adapter and client interceptor |

---

## 3. Architecture Overview

### Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            ASP.NET Core Host                                │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │                     HealthBoss.AspNetCore                           │    │
│  │  ┌──────────────────┐  ┌──────────────────┐  ┌─────────────────┐  │    │
│  │  │HealthMiddleware  │  │HealthEndpoints   │  │ WebSocket       │  │    │
│  │  │(per-endpoint     │  │/healthz          │  │ SessionTracker  │  │    │
│  │  │ inbound tracking)│  │/readyz           │  │ Adapter         │  │    │
│  │  └────────┬─────────┘  └────────┬─────────┘  └────────┬────────┘  │    │
│  └───────────┼──────────────────────┼──────────────────────┼──────────┘    │
│              │                      │                      │               │
│  ┌───────────┼──────────────────────┼──────────────────────┼──────────┐    │
│  │           ▼                      ▼                      ▼          │    │
│  │                        HealthBoss.Core                             │    │
│  │                                                                    │    │
│  │  ┌─────────────────────────────────────────────────────────────┐  │    │
│  │  │                  IHealthStateTracker                        │  │    │
│  │  │  ┌──────────────┐  ┌────────────────┐  ┌───────────────┐  │  │    │
│  │  │  │RecordSignal()│  │GetAssessment() │  │SlidingWindow  │  │  │    │
│  │  │  └──────────────┘  └────────────────┘  └───────────────┘  │  │    │
│  │  └─────────────────────────────────────────────────────────────┘  │    │
│  │                                                                    │    │
│  │  ┌──────────────┐  ┌──────────────────┐  ┌────────────────────┐  │    │
│  │  │HealthPolicy  │  │ResponseTimePolicy│  │QuorumHealthPolicy │  │    │
│  │  │(rate-based)  │  │(latency-based)   │  │(N-of-M instances) │  │    │
│  │  └──────────────┘  └──────────────────┘  └────────────────────┘  │    │
│  │                                                                    │    │
│  │  ┌──────────────────┐  ┌─────────────────────────────────────┐   │    │
│  │  │CriticalDependency│  │         HealthStateMachine          │   │    │
│  │  │Policy            │  │ Ready→NotReady→Draining→Unhealthy→  │   │    │
│  │  │                  │◄─┤ Shutdown                             │   │    │
│  │  └──────────────────┘  └─────────────────────────────────────┘   │    │
│  │                                                                    │    │
│  │  ┌──────────────────┐  ┌──────────────────┐                      │    │
│  │  │ITenantHealth     │  │ISessionHealth    │                      │    │
│  │  │Tracker           │  │Tracker           │                      │    │
│  │  │(per-tenant       │  │(active count +   │                      │    │
│  │  │ sliding windows) │  │ completion rate)  │                      │    │
│  │  └──────────────────┘  └──────────────────┘                      │    │
│  │                                                                    │    │
│  │  ┌───────────────────────┐  ┌────────────────────────────────┐   │    │
│  │  │AggregateHealthResolver│  │AggregateReadinessResolver     │   │    │
│  │  │(delegate)             │  │(delegate)                      │   │    │
│  │  └───────────────────────┘  └────────────────────────────────┘   │    │
│  │                                                                    │    │
│  │  ┌──────────────────────────────────────────────────────────┐    │    │
│  │  │               IHealthEventSink                           │    │    │
│  │  │  StructuredLogEventSink │ OpenTelemetryMetricEventSink   │    │    │
│  │  └──────────────────────────────────────────────────────────┘    │    │
│  └────────────────────────────────────────────────────────────────────┘    │
│                                                                             │
│  ┌─────────────────────────┐     ┌─────────────────────────────────┐      │
│  │   HealthBoss.Polly      │     │       HealthBoss.Grpc           │      │
│  │  ┌───────────────────┐  │     │  ┌─────────────────────────┐   │      │
│  │  │PollyCircuitBreaker│  │     │  │GrpcSubchannelHealth     │   │      │
│  │  │Bridge             │  │     │  │Adapter                  │   │      │
│  │  │(event-driven via  │  │     │  │(bridges connectivity    │   │      │
│  │  │ OnOpened/OnClosed/ │  │     │  │ state to quorum)        │   │      │
│  │  │ OnHalfOpened)     │  │     │  ├─────────────────────────┤   │      │
│  │  └───────────────────┘  │     │  │GrpcClientHealth         │   │      │
│  │  ┌───────────────────┐  │     │  │Interceptor              │   │      │
│  │  │OutboundDelegating │  │     │  │(call-level success/     │   │      │
│  │  │Handler            │  │     │  │ failure tracking)        │   │      │
│  │  └───────────────────┘  │     │  └─────────────────────────┘   │      │
│  └─────────────────────────┘     └─────────────────────────────────┘      │
│                                                                             │
│  ┌─────────────────────────────────────────────────────────────────────┐  │
│  │                     Kubernetes / Orchestrator                       │  │
│  │   GET /healthz  →  liveness probe                                   │  │
│  │   GET /readyz   →  readiness probe                                  │  │
│  └─────────────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Data Flow

```
Real Traffic (HTTP/gRPC/SDK calls)
        │
        ▼
┌─────────────────┐     ┌──────────────────┐     ┌───────────────────┐
│ Record Signal   │────▶│ Sliding Window    │────▶│ Policy Evaluation │
│ (success/fail/  │     │ (per-component,   │     │ (rate, latency,   │
│  latency)       │     │  per-tenant)      │     │  quorum, lifecycle│
└─────────────────┘     └──────────────────┘     └─────────┬─────────┘
                                                            │
                                                            ▼
                                                 ┌──────────────────┐
                                                 │HealthAssessment  │
                                                 │per component     │
                                                 └─────────┬────────┘
                                                            │
                                    ┌───────────────────────┼─────────────────┐
                                    ▼                       ▼                 ▼
                         ┌──────────────────┐   ┌─────────────────┐  ┌──────────────┐
                         │AggregateHealth   │   │AggregateReadi-  │  │IHealthEvent  │
                         │Resolver          │   │nessResolver     │  │Sink.Emit()   │
                         │(user delegate)   │   │(user delegate)  │  │              │
                         └────────┬─────────┘   └────────┬────────┘  └──────────────┘
                                  │                      │
                                  ▼                      ▼
                         ┌────────────────┐    ┌─────────────────┐
                         │  GET /healthz  │    │  GET /readyz    │
                         │  200/503       │    │  200/503        │
                         └────────────────┘    └─────────────────┘
```

---

## 4. Package Structure

### 4.1 HealthBoss.Core

**Target:** `netstandard2.1` + `net8.0` (multi-target)
**Dependencies:** None (zero external dependencies)

**Contains:**
- `IHealthStateTracker` — per-component signal ingestion and assessment
- `ITenantHealthTracker` — per-(component, tenantId) isolated tracking with LRU+TTL eviction
- `ISessionHealthTracker` — active session count gauge + completion success rate
- `HealthSignal` — record representing a single success/failure/latency observation
- `HealthAssessment` — record with computed status, success rate, window stats
- `ResponseTimeAssessment` — record with p50/p95/p99 latency metrics
- `HealthPolicy` — rate-based thresholds (DegradedThreshold, UnhealthyThreshold, WindowSize, MinimumThroughput)
- `ResponseTimePolicy` — latency-based thresholds (percentile target, DegradedThreshold, UnhealthyThreshold)
- `QuorumHealthPolicy` — N-of-M instance quorum evaluation
- `CriticalDependencyPolicy` — lifecycle policy with recovery retry, drain timeout, force shutdown
- `HealthStateMachine` — deterministic state graph for critical dependency lifecycle
- `IInstanceHealthProbe` — interface for instance discovery + probing (quorum)
- `IHealthEventSink` — interface for event notification
- `StructuredLogEventSink` — built-in ILogger-based sink
- `OpenTelemetryMetricEventSink` — built-in OTel metrics sink
- `AggregateHealthResolver` — `Func<IReadOnlyDictionary<string, HealthAssessment>, HealthStatus>` delegate type
- `AggregateReadinessResolver` — same signature, separate delegate
- `HealthStatus` enum — `Healthy`, `Degraded`, `Unhealthy`
- `ServiceState` enum — `Ready`, `NotReady`, `Draining`, `Unhealthy`, `Shutdown`
- `TenantHealthStatus` enum — `Healthy`, `Degraded`, `Unavailable`
- All in-memory data structures (sliding windows, ConcurrentDictionary, LRU cache)

### 4.2 HealthBoss.AspNetCore

**Target:** `net8.0`
**Dependencies:** `HealthBoss.Core`, `Microsoft.AspNetCore.App` (framework ref)

**Contains:**
- `HealthBossMiddleware` — inbound HTTP request tracking (per-endpoint via route-based config)
- `HealthBossEndpointRouteBuilderExtensions` — `MapHealthBossLiveness("/healthz")`, `MapHealthBossReadiness("/readyz")`
- `HealthBossDelegatingHandler` — outbound `HttpClient` request tracking
- `WebSocketSessionTrackerAdapter` — bridges ASP.NET Core WebSocket lifecycle to `ISessionHealthTracker`
- JSON response writer for probe endpoints
- `IServiceCollection` / `IApplicationBuilder` extension methods

### 4.3 HealthBoss.Polly

**Target:** `netstandard2.1` + `net8.0`
**Dependencies:** `HealthBoss.Core`, `Polly.Core` (>= 8.0.0)

**Contains:**
- `PollyCircuitBreakerBridge` — wires `OnOpened`/`OnClosed`/`OnHalfOpened` Polly callbacks to `IHealthStateTracker.RecordSignal`
- `ResiliencePipelineBuilderExtensions` — `.AddHealthBossTracking(componentName)` extension
- Maps Polly `CircuitState.Open` → `HealthSignal(success: false)`, `CircuitState.Closed` → `HealthSignal(success: true)`, `CircuitState.HalfOpen` → no-op (probe in progress)

### 4.4 HealthBoss.Grpc

**Target:** `net8.0`
**Dependencies:** `HealthBoss.Core`, `Grpc.Net.Client` (>= 2.60.0)

**Contains:**
- `GrpcSubchannelHealthAdapter` — implements `IInstanceHealthProbe` by reading gRPC subchannel connectivity state (`Ready`/`Idle`/`TransientFailure`/`Shutdown`)
- `GrpcClientHealthInterceptor` — `Interceptor` subclass that records per-call success/failure/latency signals to `IHealthStateTracker`
- Extension methods for `GrpcChannelOptions` and DI registration

---

## 5. Core Abstractions

### 5.1 Enums

```csharp
namespace HealthBoss.Core;

/// <summary>Component-level health status derived from policy evaluation.</summary>
public enum HealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Unhealthy = 2
}

/// <summary>Service-level lifecycle state for critical dependency management.</summary>
public enum ServiceState
{
    Ready = 0,
    NotReady = 1,
    Draining = 2,
    Unhealthy = 3,
    Shutdown = 4
}

/// <summary>Per-tenant health status (isolated from service-level probes).</summary>
public enum TenantHealthStatus
{
    Healthy = 0,
    Degraded = 1,
    Unavailable = 2
}
```

### 5.2 Records / Value Types

```csharp
/// <summary>A single observation from real traffic.</summary>
/// <param name="ComponentName">snake_case identifier for the dependency (e.g., "cosmos_db", "blob_storage").</param>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Timestamp">UTC timestamp of the observation.</param>
/// <param name="Duration">Optional response time for latency tracking.</param>
/// <param name="Tags">Optional key-value metadata (e.g., tenant_id, endpoint).</param>
public sealed record HealthSignal(
    string ComponentName,
    bool Success,
    DateTimeOffset Timestamp,
    TimeSpan? Duration = null,
    IReadOnlyDictionary<string, string>? Tags = null);

/// <summary>Computed health assessment for a single component.</summary>
public sealed record HealthAssessment(
    string ComponentName,
    HealthStatus Status,
    double SuccessRate,
    long TotalRequests,
    long FailedRequests,
    TimeSpan WindowSize,
    DateTimeOffset EvaluatedAt,
    ResponseTimeAssessment? ResponseTime = null);

/// <summary>Latency percentile metrics for a component.</summary>
public sealed record ResponseTimeAssessment(
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan P99,
    HealthStatus LatencyStatus,
    string EvaluatedPercentile);

/// <summary>Quorum evaluation result.</summary>
public sealed record QuorumAssessment(
    string ComponentName,
    int HealthyInstances,
    int TotalInstances,
    int MinimumRequired,
    bool QuorumMet,
    HealthStatus Status,
    DateTimeOffset EvaluatedAt);

/// <summary>Per-tenant health assessment.</summary>
public sealed record TenantHealthAssessment(
    string ComponentName,
    string TenantId,
    TenantHealthStatus Status,
    double SuccessRate,
    long TotalRequests,
    long FailedRequests,
    TimeSpan WindowSize,
    DateTimeOffset EvaluatedAt);

/// <summary>Session tracker snapshot.</summary>
public sealed record SessionHealthSnapshot(
    int ActiveSessions,
    double RecentCompletionSuccessRate,
    long TotalCompleted,
    long TotalFailed,
    DateTimeOffset SnapshotAt);
```

### 5.3 Policy Types

```csharp
/// <summary>Rate-based health policy for a component.</summary>
public sealed class HealthPolicy
{
    /// <summary>Component identifier (snake_case). Example: "cosmos_db".</summary>
    public required string ComponentName { get; init; }

    /// <summary>Sliding window duration. Default: 60 seconds.</summary>
    public TimeSpan WindowSize { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Success rate below which status becomes Degraded. Default: 0.95 (95%).</summary>
    public double DegradedThreshold { get; init; } = 0.95;

    /// <summary>Success rate below which status becomes Unhealthy. Default: 0.70 (70%).</summary>
    public double UnhealthyThreshold { get; init; } = 0.70;

    /// <summary>Minimum signals in window before policy evaluates. Below this, status is Healthy. Default: 10.</summary>
    public int MinimumThroughput { get; init; } = 10;
}

/// <summary>Latency-based health policy. Opt-in per component.</summary>
public sealed class ResponseTimePolicy
{
    /// <summary>Component this policy applies to (must match a HealthPolicy.ComponentName).</summary>
    public required string ComponentName { get; init; }

    /// <summary>Which percentile to evaluate. Default: "p95". Allowed: "p50", "p95", "p99".</summary>
    public string Percentile { get; init; } = "p95";

    /// <summary>Latency above which status becomes Degraded.</summary>
    public required TimeSpan DegradedThreshold { get; init; }

    /// <summary>Latency above which status becomes Unhealthy. Null = Degraded only, never Unhealthy from latency alone.</summary>
    public TimeSpan? UnhealthyThreshold { get; init; }

    /// <summary>Minimum signals in window before latency policy evaluates. Default: 10.</summary>
    public int MinimumThroughput { get; init; } = 10;
}

/// <summary>Quorum-based policy for pooled backends. "N of M must be healthy."</summary>
public sealed class QuorumHealthPolicy
{
    /// <summary>Logical component name for the pool (e.g., "grpc_backend_pool").</summary>
    public required string ComponentName { get; init; }

    /// <summary>Minimum number of healthy instances required.</summary>
    public required int MinimumHealthyInstances { get; init; }

    /// <summary>Probe interval for instance discovery. Default: 10 seconds.</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout for individual instance probe. Default: 5 seconds.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Lifecycle policy for critical dependencies (e.g., CosmosDB).</summary>
public sealed class CriticalDependencyPolicy
{
    /// <summary>Component this policy governs.</summary>
    public required string ComponentName { get; init; }

    /// <summary>Number of recovery attempts before transitioning to Draining. Default: 3.</summary>
    public int RecoveryRetryCount { get; init; } = 3;

    /// <summary>Interval between recovery retries. Default: 5 seconds.</summary>
    public TimeSpan RecoveryRetryInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Time to wait in Draining state before transitioning to Unhealthy. Default: 30 seconds.</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Time after Unhealthy before forcing shutdown. Default: 60 seconds.</summary>
    public TimeSpan ForceShutdownTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
```

### 5.4 Interfaces

```csharp
/// <summary>Per-component signal ingestion and health assessment.</summary>
public interface IHealthStateTracker
{
    /// <summary>Record a signal from real traffic.</summary>
    void RecordSignal(HealthSignal signal);

    /// <summary>Get the current assessment for a specific component.</summary>
    HealthAssessment GetAssessment(string componentName);

    /// <summary>Get assessments for all tracked components.</summary>
    IReadOnlyDictionary<string, HealthAssessment> GetAllAssessments();

    /// <summary>Get the list of registered component names.</summary>
    IReadOnlyList<string> RegisteredComponents { get; }
}

/// <summary>Per-(component, tenantId) health tracking with sliding windows.</summary>
public interface ITenantHealthTracker
{
    /// <summary>Record a signal for a specific tenant.</summary>
    void RecordSignal(string componentName, string tenantId, HealthSignal signal);

    /// <summary>Get health assessment for a specific tenant on a specific component.</summary>
    TenantHealthAssessment GetAssessment(string componentName, string tenantId);

    /// <summary>Get all tenant assessments for a component.</summary>
    IReadOnlyDictionary<string, TenantHealthAssessment> GetAssessments(string componentName);

    /// <summary>Get count of tracked tenants for a component.</summary>
    int GetTrackedTenantCount(string componentName);
}

/// <summary>Session lifecycle tracking (WebSocket, streaming).</summary>
public interface ISessionHealthTracker
{
    /// <summary>Record a session start. Returns a session ID for correlation.</summary>
    string StartSession(string? metadata = null);

    /// <summary>Record a session completion.</summary>
    void CompleteSession(string sessionId, bool success);

    /// <summary>Get current snapshot of session health.</summary>
    SessionHealthSnapshot GetSnapshot();

    /// <summary>Current active session count (gauge).</summary>
    int ActiveSessionCount { get; }
}

/// <summary>Instance discovery and probing for quorum evaluation.</summary>
public interface IInstanceHealthProbe
{
    /// <summary>Discover current instances and their health status.</summary>
    Task<IReadOnlyList<InstanceHealthResult>> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Result of probing a single instance.</summary>
public sealed record InstanceHealthResult(
    string InstanceId,
    bool IsHealthy,
    string? Endpoint = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>Event sink for health state change notifications.</summary>
public interface IHealthEventSink
{
    /// <summary>Called when a component's health status changes at the service level.</summary>
    Task OnServiceStateChangedAsync(ServiceStateChangedEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Called when a tenant's health status degrades.</summary>
    Task OnTenantDegradedAsync(TenantHealthEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Called when a tenant's health recovers.</summary>
    Task OnTenantRecoveredAsync(TenantHealthEvent @event, CancellationToken cancellationToken = default);

    /// <summary>Called when quorum state changes.</summary>
    Task OnQuorumChangedAsync(QuorumChangedEvent @event, CancellationToken cancellationToken = default);
}

/// <summary>Events emitted by HealthBoss.</summary>
public sealed record ServiceStateChangedEvent(
    string ComponentName,
    HealthStatus PreviousStatus,
    HealthStatus NewStatus,
    HealthAssessment Assessment,
    DateTimeOffset Timestamp);

public sealed record TenantHealthEvent(
    string ComponentName,
    string TenantId,
    TenantHealthStatus PreviousStatus,
    TenantHealthStatus NewStatus,
    TenantHealthAssessment Assessment,
    DateTimeOffset Timestamp);

public sealed record QuorumChangedEvent(
    string ComponentName,
    bool PreviousQuorumMet,
    bool NewQuorumMet,
    QuorumAssessment Assessment,
    DateTimeOffset Timestamp);
```

### 5.5 HealthStateMachine

```csharp
/// <summary>
/// Deterministic state machine for critical dependency lifecycle.
/// State graph: Ready → NotReady → Draining → Unhealthy → Shutdown
/// Orchestration model: Hybrid — HealthBoss manages probe state + drain timeout by default;
/// user can replace orchestration behavior via IShutdownOrchestrator.
/// </summary>
public sealed class HealthStateMachine
{
    public ServiceState CurrentState { get; }
    public DateTimeOffset LastTransitionAt { get; }
    public int RecoveryAttemptsRemaining { get; }

    /// <summary>Process a health signal and potentially transition state.</summary>
    public ServiceState ProcessSignal(HealthSignal signal);

    /// <summary>Force a specific state transition (for testing or manual override).</summary>
    public void ForceTransition(ServiceState newState);

    /// <summary>Event raised on every state transition.</summary>
    public event EventHandler<StateTransitionEventArgs>? StateTransitioned;
}

public sealed record StateTransitionEventArgs(
    ServiceState FromState,
    ServiceState ToState,
    string Reason,
    DateTimeOffset Timestamp);

/// <summary>
/// Replaceable shutdown orchestration strategy.
/// Default implementation calls IHostApplicationLifetime.StopApplication().
/// Users can wire custom shutdown actions.
/// </summary>
public interface IShutdownOrchestrator
{
    Task InitiateDrainAsync(CancellationToken cancellationToken = default);
    Task InitiateShutdownAsync(CancellationToken cancellationToken = default);
}
```

### 5.6 Delegate Types

```csharp
/// <summary>
/// Aggregate all component assessments into a single service-level health status.
/// Wired by the user — different logic for liveness vs readiness.
/// </summary>
public delegate HealthStatus AggregateHealthResolver(
    IReadOnlyDictionary<string, HealthAssessment> assessments);

/// <summary>
/// Aggregate all component assessments into a single readiness status.
/// Separate from health — readiness may have stricter thresholds.
/// </summary>
public delegate HealthStatus AggregateReadinessResolver(
    IReadOnlyDictionary<string, HealthAssessment> assessments);
```

### 5.7 Tenant Eviction Configuration

```csharp
/// <summary>Memory management configuration for per-tenant tracking.</summary>
public sealed class TenantEvictionOptions
{
    /// <summary>Evict tenant windows after this duration of inactivity. Default: 30 minutes.</summary>
    public TimeSpan TtlAfterLastAccess { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Maximum number of tracked tenants per component. LRU eviction above this. Default: 10,000.</summary>
    public int MaxTenantsPerComponent { get; init; } = 10_000;

    /// <summary>Frequency of the eviction scan. Default: 60 seconds.</summary>
    public TimeSpan EvictionScanInterval { get; init; } = TimeSpan.FromSeconds(60);
}
```

---

## 6. Policy Evaluation Rules

### 6.1 Rate-Based Evaluation (HealthPolicy)

```
Given: window signals, MinimumThroughput, DegradedThreshold, UnhealthyThreshold

IF totalSignals < MinimumThroughput → Healthy (insufficient data)
ELSE:
  successRate = successCount / totalSignals
  IF successRate < UnhealthyThreshold → Unhealthy
  ELSE IF successRate < DegradedThreshold → Degraded
  ELSE → Healthy
```

### 6.2 Latency-Based Evaluation (ResponseTimePolicy)

```
Given: window latencies, Percentile, DegradedThreshold, UnhealthyThreshold

IF totalSignals < MinimumThroughput → Healthy (insufficient data)
ELSE:
  percentileValue = compute(Percentile, latencies)  // e.g., p95
  IF UnhealthyThreshold != null AND percentileValue > UnhealthyThreshold → Unhealthy
  ELSE IF percentileValue > DegradedThreshold → Degraded
  ELSE → Healthy
```

### 6.3 Two-Dimensional Evaluation (Combined)

When a component has BOTH `HealthPolicy` and `ResponseTimePolicy`, the final status is **worst-of-both**:

```
finalStatus = Max(rateStatus, latencyStatus)
// where Healthy=0, Degraded=1, Unhealthy=2 → Max gives worst status
```

### 6.4 Quorum Evaluation (QuorumHealthPolicy)

```
Given: probe results, MinimumHealthyInstances

healthyCount = probeResults.Count(r => r.IsHealthy)
totalCount = probeResults.Count
quorumMet = healthyCount >= MinimumHealthyInstances

IF quorumMet → Healthy
ELSE IF healthyCount > 0 → Degraded
ELSE → Unhealthy
```

### 6.5 Critical Dependency Lifecycle (HealthStateMachine)

```
State transitions (deterministic):

Ready:
  ON consecutive failures >= RecoveryRetryCount → NotReady
  
NotReady:
  ON recovery signal (success) → Ready
  ON recovery retries exhausted → Draining
  
Draining:
  Readiness probe returns Unhealthy (removes from K8s service)
  ON DrainTimeout elapsed → Unhealthy
  ON recovery signal → Ready (abort drain)
  
Unhealthy:
  ON ForceShutdownTimeout elapsed → Shutdown
  ON recovery signal → Ready (abort shutdown)
  
Shutdown:
  Terminal. IShutdownOrchestrator.InitiateShutdownAsync() called.
  Default: IHostApplicationLifetime.StopApplication()
```

---

## 7. Configuration API

### 7.1 Complete DI Registration

```csharp
var builder = WebApplication.CreateBuilder(args);

// ── HealthBoss.Core ──────────────────────────────────────────────
builder.Services.AddHealthBoss(options =>
{
    // ── Rate-Based Policies ──────────────────────────────────
    options.AddComponent(new HealthPolicy
    {
        ComponentName = "cosmos_db",
        WindowSize = TimeSpan.FromSeconds(60),
        DegradedThreshold = 0.95,
        UnhealthyThreshold = 0.70,
        MinimumThroughput = 10
    });

    options.AddComponent(new HealthPolicy
    {
        ComponentName = "blob_storage",
        WindowSize = TimeSpan.FromSeconds(120),
        DegradedThreshold = 0.90,
        UnhealthyThreshold = 0.50,
        MinimumThroughput = 5
    });

    options.AddComponent(new HealthPolicy
    {
        ComponentName = "redis_cache",
        WindowSize = TimeSpan.FromSeconds(30),
        DegradedThreshold = 0.99,
        UnhealthyThreshold = 0.90,
        MinimumThroughput = 20
    });

    // ── Latency Policies (opt-in per component) ─────────────
    options.AddResponseTimePolicy(new ResponseTimePolicy
    {
        ComponentName = "cosmos_db",
        Percentile = "p95",
        DegradedThreshold = TimeSpan.FromMilliseconds(200),
        UnhealthyThreshold = TimeSpan.FromMilliseconds(1000)
    });

    options.AddResponseTimePolicy(new ResponseTimePolicy
    {
        ComponentName = "blob_storage",
        Percentile = "p99",
        DegradedThreshold = TimeSpan.FromMilliseconds(500),
        UnhealthyThreshold = null // Degraded only, never Unhealthy from latency
    });

    // ── Critical Dependency Policy ──────────────────────────
    options.AddCriticalDependency(new CriticalDependencyPolicy
    {
        ComponentName = "cosmos_db",
        RecoveryRetryCount = 3,
        RecoveryRetryInterval = TimeSpan.FromSeconds(5),
        DrainTimeout = TimeSpan.FromSeconds(30),
        ForceShutdownTimeout = TimeSpan.FromSeconds(60)
    });

    // ── Quorum Policy ───────────────────────────────────────
    options.AddQuorumPolicy(new QuorumHealthPolicy
    {
        ComponentName = "grpc_backend_pool",
        MinimumHealthyInstances = 2,
        ProbeInterval = TimeSpan.FromSeconds(10),
        ProbeTimeout = TimeSpan.FromSeconds(5)
    });

    // ── Tenant Health Tracking ──────────────────────────────
    options.AddTenantTracking(tenant =>
    {
        tenant.Components = ["blob_storage"]; // Which components have per-tenant tracking
        tenant.Eviction = new TenantEvictionOptions
        {
            TtlAfterLastAccess = TimeSpan.FromMinutes(30),
            MaxTenantsPerComponent = 10_000,
            EvictionScanInterval = TimeSpan.FromSeconds(60)
        };
    });

    // ── Session Health Tracking ─────────────────────────────
    options.AddSessionTracking(session =>
    {
        session.CompletionRateWindow = TimeSpan.FromSeconds(300);
        session.MinimumCompletionsForRate = 10;
    });

    // ── Aggregate Resolvers (two separate delegates) ────────
    options.AggregateHealthResolver = assessments =>
    {
        // Liveness: only fail if a critical component is Unhealthy
        if (assessments.TryGetValue("cosmos_db", out var cosmos) && cosmos.Status == HealthStatus.Unhealthy)
            return HealthStatus.Unhealthy;
        return assessments.Values.Any(a => a.Status == HealthStatus.Degraded)
            ? HealthStatus.Degraded
            : HealthStatus.Healthy;
    };

    options.AggregateReadinessResolver = assessments =>
    {
        // Readiness: stricter — any Degraded or worse means not ready
        return assessments.Values.Any(a => a.Status >= HealthStatus.Degraded)
            ? HealthStatus.Unhealthy
            : HealthStatus.Healthy;
    };

    // ── Event Sinks ─────────────────────────────────────────
    options.AddEventSink<StructuredLogEventSink>();       // Built-in: ILogger
    options.AddEventSink<OpenTelemetryMetricEventSink>(); // Built-in: OTel
    // options.AddEventSink<GenevaEventSink>();            // User-provided
});

// ── HealthBoss.Polly ─────────────────────────────────────────────
builder.Services.AddHttpClient("CosmosClient")
    .AddResilienceHandler("cosmos-pipeline", (pipeline, sp) =>
    {
        pipeline.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            FailureRatio = 0.1,
            SamplingDuration = TimeSpan.FromSeconds(10),
            MinimumThroughput = 8,
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .Handle<HttpRequestException>()
                .HandleResult(r => r.StatusCode >= System.Net.HttpStatusCode.InternalServerError)
        }
        .WithHealthBossTracking("cosmos_db")); // Extension wires OnOpened/OnClosed/OnHalfOpened
    });

// ── HealthBoss.Grpc ──────────────────────────────────────────────
builder.Services.AddHealthBossGrpc(grpc =>
{
    grpc.AddSubchannelAdapter("grpc_backend_pool"); // Bridges subchannel state → quorum
    grpc.AddClientInterceptor("grpc_backend_pool"); // Optional per-call tracking
});

// ── HealthBoss.AspNetCore ────────────────────────────────────────

var app = builder.Build();

// Per-endpoint inbound tracking
app.UseHealthBossTracking(tracking =>
{
    tracking.MapRoute("/api/documents/**", "cosmos_db");
    tracking.MapRoute("/api/blobs/**", "blob_storage");
    tracking.MapRoute("/api/cache/**", "redis_cache");
});

// K8s probe endpoints
app.MapHealthBossLiveness("/healthz");
app.MapHealthBossReadiness("/readyz");
```

### 7.2 Outbound DelegatingHandler Registration

```csharp
// Track outbound HTTP calls to external services
builder.Services.AddHttpClient("BlobServiceClient")
    .AddHealthBossTracking("blob_storage"); // Adds DelegatingHandler that records signals
```

### 7.3 Custom Shutdown Orchestrator

```csharp
// Replace default shutdown behavior
builder.Services.AddSingleton<IShutdownOrchestrator, MyCustomShutdownOrchestrator>();

public class MyCustomShutdownOrchestrator : IShutdownOrchestrator
{
    public async Task InitiateDrainAsync(CancellationToken ct)
    {
        // Notify load balancer, stop accepting new connections, etc.
    }

    public async Task InitiateShutdownAsync(CancellationToken ct)
    {
        // Flush queues, close connections, signal orchestrator, etc.
    }
}
```

---

## 8. JSON Output

### 8.1 Liveness Endpoint (`GET /healthz`)

**HTTP 200** (healthy/degraded) or **HTTP 503** (unhealthy):

```json
{
  "status": "degraded",
  "evaluated_at": "2025-01-15T10:30:00.000Z",
  "service_state": "ready",
  "components": {
    "cosmos_db": {
      "status": "degraded",
      "success_rate": 0.923,
      "total_requests": 1547,
      "failed_requests": 119,
      "window_size_seconds": 60,
      "response_time": {
        "p50_ms": 45.2,
        "p95_ms": 312.7,
        "p99_ms": 892.1,
        "latency_status": "degraded",
        "evaluated_percentile": "p95"
      }
    },
    "blob_storage": {
      "status": "healthy",
      "success_rate": 0.998,
      "total_requests": 3201,
      "failed_requests": 6,
      "window_size_seconds": 120,
      "response_time": {
        "p50_ms": 22.1,
        "p95_ms": 67.3,
        "p99_ms": 145.8,
        "latency_status": "healthy",
        "evaluated_percentile": "p99"
      }
    },
    "redis_cache": {
      "status": "healthy",
      "success_rate": 0.999,
      "total_requests": 8923,
      "failed_requests": 8,
      "window_size_seconds": 30,
      "response_time": null
    },
    "grpc_backend_pool": {
      "status": "healthy",
      "quorum": {
        "healthy_instances": 3,
        "total_instances": 4,
        "minimum_required": 2,
        "quorum_met": true
      }
    }
  },
  "sessions": {
    "active_sessions": 42,
    "recent_completion_success_rate": 0.987,
    "total_completed": 15234,
    "total_failed": 198
  }
}
```

All keys use **snake_case** (Prometheus/K8s convention).

### 8.2 Readiness Endpoint (`GET /readyz`)

Same structure as `/healthz`, evaluated with `AggregateReadinessResolver` (stricter thresholds). Additionally, the `service_state` from `HealthStateMachine` directly influences readiness:

- `service_state: "draining"` or `"unhealthy"` or `"shutdown"` → **HTTP 503** regardless of component status
- `service_state: "not_ready"` → **HTTP 503**
- `service_state: "ready"` → delegate to `AggregateReadinessResolver`

### 8.3 Tenant Health (Optional Sub-Endpoint)

If tenant tracking is enabled, `GET /healthz/tenants/{componentName}` returns:

```json
{
  "component_name": "blob_storage",
  "tracked_tenant_count": 847,
  "tenants": {
    "tenant_abc123": {
      "status": "degraded",
      "success_rate": 0.82,
      "total_requests": 234,
      "failed_requests": 42,
      "window_size_seconds": 60
    },
    "tenant_def456": {
      "status": "healthy",
      "success_rate": 0.99,
      "total_requests": 1023,
      "failed_requests": 10,
      "window_size_seconds": 60
    }
  }
}
```

**Important:** Tenant health does NOT affect service-level probes (`/healthz`, `/readyz`). It is an isolated dimension for operational visibility and alarm routing.

---

## 9. Acceptance Criteria

### Original Core (AC1–AC15)

**AC1: Signal Recording**
- Given a registered component "cosmos_db"
- When `RecordSignal(new HealthSignal("cosmos_db", true, now))` is called
- Then the signal is stored in the component's sliding window

**AC2: Sliding Window Expiry**
- Given a component with `WindowSize = 60s` and signals older than 60s
- When `GetAssessment("cosmos_db")` is called
- Then only signals within the last 60s are included in the calculation

**AC3: Degraded Threshold**
- Given `DegradedThreshold = 0.95` and 100 signals with 6 failures (94% success)
- When assessment is computed
- Then `Status == Degraded`

**AC4: Unhealthy Threshold**
- Given `UnhealthyThreshold = 0.70` and 100 signals with 35 failures (65% success)
- When assessment is computed
- Then `Status == Unhealthy`

**AC5: Minimum Throughput Guard**
- Given `MinimumThroughput = 10` and only 5 signals in window (all failures)
- When assessment is computed
- Then `Status == Healthy` (insufficient data)

**AC6: Aggregate Health Resolution**
- Given multiple component assessments
- When `/healthz` is called
- Then `AggregateHealthResolver` delegate is invoked with all assessments and its return value determines HTTP status

**AC7: Aggregate Readiness Resolution**
- Given multiple component assessments
- When `/readyz` is called
- Then `AggregateReadinessResolver` delegate is invoked (separate from health resolver)

**AC8: Dual Probe HTTP Semantics**
- Given `AggregateHealthResolver` returns `Healthy` or `Degraded`
- When `/healthz` is called
- Then HTTP 200 is returned
- Given `AggregateHealthResolver` returns `Unhealthy`
- When `/healthz` is called
- Then HTTP 503 is returned

**AC9: Polly Circuit Breaker Bridge — Open**
- Given a Polly circuit breaker transitions to `Open` (via `OnOpened` callback)
- When the callback fires
- Then `RecordSignal(componentName, success: false)` is called on `IHealthStateTracker`

**AC10: Polly Circuit Breaker Bridge — Closed**
- Given a Polly circuit breaker transitions to `Closed` (via `OnClosed` callback)
- When the callback fires
- Then `RecordSignal(componentName, success: true)` is called on `IHealthStateTracker`

**AC11: Polly Circuit Breaker Bridge — HalfOpen**
- Given a Polly circuit breaker transitions to `HalfOpen` (via `OnHalfOpened` callback)
- When the callback fires
- Then no signal is recorded (probe in progress, outcome unknown)

**AC12: Inbound Middleware Per-Endpoint Tracking**
- Given route mapping `"/api/documents/**" → "cosmos_db"`
- When an HTTP request to `/api/documents/123` completes with 200 OK
- Then `RecordSignal("cosmos_db", success: true, duration: ...)` is called

**AC13: Inbound Middleware — Failed Request**
- Given route mapping `"/api/documents/**" → "cosmos_db"`
- When an HTTP request to `/api/documents/123` returns 500
- Then `RecordSignal("cosmos_db", success: false, duration: ...)` is called

**AC14: Outbound DelegatingHandler Tracking**
- Given `AddHealthBossTracking("blob_storage")` on an HttpClient
- When an outbound HTTP request completes
- Then success/failure + duration is recorded as a signal for "blob_storage"

**AC15: JSON Output Format — snake_case**
- Given any health endpoint response
- When the response body is inspected
- Then all JSON keys use snake_case (e.g., `success_rate`, `total_requests`, `window_size_seconds`)

### Latency Addendum (AC16–AC23)

**AC16: ResponseTimePolicy Registration**
- Given a `ResponseTimePolicy` is configured for "cosmos_db"
- When the system starts
- Then latency tracking is active for that component alongside rate tracking

**AC17: Percentile Computation**
- Given 100 signals with known durations and `Percentile = "p95"`
- When assessment is computed
- Then `ResponseTimeAssessment.P95` equals the 95th percentile of recorded durations

**AC18: Latency Degraded**
- Given `DegradedThreshold = 200ms` and computed p95 = 250ms
- When assessment is computed
- Then `LatencyStatus == Degraded`

**AC19: Latency Unhealthy**
- Given `UnhealthyThreshold = 1000ms` and computed p95 = 1200ms
- When assessment is computed
- Then `LatencyStatus == Unhealthy`

**AC20: Latency-Only Degraded (No Unhealthy Threshold)**
- Given `UnhealthyThreshold = null` and computed p95 = 5000ms
- When assessment is computed
- Then `LatencyStatus == Degraded` (never Unhealthy from latency alone)

**AC21: Two-Dimensional Worst-Of-Both**
- Given rate-based status = Healthy and latency-based status = Degraded
- When final component status is determined
- Then `Status == Degraded` (worst of both)

**AC22: Latency Minimum Throughput Guard**
- Given `ResponseTimePolicy.MinimumThroughput = 10` and only 3 signals with durations
- When assessment is computed
- Then `LatencyStatus == Healthy` (insufficient data)

**AC23: ResponseTimeAssessment in JSON**
- Given a component with `ResponseTimePolicy` configured
- When `/healthz` is called
- Then the component's JSON includes `response_time` object with `p50_ms`, `p95_ms`, `p99_ms`, `latency_status`, `evaluated_percentile`

### State Machine (AC24–AC30)

**AC24: Happy Path — Ready State**
- Given a component with `CriticalDependencyPolicy` and all signals are successful
- When `HealthStateMachine.CurrentState` is checked
- Then `ServiceState == Ready`

**AC25: Transition Ready → NotReady**
- Given `RecoveryRetryCount = 3` and the component records 3 consecutive failures
- When the 3rd failure is processed
- Then state transitions to `NotReady` and `StateTransitioned` event fires

**AC26: Recovery from NotReady → Ready**
- Given state is `NotReady` and a success signal is recorded
- When the signal is processed
- Then state transitions back to `Ready`

**AC27: Transition NotReady → Draining**
- Given state is `NotReady` and recovery retries are exhausted (all fail)
- When the last retry fails
- Then state transitions to `Draining` and `IShutdownOrchestrator.InitiateDrainAsync()` is called

**AC28: Readiness Probe During Draining**
- Given `HealthStateMachine.CurrentState == Draining`
- When `/readyz` is called
- Then HTTP 503 is returned (regardless of other components' status)

**AC29: Transition Draining → Unhealthy → Shutdown**
- Given state is `Draining` and `DrainTimeout` elapses without recovery
- When the timeout fires
- Then state transitions to `Unhealthy`
- Given state is `Unhealthy` and `ForceShutdownTimeout` elapses without recovery
- When the timeout fires
- Then state transitions to `Shutdown` and `IShutdownOrchestrator.InitiateShutdownAsync()` is called

**AC30: Recovery Abort from Draining**
- Given state is `Draining` and a success signal arrives before `DrainTimeout`
- When the signal is processed
- Then state transitions back to `Ready` and drain is cancelled

### Tenant Health (AC31–AC37)

**AC31: Per-Tenant Signal Recording**
- Given tenant tracking enabled for "blob_storage"
- When `ITenantHealthTracker.RecordSignal("blob_storage", "tenant_abc", signal)` is called
- Then the signal is stored in an isolated sliding window for ("blob_storage", "tenant_abc")

**AC32: Tenant Health Isolation**
- Given tenant "tenant_abc" has 100% failure rate on "blob_storage"
- When `/healthz` is called
- Then the service-level status for "blob_storage" is NOT affected by tenant-level data

**AC33: Tenant Status Evaluation**
- Given tenant "tenant_abc" has success rate below the component's `DegradedThreshold`
- When `GetAssessment("blob_storage", "tenant_abc")` is called
- Then `TenantHealthStatus == Degraded`

**AC34: TTL Eviction**
- Given `TtlAfterLastAccess = 30m` and tenant "tenant_xyz" has no signals for 31 minutes
- When eviction scan runs
- Then tenant "tenant_xyz" window is evicted from memory

**AC35: LRU Eviction**
- Given `MaxTenantsPerComponent = 10,000` and 10,001 tenants tracked for "blob_storage"
- When a new tenant signal arrives
- Then the least-recently-active tenant is evicted to stay at or below 10,000

**AC36: Tenant Degraded Event**
- Given tenant "tenant_abc" transitions from Healthy to Degraded
- When the transition occurs
- Then `IHealthEventSink.OnTenantDegradedAsync()` is called with the tenant event

**AC37: Tenant Recovered Event**
- Given tenant "tenant_abc" transitions from Degraded back to Healthy
- When the transition occurs
- Then `IHealthEventSink.OnTenantRecoveredAsync()` is called with the tenant event

### Quorum (AC38–AC42)

**AC38: Quorum Met**
- Given `MinimumHealthyInstances = 2` and probe returns 3 healthy out of 4 total
- When quorum is evaluated
- Then `QuorumAssessment.QuorumMet == true` and `Status == Healthy`

**AC39: Quorum Not Met (Partial)**
- Given `MinimumHealthyInstances = 2` and probe returns 1 healthy out of 4 total
- When quorum is evaluated
- Then `QuorumAssessment.QuorumMet == false` and `Status == Degraded`

**AC40: Quorum Not Met (Zero Healthy)**
- Given `MinimumHealthyInstances = 2` and probe returns 0 healthy out of 4 total
- When quorum is evaluated
- Then `QuorumAssessment.QuorumMet == false` and `Status == Unhealthy`

**AC41: Quorum in Aggregate Resolution**
- Given quorum component "grpc_backend_pool" has `Status == Degraded`
- When aggregate resolver runs
- Then the quorum assessment is available in the assessments dictionary for the delegate to consume

**AC42: Quorum Changed Event**
- Given quorum transitions from met to not-met
- When the transition occurs
- Then `IHealthEventSink.OnQuorumChangedAsync()` is called

### Session Tracking (AC43–AC47)

**AC43: Session Start/Complete Lifecycle**
- Given `ISessionHealthTracker.StartSession()` is called returning sessionId "sess_1"
- When `CompleteSession("sess_1", success: true)` is called
- Then `ActiveSessionCount` decrements and the completion is recorded as successful

**AC44: Active Session Count Gauge**
- Given 5 sessions are started and 2 are completed
- When `ActiveSessionCount` is read
- Then it returns 3

**AC45: Completion Success Rate**
- Given 100 sessions completed: 95 success, 5 failure
- When `GetSnapshot()` is called
- Then `RecentCompletionSuccessRate == 0.95`

**AC46: Sessions in JSON Output**
- Given session tracking is enabled
- When `/healthz` is called
- Then JSON includes `sessions` object with `active_sessions`, `recent_completion_success_rate`, `total_completed`, `total_failed`

**AC47: Sessions Not in Aggregate Resolution**
- Given 0 active sessions
- When aggregate health/readiness resolvers run
- Then session data is NOT passed into the assessment dictionary (sessions are a separate metric dimension, not a component)

### Event Sinks (AC48–AC51)

**AC48: StructuredLogEventSink**
- Given `StructuredLogEventSink` is registered
- When a component transitions from Healthy to Degraded
- Then an `ILogger.LogWarning` entry is written with structured properties (component_name, previous_status, new_status, success_rate)

**AC49: OpenTelemetryMetricEventSink**
- Given `OpenTelemetryMetricEventSink` is registered
- When any health event fires
- Then the corresponding OTel metric/counter is incremented with appropriate tags

**AC50: Multiple Sinks**
- Given both `StructuredLogEventSink` and a custom `GenevaEventSink` are registered
- When an event fires
- Then ALL registered sinks receive the event (fan-out, not short-circuit)

**AC51: Sink Failure Isolation**
- Given a custom sink throws an exception in `OnTenantDegradedAsync`
- When the event fires
- Then other sinks still receive the event, and the exception is logged but does not crash the system

### gRPC (AC52–AC55)

**AC52: Subchannel Adapter — Instance Discovery**
- Given `GrpcSubchannelHealthAdapter` is configured for "grpc_backend_pool"
- When `ProbeAsync()` is called
- Then it returns `InstanceHealthResult` for each subchannel with `IsHealthy` based on connectivity state (`Ready` = healthy, `TransientFailure`/`Shutdown` = unhealthy, `Idle` = healthy)

**AC53: Subchannel Adapter Feeds Quorum**
- Given `GrpcSubchannelHealthAdapter` implements `IInstanceHealthProbe`
- When `QuorumHealthPolicy` evaluates "grpc_backend_pool"
- Then it uses the adapter's probe results for quorum calculation

**AC54: Client Interceptor — Call Tracking**
- Given `GrpcClientHealthInterceptor` is configured for "grpc_backend_pool"
- When a gRPC call completes with `StatusCode.OK`
- Then `RecordSignal("grpc_backend_pool", success: true, duration: ...)` is called

**AC55: Client Interceptor — Failure Tracking**
- Given `GrpcClientHealthInterceptor` is configured for "grpc_backend_pool"
- When a gRPC call completes with `StatusCode.Unavailable`
- Then `RecordSignal("grpc_backend_pool", success: false, duration: ...)` is called

---

## 10. Edge Cases

| # | Scenario | Expected Behavior |
|---|----------|-------------------|
| E1 | Signal for unregistered component | `InvalidOperationException` with message "Component '{name}' is not registered. Call AddComponent() first." |
| E2 | Empty window (no signals yet) | `HealthAssessment` with `Status = Healthy`, `SuccessRate = 1.0`, `TotalRequests = 0` |
| E3 | All signals are failures but below MinimumThroughput | `Status = Healthy` (insufficient data to evaluate) |
| E4 | Window rolls over — all signals expire | Returns to empty window behavior (E2) |
| E5 | Concurrent signal recording from multiple threads | Thread-safe; no data loss, no exceptions |
| E6 | Component with HealthPolicy but no ResponseTimePolicy | `ResponseTimeAssessment` is `null` in the assessment |
| E7 | Component with ResponseTimePolicy but signal has no Duration | Signal is included in rate calculation but excluded from latency calculation |
| E8 | Aggregate resolver delegate throws exception | Exception is caught, logged, and `/healthz` returns HTTP 503 with error body |
| E9 | Tenant signal for component without tenant tracking | `InvalidOperationException`: "Tenant tracking not enabled for component '{name}'" |
| E10 | TTL and LRU eviction race condition | LRU eviction runs first (synchronous on insert), TTL runs on background timer; both are thread-safe |
| E11 | Session completed twice with same ID | Second call is no-op (idempotent); logged as warning |
| E12 | Session started but never completed | `ActiveSessionCount` remains incremented; no automatic timeout (caller's responsibility) |
| E13 | State machine receives success signal in Shutdown state | No-op; Shutdown is terminal |
| E14 | IInstanceHealthProbe.ProbeAsync times out | Timed-out instances are marked unhealthy in quorum assessment |
| E15 | IInstanceHealthProbe.ProbeAsync throws exception | Exception caught, all instances marked unhealthy, `Status = Unhealthy` |
| E16 | DegradedThreshold <= UnhealthyThreshold (invalid config) | `ArgumentException` at registration time: "DegradedThreshold must be greater than UnhealthyThreshold" |
| E17 | ComponentName is empty or null | `ArgumentException` at registration: "ComponentName is required" |
| E18 | ComponentName contains uppercase or spaces | `ArgumentException`: "ComponentName must be snake_case (lowercase, underscores only)" |
| E19 | WindowSize is zero or negative | `ArgumentOutOfRangeException`: "WindowSize must be positive" |
| E20 | Two HealthPolicies with same ComponentName | `InvalidOperationException`: "Component '{name}' is already registered" |
| E21 | ResponseTimePolicy for unregistered component | `InvalidOperationException`: "Component '{name}' must be registered with AddComponent() before adding ResponseTimePolicy" |
| E22 | gRPC subchannel adapter with no subchannels available | `ProbeAsync()` returns empty list; quorum evaluates as `Unhealthy` (0 of N) |
| E23 | Custom IShutdownOrchestrator throws during InitiateShutdownAsync | Exception is logged; state machine still transitions to Shutdown; process may need external intervention |
| E24 | Clock skew in HealthSignal.Timestamp | Signals are ordered by arrival time, not timestamp; future timestamps are accepted but don't extend the window |

---

## 11. Non-Functional Requirements

### 11.1 Performance

| Metric | Target | Rationale |
|--------|--------|-----------|
| `RecordSignal` latency | p99 < 1μs | Must not add observable latency to request path |
| `GetAssessment` latency | p99 < 10μs | Probe endpoint must be fast |
| `GetAllAssessments` latency | p99 < 50μs (10 components) | Linear in component count |
| Probe endpoint (`/healthz`) | p99 < 5ms including JSON serialization | Kubernetes default timeout is 1s |
| Signal throughput | ≥ 100,000 signals/sec per component | High-throughput services |
| Sliding window memory per component | ≤ 1 MB for 60s window at 10k signals/sec | 600k signals × ~1.5 bytes compressed |

### 11.2 Memory Budget

| Data Structure | Budget | Notes |
|----------------|--------|-------|
| Per-component sliding window | ~100 KB–1 MB each | Depends on throughput × window size |
| Per-tenant windows (10k tenants × 1 component) | ≤ 100 MB | Each tenant window is smaller (~10 KB) due to lower per-tenant throughput |
| LRU eviction overhead | ≤ 5 MB for 10k tenant index | Linked list + dictionary |
| Session tracker | ≤ 10 MB for 100k concurrent sessions | HashMap of session IDs |
| Total HealthBoss overhead | ≤ 200 MB at max configured scale | Configurable via eviction options |

### 11.3 Thread Safety

- All public APIs are thread-safe
- `IHealthStateTracker`: lock-free sliding window using `ConcurrentQueue<HealthSignal>` with atomic counters
- `ITenantHealthTracker`: `ConcurrentDictionary<(string, string), SlidingWindow>` with per-entry locking for eviction
- `ISessionHealthTracker`: `ConcurrentDictionary<string, SessionState>` with `Interlocked` for active count
- `HealthStateMachine`: single-writer (state transitions) with `lock` for deterministic transitions, readers are lock-free
- Event sink fan-out: fire-and-forget with `Task.WhenAll`; individual sink failures do not block others

### 11.4 Reliability

- HealthBoss itself must never cause the host application to crash
- All exceptions in user-provided delegates (resolvers, sinks, shutdown orchestrator) are caught and logged
- Probe endpoints return HTTP 503 with error details if HealthBoss internals fail
- No external dependencies in `HealthBoss.Core` — pure in-memory, no I/O

### 11.5 Compatibility

| Requirement | Target |
|-------------|--------|
| .NET version | net8.0 (LTS); `HealthBoss.Core` and `HealthBoss.Polly` also target `netstandard2.1` |
| ASP.NET Core | 8.0+ |
| Polly | >= 8.0.0 (Polly.Core) |
| Grpc.Net.Client | >= 2.60.0 |
| System.Text.Json | Used for JSON output (no Newtonsoft dependency) |

---

## 12. Observability

### 12.1 Metrics (OpenTelemetry)

All metrics use meter name `HealthBoss` and follow OTel semantic conventions.

| Metric Name | Type | Unit | Tags | Description |
|-------------|------|------|------|-------------|
| `healthboss.component.success_rate` | Gauge | ratio | `component_name` | Current success rate (0.0–1.0) |
| `healthboss.component.status` | Gauge | enum | `component_name`, `status` | 0=Healthy, 1=Degraded, 2=Unhealthy |
| `healthboss.component.total_requests` | Counter | requests | `component_name` | Total signals recorded |
| `healthboss.component.failed_requests` | Counter | requests | `component_name` | Total failure signals |
| `healthboss.component.response_time.p50` | Gauge | ms | `component_name` | Current p50 latency |
| `healthboss.component.response_time.p95` | Gauge | ms | `component_name` | Current p95 latency |
| `healthboss.component.response_time.p99` | Gauge | ms | `component_name` | Current p99 latency |
| `healthboss.quorum.healthy_instances` | Gauge | instances | `component_name` | Healthy instance count |
| `healthboss.quorum.total_instances` | Gauge | instances | `component_name` | Total instance count |
| `healthboss.quorum.minimum_required` | Gauge | instances | `component_name` | Minimum required count |
| `healthboss.quorum.met` | Gauge | boolean | `component_name` | 1=met, 0=not met |
| `healthboss.tenant.tracked_count` | Gauge | tenants | `component_name` | Number of tracked tenants |
| `healthboss.tenant.evictions` | Counter | evictions | `component_name`, `reason` (ttl/lru) | Tenant window evictions |
| `healthboss.session.active_count` | UpDownCounter | sessions | | Active session gauge |
| `healthboss.session.completion_rate` | Gauge | ratio | | Recent completion success rate |
| `healthboss.state_machine.state` | Gauge | enum | `component_name` | Current ServiceState ordinal |
| `healthboss.state_machine.transitions` | Counter | transitions | `component_name`, `from_state`, `to_state` | State transition count |
| `healthboss.event_sink.errors` | Counter | errors | `sink_type` | Event sink invocation failures |

### 12.2 Structured Logging

All log entries use `ILogger` with structured properties. Source context: `HealthBoss`.

| Level | Event | Properties |
|-------|-------|------------|
| Information | Component status changed | `component_name`, `previous_status`, `new_status`, `success_rate` |
| Warning | Component degraded | `component_name`, `success_rate`, `threshold` |
| Error | Component unhealthy | `component_name`, `success_rate`, `threshold` |
| Warning | Tenant degraded | `component_name`, `tenant_id`, `success_rate` |
| Information | Tenant recovered | `component_name`, `tenant_id`, `success_rate` |
| Warning | Quorum not met | `component_name`, `healthy_instances`, `total_instances`, `minimum_required` |
| Error | State machine: Draining | `component_name`, `from_state`, `to_state`, `recovery_attempts` |
| Critical | State machine: Shutdown | `component_name`, `from_state` |
| Warning | Tenant evicted (TTL) | `component_name`, `tenant_id`, `idle_duration` |
| Debug | Tenant evicted (LRU) | `component_name`, `tenant_id`, `tracked_count` |
| Error | Event sink failure | `sink_type`, `event_type`, `exception` |
| Warning | Aggregate resolver exception | `resolver_type`, `exception` |

### 12.3 SLIs / SLOs for HealthBoss Itself

| SLI | Target (SLO) | Window |
|-----|-------------|--------|
| Probe endpoint availability (2xx or 503) | 99.99% | 30 days |
| Probe endpoint latency (p95) | < 5ms | Rolling |
| Signal recording latency (p99) | < 1μs | Rolling |
| Event sink delivery (non-errored) | 99.9% | 30 days |

---

## 13. Security Considerations

| Area | Requirement | Implementation |
|------|-------------|----------------|
| **Authentication** | Probe endpoints (`/healthz`, `/readyz`) are unauthenticated by default (K8s kubelet access). Tenant sub-endpoints should be authenticated. | Use ASP.NET Core `RequireAuthorization()` on tenant endpoints |
| **Authorization** | Tenant health data is operationally sensitive. Restrict to ops/admin roles. | `[Authorize(Roles = "ops")]` on tenant health endpoint |
| **Data sensitivity** | Tenant IDs may be PII depending on naming convention. Component names are internal. | Tenant health endpoint should not be publicly exposed. Consider RBAC. |
| **Input validation** | Component names must be snake_case, ≤ 64 chars. Tenant IDs ≤ 128 chars. | Validate at registration time and on signal recording |
| **Denial of service** | Unbounded tenant tracking could exhaust memory | LRU + TTL eviction enforces memory cap. MaxTenantsPerComponent is mandatory. |
| **Information disclosure** | Health endpoint JSON reveals internal architecture | Production may use minimal output (`?verbose=false` returns only status + HTTP code) |
| **Dependency injection** | Custom sinks, orchestrators, probes are user-provided code | All user code is invoked in try/catch; timeouts enforced for probes |
| **OWASP** | A04:2021 Insecure Design — probe endpoints revealing internal topology | Provide option to disable detailed JSON output in production |

---

## 14. Testing Strategy

### 14.1 Unit Tests (HealthBoss.Core)

| # | Test Area | Key Tests |
|---|-----------|-----------|
| U1 | SlidingWindow | Add signals, verify window expiry, concurrent access, edge cases |
| U2 | HealthPolicy evaluation | Healthy/Degraded/Unhealthy transitions, minimum throughput guard, boundary values |
| U3 | ResponseTimePolicy evaluation | p50/p95/p99 computation, degraded/unhealthy thresholds, null UnhealthyThreshold, minimum throughput |
| U4 | Two-dimensional evaluation | Worst-of-both combinations (rate × latency) |
| U5 | HealthStateMachine | All transitions: Ready→NotReady→Draining→Unhealthy→Shutdown; recovery paths; terminal state |
| U6 | QuorumHealthPolicy evaluation | Met/not-met/zero, boundary (exactly N healthy) |
| U7 | TenantHealthTracker | Per-tenant isolation, TTL eviction, LRU eviction, concurrent tenant access |
| U8 | SessionHealthTracker | Start/complete lifecycle, active count, completion rate, idempotent double-complete |
| U9 | Aggregate resolvers | Custom delegate logic, exception handling |
| U10 | Event sink fan-out | Multiple sinks, failure isolation, async behavior |
| U11 | Validation | Invalid component names, duplicate registration, invalid thresholds |
| U12 | HealthSignal | null/empty component name, tags, optional duration |

### 14.2 Unit Tests (HealthBoss.Polly)

| # | Test Area | Key Tests |
|---|-----------|-----------|
| P1 | PollyCircuitBreakerBridge | OnOpened→failure signal, OnClosed→success signal, OnHalfOpened→no signal |
| P2 | DelegatingHandler | Outbound success/failure tracking, duration recording, exception handling |
| P3 | Extension method | Pipeline builder wiring, component name propagation |

### 14.3 Unit Tests (HealthBoss.Grpc)

| # | Test Area | Key Tests |
|---|-----------|-----------|
| G1 | GrpcSubchannelHealthAdapter | Connectivity state mapping (Ready→healthy, TransientFailure→unhealthy, Idle→healthy, Shutdown→unhealthy) |
| G2 | GrpcClientHealthInterceptor | OK→success signal, Unavailable→failure signal, duration tracking, exception handling |
| G3 | Empty subchannel list | ProbeAsync returns empty → quorum evaluates Unhealthy |

### 14.4 Integration Tests (HealthBoss.AspNetCore)

| # | Test Area | Key Tests |
|---|-----------|-----------|
| I1 | Probe endpoints | `/healthz` and `/readyz` return correct HTTP status codes and JSON |
| I2 | Per-endpoint middleware | Route-based signal recording, multiple routes, wildcard matching |
| I3 | DelegatingHandler with TestServer | Outbound calls via HttpClient record signals |
| I4 | Full pipeline | Record signals → policy evaluation → aggregate resolution → probe response |
| I5 | State machine integration | CriticalDependencyPolicy + `/readyz` returns 503 during Draining |
| I6 | Tenant health endpoint | Authentication required, JSON format, isolation from service probes |
| I7 | Concurrent load | 10,000 concurrent signals, verify no data loss or deadlock |

### 14.5 Performance Tests

| # | Test | Target |
|---|------|--------|
| PF1 | RecordSignal throughput | ≥ 100k signals/sec/component on single core |
| PF2 | GetAssessment latency under load | p99 < 10μs with 100k signals in window |
| PF3 | Probe endpoint under load | p99 < 5ms with 10 components |
| PF4 | Tenant eviction under pressure | 50k tenants, verify eviction keeps memory under budget |
| PF5 | Memory profiling | Total allocation < 200MB at max configured scale |
| PF6 | GC pressure | RecordSignal should not allocate on hot path (record struct or pooling) |

---

## 15. Out of Scope (v2+)

| Item | Rationale |
|------|-----------|
| **Persistent state** | v1 is in-memory only. Redis/distributed cache backing is v2. |
| **Distributed health aggregation** | Cross-instance health (e.g., cluster-wide quorum) requires distributed state. v2. |
| **UI dashboard** | No built-in web UI. Users consume metrics via Grafana/Datadog. |
| **Automatic remediation** | HealthBoss observes and reports; it does not auto-heal (except state machine shutdown). |
| **Custom percentile algorithms** | v1 uses linear interpolation for percentiles. HdrHistogram or t-digest is v2. |
| **Health check publisher integration** | Bridging to `IHealthCheckPublisher` for push-based reporting is v2. |
| **Configuration hot-reload** | Policies are immutable after startup. Hot-reload from `IOptionsMonitor<T>` is v2. |
| **Multi-window policies** | e.g., "degraded if 5m window is bad AND 1m window is bad." v2. |
| **Weighted aggregate resolution** | Built-in weighted resolver (instead of user-provided delegate). v2. |
| **OpenTelemetry tracing integration** | Correlating signals to specific traces/spans. v2. |
| **Health check adapter** | Bridging existing `IHealthCheck` implementations into HealthBoss signal streams. v2. |

---

## 16. Sprint Delivery Plan

### Sprint 1 — Foundation (Core primitives)
**Goal:** Signal ingestion, sliding window, rate-based policy evaluation, aggregate resolution

| Task | Est. Points |
|------|-------------|
| `HealthSignal`, `HealthAssessment`, `HealthStatus` records/enums | 2 |
| `SlidingWindow<T>` data structure (thread-safe, time-based expiry) | 5 |
| `HealthPolicy` evaluation logic | 3 |
| `IHealthStateTracker` + in-memory implementation | 5 |
| `AggregateHealthResolver` + `AggregateReadinessResolver` delegate types | 2 |
| Input validation (component names, threshold bounds) | 2 |
| Unit tests (U1, U2, U9, U11, U12) | 5 |
| **Sprint 1 total** | **24** |

**Covers AC:** 1–5, 15 (snake_case naming), E1–E5, E16–E20

### Sprint 2 — Latency + ASP.NET Core
**Goal:** Response time tracking, HTTP middleware, probe endpoints

| Task | Est. Points |
|------|-------------|
| `ResponseTimePolicy` + percentile computation | 5 |
| `ResponseTimeAssessment` record | 2 |
| Two-dimensional evaluation (worst-of-both) | 3 |
| `HealthBoss.AspNetCore` package scaffold | 2 |
| Inbound `HealthBossMiddleware` (per-endpoint route mapping) | 5 |
| Outbound `HealthBossDelegatingHandler` | 3 |
| `/healthz` and `/readyz` endpoint mapping + JSON writer | 5 |
| `AddHealthBoss()` DI extension method | 3 |
| Unit tests (U3, U4) | 3 |
| Integration tests (I1–I4) | 5 |
| **Sprint 2 total** | **36** |

**Covers AC:** 6–8, 12–14, 16–23, E6–E8, E21, E24

### Sprint 3 — Polly Bridge + State Machine
**Goal:** Circuit breaker integration, critical dependency lifecycle

| Task | Est. Points |
|------|-------------|
| `HealthBoss.Polly` package scaffold | 2 |
| `PollyCircuitBreakerBridge` (OnOpened/OnClosed/OnHalfOpened) | 5 |
| `.WithHealthBossTracking()` extension method | 2 |
| `HealthStateMachine` (state graph, transitions, timers) | 8 |
| `CriticalDependencyPolicy` | 3 |
| `IShutdownOrchestrator` interface + default implementation | 3 |
| Readiness probe integration with state machine | 2 |
| Unit tests (P1–P3, U5) | 5 |
| Integration tests (I5) | 3 |
| **Sprint 3 total** | **33** |

**Covers AC:** 9–11, 24–30, E13, E23

### Sprint 4 — Tenant Health + Session Tracking
**Goal:** Multi-tenant isolation, session lifecycle, eviction

| Task | Est. Points |
|------|-------------|
| `ITenantHealthTracker` + implementation with per-tenant windows | 8 |
| `TenantEvictionOptions`, TTL timer, LRU cache | 5 |
| `TenantHealthAssessment`, `TenantHealthStatus` | 2 |
| `ISessionHealthTracker` + implementation | 5 |
| `SessionHealthSnapshot` | 1 |
| Tenant health sub-endpoint in AspNetCore | 3 |
| Sessions in JSON output | 2 |
| Unit tests (U7, U8) | 5 |
| Integration tests (I6, I7) | 3 |
| **Sprint 4 total** | **34** |

**Covers AC:** 31–37, 43–47, E9–E12

### Sprint 5 — Quorum + gRPC + Event Sinks
**Goal:** Quorum health, gRPC integration, alarm system

| Task | Est. Points |
|------|-------------|
| `QuorumHealthPolicy` evaluation logic | 3 |
| `IInstanceHealthProbe` interface | 2 |
| `QuorumAssessment` in aggregate + JSON | 2 |
| `HealthBoss.Grpc` package scaffold | 2 |
| `GrpcSubchannelHealthAdapter` (implements IInstanceHealthProbe) | 5 |
| `GrpcClientHealthInterceptor` | 3 |
| `IHealthEventSink` interface + fan-out dispatcher | 3 |
| `StructuredLogEventSink` | 2 |
| `OpenTelemetryMetricEventSink` | 3 |
| Unit tests (U6, U10, G1–G3) | 5 |
| **Sprint 5 total** | **30** |

**Covers AC:** 38–42, 48–55, E14–E15, E22

### Sprint 6 — Observability + Performance + Hardening
**Goal:** OTel metrics, performance validation, documentation

| Task | Est. Points |
|------|-------------|
| Full OTel metrics instrumentation (all 17 metrics) | 5 |
| Structured logging for all events | 3 |
| Performance tests (PF1–PF6) | 5 |
| Memory profiling + GC optimization | 3 |
| API documentation (XML docs on all public types) | 3 |
| README, ARCHITECTURE.md, CHANGELOG.md | 3 |
| NuGet package metadata + CI pipeline | 3 |
| End-to-end integration test (full scenario) | 5 |
| **Sprint 6 total** | **30** |

### Total: ~187 story points across 6 sprints

---

## 17. Open Questions

All previously open questions have been resolved. The following are new questions surfaced during consolidation:

- [ ] **OQ-1: Percentile algorithm precision** — v1 uses sorted-array linear interpolation for percentile computation. Should we use a more memory-efficient algorithm (e.g., P² or t-digest) if signal volumes exceed 100k/window? *Current decision: linear interpolation for v1; revisit in v2 if profiling shows memory pressure.*
- [ ] **OQ-2: Verbose vs minimal probe output** — Should `/healthz?verbose=false` be supported in v1 to hide internal topology? Or is this a deployment concern (reverse proxy strips the body)? *Recommendation: support `verbose` query parameter in v1.*
- [ ] **OQ-3: gRPC subchannel access** — `Grpc.Net.Client` does not expose subchannel connectivity state via public API as of v2.60. The `GrpcSubchannelHealthAdapter` may need to use `CircuitBreakerStateProvider` as a proxy or rely on custom `SubchannelFactory`. *Needs spike in Sprint 5 to confirm feasibility.*
- [ ] **OQ-4: Startup probe support** — Kubernetes startup probes (`/startupz`) are distinct from liveness/readiness. Should HealthBoss expose a third endpoint for startup probes, or is this out of scope? *Recommendation: add `MapHealthBossStartup("/startupz")` that returns 200 once all components have recorded at least MinimumThroughput signals.*

---

## INVEST Quality Check

| Criterion | Assessment |
|-----------|------------|
| **Independent** | Each sprint is independently deliverable and testable. Sprint 1 produces a usable library (Core only). Each subsequent sprint adds a package or feature set. |
| **Negotiable** | Implementation details (data structure choices, serialization) are flexible. The interfaces and behavior are specified; the internals are not. |
| **Valuable** | Each sprint delivers incrementally usable health intelligence. Sprint 1+2 alone solve the core problem (stateful health tracking + K8s probes). |
| **Estimable** | All components have clear acceptance criteria, edge cases, and test strategies. Story point estimates are provided per sprint. |
| **Small** | The full spec is too large for one sprint. It is split into 6 sprints of 24–36 points each. Each sprint can be further decomposed into individual tasks. |
| **Testable** | 55 acceptance criteria with Given/When/Then format. 24 edge cases. 6 performance benchmarks. All criteria are specific and measurable. |

---

## Research Findings

### Internal (Codebase)
- Repository is greenfield (empty). No existing code to reference or conflict with.
- No existing CI/CD, documentation, or issue templates.

### External (Web/Standards)
- **ASP.NET Core Health Checks**: Built-in `IHealthCheck`/`MapHealthChecks` is stateless and polling-based. HealthBoss is complementary, not a replacement — it can coexist and eventually bridge via `IHealthCheckPublisher` (v2).
- **Polly v8 Circuit Breaker**: Confirmed `OnOpened`/`OnClosed`/`OnHalfOpened` callbacks exist as `Action<OnCircuitOpenedArguments>` etc. `CircuitBreakerStateProvider` provides read access to circuit state. Event-driven bridge is the correct pattern.
- **Kubernetes Probes**: Liveness = "should I restart?", Readiness = "should I route traffic?", Startup = "is the app still starting?". HealthBoss maps to liveness + readiness with separate resolvers. Startup probe is flagged as OQ-4.
- **gRPC Health Protocol**: Standard `grpc.health.v1.Health` service with `Check`/`Watch` RPCs. `Grpc.Net.Client` supports client-side load balancing with subchannel concept, but subchannel connectivity state is not directly exposed in the public API (OQ-3 flagged).
- **OpenTelemetry .NET Conventions**: ASP.NET Core metrics use `Microsoft.AspNetCore.*` meter names. HealthBoss should use its own meter (`HealthBoss`) to avoid collision. Metric naming follows OTel semantic conventions (dot-separated, lowercase).

---

## Product Owner Guardian — Ticket Ready

### For the Default Agent
The specification above is complete and ready to be created as a GitHub issue.

1. **Create the issue** with title: `HealthBoss v1.0 — Stateful Health Intelligence Layer for ASP.NET Core`
2. **Add labels:** `feature`, `epic`, `v1.0`, `specification`
3. **This is an epic.** Create 6 sub-issues corresponding to the Sprint Delivery Plan:
   - Sprint 1: Foundation (Core Primitives) — AC1–5, E1–E5, E16–E20
   - Sprint 2: Latency + ASP.NET Core — AC6–8, AC12–14, AC16–23
   - Sprint 3: Polly Bridge + State Machine — AC9–11, AC24–30
   - Sprint 4: Tenant Health + Session Tracking — AC31–37, AC43–47
   - Sprint 5: Quorum + gRPC + Event Sinks — AC38–42, AC48–55
   - Sprint 6: Observability + Performance + Hardening
4. **Cross-reference:** Security Guardian should review Sprint 4 (tenant data) and Sprint 6 (observability). Code Review Guardian should review all PRs against the interface contracts defined in Section 5.