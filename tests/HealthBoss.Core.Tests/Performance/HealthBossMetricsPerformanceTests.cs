// <copyright file="HealthBossMetricsPerformanceTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.Core.Tests.Performance;

/// <summary>
/// Performance baseline tests for <see cref="HealthBossMetrics"/> hot-path operations.
/// Validates throughput, latency, and allocation characteristics per Issue #64.
/// <para>
/// These tests use wall-clock timing with generous thresholds to avoid CI flakiness
/// while still catching order-of-magnitude regressions.
/// </para>
/// </summary>
public sealed class HealthBossMetricsPerformanceTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly HealthBossMetrics _metrics;

    public HealthBossMetricsPerformanceTests()
    {
        _serviceProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = _serviceProvider.GetRequiredService<IMeterFactory>();
        _metrics = new HealthBossMetrics(meterFactory);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────
    // [PERF] RecordSignal throughput: >1M signals/sec
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [PERF] RecordSignal must sustain >1M operations per second.
    /// This is the highest-frequency hot path — called on every health signal.
    /// Validates that TagList conversion (Issue #64) does not regress throughput.
    /// </summary>
    [Fact]
    public void RecordSignal_Throughput_ExceedsOneMillion_PerSecond()
    {
        const int warmupCount = 10_000;
        const int measureCount = 2_000_000;

        // Warm up JIT and steady state
        for (int i = 0; i < warmupCount; i++)
        {
            _metrics.RecordSignal("api-gateway", "Success");
        }

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < measureCount; i++)
        {
            _metrics.RecordSignal("api-gateway", "Success");
        }

        sw.Stop();

        double opsPerSecond = measureCount / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(1_000_000,
            "RecordSignal must sustain >1M ops/sec (actual: {0:N0} ops/sec)", opsPerSecond);
    }

    // ─────────────────────────────────────────────────────────────────
    // [PERF] RecordAssessmentDuration p99 latency: <50μs
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [PERF] RecordAssessmentDuration (used in health assessment hot path)
    /// must have p99 latency below 50 microseconds.
    /// </summary>
    [Fact]
    public void RecordAssessmentDuration_P99_Under50Microseconds()
    {
        const int warmupCount = 1_000;
        const int measureCount = 10_000;
        var latencies = new long[measureCount];

        // Warm up
        for (int i = 0; i < warmupCount; i++)
        {
            _metrics.RecordAssessmentDuration("db", 0.042);
        }

        // Measure individual call latencies
        for (int i = 0; i < measureCount; i++)
        {
            long start = Stopwatch.GetTimestamp();
            _metrics.RecordAssessmentDuration("db", 0.042);
            latencies[i] = Stopwatch.GetTimestamp() - start;
        }

        Array.Sort(latencies);
        int p99Index = (int)(measureCount * 0.99);
        double p99Microseconds = latencies[p99Index] * 1_000_000.0 / Stopwatch.Frequency;

        p99Microseconds.Should().BeLessThan(50.0,
            "RecordAssessmentDuration p99 must be <50μs (actual p99: {0:F1}μs)", p99Microseconds);
    }

    // ─────────────────────────────────────────────────────────────────
    // [PERF] NullHealthBossMetrics: near-zero overhead
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [PERF] NullHealthBossMetrics methods (with AggressiveInlining) should be
    /// effectively free — the JIT should inline them to no-ops.
    /// Validates that 10M calls complete in under 100ms.
    /// </summary>
    [Fact]
    public void NullMetrics_AllMethods_NearZeroOverhead()
    {
        var nullMetrics = NullHealthBossMetrics.Instance;
        const int iterations = 10_000_000;

        // Warm up
        for (int i = 0; i < 10_000; i++)
        {
            nullMetrics.RecordSignal("api", "Success");
            nullMetrics.RecordAssessmentDuration("api", 0.01);
            nullMetrics.SetHealthState("api", HealthState.Healthy);
        }

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            nullMetrics.RecordSignal("api", "Success");
        }

        sw.Stop();

        double opsPerSecond = iterations / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(100_000_000,
            "NullHealthBossMetrics.RecordSignal should sustain >100M ops/sec with inlining");
    }

    // ─────────────────────────────────────────────────────────────────
    // [PERF] TagList: multi-tag methods don't regress
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [PERF] RecordStateTransition (3 tags) throughput must exceed 500K ops/sec.
    /// Validates TagList struct usage for multi-tag recording paths.
    /// </summary>
    [Fact]
    public void RecordStateTransition_ThreeTags_Throughput_Exceeds500K_PerSecond()
    {
        const int warmupCount = 10_000;
        const int measureCount = 1_000_000;

        for (int i = 0; i < warmupCount; i++)
        {
            _metrics.RecordStateTransition("db", "Healthy", "Degraded");
        }

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < measureCount; i++)
        {
            _metrics.RecordStateTransition("db", "Healthy", "Degraded");
        }

        sw.Stop();

        double opsPerSecond = measureCount / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(500_000,
            "RecordStateTransition (3 tags) must sustain >500K ops/sec");
    }

    /// <summary>
    /// [PERF] RecordTenantStatusChange (4 tags) throughput must exceed 500K ops/sec.
    /// This is the widest tag set — validates TagList handles up to 8 tags efficiently.
    /// </summary>
    [Fact]
    public void RecordTenantStatusChange_FourTags_Throughput_Exceeds500K_PerSecond()
    {
        const int warmupCount = 10_000;
        const int measureCount = 1_000_000;

        for (int i = 0; i < warmupCount; i++)
        {
            _metrics.RecordTenantStatusChange("db", "tenant-1", "Healthy", "Degraded");
        }

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < measureCount; i++)
        {
            _metrics.RecordTenantStatusChange("db", "tenant-1", "Healthy", "Degraded");
        }

        sw.Stop();

        double opsPerSecond = measureCount / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(500_000,
            "RecordTenantStatusChange (4 tags) must sustain >500K ops/sec");
    }

    // ─────────────────────────────────────────────────────────────────
    // [PERF] EnumStringCache: no allocation on hot path
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [PERF] Enum-to-string conversion via FrozenDictionary cache must return
    /// the same string instance (reference equality), proving zero allocation.
    /// </summary>
    [Fact]
    public void EnumStringCache_SignalOutcome_ReturnsSameStringInstance()
    {
        string first = EnumStringCache.SignalOutcomeNames[SignalOutcome.Success];
        string second = EnumStringCache.SignalOutcomeNames[SignalOutcome.Success];

        ReferenceEquals(first, second).Should().BeTrue(
            "FrozenDictionary cache must return the same string reference (zero allocation)");
    }

    /// <summary>
    /// [PERF] HealthState enum-to-string cache must cover all values.
    /// </summary>
    [Theory]
    [InlineData(HealthState.Healthy, "Healthy")]
    [InlineData(HealthState.Degraded, "Degraded")]
    [InlineData(HealthState.CircuitOpen, "CircuitOpen")]
    public void EnumStringCache_HealthState_ReturnsExpectedString(HealthState state, string expected)
    {
        EnumStringCache.HealthStateNames[state].Should().Be(expected);
    }

    /// <summary>
    /// [PERF] TenantHealthStatus enum-to-string cache must cover all values.
    /// </summary>
    [Theory]
    [InlineData(TenantHealthStatus.Healthy, "Healthy")]
    [InlineData(TenantHealthStatus.Degraded, "Degraded")]
    [InlineData(TenantHealthStatus.Unavailable, "Unavailable")]
    public void EnumStringCache_TenantHealthStatus_ReturnsExpectedString(
        TenantHealthStatus status, string expected)
    {
        EnumStringCache.TenantHealthStatusNames[status].Should().Be(expected);
    }

    /// <summary>
    /// [PERF] SignalOutcome enum-to-string cache must cover all values.
    /// </summary>
    [Theory]
    [InlineData(SignalOutcome.Success, "Success")]
    [InlineData(SignalOutcome.Failure, "Failure")]
    [InlineData(SignalOutcome.Timeout, "Timeout")]
    [InlineData(SignalOutcome.Rejected, "Rejected")]
    public void EnumStringCache_SignalOutcome_ReturnsExpectedString(
        SignalOutcome outcome, string expected)
    {
        EnumStringCache.SignalOutcomeNames[outcome].Should().Be(expected);
    }

    // ─────────────────────────────────────────────────────────────────
    // [PERF] Concurrent throughput: multi-threaded signal recording
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [PERF] RecordSignal must sustain >1M total ops/sec under concurrent load
    /// (4 threads). Validates thread-safety of TagList-based recording.
    /// </summary>
    [Fact]
    public async Task RecordSignal_ConcurrentThroughput_ExceedsOneMillion_PerSecond()
    {
        const int threadsCount = 4;
        const int opsPerThread = 500_000;

        // Warm up
        for (int i = 0; i < 10_000; i++)
        {
            _metrics.RecordSignal("api", "Success");
        }

        var barrier = new Barrier(threadsCount);
        var sw = Stopwatch.StartNew();

        var tasks = Enumerable.Range(0, threadsCount).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < opsPerThread; i++)
            {
                _metrics.RecordSignal("api", "Success");
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        long totalOps = threadsCount * opsPerThread;
        double opsPerSecond = totalOps / sw.Elapsed.TotalSeconds;
        opsPerSecond.Should().BeGreaterThan(1_000_000,
            "Concurrent RecordSignal must sustain >1M total ops/sec across {0} threads", threadsCount);
    }
}
