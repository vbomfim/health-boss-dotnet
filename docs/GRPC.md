# HealthBoss.Grpc

Track the health of gRPC backend pools by monitoring call success/failure and subchannel quorum.

## What it does

Two components, two concerns:

| Component | What it tracks | How |
|-----------|---------------|-----|
| **GrpcClientHealthInterceptor** | Individual gRPC call outcomes | Intercepts unary calls, records success/failure signals |
| **GrpcSubchannelHealthAdapter** | Backend pool availability | Bridges subchannel counts into quorum evaluation |

## 1. Call-Level Health: GrpcClientHealthInterceptor

A gRPC client interceptor that records each call as a health signal. Successful calls = success signal. Failed calls (Unavailable, DeadlineExceeded, etc.) = failure signal.

### Setup

```csharp
// Register HealthBoss with a component for your gRPC backend
services.AddHealthBoss(opts =>
{
    opts.AddComponent("payment-grpc", c => c
        .HealthyAbove(0.9)
        .DegradedAbove(0.5)
        .MinimumSignals(10));
});

// Create the interceptor
var interceptor = new GrpcClientHealthInterceptor(
    recorder: signalWriter,          // ISignalWriter (keyed DI per component)
    clock: systemClock,              // ISystemClock
    componentName: "payment-grpc");  // Must match AddComponent name

// Add to your gRPC channel
var channel = GrpcChannel.ForAddress("https://payment-service:5001");
var invoker = channel.Intercept(interceptor);
var client = new PaymentService.PaymentServiceClient(invoker);
```

### What it records

Every unary gRPC call produces a `HealthSignal`:

| gRPC StatusCode | SignalOutcome | Meaning |
|----------------|---------------|---------|
| OK | Success | Call succeeded |
| Unavailable | Failure | Server unreachable |
| DeadlineExceeded | Timeout | Call timed out |
| Internal, Unknown, DataLoss | Failure | Server error |
| Cancelled | Failure | Call cancelled |
| All others (NotFound, InvalidArgument, etc.) | Success | Client errors, not server health issues |

The signal includes:
- Timestamp and latency (measured by the interceptor)
- `GrpcStatus` field — sanitized status code name (e.g., "Unavailable"), never raw error details

### How it feeds the state machine

```
gRPC call → Interceptor → ISignalWriter.Record() → SignalBuffer
    → PolicyEvaluator → TransitionEngine → Healthy/Degraded/CircuitOpen
```

After enough failures (based on your thresholds), the component transitions to Degraded then CircuitOpen. The `/healthz/live` probe reflects this.

## 2. Quorum Health: GrpcSubchannelHealthAdapter

For load-balanced gRPC backends with multiple instances, individual call failures don't tell the full story. You might have 10 instances with 2 down — calls mostly succeed, but you're losing capacity.

The quorum evaluator answers: **"Do I have enough healthy instances?"**

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

## Combining Both

For a complete gRPC health picture, use both components together:

```csharp
// 1. Call-level: interceptor records per-call success/failure
var interceptor = new GrpcClientHealthInterceptor(writer, clock, "payment-grpc");

// 2. Pool-level: adapter + quorum evaluator checks instance availability
var adapter = new GrpcSubchannelHealthAdapter(source, "payment-grpc");

// 3. Periodic quorum check (e.g., in a BackgroundService)
var results = await adapter.ProbeAllAsync();
var assessment = quorumEvaluator.Evaluate(results, quorumPolicy);

if (!assessment.QuorumMet)
{
    // Feed a failure signal to trigger state transition
    ingress.RecordSignal(
        new DependencyId("payment-grpc"),
        new HealthSignal(clock.UtcNow, depId, SignalOutcome.Failure));
}
```

The interceptor catches individual call failures. The quorum evaluator catches capacity loss even when most calls succeed. Together, they give you a complete picture of gRPC backend health.

## Metrics

When the quorum evaluator runs, HealthBoss emits:

| Metric | Type | Description |
|--------|------|-------------|
| `healthboss.quorum_healthy_instances` | Gauge | Healthy instances per component |
| `healthboss.quorum_total_instances` | Gauge | Total instances per component |
| `healthboss.quorum_met` | Gauge | 1 if quorum met, 0 if not |
