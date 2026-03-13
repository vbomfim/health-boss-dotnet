# HealthBoss State Machine

How HealthBoss decides if your dependencies are healthy, degraded, or down.

## Signal Ingestion

HealthBoss makes decisions based on **health signals** — success/failure events from your dependencies. Signals flow in through two paths:

### 1. ISignalRecorder (primary API)

The main entry point for feeding signals from any source:

```csharp
var recorder = sp.GetRequiredService<ISignalRecorder>();
recorder.RecordSignal(
    new DependencyId("orders-db"),
    new HealthSignal(DateTimeOffset.UtcNow, dep, SignalOutcome.Failure, latency));
```

This is how external systems (like otel-events subscriptions) feed signals into HealthBoss. The orchestrator routes the signal to the correct component's buffer internally.

### 2. ISignalWriter (component-level)

For code that already knows which component it's recording for (e.g., Polly bridge, gRPC interceptor):

```csharp
// Polly circuit breaker integration
builder.AddCircuitBreaker(options)
    .WithHealthBossTracking(signalWriter, dependencyId, clock);
```

### Connecting with otel-events

HealthBoss doesn't track HTTP requests itself — that's [otel-events](https://github.com/vbomfim/otel-events-dotnet)' job. The consumer wires them together via subscriptions:

```csharp
// otel-events emits events, you subscribe and feed HealthBoss
builder.Services.AddOtelEventsSubscriptions(subs =>
{
    subs.On("http.request.failed", (ctx, ct) =>
    {
        var recorder = sp.GetRequiredService<ISignalRecorder>();
        recorder.RecordSignal(
            new DependencyId("orders-db"),
            new HealthSignal(ctx.Timestamp, ..., SignalOutcome.Failure));
        return Task.CompletedTask;
    });
});
```

## Signal Flow

```
  otel-events ─────────┐
  (http.request.failed, │
   cosmosdb.throttled,  │  Consumer subscribes
   grpc.call.failed)    │  and calls RecordSignal
                        ▼
            ┌──────────────────┐
            │  ISignalRecorder  │  Routes by DependencyId
            └────────┬─────────┘
                     ▼
            ┌─────────────┐
            │ SignalBuffer │  Ring buffer per component (10K default)
            │  (per comp.) │
            └──────┬──────┘
                   │ GetSignals(window)
                   ▼
            ┌─────────────┐
            │   Policy     │  Compute success rate over sliding window
            │  Evaluator   │
            └──────┬──────┘
                   │ HealthAssessment (recommendedState)
                   ▼
            ┌─────────────┐
            │ Transition   │  Apply guards + cooldown
            │   Engine     │
            └──────┬──────┘
                   │ TransitionDecision
                   ▼
            ┌─────────────┐
            │ Dependency   │  Current state: Healthy | Degraded | CircuitOpen
            │   Monitor    │
            └──────┬──────┘
                   │ DependencySnapshot
                   ▼
            ┌─────────────┐
            │   Health     │  Aggregate across all components
            │ Orchestrator │  → HealthReport / ReadinessReport
            └──────┬──────┘
                   │
         ┌─────────┼──────────┐
         ▼         ▼          ▼
    /healthz/*   /status   IHealthReportProvider
    (K8s probes)  (API)    (programmatic)
```

## Three-State Model

```
                 success rate drops
          ┌─────────────────────────┐
          │                         ▼
    ┌───────────┐           ┌───────────┐           ┌───────────────┐
    │  Healthy  │──────────►│  Degraded │──────────►│  CircuitOpen  │
    │           │           │           │           │               │
    │ rate ≥ D  │◄──────────│ D > rate  │           │  rate < C     │
    │           │  recovers │    ≥ C    │           │               │
    └───────────┘           └───────────┘           └───────┬───────┘
          ▲                                                 │
          │              recovery probe succeeds            │
          └─────────────────────────────────────────────────┘

    D = DegradedThreshold    C = CircuitOpenThreshold
```

### State Definitions

| State | Meaning | K8s Liveness | K8s Readiness |
|-------|---------|-------------|---------------|
| **Healthy** | Success rate ≥ DegradedThreshold | 200 OK | 200 OK |
| **Degraded** | Success rate between thresholds | 200 OK | 200 OK |
| **CircuitOpen** | Success rate < CircuitOpenThreshold | **503** | **503** |

## Assessment Rules

On every call to `GetHealthReport()`, each component is assessed:

```
signals = buffer.GetSignals(slidingWindow)    // e.g., last 5 minutes
successRate = successCount / totalSignals

if totalSignals < MinSignals:
    recommendedState = currentState            // not enough data, keep as-is

else if successRate >= DegradedThreshold:
    recommendedState = Healthy

else if successRate >= CircuitOpenThreshold:
    recommendedState = Degraded

else:
    recommendedState = CircuitOpen
```

If a `ResponseTimePolicy` is also configured, latency is evaluated independently and the **worst of both dimensions** wins:

```
finalRecommendation = worst(successRateState, latencyState)
```

## Transition Guards

The state machine enforces **step-by-step degradation** but allows **fast recovery**:

| From | To | Guard | Why |
|------|----|-------|-----|
| Healthy | Degraded | recommended is Degraded OR CircuitOpen | Can't skip straight to CircuitOpen |
| Degraded | CircuitOpen | recommended is CircuitOpen | Second step of degradation |
| Degraded | Healthy | recommended is Healthy | Recovery from Degraded |
| CircuitOpen | Healthy | recommended is Healthy | Recovery skips Degraded (fast path) |

**Not allowed:** Healthy → CircuitOpen in one step. This prevents a single bad assessment from opening the circuit.

## Cooldown

A **30-second cooldown** (default) is enforced between transitions:

```
timeSinceLastTransition = now - lastTransitionTime

if timeSinceLastTransition < CooldownBeforeTransition:
    transition blocked (even if guard passes)
```

This prevents **flapping** — rapid oscillation between states when success rate hovers near a threshold.

**Consequence:** Full degradation (Healthy → CircuitOpen) takes at minimum **60 seconds** (two transitions × 30s cooldown each).

## Configuration Reference

```csharp
services.AddHealthBoss(opts =>
{
    opts.AddComponent("my-dependency", c => c
        .Window(TimeSpan.FromMinutes(5))      // Sliding window for signals
        .HealthyAbove(0.9)                    // ≥90% success = Healthy (DegradedThreshold)
        .DegradedAbove(0.5)                   // ≥50% success = Degraded (CircuitOpenThreshold)
        .MinimumSignals(5)                    // Need 5 signals before assessing
        .WithResponseTime(rt => rt            // Optional: latency dimension
            .Percentile(0.95)                 //   Track p95
            .DegradedAfter(TimeSpan.FromMs(500))  //   p95 > 500ms = Degraded
            .UnhealthyAfter(TimeSpan.FromMs(2000)) // p95 > 2s = CircuitOpen
            .MinimumSignals(5)));
});
```

| Parameter | Default | Description |
|-----------|---------|-------------|
| `Window` | 5 min | Sliding window duration |
| `HealthyAbove` | 0.9 (90%) | Success rate threshold for Healthy |
| `DegradedAbove` | 0.5 (50%) | Success rate threshold for Degraded (below = CircuitOpen) |
| `MinimumSignals` | 5 | Minimum signals before first assessment |
| `CooldownBeforeTransition` | 30s | Minimum time between state changes |
| `RecoveryProbeInterval` | 10s | How often to probe when CircuitOpen |

## Aggregate Health

The overall service status is the **worst** across all components:

| Any component in CircuitOpen? | Overall Status | Liveness |
|-------------------------------|---------------|----------|
| Yes | **Unhealthy** | 503 |
| Any Degraded (none CircuitOpen) | **Degraded** | 200 |
| All Healthy | **Healthy** | 200 |

## Example: Watching State Transitions

Using the sample app's `/simulate` endpoint:

```bash
# 1. Seed signals (need MinimumSignals=10)
curl -X POST localhost:5100/simulate/orders-db/success/10

# 2. Check status → Healthy (100% success rate)
curl localhost:5100/status

# 3. Wait 30s (cooldown from startup), then inject failures
sleep 31
curl -X POST localhost:5100/simulate/orders-db/failure/20

# 4. Check status → Degraded (33% success rate, below 95% threshold)
curl localhost:5100/status

# 5. Wait another 30s, inject more failures
sleep 31
curl -X POST localhost:5100/simulate/orders-db/failure/10

# 6. Check status → CircuitOpen (below 70% threshold)
curl localhost:5100/status
curl localhost:5100/healthz/live   # → 503

# 7. Recover: flood with successes, wait for cooldown
curl -X POST localhost:5100/simulate/orders-db/success/50
sleep 31
curl localhost:5100/status         # → Healthy again
curl localhost:5100/healthz/live   # → 200
```
