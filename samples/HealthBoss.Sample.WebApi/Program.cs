using HealthBoss.AspNetCore;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// ──────────────────────────────────────────────────────────────
// 1. Register HealthBoss — define the components you depend on
// ──────────────────────────────────────────────────────────────
builder.Services.AddHealthBoss(opts =>
{
    // A database with tight latency requirements
    opts.AddComponent("orders-db", c => c
        .Window(TimeSpan.FromMinutes(5))
        .HealthyAbove(0.95)
        .DegradedAbove(0.7)
        .MinimumSignals(10)
        .WithResponseTime(rt => rt
            .Percentile(0.95)
            .DegradedAfter(TimeSpan.FromMilliseconds(200))));

    // An external payment API — more tolerant thresholds
    opts.AddComponent("payment-api", c => c
        .Window(TimeSpan.FromMinutes(10))
        .HealthyAbove(0.8)
        .DegradedAbove(0.5)
        .MinimumSignals(5));

    // A cache — optional, so we tolerate more failures
    opts.AddComponent("redis-cache", c => c
        .HealthyAbove(0.7)
        .DegradedAbove(0.3));
});

// ──────────────────────────────────────────────────────────────
// 2. Track outbound HTTP calls automatically
// ──────────────────────────────────────────────────────────────
builder.Services.AddHttpClient("PaymentApi", client =>
{
    client.BaseAddress = new Uri("https://api.payments.example.com");
})
.AddHealthBossOutboundTracking(opts =>
{
    opts.ComponentName = "payment-api";
});

// ──────────────────────────────────────────────────────────────
// 3. Wire OpenTelemetry to export HealthBoss metrics
// ──────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("HealthBoss")
        .AddConsoleExporter());

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

// ──────────────────────────────────────────────────────────────
// 4. Track inbound requests — map URL paths to components
// ──────────────────────────────────────────────────────────────
app.UseHealthBossInboundTracking(opts =>
{
    opts.Map("/orders", "orders-db");
    opts.Map("/payments", "payment-api");
});

// ──────────────────────────────────────────────────────────────
// 5. Map Kubernetes probe endpoints
// ──────────────────────────────────────────────────────────────
app.MapHealthBossEndpoints(opts =>
{
    opts.DefaultDetailLevel = DetailLevel.Summary;
});

// ──────────────────────────────────────────────────────────────
// 6. Application endpoints
// ──────────────────────────────────────────────────────────────

// Simulate placing an order — signals flow automatically via inbound tracking
var nextOrderId = 0;
app.MapPost("/orders", () =>
{
    var id = Interlocked.Increment(ref nextOrderId);
    return Results.Created($"/orders/{id}", new { Id = id, Status = "created" });
}).WithTags("Orders").WithSummary("Place an order (inbound tracking records the signal)");

// Manual signal recording — useful for non-HTTP dependencies
app.MapPost("/orders/{id}/fulfill", (int id, HttpContext ctx) =>
{
    var recorder = ctx.RequestServices.GetRequiredKeyedService<ISignalBuffer>("orders-db");
    var dep = new DependencyId("orders-db");
    var sw = System.Diagnostics.Stopwatch.StartNew();

    try
    {
        // Simulate a database write
        if (Random.Shared.NextDouble() < 0.1)
            throw new TimeoutException("DB timeout");

        recorder.Record(new HealthSignal(
            DateTimeOffset.UtcNow, dep, SignalOutcome.Success, sw.Elapsed));

        return Results.Ok(new { Id = id, Status = "fulfilled" });
    }
    catch (Exception)
    {
        recorder.Record(new HealthSignal(
            DateTimeOffset.UtcNow, dep, SignalOutcome.Failure, sw.Elapsed));

        return Results.Problem("Order fulfillment failed", statusCode: 503);
    }
}).WithTags("Orders").WithSummary("Fulfill an order (~10% simulated failure, manual signal recording)");

// Read health programmatically
app.MapGet("/status", (HealthBoss.Core.IHealthOrchestrator health) =>
{
    var report = health.GetHealthReport();
    return Results.Ok(new
    {
        Status = report.Status.ToString(),
        Components = report.Dependencies.Select(d => new
        {
            Name = d.DependencyId.Value,
            State = d.CurrentState.ToString(),
            d.ConsecutiveFailures,
            SuccessRate = d.LatestAssessment.SuccessRate,
            TotalSignals = d.LatestAssessment.TotalSignals,
            RecommendedState = d.LatestAssessment.RecommendedState.ToString(),
        }),
    });
}).WithTags("Health").WithSummary("Programmatic health report (IHealthReportProvider)");

// Inject signals on demand — use this to simulate failures and watch health degrade
app.MapPost("/simulate/{component}/{outcome}/{count:int}", (string component, string outcome, int count, HttpContext ctx) =>
{
    var orchestrator = ctx.RequestServices.GetRequiredService<HealthBoss.Core.IHealthOrchestrator>();
    var dep = new DependencyId(component);
    var signalOutcome = outcome.ToLowerInvariant() switch
    {
        "success" => SignalOutcome.Success,
        "failure" => SignalOutcome.Failure,
        "timeout" => SignalOutcome.Timeout,
        _ => SignalOutcome.Failure,
    };

    for (var i = 0; i < count; i++)
        orchestrator.RecordSignal(dep, new HealthSignal(DateTimeOffset.UtcNow, dep, signalOutcome, TimeSpan.FromMilliseconds(50)));

    return Results.Ok(new { Component = component, Outcome = outcome, Count = count });
}).WithTags("Simulate").WithSummary("Inject signals: /simulate/{component}/{success|failure|timeout}/{count}");

// Mark startup complete once initialization is done
var startup = app.Services.GetRequiredService<HealthBoss.Core.IStartupTracker>();
startup.MarkReady();

app.Run();
