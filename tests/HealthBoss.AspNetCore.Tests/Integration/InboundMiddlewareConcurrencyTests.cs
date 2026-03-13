// <copyright file="InboundMiddlewareConcurrencyTests.cs" company="HealthBoss">
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
/// [EDGE] Concurrent request stress tests for InboundHealthMiddleware.
/// Validates thread safety under parallel HTTP load — no signal loss,
/// no corruption, no deadlocks.
/// </summary>
public sealed class InboundMiddlewareConcurrencyTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingSignalBuffer _buffer = new();

    public InboundMiddlewareConcurrencyTests()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        // No unmanaged resources.
    }

    /// <summary>
    /// [EDGE] 50 concurrent requests should each produce exactly one signal.
    /// Verifies no signal loss under parallel load through the full middleware pipeline.
    /// </summary>
    [Fact]
    public async Task Fifty_concurrent_requests_each_produce_a_signal()
    {
        // Arrange
        const int concurrentRequests = 50;
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        // Act — fire 50 parallel GET requests
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(i => client.GetAsync($"/api/item/{i}"))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert — exactly one signal per request, no loss
        _buffer.Signals.Should().HaveCount(concurrentRequests,
            "each concurrent request must produce exactly one health signal");
        _buffer.Signals.Should().OnlyContain(s =>
            s.Outcome == SignalOutcome.Success,
            "all requests returned 200, so all signals should be Success");
    }

    /// <summary>
    /// [EDGE] Mixed success/failure concurrent requests maintain correct outcome classification.
    /// </summary>
    [Fact]
    public async Task Concurrent_mixed_outcomes_classified_correctly()
    {
        // Arrange — server returns 200 for even indices, 500 for odd
        int requestIndex = 0;
        using var server = CreateServer(
            dynamicStatusCode: () =>
            {
                var idx = Interlocked.Increment(ref requestIndex);
                return idx % 2 == 0 ? 200 : 500;
            });
        var client = server.CreateClient();

        const int totalRequests = 40;

        // Act
        var tasks = Enumerable.Range(0, totalRequests)
            .Select(i => client.GetAsync($"/api/item/{i}"))
            .ToArray();
        await Task.WhenAll(tasks);

        // Assert
        _buffer.Signals.Should().HaveCount(totalRequests);

        var successCount = _buffer.Signals.Count(s => s.Outcome == SignalOutcome.Success);
        var failureCount = _buffer.Signals.Count(s => s.Outcome == SignalOutcome.Failure);

        (successCount + failureCount).Should().Be(totalRequests,
            "every signal must be classified as either Success or Failure");
    }

    /// <summary>
    /// [EDGE] Concurrent requests with exceptions don't cause middleware to lose signals
    /// for other requests. Exception-throwing requests still produce failure signals.
    /// </summary>
    [Fact]
    public async Task Concurrent_requests_with_intermittent_exceptions_still_record_all_signals()
    {
        // Arrange — every 5th request throws, rest return 200
        int requestIndex = 0;
        using var server = CreateServerWithDynamicBehavior(
            behavior: () =>
            {
                var idx = Interlocked.Increment(ref requestIndex);
                if (idx % 5 == 0)
                {
                    throw new InvalidOperationException($"Simulated failure for request {idx}");
                }

                return 200;
            });
        var client = server.CreateClient();

        const int totalRequests = 25;

        // Act — fire all requests, some will fail
        var tasks = Enumerable.Range(0, totalRequests)
            .Select(async i =>
            {
                try
                {
                    await client.GetAsync($"/api/item/{i}");
                }
                catch
                {
                    // Expected for exception-throwing requests (TestHost propagates)
                }
            })
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert — every request (success or exception) should have produced a signal
        _buffer.Signals.Should().HaveCount(totalRequests,
            "exception-throwing requests must still produce failure signals via the finally block");
    }

    /// <summary>
    /// [EDGE] All signals from concurrent requests have valid, non-negative latency values.
    /// </summary>
    [Fact]
    public async Task Concurrent_requests_all_have_valid_latency()
    {
        // Arrange
        const int concurrentRequests = 30;
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        // Act
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(i => client.GetAsync($"/api/item/{i}"))
            .ToArray();
        await Task.WhenAll(tasks);

        // Assert
        _buffer.Signals.Should().HaveCount(concurrentRequests);
        _buffer.Signals.Should().OnlyContain(s =>
            s.Latency >= TimeSpan.Zero,
            "latency must never be negative even under concurrent load");
    }

    /// <summary>
    /// [EDGE] Concurrent requests to excluded and non-excluded paths: only non-excluded
    /// paths produce signals.
    /// </summary>
    [Fact]
    public async Task Concurrent_requests_to_excluded_and_non_excluded_paths()
    {
        // Arrange
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        // Act — 10 to /healthz (excluded) + 10 to /api/data (not excluded)
        var excludedTasks = Enumerable.Range(0, 10)
            .Select(_ => client.GetAsync("/healthz"))
            .ToArray();
        var apiTasks = Enumerable.Range(0, 10)
            .Select(_ => client.GetAsync("/api/data"))
            .ToArray();

        await Task.WhenAll(excludedTasks.Concat(apiTasks));

        // Assert — only the 10 /api/data requests should produce signals
        _buffer.Signals.Should().HaveCount(10,
            "excluded /healthz requests must not produce signals");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private TestServer CreateServer(
        int statusCode = 200,
        Func<int>? dynamicStatusCode = null)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISignalBuffer>(_buffer);
                services.AddSingleton<TimeProvider>(_timeProvider);
            })
            .Configure(app =>
            {
                app.UseHealthBossInboundTracking();

                app.Run(ctx =>
                {
                    ctx.Response.StatusCode = dynamicStatusCode?.Invoke() ?? statusCode;
                    return Task.CompletedTask;
                });
            });

        return new TestServer(builder);
    }

    private TestServer CreateServerWithDynamicBehavior(Func<int> behavior)
    {
        var builder = new WebHostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISignalBuffer>(_buffer);
                services.AddSingleton<TimeProvider>(_timeProvider);
            })
            .Configure(app =>
            {
                app.UseHealthBossInboundTracking();

                app.Run(ctx =>
                {
                    var code = behavior();
                    ctx.Response.StatusCode = code;
                    return Task.CompletedTask;
                });
            });

        return new TestServer(builder);
    }
}
