using HealthBoss.AspNetCore;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using OtelEvents.AspNetCore;
using OtelEvents.Causality;
using OtelEvents.Exporter.Json;
using OtelEvents.Subscriptions;
using OpenTelemetry.Metrics;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// ──────────────────────────────────────────────────────────────
// 1. Register HealthBoss — define the components you depend on
// ──────────────────────────────────────────────────────────────
builder.Services.AddHealthBoss(opts =>
{
    opts.AddComponent("orders-db", c => c
        .Window(TimeSpan.FromMinutes(5))
        .HealthyAbove(0.95)
        .DegradedAbove(0.7)
        .MinimumSignals(10)
        .WithResponseTime(rt => rt
            .Percentile(0.95)
            .DegradedAfter(TimeSpan.FromMilliseconds(200))));

    opts.AddComponent("payment-api", c => c
        .Window(TimeSpan.FromMinutes(10))
        .HealthyAbove(0.8)
        .DegradedAbove(0.5)
        .MinimumSignals(5));

    opts.AddComponent("redis-cache", c => c
        .HealthyAbove(0.7)
        .DegradedAbove(0.3));
});

// ──────────────────────────────────────────────────────────────
// 2. Register otel-events — structured event logging
// ──────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .WithLogging(logging =>
    {
        logging.AddOtelEventsJsonExporter();                    // JSONL to stdout
        logging.AddOtelEventsCausalityProcessor();              // Causal linking
    })
    .WithMetrics(m => m
        .AddMeter("HealthBoss")
        .AddConsoleExporter());

builder.Services.AddOtelEventsAspNetCore();   // Auto-emit HTTP events

// ──────────────────────────────────────────────────────────────
// 3. Wire otel-events → HealthBoss via subscriptions
//    otel-events emits events, we subscribe and feed signals
// ──────────────────────────────────────────────────────────────
builder.Services.AddOtelEventsSubscriptions(subs =>
{
    // HTTP failures → HealthBoss signal
    subs.On("http.request.failed", (ctx, ct) =>
    {
        // Note: subscription handlers don't have DI access.
        // For production, resolve ISignalRecorder from a captured IServiceProvider.
        // This sample uses /simulate for signal injection instead.
        return Task.CompletedTask;
    });
});

// ──────────────────────────────────────────────────────────────
// 4. Register outbound HTTP clients
// ──────────────────────────────────────────────────────────────
builder.Services.AddHttpClient("PaymentApi", client =>
{
    client.BaseAddress = new Uri("https://api.payments.example.com");
});

var app = builder.Build();

app.MapOpenApi();
app.MapScalarApiReference();

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

var nextOrderId = 0;
app.MapPost("/orders", () =>
{
    var id = Interlocked.Increment(ref nextOrderId);
    return Results.Created($"/orders/{id}", new { Id = id, Status = "created" });
}).WithTags("Orders").WithSummary("Place an order (otel-events tracks the HTTP request automatically)");

// Manual signal recording — for non-HTTP dependencies
app.MapPost("/orders/{id}/fulfill", (int id, HttpContext ctx) =>
{
    var recorder = ctx.RequestServices.GetRequiredService<ISignalRecorder>();
    var dep = new DependencyId("orders-db");
    var sw = System.Diagnostics.Stopwatch.StartNew();

    try
    {
        if (Random.Shared.NextDouble() < 0.1)
            throw new TimeoutException("DB timeout");

        recorder.RecordSignal(dep, new HealthSignal(
            DateTimeOffset.UtcNow, dep, SignalOutcome.Success, sw.Elapsed));

        return Results.Ok(new { Id = id, Status = "fulfilled" });
    }
    catch (Exception)
    {
        recorder.RecordSignal(dep, new HealthSignal(
            DateTimeOffset.UtcNow, dep, SignalOutcome.Failure, sw.Elapsed));

        return Results.Problem("Order fulfillment failed", statusCode: 503);
    }
}).WithTags("Orders").WithSummary("Fulfill an order (~10% simulated failure)");

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
}).WithTags("Health").WithSummary("Programmatic health report");

// Inject signals on demand for testing state transitions
app.MapPost("/simulate/{component}/{outcome}/{count:int}", (string component, string outcome, int count, HttpContext ctx) =>
{
    var recorder = ctx.RequestServices.GetRequiredService<ISignalRecorder>();
    var dep = new DependencyId(component);
    var signalOutcome = outcome.ToLowerInvariant() switch
    {
        "success" => SignalOutcome.Success,
        "failure" => SignalOutcome.Failure,
        "timeout" => SignalOutcome.Timeout,
        _ => SignalOutcome.Failure,
    };

    for (var i = 0; i < count; i++)
        recorder.RecordSignal(dep, new HealthSignal(DateTimeOffset.UtcNow, dep, signalOutcome, TimeSpan.FromMilliseconds(50)));

    return Results.Ok(new { Component = component, Outcome = outcome, Count = count });
}).WithTags("Simulate").WithSummary("Inject signals: /simulate/{component}/{success|failure|timeout}/{count}");

// Mark startup complete
var startup = app.Services.GetRequiredService<HealthBoss.Core.IStartupTracker>();
startup.MarkReady();

app.Run();
