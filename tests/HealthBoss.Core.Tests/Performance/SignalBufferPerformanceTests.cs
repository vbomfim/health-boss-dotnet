// <copyright file="SignalBufferPerformanceTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Diagnostics;
using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests.Performance;

/// <summary>
/// Performance baseline tests for SignalBuffer operations.
/// These establish throughput expectations for Sprint 1.
/// </summary>
public sealed class SignalBufferPerformanceTests
{
    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;

    public SignalBufferPerformanceTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
    }

    /// <summary>
    /// [PERF] Baseline: Record throughput for 10K signals on a single thread.
    /// Establishes a performance floor for future regression detection.
    /// </summary>
    [Fact]
    public void Record_10K_signals_single_thread_under_100ms()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 20_000);
        const int signalCount = 10_000;

        // Warm up
        for (int i = 0; i < 100; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddMilliseconds(i)));
        }

        buffer = new SignalBuffer(_clock, maxCapacity: 20_000); // Fresh buffer

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < signalCount; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddMilliseconds(i)));
        }

        sw.Stop();

        buffer.Count.Should().Be(signalCount);
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "Recording 10K signals should complete well under 100ms on any modern hardware");
    }

    /// <summary>
    /// [PERF] Baseline: GetSignals query on 100K buffered signals.
    /// </summary>
    [Fact]
    public void GetSignals_from_100K_buffer_under_500ms()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 200_000);
        const int signalCount = 100_000;

        // Populate buffer — all signals within 5-minute window
        for (int i = 0; i < signalCount; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddMilliseconds(i)));
        }

        buffer.Count.Should().Be(signalCount);

        var sw = Stopwatch.StartNew();

        var signals = buffer.GetSignals(TimeSpan.FromMinutes(5));

        sw.Stop();

        signals.Should().HaveCount(signalCount);
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "Querying 100K signals should complete under 500ms");
    }

    /// <summary>
    /// [PERF] Baseline: Trim throughput with 50K signals to remove.
    /// </summary>
    [Fact]
    public void Trim_50K_signals_under_200ms()
    {
        var buffer = new SignalBuffer(_clock, maxCapacity: 100_000);
        const int totalSignals = 100_000;

        // Populate: first 50K at T=0, next 50K at T=3min
        for (int i = 0; i < totalSignals / 2; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddMilliseconds(i)));
        }

        for (int i = 0; i < totalSignals / 2; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddMinutes(3).AddMilliseconds(i)));
        }

        buffer.Count.Should().Be(totalSignals);

        var sw = Stopwatch.StartNew();

        // Trim the first 50K signals
        buffer.Trim(_clock.UtcNow.AddMinutes(1));

        sw.Stop();

        buffer.Count.Should().Be(totalSignals / 2);
        sw.ElapsedMilliseconds.Should().BeLessThan(200,
            "Trimming 50K signals should complete under 200ms");
    }

    /// <summary>
    /// [PERF] Baseline: Capacity-eviction throughput — Record with small capacity
    /// under heavy write load (each write triggers eviction).
    /// </summary>
    [Fact]
    public void Capacity_eviction_10K_writes_with_capacity_100_under_100ms()
    {
        const int capacity = 100;
        var buffer = new SignalBuffer(_clock, maxCapacity: capacity);
        const int writeCount = 10_000;

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < writeCount; i++)
        {
            buffer.Record(TestFixtures.CreateSignal(
                SignalOutcome.Success,
                timestamp: _clock.UtcNow.AddMilliseconds(i)));
        }

        sw.Stop();

        buffer.Count.Should().Be(capacity);
        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "10K writes with eviction should complete under 100ms");
    }

    /// <summary>
    /// [PERF] PolicyEvaluator throughput with 10K signals.
    /// Evaluate should be fast even with large signal lists.
    /// </summary>
    [Fact]
    public void PolicyEvaluator_10K_signals_under_50ms()
    {
        var evaluator = new PolicyEvaluator();
        var policy = TestFixtures.DefaultPolicy;

        var signals = TestFixtures.CreateSignals(
            successCount: 8_000,
            failureCount: 2_000);

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 100; i++)
        {
            _ = evaluator.Evaluate(signals, policy, HealthState.Healthy, TestFixtures.BaseTime);
        }

        sw.Stop();

        // 100 evaluations of 10K signals
        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "100 evaluations of 10K signals should complete under 500ms");
    }
}
