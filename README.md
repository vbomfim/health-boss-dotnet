# HealthBoss

Stateful health intelligence for ASP.NET Core. HealthBoss tracks dependency health over time using sliding-window signal analysis, automatic state transitions, and recovery probing — then exposes Kubernetes-ready probe endpoints.

## Packages

| Package | Purpose |
|---------|---------|
| `HealthBoss.Core` | Signal recording, health assessment, state machine |
| `HealthBoss.AspNetCore` | Probe endpoints + HTTP request/response tracking |
| `HealthBoss.Grpc` | gRPC subchannel health (quorum evaluation) |
| `HealthBoss.Polly` | Polly v8 circuit breaker bridge |

## Quick Start

```csharp
// Program.cs
services.AddHealthBoss(opts =>
{
    opts.AddComponent("orders-db");
    opts.AddComponent("payment-api");
});

app.MapHealthBossEndpoints();
```

That's it. HealthBoss will:
- Serve `/healthz/live`, `/healthz/ready`, `/healthz/startup` for Kubernetes probes
- Track health state per component (Healthy → Degraded → CircuitOpen)
- Auto-recover via background probing when a circuit opens

## Recording Signals

HealthBoss needs signals (success/failure events) to assess health. You can feed them automatically or manually.

### Automatic: HTTP tracking

```csharp
// Track inbound requests (maps URL paths to components)
app.UseHealthBossInboundTracking(opts =>
{
    opts.Map("/api/orders", "orders-db");
    opts.Map("/api/payments", "payment-api");
});

// Track outbound HttpClient calls
services.AddHttpClient("payment-api")
    .AddHealthBossOutboundTracking(opts =>
    {
        opts.ComponentName = "payment-api";
    });
```

### Automatic: Polly circuit breakers

```csharp
builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions())
    .WithHealthBossTracking(signalRecorder, new DependencyId("payment-api"), clock);
```

### Manual: ISignalRecorder

```csharp
public class MyService(ISignalRecorder recorder)
{
    public async Task DoWork()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await CallDependency();
            recorder.Record(new HealthSignal(
                DateTimeOffset.UtcNow, new DependencyId("my-dep"),
                SignalOutcome.Success, sw.Elapsed));
        }
        catch
        {
            recorder.Record(new HealthSignal(
                DateTimeOffset.UtcNow, new DependencyId("my-dep"),
                SignalOutcome.Failure, sw.Elapsed));
            throw;
        }
    }
}
```

## Configuring Components

Each component has its own health thresholds:

```csharp
services.AddHealthBoss(opts =>
{
    opts.AddComponent("orders-db", c => c
        .Window(TimeSpan.FromMinutes(5))    // Sliding window (default: 5 min)
        .HealthyAbove(0.9)                  // ≥90% success → Healthy (default)
        .DegradedAbove(0.5)                 // ≥50% success → Degraded (default)
        .MinimumSignals(5)                  // Need 5 signals before assessing (default)
        .WithResponseTime(rt => rt
            .Percentile(0.95)               // Track p95 latency
            .DegradedAfter(TimeSpan.FromMilliseconds(500))
        ));
});
```

## Probe Endpoints

| Endpoint | Purpose | Kubernetes Field |
|----------|---------|-----------------|
| `/healthz/live` | Liveness — is the process healthy? | `livenessProbe` |
| `/healthz/ready` | Readiness — can it accept traffic? | `readinessProbe` |
| `/healthz/startup` | Startup — has initialization completed? | `startupProbe` |
| `/healthz/tenants` | Per-tenant health (requires `DetailLevel.Full`) | — |

Customize paths and detail level:

```csharp
app.MapHealthBossEndpoints(opts =>
{
    opts.LivenessPath = "/health/live";
    opts.DefaultDetailLevel = DetailLevel.Summary;  // StatusOnly | Summary | Full
});
```

Endpoints are unauthenticated by default for Kubernetes compatibility. Your app controls security:

```csharp
app.MapHealthBossEndpoints().RequireAuthorization("HealthOpsPolicy");
```

## Reading Health Programmatically

```csharp
public class MyController(IHealthReportProvider health)
{
    public IActionResult GetStatus()
    {
        var report = health.GetHealthReport();
        // report.Status: Healthy | Degraded | Unhealthy
        // report.Dependencies: per-component snapshots
        return Ok(report);
    }
}
```

Key interfaces available via DI:

| Interface | Purpose |
|-----------|---------|
| `IHealthReportProvider` | Aggregate health + readiness reports |
| `IHealthStateReader` | Current state, snapshots, signal counts |
| `IStartupTracker` | Mark startup as `Ready` or `Failed` |
| `ISignalRecorder` | Record health signals manually |
| `ISessionHealthTracker` | Track active sessions for graceful drain |

## Multi-Tenant Health

HealthBoss tracks health per tenant per component. Query via endpoint:

```
GET /healthz/tenants?component=orders-db&detail=Full
```

Or programmatically:

```csharp
public class TenantDashboard(ITenantHealthProvider tenants)
{
    public IActionResult GetTenantHealth()
    {
        var health = tenants.GetAllTenantHealth(new DependencyId("orders-db"));
        // Dictionary<TenantId, TenantHealthAssessment>
        return Ok(health);
    }
}
```

## Custom Event Sinks

React to health state changes (alerting, logging, database writes):

```csharp
public class SlackAlertSink : IHealthEventSink
{
    public async Task OnHealthStateChanged(HealthEvent evt, CancellationToken ct)
    {
        if (evt.NewState == HealthState.CircuitOpen)
            await SendSlackAlert($"{evt.DependencyId} is down!");
    }

    public Task OnTenantHealthChanged(TenantHealthEvent evt, CancellationToken ct)
        => Task.CompletedTask;
}
```

## Observability

HealthBoss emits 18 OpenTelemetry instruments under the `HealthBoss` meter. Wire them with any OTel exporter:

```csharp
services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("HealthBoss"));
```

**Important:** Component and tenant identifiers flow into metric tags and structured logs. Use opaque IDs, not PII:

| | ❌ Bad | ✅ Good |
|---|---|---|
| `dependency.id` | `"john.doe@company.com-db"` | `"cosmosdb-orders"` |
| `tenant.id` | `"john.doe@company.com"` | `"tenant-a1b2c3"` |

See [METRICS-CARDINALITY.md](docs/METRICS-CARDINALITY.md) for capacity planning guidance.

## Further Reading

| Document | Audience |
|----------|----------|
| [samples/HealthBoss.Sample.WebApi](samples/HealthBoss.Sample.WebApi) | Runnable reference implementation |
| [STATE-MACHINE.md](docs/STATE-MACHINE.md) | How health assessment works |
| [SPECIFICATION.md](docs/SPECIFICATION.md) | Full v1.0 specification |
| [METRICS-CARDINALITY.md](docs/METRICS-CARDINALITY.md) | Ops — cardinality planning |
| [COMPONENT-DESIGN.md](docs/architecture/COMPONENT-DESIGN.md) | Contributors — internal architecture |
| [ADR-001](docs/architecture/ADR-001-observable-gauge-push-model.md) | Contributors — design decisions |

## License

See [LICENSE](LICENSE).
