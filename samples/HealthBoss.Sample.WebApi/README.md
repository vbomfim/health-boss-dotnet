# HealthBoss Sample — Order Service

A minimal ASP.NET Core Web API showing how to integrate HealthBoss.

## Run it

```bash
dotnet run
```

## Try it

```bash
# Place an order (signals flow via inbound tracking)
curl -X POST http://localhost:5000/orders

# Fulfill an order (manual signal recording, ~10% failure rate)
curl -X POST http://localhost:5000/orders/1/fulfill

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
| Outbound HTTP tracking | `AddHealthBossOutboundTracking()` on HttpClient |
| Inbound request tracking | `UseHealthBossInboundTracking()` with path mapping |
| Manual signal recording | `POST /orders/{id}/fulfill` using `ISignalRecorder` |
| Programmatic health reading | `GET /status` using `IHealthReportProvider` |
| Kubernetes probes | `MapHealthBossEndpoints()` |
| OpenTelemetry metrics | Console exporter wired to `HealthBoss` meter |
| Startup lifecycle | `IStartupTracker.MarkReady()` |
