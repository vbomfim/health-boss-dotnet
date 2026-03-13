# HealthBoss Sample — Order Service

A minimal ASP.NET Core Web API showing how to integrate HealthBoss.

## Run it

```bash
dotnet run
```

## Try it

```bash
# Place an order
curl -X POST http://localhost:5000/orders

# Fulfill an order (manual signal recording via ISignalIngress, ~10% failure rate)
curl -X POST http://localhost:5000/orders/1/fulfill

# Inject signals to simulate failures and watch health degrade
curl -X POST http://localhost:5000/simulate/orders-db/failure/20

# Read health programmatically
curl http://localhost:5000/status

# Kubernetes probe endpoints
curl http://localhost:5000/healthz/live
curl http://localhost:5000/healthz/ready
curl http://localhost:5000/healthz/startup
```

## What this demonstrates

| Feature | Where |
|---------|-------|
| Component registration with thresholds | `AddHealthBoss()` in Program.cs |
| Manual signal recording | `POST /orders/{id}/fulfill` using `ISignalIngress` |
| Signal injection for testing | `POST /simulate/{component}/{outcome}/{count}` using `ISignalIngress` |
| Programmatic health reading | `GET /status` using `IHealthOrchestrator` |
| Kubernetes probes | `MapHealthBossEndpoints()` |
| OpenTelemetry metrics | Console exporter wired to `HealthBoss` meter |
| Startup lifecycle | `IStartupTracker.MarkReady()` |
