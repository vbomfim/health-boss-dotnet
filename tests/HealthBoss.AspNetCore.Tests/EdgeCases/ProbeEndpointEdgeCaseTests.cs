// <copyright file="ProbeEndpointEdgeCaseTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

// ──────────────────────────────────────────────────────────────────────────
// QA Guardian — Missing edge case tests for feature/probe-endpoints.
//
// Target branch: feature/probe-endpoints
// File location: tests/HealthBoss.AspNetCore.Tests/EdgeCases/ProbeEndpointEdgeCaseTests.cs
//
// Prerequisites: TestFixtures (FakeHealthReportProvider, ThrowingHealthReportProvider),
//   ProbeEndpointHandler, ProbeResponseWriter from the probe-endpoints branch.
//
// These tests cover gaps G06, G07, G08 from QA coverage analysis:
//   G06: Empty dependencies (no components registered)
//   G07: All components in CircuitOpen state
//   G08: Concurrent StartupTracker access
// ──────────────────────────────────────────────────────────────────────────

using System.Net;
using FluentAssertions;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static HealthBoss.AspNetCore.Tests.TestFixtures;

namespace HealthBoss.AspNetCore.Tests.EdgeCases;

/// <summary>
/// [EDGE] Edge case tests for probe endpoints:
/// Empty dependency lists, all-CircuitOpen, concurrent startup transitions.
/// </summary>
public sealed class ProbeEndpointEdgeCaseTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _disposables = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    // ────────────────────────────────────────────────────────────
    // [EDGE][AC-14] No components registered (empty dependencies)
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE][AC-14] Liveness probe with no components registered returns 200/healthy.
    /// K8s scenario: pod starts before any dependency monitors are configured.
    /// </summary>
    [Fact]
    public async Task Liveness_with_no_components_returns_200_healthy()
    {
        var provider = new FakeHealthReportProvider
        {
            HealthReport = CreateEmptyReport(HealthStatus.Healthy),
        };
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/live");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("healthy");
    }

    /// <summary>
    /// [EDGE][AC-14] Readiness probe with no dependencies returns 200/ready.
    /// </summary>
    [Fact]
    public async Task Readiness_with_no_components_returns_200_ready()
    {
        var provider = new FakeHealthReportProvider
        {
            ReadinessReport = new ReadinessReport(
                ReadinessStatus.Ready,
                Array.Empty<DependencySnapshot>(),
                BaseTime,
                StartupStatus.Ready,
                DrainStatus.Idle),
        };
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// [EDGE][AC-14] Summary detail level with no dependencies shows empty array.
    /// </summary>
    [Fact]
    public async Task Summary_detail_with_no_components_shows_empty_dependencies()
    {
        var provider = new FakeHealthReportProvider
        {
            HealthReport = CreateEmptyReport(HealthStatus.Healthy),
        };
        await using var env = await CreateTestEnv(provider, configure: opts =>
        {
            opts.DefaultDetailLevel = DetailLevel.Summary;
        });

        var response = await env.Client.GetAsync("/healthz/live");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("dependencies");
        body.Should().Contain("[]"); // empty array in JSON
    }

    // ────────────────────────────────────────────────────────────
    // [EDGE][AC-14] All components in CircuitOpen state
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE][AC-14] All components in CircuitOpen → liveness returns 503/unhealthy.
    /// </summary>
    [Fact]
    public async Task Liveness_with_all_components_circuit_open_returns_503()
    {
        var provider = new FakeHealthReportProvider
        {
            HealthReport = CreateAllCircuitOpenReport(),
        };
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/live");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("unhealthy");
    }

    /// <summary>
    /// [EDGE][AC-14] All components CircuitOpen → readiness returns 503/not_ready.
    /// </summary>
    [Fact]
    public async Task Readiness_with_all_components_circuit_open_returns_503()
    {
        var provider = new FakeHealthReportProvider
        {
            ReadinessReport = new ReadinessReport(
                ReadinessStatus.NotReady,
                CreateAllCircuitOpenDependencies(),
                BaseTime,
                StartupStatus.Ready,
                DrainStatus.Idle),
        };
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/ready");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// [EDGE][AC-14] Summary detail with all CircuitOpen shows correct states.
    /// </summary>
    [Fact]
    public async Task Summary_detail_with_all_circuit_open_shows_all_as_circuit_open()
    {
        var provider = new FakeHealthReportProvider
        {
            HealthReport = CreateAllCircuitOpenReport(),
        };
        await using var env = await CreateTestEnv(provider, configure: opts =>
        {
            opts.DefaultDetailLevel = DetailLevel.Summary;
        });

        var response = await env.Client.GetAsync("/healthz/live");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("circuit_open");
    }

    // ────────────────────────────────────────────────────────────
    // [EDGE] Concurrent StartupTracker access
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] Multiple threads calling MarkReady concurrently — volatile field
    /// guarantees visibility, no exceptions.
    /// </summary>
    [Fact]
    public async Task StartupTracker_concurrent_MarkReady_does_not_throw()
    {
        var tracker = new FakeStartupTracker();

        // Act — 20 parallel calls to MarkReady
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => tracker.MarkReady()))
            .ToArray();

        await Task.WhenAll(tasks);

        // Assert — final status is Ready
        tracker.Status.Should().Be(StartupStatus.Ready);
    }

    /// <summary>
    /// [EDGE] Concurrent MarkReady and MarkFailed — MarkFailed wins (last writer wins
    /// with volatile). Final state should be one of the two valid end states.
    /// </summary>
    [Fact]
    public async Task StartupTracker_concurrent_ready_and_failed_reaches_valid_end_state()
    {
        var tracker = new FakeStartupTracker();

        // Act — racing MarkReady and MarkFailed
        var readyTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => tracker.MarkReady()))
            .ToArray();
        var failTasks = Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => tracker.MarkFailed()))
            .ToArray();

        await Task.WhenAll(readyTasks.Concat(failTasks));

        // Assert — status should be one of the two valid end states
        tracker.Status.Should().BeOneOf(
            [StartupStatus.Ready, StartupStatus.Failed],
            "last writer wins; final state depends on thread scheduling");
    }

    /// <summary>
    /// [EDGE] Reading Status while writers are updating doesn't throw.
    /// </summary>
    [Fact]
    public async Task StartupTracker_concurrent_read_and_write_does_not_throw()
    {
        var tracker = new FakeStartupTracker();
        var statuses = new System.Collections.Concurrent.ConcurrentBag<StartupStatus>();

        // Act — readers and writers in parallel
        var writerTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                if (i % 2 == 0)
                {
                    tracker.MarkReady();
                }
                else
                {
                    tracker.MarkFailed();
                }
            }
        });

        var readerTask = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                statuses.Add(tracker.Status);
            }
        });

        await Task.WhenAll(writerTask, readerTask);

        // Assert — all observed statuses are valid enum values
        statuses.Should().OnlyContain(s =>
            s == StartupStatus.Starting ||
            s == StartupStatus.Ready ||
            s == StartupStatus.Failed);
    }

    // ────────────────────────────────────────────────────────────
    // [EDGE] Single dependency degraded, rest healthy
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] One component Degraded among 3 Healthy → liveness returns 200 (degraded is still alive).
    /// </summary>
    [Fact]
    public async Task Liveness_single_degraded_among_healthy_returns_200()
    {
        var provider = new FakeHealthReportProvider
        {
            HealthReport = new HealthReport(
                HealthStatus.Degraded,
                [
                    CreateSnapshot(new DependencyId("svc-a"), HealthState.Healthy, 0.99, 100, 1, 99),
                    CreateSnapshot(new DependencyId("svc-b"), HealthState.Degraded, 0.85, 100, 15, 85),
                    CreateSnapshot(new DependencyId("svc-c"), HealthState.Healthy, 1.0, 50, 0, 50),
                ],
                BaseTime),
        };
        await using var env = await CreateTestEnv(provider);

        var response = await env.Client.GetAsync("/healthz/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Degraded is still alive — K8s should NOT restart the pod");
    }

    // ─── Test Infrastructure ───────────────────────────────────

    private async Task<TestEnv> CreateTestEnv(
        HealthBoss.Core.IHealthReportProvider? provider = null,
        HealthBoss.Core.IStartupTracker? tracker = null,
        Action<HealthBossEndpointOptions>? configure = null)
    {
        provider ??= new FakeHealthReportProvider();
        tracker ??= new FakeStartupTracker();

        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<HealthBoss.Core.IHealthReportProvider>(provider);
        builder.Services.AddSingleton<HealthBoss.Core.IStartupTracker>(tracker);

        var app = builder.Build();
        app.MapHealthBossEndpoints(configure);
        await app.StartAsync();

        var client = app.GetTestClient();
        var env = new TestEnv(client, app);
        _disposables.Add(env);
        return env;
    }

    // ─── Helpers ──────────────────────────────────────────────

    private static HealthReport CreateAllCircuitOpenReport() => new(
        HealthStatus.Unhealthy,
        CreateAllCircuitOpenDependencies(),
        BaseTime);

    private static IReadOnlyList<DependencySnapshot> CreateAllCircuitOpenDependencies() =>
    [
        CreateSnapshot(DbDependency, HealthState.CircuitOpen, 0.3, 100, 70, 30),
        CreateSnapshot(CacheDependency, HealthState.CircuitOpen, 0.1, 50, 45, 5),
    ];

    private static DependencySnapshot CreateSnapshot(
        DependencyId id,
        HealthState state,
        double successRate,
        int totalSignals,
        int failureCount,
        int successCount) => new(
            id,
            state,
            new HealthAssessment(
                id,
                successRate,
                totalSignals,
                failureCount,
                successCount,
                TimeSpan.FromMinutes(5),
                BaseTime,
                state,
                state,
                null),
            BaseTime,
            state == HealthState.Healthy ? 0 : failureCount);

    private sealed class TestEnv(HttpClient client, WebApplication app) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
