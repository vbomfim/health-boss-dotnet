// <copyright file="MetricsMiddlewareIntegrationTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Diagnostics.Metrics;
using System.Net;
using FluentAssertions;
using HealthBoss.AspNetCore.Tests.Fakes;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.AspNetCore.Tests.Integration;

/// <summary>
/// Integration tests verifying that ASP.NET Core middleware and HTTP handler
/// components emit OTel metrics (inbound/outbound duration histograms)
/// when processing real HTTP requests via TestHost.
/// Uses AddHealthBoss() DI registration so IHealthBossMetrics is resolved
/// through the public API (HealthBossMetrics is internal to Core).
/// </summary>
public sealed class MetricsMiddlewareIntegrationTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingSignalBuffer _buffer = new();
    private readonly ServiceProvider _metricsProvider;
    private readonly IHealthBossMetrics _metrics;

    private readonly List<DoubleMeasurement> _doubleMeasurements = [];
    private readonly object _lock = new();

    public MetricsMiddlewareIntegrationTests()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero));

        // Build a DI container to resolve IHealthBossMetrics through the public AddHealthBoss API
        var services = new ServiceCollection();
        services.AddHealthBoss(opts =>
        {
            opts.AddComponent("web-api");
        });

        _metricsProvider = services.BuildServiceProvider();
        _metrics = _metricsProvider.GetRequiredService<IHealthBossMetrics>();

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "HealthBoss")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            lock (_lock)
            {
                _doubleMeasurements.Add(new DoubleMeasurement(instrument.Name, value, ExtractTags(tags)));
            }
        });

        // Register other type callbacks to avoid unhandled instrument types
        _listener.SetMeasurementEventCallback<long>((_, _, _, _) => { });
        _listener.SetMeasurementEventCallback<int>((_, _, _, _) => { });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _metricsProvider.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────
    // [AC-1][INTEGRATION] InboundHealthMiddleware → middleware_inbound_duration_seconds
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [AC-1][INTEGRATION] When a request passes through InboundHealthMiddleware,
    /// the middleware_inbound_duration_seconds histogram is recorded with the
    /// correct component name and a positive duration.
    /// </summary>
    [Fact]
    public async Task InboundMiddleware_EmitsInboundDurationHistogram()
    {
        // Arrange: TestHost with middleware + metrics in DI
        using var server = new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISignalBuffer>(_buffer);
                    services.AddSingleton<IComponentMetrics>(_metrics);
                    services.AddSingleton<TimeProvider>(_timeProvider);
                })
                .Configure(app =>
                {
                    app.UseMiddleware<InboundHealthMiddleware>(new InboundTrackingOptions
                    {
                        DefaultComponent = "web-api",
                    });
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        return Task.CompletedTask;
                    });
                }));

        var client = server.CreateClient();

        // Act
        var response = await client.GetAsync("/api/orders");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var durations = GetDoubleMeasurements("healthboss.middleware_inbound_duration_seconds");
        durations.Should().ContainSingle();
        durations[0].Value.Should().BeGreaterOrEqualTo(0, "duration must be non-negative");
        durations[0].Tags.Should().ContainKey("component")
            .WhoseValue.Should().Be("web-api");
    }

    /// <summary>
    /// [EDGE][INTEGRATION] When IHealthBossMetrics is not in DI, the middleware
    /// still processes requests without throwing (metrics line uses ?. null check).
    /// </summary>
    [Fact]
    public async Task InboundMiddleware_WithoutMetrics_StillProcessesRequests()
    {
        // Arrange: no IHealthBossMetrics registered
        using var server = new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISignalBuffer>(_buffer);
                    services.AddSingleton<TimeProvider>(_timeProvider);
                    // NOTE: no IHealthBossMetrics registered
                })
                .Configure(app =>
                {
                    app.UseMiddleware<InboundHealthMiddleware>(new InboundTrackingOptions
                    {
                        DefaultComponent = "web-api",
                    });
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        return Task.CompletedTask;
                    });
                }));

        var client = server.CreateClient();

        // Act — should not throw
        var response = await client.GetAsync("/api/data");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _buffer.Signals.Should().ContainSingle("signal should still be recorded even without metrics");

        // No metric emitted (metrics was null)
        var durations = GetDoubleMeasurements("healthboss.middleware_inbound_duration_seconds");
        durations.Should().BeEmpty("no metrics service was registered");
    }

    /// <summary>
    /// [AC-1][INTEGRATION] Multiple requests through InboundHealthMiddleware
    /// each produce their own duration histogram recording.
    /// </summary>
    [Fact]
    public async Task InboundMiddleware_MultipleRequests_EmitMultipleHistograms()
    {
        using var server = new TestServer(
            new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<ISignalBuffer>(_buffer);
                    services.AddSingleton<IComponentMetrics>(_metrics);
                    services.AddSingleton<TimeProvider>(_timeProvider);
                })
                .Configure(app =>
                {
                    app.UseMiddleware<InboundHealthMiddleware>(new InboundTrackingOptions
                    {
                        DefaultComponent = "web-api",
                    });
                    app.Run(ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        return Task.CompletedTask;
                    });
                }));

        var client = server.CreateClient();

        // Act: 3 requests
        await client.GetAsync("/api/a");
        await client.GetAsync("/api/b");
        await client.GetAsync("/api/c");

        // Assert: 3 histogram recordings
        var durations = GetDoubleMeasurements("healthboss.middleware_inbound_duration_seconds");
        durations.Should().HaveCount(3);
    }

    // ─────────────────────────────────────────────────────────────────
    // [AC-1][INTEGRATION] HealthBossDelegatingHandler → middleware_outbound_duration_seconds
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [AC-1][INTEGRATION] When an outbound HTTP request is made through
    /// HealthBossDelegatingHandler, the middleware_outbound_duration_seconds
    /// histogram is recorded with the component name.
    /// </summary>
    [Fact]
    public async Task DelegatingHandler_EmitsOutboundDurationHistogram()
    {
        // Arrange
        var clock = new FakeSystemClock();
        var options = new OutboundTrackingOptions { ComponentName = "payment-api" };
        var innerHandler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));

        var handler = new HealthBossDelegatingHandler(_buffer, clock, options, _metrics)
        {
            InnerHandler = innerHandler,
        };

        using var invoker = new HttpMessageInvoker(handler);

        // Act
        clock.Advance(TimeSpan.FromMilliseconds(50)); // simulate some time passing before call
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/pay"), CancellationToken.None);

        // Assert
        var durations = GetDoubleMeasurements("healthboss.middleware_outbound_duration_seconds");
        durations.Should().ContainSingle();
        durations[0].Value.Should().BeGreaterOrEqualTo(0);
        durations[0].Tags.Should().ContainKey("component")
            .WhoseValue.Should().Be("payment-api");
    }

    /// <summary>
    /// [EDGE][INTEGRATION] DelegatingHandler without metrics parameter (null)
    /// still records signals but does NOT emit OTel metrics.
    /// </summary>
    [Fact]
    public async Task DelegatingHandler_WithoutMetrics_StillRecordsSignals()
    {
        var clock = new FakeSystemClock();
        var options = new OutboundTrackingOptions { ComponentName = "service-a" };
        var innerHandler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));

        // metrics: null (not passed)
        var handler = new HealthBossDelegatingHandler(_buffer, clock, options)
        {
            InnerHandler = innerHandler,
        };

        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/data"),
            CancellationToken.None);

        // Signals recorded
        _buffer.Signals.Should().ContainSingle();

        // No metric emitted
        var durations = GetDoubleMeasurements("healthboss.middleware_outbound_duration_seconds");
        durations.Should().BeEmpty("no metrics instance was provided");
    }

    /// <summary>
    /// [EDGE][INTEGRATION] DelegatingHandler records outbound metric even on 5xx failure.
    /// </summary>
    [Fact]
    public async Task DelegatingHandler_ServerError_StillEmitsOutboundDurationMetric()
    {
        var clock = new FakeSystemClock();
        var options = new OutboundTrackingOptions { ComponentName = "failing-api" };
        var innerHandler = new FakeHttpMessageHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var handler = new HealthBossDelegatingHandler(_buffer, clock, options, _metrics)
        {
            InnerHandler = innerHandler,
        };

        using var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/fail"),
            CancellationToken.None);

        var durations = GetDoubleMeasurements("healthboss.middleware_outbound_duration_seconds");
        durations.Should().ContainSingle(
            "outbound duration should be recorded even for failed requests");
        durations[0].Tags["component"].Should().Be("failing-api");
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> ExtractTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, string>();
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        return dict;
    }

    private List<DoubleMeasurement> GetDoubleMeasurements(string name)
    {
        lock (_lock)
        {
            return _doubleMeasurements.Where(m => m.InstrumentName == name).ToList();
        }
    }

    private sealed record DoubleMeasurement(
        string InstrumentName,
        double Value,
        Dictionary<string, string> Tags);
}
