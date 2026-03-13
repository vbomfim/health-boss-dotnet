# HealthBoss Sample — Order Service

A minimal ASP.NET Core Web API showing how to integrate HealthBoss.

## Run it

```bash
cd samples/HealthBoss.Sample.WebApi
dotnet run --urls http://localhost:5100
```

Open **http://localhost:5100/scalar/v1** for the interactive API explorer.

## Try it

```bash
# Place orders (auto-incrementing IDs)
curl -X POST http://localhost:5100/orders
curl -X POST http://localhost:5100/orders

# Fulfill an order (~10% simulated failure, records signal via ISignalRecorder)
curl -X POST http://localhost:5100/orders/1/fulfill

# Check health
curl http://localhost:5100/status
curl http://localhost:5100/healthz/live
curl http://localhost:5100/healthz/ready
```

## Watch the state machine

HealthBoss has a 30-second cooldown between transitions. To see Healthy → Degraded → CircuitOpen:

```bash
# 1. Seed signals (need MinimumSignals=10)
curl -X POST http://localhost:5100/simulate/orders-db/success/10

# 2. Wait 30s for cooldown, then inject failures
sleep 31
curl -X POST http://localhost:5100/simulate/orders-db/failure/20

# 3. Check — should be Degraded
curl http://localhost:5100/status

# 4. Wait 30s, inject more failures
sleep 31
curl -X POST http://localhost:5100/simulate/orders-db/failure/10

# 5. Check — should be CircuitOpen, liveness returns 503
curl http://localhost:5100/status
curl -w "\n%{http_code}\n" http://localhost:5100/healthz/live

# 6. Recover with successes
curl -X POST http://localhost:5100/simulate/orders-db/success/50
sleep 31
curl http://localhost:5100/status   # Back to Healthy
```

## What this demonstrates

| Feature | Where |
|---------|-------|
| Component registration with thresholds | `AddHealthBoss()` in Program.cs |
| Manual signal recording | `POST /orders/{id}/fulfill` via `ISignalRecorder` |
| Signal injection for testing | `POST /simulate/{component}/{outcome}/{count}` |
| Programmatic health reading | `GET /status` via `IHealthOrchestrator` |
| Kubernetes probes | `MapHealthBossEndpoints()` |
| State machine transitions | `/simulate` + `/status` (see above) |
| OpenTelemetry metrics | Console exporter wired to `HealthBoss` meter |
| Startup lifecycle | `IStartupTracker.MarkReady()` |
| Interactive API explorer | Scalar UI at `/scalar/v1` |
