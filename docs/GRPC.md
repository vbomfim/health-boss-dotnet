# HealthBoss.Grpc

Track the health of gRPC backend pools via quorum evaluation — are enough instances healthy?

## What it does

`HealthBoss.Grpc` provides **quorum evaluation** for gRPC backend pools. It answers: *"Do I have enough healthy instances behind this gRPC service?"*

Individual gRPC call tracking (success/failure per call) is handled by [otel-events](https://github.com/vbomfim/otel-events-dotnet) `OtelEvents.Grpc` — HealthBoss subscribes to those events via `ISignalRecorder`.

| Concern | Owner |
|---------|-------|
| gRPC call success/failure events | **otel-events** (`OtelEvents.Grpc`) |
| gRPC call → HealthBoss signal | **Consumer** wires via subscriptions |
| Backend pool quorum evaluation | **HealthBoss.Grpc** |

## Feeding gRPC signals into HealthBoss

otel-events intercepts gRPC calls and emits events. You subscribe and feed HealthBoss:

```csharp
// otel-events handles gRPC interception
builder.Services.AddOtelEventsGrpc();

// Wire events into HealthBoss
builder.Services.AddOtelEventsSubscriptions(subs =>
{
    subs.On("grpc.call.failed", (ctx, ct) =>
    {
        var ingress = sp.GetRequiredService<ISignalRecorder>();
        ingress.RecordSignal(
            new DependencyId("payment-grpc"),
            new HealthSignal(ctx.Timestamp, ..., SignalOutcome.Failure));
        return Task.CompletedTask;
    });

    subs.On("grpc.call.completed", (ctx, ct) =>
    {
        var ingress = sp.GetRequiredService<ISignalRecorder>();
        ingress.RecordSignal(
            new DependencyId("payment-grpc"),
            new HealthSignal(ctx.Timestamp, ..., SignalOutcome.Success));
        return Task.CompletedTask;
    });
});
```

This feeds the sliding-window state machine: after enough failures, `payment-grpc` transitions Healthy → Degraded → CircuitOpen.

## Quorum Health: GrpcSubchannelHealthAdapter

Call-level tracking catches failures, but not capacity loss. If you have 10 instances with 2 down, most calls still succeed — but you're losing capacity. The quorum evaluator catches this.

### Setup

#### Step 1: Implement IGrpcHealthSource

You provide the subchannel counts from your infrastructure:

```csharp
public class MyGrpcHealthSource : IGrpcHealthSource
{
    private readonly MyServiceDiscovery _discovery;

    public MyGrpcHealthSource(MyServiceDiscovery discovery)
        => _discovery = discovery;

    public int ReadySubchannelCount
        => _discovery.GetReadyEndpoints("payment-service").Count;

    public int TotalSubchannelCount
        => _discovery.GetAllEndpoints("payment-service").Count;
}
```

This could read from:
- Kubernetes endpoint slices
- Consul/Eureka service registry
- gRPC load balancer state (if exposed)
- A custom health check loop

#### Step 2: Create the adapter

```csharp
var source = new MyGrpcHealthSource(discovery);
var adapter = new GrpcSubchannelHealthAdapter(source, "payment-grpc");
```

#### Step 3: Evaluate quorum

```csharp
var quorumEvaluator = sp.GetRequiredService<IQuorumEvaluator>();

// Probe current state
var results = await adapter.ProbeAllAsync();

// Evaluate against policy
var assessment = quorumEvaluator.Evaluate(results, new QuorumHealthPolicy(
    MinimumHealthyInstances: 3,     // Need at least 3 healthy
    TotalExpectedInstances: 5));    // Expect 5 total

// assessment.QuorumMet → true/false
// assessment.HealthyInstances → e.g., 4
// assessment.Status → Healthy, Degraded, or CircuitOpen
```

### What the adapter returns

The adapter converts subchannel counts into `InstanceHealthResult` entries:

```
Total: 5 subchannels, Ready: 3

→ instance-0: Healthy
→ instance-1: Healthy
→ instance-2: Healthy
→ instance-3: Unhealthy
→ instance-4: Unhealthy
```

Instance identifiers are opaque (`instance-0`, `instance-1`, ...) — never IPs or hostnames.

### Quorum policy

| Parameter | Description |
|-----------|-------------|
| `MinimumHealthyInstances` | How many instances must be healthy. Quorum fails below this. |
| `TotalExpectedInstances` | Expected fleet size (0 = dynamic/unknown) |
| `ProbeInterval` | How often to probe (used by callers, not the evaluator itself) |
| `ProbeTimeout` | Timeout per probe call |

### Quorum result → HealthState

| Healthy instances vs minimum | HealthState |
|------------------------------|-------------|
| `healthy >= minimum` | Healthy |
| `healthy > 0 but < minimum` | Degraded |
| `healthy == 0` | CircuitOpen |

## Combining Call Tracking + Quorum

For a complete gRPC health picture, use both:

```csharp
// 1. Call-level: otel-events intercepts, you subscribe → ISignalRecorder
//    (see "Feeding gRPC signals" above)

// 2. Pool-level: adapter + quorum evaluator checks instance availability
var adapter = new GrpcSubchannelHealthAdapter(source, "payment-grpc");

// 3. Periodic quorum check (e.g., in a BackgroundService)
var results = await adapter.ProbeAllAsync();
var assessment = quorumEvaluator.Evaluate(results, quorumPolicy);

if (!assessment.QuorumMet)
{
    ingress.RecordSignal(
        new DependencyId("payment-grpc"),
        new HealthSignal(clock.UtcNow, depId, SignalOutcome.Failure));
}
```

Call tracking catches individual failures. Quorum catches capacity loss even when most calls succeed.

## Metrics

When the quorum evaluator runs, HealthBoss emits:

| Metric | Type | Description |
|--------|------|-------------|
| `healthboss.quorum_healthy_instances` | Gauge | Healthy instances per component |
| `healthboss.quorum_total_instances` | Gauge | Total instances per component |
| `healthboss.quorum_met` | Gauge | 1 if quorum met, 0 if not |
