// <copyright file="InboundSignalPipelineIntegrationTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.AspNetCore;
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
/// [AC-12][INTEGRATION] End-to-end tests: HTTP request → InboundMiddleware → RecordingSignalBuffer.
/// Verifies the full inbound signal pipeline produces correct signals that a downstream
/// PolicyEvaluator would consume. Uses RecordingSignalBuffer to capture signals through
/// the public ISignalBuffer interface.
/// </summary>
public sealed class InboundSignalPipelineIntegrationTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingSignalBuffer _buffer = new();

    public InboundSignalPipelineIntegrationTests()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        // No unmanaged resources.
    }

    /// <summary>
    /// [AC-12][INTEGRATION] Full pipeline: 10 HTTP requests (8 success + 2 failure)
    /// → middleware records signals into buffer → signals have correct outcomes
    /// and counts matching what PolicyEvaluator expects downstream.
    /// </summary>
    [Fact]
    public async Task AC12_Http_requests_produce_correct_signal_distribution_for_evaluation()
    {
        // Arrange
        int requestCount = 0;
        using var server = CreateServer(
            dynamicStatusCode: () =>
            {
                var idx = Interlocked.Increment(ref requestCount);
                return idx <= 8 ? 200 : 500;
            });
        var client = server.CreateClient();

        // Act — send 10 HTTP requests through the middleware
        for (int i = 0; i < 10; i++)
        {
            await client.GetAsync($"/api/data/{i}");
        }

        // Assert — signals match expected distribution
        var signals = _buffer.Signals;
        signals.Should().HaveCount(10, "middleware should record one signal per request");

        var successSignals = signals.Where(s => s.Outcome == SignalOutcome.Success).ToList();
        var failureSignals = signals.Where(s => s.Outcome == SignalOutcome.Failure).ToList();

        successSignals.Should().HaveCount(8, "8 requests returned 200");
        failureSignals.Should().HaveCount(2, "2 requests returned 500");
        failureSignals.Should().OnlyContain(s => s.HttpStatusCode == 500);
    }

    /// <summary>
    /// [AC-12][INTEGRATION] All successful requests → all signals have Success outcome.
    /// </summary>
    [Fact]
    public async Task AC12_All_successful_requests_produce_success_signals()
    {
        // Arrange
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        // Act — send 10 successful requests
        for (int i = 0; i < 10; i++)
        {
            await client.GetAsync($"/api/data/{i}");
        }

        // Assert
        _buffer.Signals.Should().HaveCount(10);
        _buffer.Signals.Should().OnlyContain(s => s.Outcome == SignalOutcome.Success);
    }

    /// <summary>
    /// [AC-12][INTEGRATION] All failed requests → all signals have Failure outcome.
    /// </summary>
    [Fact]
    public async Task AC12_All_failed_requests_produce_failure_signals()
    {
        // Arrange
        using var server = CreateServer(statusCode: 503);
        var client = server.CreateClient();

        // Act
        for (int i = 0; i < 10; i++)
        {
            await client.GetAsync($"/api/data/{i}");
        }

        // Assert
        _buffer.Signals.Should().HaveCount(10);
        _buffer.Signals.Should().OnlyContain(s => s.Outcome == SignalOutcome.Failure);
        _buffer.Signals.Should().OnlyContain(s => s.HttpStatusCode == 503);
    }

    /// <summary>
    /// [AC-12][INTEGRATION] Excluded paths don't pollute signal buffer.
    /// </summary>
    [Fact]
    public async Task AC12_Excluded_paths_dont_pollute_buffer()
    {
        // Arrange
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        // Act — 5 API requests + 10 health check requests (excluded)
        for (int i = 0; i < 5; i++)
        {
            await client.GetAsync($"/api/data/{i}");
        }

        for (int i = 0; i < 10; i++)
        {
            await client.GetAsync("/healthz");
        }

        // Assert — only API requests in buffer
        _buffer.Signals.Should().HaveCount(5, "health check requests are excluded");
    }

    /// <summary>
    /// [AC-12][INTEGRATION] All signals have valid latency and DependencyId.
    /// </summary>
    [Fact]
    public async Task AC12_Signals_carry_valid_latency_and_component_identity()
    {
        // Arrange
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        // Act
        for (int i = 0; i < 5; i++)
        {
            await client.GetAsync($"/api/data/{i}");
        }

        // Assert
        _buffer.Signals.Should().HaveCount(5);
        _buffer.Signals.Should().OnlyContain(s =>
            s.Latency >= TimeSpan.Zero,
            "latency must be non-negative");
        _buffer.Signals.Should().OnlyContain(s =>
            s.DependencyId.Value == "api",
            "default component name should be 'api'");
    }

    /// <summary>
    /// [AC-12][INTEGRATION] Route-mapped requests produce signals with the
    /// mapped component name, not a raw URL.
    /// </summary>
    [Fact]
    public async Task AC12_Route_mapping_flows_through_to_signal_dependency_id()
    {
        // Arrange
        using var server = CreateServer(
            statusCode: 200,
            configureOptions: opts =>
            {
                opts.Map("/api/payments", "payments-handler");
                opts.Map("/api/users", "users-handler");
            });
        var client = server.CreateClient();

        // Act
        await client.GetAsync("/api/payments/charge");
        await client.GetAsync("/api/users/123");
        await client.GetAsync("/api/other/resource");

        // Assert — order-independent (ConcurrentBag doesn't preserve insertion order)
        _buffer.Signals.Should().HaveCount(3);
        var componentNames = _buffer.Signals.Select(s => s.DependencyId.Value).ToList();
        componentNames.Should().Contain("payments-handler");
        componentNames.Should().Contain("users-handler");
        componentNames.Should().Contain("api"); // default fallback
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private TestServer CreateServer(
        int statusCode = 200,
        Func<int>? dynamicStatusCode = null,
        Action<InboundTrackingOptions>? configureOptions = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISignalBuffer>(_buffer);
                services.AddSingleton<TimeProvider>(_timeProvider);
            })
            .Configure(app =>
            {
                app.UseHealthBossInboundTracking(opts =>
                {
                    configureOptions?.Invoke(opts);
                });

                app.Run(ctx =>
                {
                    ctx.Response.StatusCode = dynamicStatusCode?.Invoke() ?? statusCode;
                    return Task.CompletedTask;
                });
            });

        return new TestServer(builder);
    }
}
