// <copyright file="InboundMiddlewarePerformanceTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

// ──────────────────────────────────────────────────────────────────────────
// QA Guardian — Performance baseline tests for InboundHealthMiddleware.
//
// Target branch: feature/inbound-middleware
// File location: tests/HealthBoss.AspNetCore.Tests/Performance/InboundMiddlewarePerformanceTests.cs
//
// These tests establish throughput and latency baselines for the middleware
// hot path. They validate that middleware overhead stays within acceptable
// bounds.
//
// Gap covered: G13 (No performance baseline for middleware hot path)
// ──────────────────────────────────────────────────────────────────────────

using System.Diagnostics;
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

namespace HealthBoss.AspNetCore.Tests.Performance;

/// <summary>
/// [PERF] Performance baseline tests for InboundHealthMiddleware.
/// Establishes throughput and overhead bounds for the signal recording hot path.
/// </summary>
/// <remarks>
/// These tests use lenient thresholds to avoid flakiness in CI environments.
/// The primary purpose is regression detection, not precise benchmarking.
/// For production benchmarking, use BenchmarkDotNet.
/// </remarks>
public sealed class InboundMiddlewarePerformanceTests : IDisposable
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly RecordingSignalBuffer _buffer = new();

    public InboundMiddlewarePerformanceTests()
    {
        _timeProvider.SetUtcNow(new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero));
    }

    public void Dispose()
    {
        // No unmanaged resources.
    }

    /// <summary>
    /// [PERF] Sequential throughput: 1000 requests should complete within
    /// reasonable time, verifying middleware overhead is minimal.
    /// </summary>
    [Fact]
    public async Task Sequential_1000_requests_complete_within_time_budget()
    {
        // Arrange
        const int requestCount = 1000;
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        // Warm up
        await client.GetAsync("/api/warmup");

        // Act
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < requestCount; i++)
        {
            await client.GetAsync($"/api/data/{i}");
        }

        sw.Stop();

        // Assert — lenient: 10 seconds for 1000 in-process requests
        // (real world would be < 1s, but CI can be slow)
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            $"1000 in-process requests should complete within 10s (actual: {sw.ElapsedMilliseconds}ms)");

        // All signals recorded
        _buffer.Signals.Should().HaveCount(requestCount + 1, // +1 for warmup
            "every request must produce exactly one signal");
    }

    /// <summary>
    /// [PERF] Concurrent throughput: 100 parallel requests should complete
    /// without contention issues.
    /// </summary>
    [Fact]
    public async Task Concurrent_100_requests_complete_without_contention()
    {
        // Arrange
        const int concurrentRequests = 100;
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        // Warm up
        await client.GetAsync("/api/warmup");

        // Act
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(i => client.GetAsync($"/api/data/{i}"))
            .ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        // Assert
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            $"100 concurrent in-process requests should complete within 5s (actual: {sw.ElapsedMilliseconds}ms)");

        _buffer.Signals.Should().HaveCount(concurrentRequests + 1); // +1 for warmup
    }

    /// <summary>
    /// [PERF] Excluded path check should be fast — 1000 requests to excluded paths
    /// should complete quickly since no signal is recorded.
    /// </summary>
    [Fact]
    public async Task Excluded_path_bypass_is_fast()
    {
        // Arrange
        const int requestCount = 1000;
        using var server = CreateServer(statusCode: 200);
        var client = server.CreateClient();

        // Act — all requests to excluded /healthz
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < requestCount; i++)
        {
            await client.GetAsync("/healthz");
        }

        sw.Stop();

        // Assert — no signals recorded (all excluded)
        _buffer.Signals.Should().BeEmpty();

        // Excluded path bypass should be faster than recording path
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "excluded path bypass should be very fast (no signal recording overhead)");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private TestServer CreateServer(int statusCode = 200)
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
                    ctx.Response.StatusCode = statusCode;
                    return Task.CompletedTask;
                });
            });

        return new TestServer(builder);
    }
}
