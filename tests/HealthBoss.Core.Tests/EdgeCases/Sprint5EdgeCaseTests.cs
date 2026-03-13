// <copyright file="Sprint5EdgeCaseTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using HealthBoss.Core.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using static HealthBoss.Core.Tests.EventSinkDispatcherTests;

namespace HealthBoss.Core.Tests.EdgeCases;

/// <summary>
/// Edge-case and boundary-value tests for Sprint 5 components:
/// QuorumEvaluator, EventSinkDispatcher, StructuredLogEventSink.
/// These fill coverage gaps left by the developer unit tests.
/// </summary>
public sealed class Sprint5EdgeCaseTests
{
    // ═══════════════════════════════════════════════════════════════
    // Shared fixtures
    // ═══════════════════════════════════════════════════════════════

    private static readonly DependencyId TestDep = new("edge-case-dep");
    private static readonly TenantId TestTenant = new("edge-case-tenant");

    private static readonly HealthEvent SampleHealthEvent = new(
        TestDep, HealthState.Healthy, HealthState.Degraded, TestFixtures.BaseTime);

    private static readonly TenantHealthEvent SampleTenantEvent = new(
        TestDep, TestTenant,
        TenantHealthStatus.Healthy, TenantHealthStatus.Degraded,
        SuccessRate: 0.75, OccurredAt: TestFixtures.BaseTime);

    private readonly FakeTimeProvider _timeProvider = new();
    private readonly ISystemClock _clock;

    public Sprint5EdgeCaseTests()
    {
        _timeProvider.SetUtcNow(TestFixtures.BaseTime);
        _clock = new SystemClock(_timeProvider);
    }

    private EventSinkDispatcher CreateDispatcher(
        IReadOnlyList<IHealthEventSink> sinks,
        EventSinkDispatcherOptions? options = null,
        ILogger<EventSinkDispatcher>? logger = null) =>
        new(sinks, options ?? new EventSinkDispatcherOptions(), _clock, logger);

    // ═══════════════════════════════════════════════════════════════
    // [BOUNDARY] QuorumEvaluator — exact boundary and scale
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [BOUNDARY][AC-38] MinRequired equals TotalInstances and all healthy.
    /// All nodes must be healthy for quorum — verifies >= not >.
    /// </summary>
    [Fact]
    public void Quorum_all_required_all_healthy_returns_Healthy()
    {
        var evaluator = new QuorumEvaluator();
        var results = CreateInstanceResults(healthyCount: 5, unhealthyCount: 0);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 5);

        var assessment = evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Healthy);
        assessment.QuorumMet.Should().BeTrue();
    }

    /// <summary>
    /// [BOUNDARY][AC-39] MinRequired exceeds total probed instances.
    /// Even if all probed instances are healthy, quorum is not met because min > count.
    /// </summary>
    [Fact]
    public void Quorum_min_exceeds_total_instances_returns_Degraded()
    {
        var evaluator = new QuorumEvaluator();
        var results = CreateInstanceResults(healthyCount: 5, unhealthyCount: 0);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 10);

        var assessment = evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Degraded);
        assessment.QuorumMet.Should().BeFalse();
        assessment.HealthyInstances.Should().Be(5);
    }

    /// <summary>
    /// [BOUNDARY][AC-38] Smallest possible quorum: 1 of 1.
    /// </summary>
    [Fact]
    public void Quorum_single_instance_single_required_returns_Healthy()
    {
        var evaluator = new QuorumEvaluator();
        var results = CreateInstanceResults(healthyCount: 1, unhealthyCount: 0);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 1);

        var assessment = evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Healthy);
        assessment.QuorumMet.Should().BeTrue();
        assessment.HealthyInstances.Should().Be(1);
        assessment.TotalInstances.Should().Be(1);
    }

    /// <summary>
    /// [BOUNDARY][AC-40] Smallest non-quorum: 0 of 1.
    /// </summary>
    [Fact]
    public void Quorum_single_instance_unhealthy_returns_CircuitOpen()
    {
        var evaluator = new QuorumEvaluator();
        var results = CreateInstanceResults(healthyCount: 0, unhealthyCount: 1);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 1);

        var assessment = evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.CircuitOpen);
        assessment.QuorumMet.Should().BeFalse();
    }

    /// <summary>
    /// [BOUNDARY] Large fleet (200 instances) counting accuracy.
    /// Verifies the counting loop handles large collections without overflow or off-by-one.
    /// </summary>
    [Fact]
    public void Quorum_large_fleet_200_instances_counts_correctly()
    {
        var evaluator = new QuorumEvaluator();
        var results = CreateInstanceResults(healthyCount: 150, unhealthyCount: 50);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 100);

        var assessment = evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Healthy);
        assessment.QuorumMet.Should().BeTrue();
        assessment.HealthyInstances.Should().Be(150);
        assessment.TotalInstances.Should().Be(200);
    }

    /// <summary>
    /// [BOUNDARY] Large fleet at exact threshold: 100 healthy, need 100.
    /// </summary>
    [Fact]
    public void Quorum_large_fleet_exact_boundary_returns_Healthy()
    {
        var evaluator = new QuorumEvaluator();
        var results = CreateInstanceResults(healthyCount: 100, unhealthyCount: 100);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 100);

        var assessment = evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Healthy);
        assessment.QuorumMet.Should().BeTrue();
    }

    /// <summary>
    /// [BOUNDARY] Large fleet one below threshold: 99 healthy, need 100.
    /// </summary>
    [Fact]
    public void Quorum_large_fleet_one_below_boundary_returns_Degraded()
    {
        var evaluator = new QuorumEvaluator();
        var results = CreateInstanceResults(healthyCount: 99, unhealthyCount: 101);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 100);

        var assessment = evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Degraded);
        assessment.QuorumMet.Should().BeFalse();
    }

    /// <summary>
    /// [BOUNDARY][AC-41] TotalExpectedInstances equals probed count — uses that value.
    /// </summary>
    [Fact]
    public void Quorum_TotalExpected_equals_probed_uses_probed()
    {
        var evaluator = new QuorumEvaluator();
        var results = CreateInstanceResults(healthyCount: 3, unhealthyCount: 2);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3, TotalExpectedInstances: 5);

        var assessment = evaluator.Evaluate(results, policy);

        assessment.TotalInstances.Should().Be(5);
    }

    // ═══════════════════════════════════════════════════════════════
    // [EDGE] Rate limiter — exact window boundary
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [EDGE][AC-51] Rate limiter window resets at exactly TicksPerSecond.
    /// SinkRateLimiter uses >= comparison: nowTicks - windowStart >= TicksPerSecond.
    /// An event arriving at exactly the boundary should reset the window.
    /// </summary>
    [Fact]
    public async Task RateLimiter_exact_one_second_boundary_resets_window()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 1);
        var dispatcher = CreateDispatcher([sink], options);

        // First event consumes the limit
        await dispatcher.DispatchAsync(SampleHealthEvent);
        sink.HealthEvents.Should().HaveCount(1);

        // Second event in same window is dropped
        await dispatcher.DispatchAsync(SampleHealthEvent);
        sink.HealthEvents.Should().HaveCount(1);

        // Advance clock by exactly 1 second (TicksPerSecond)
        _timeProvider.Advance(TimeSpan.FromSeconds(1));

        // Third event should succeed — window has reset
        await dispatcher.DispatchAsync(SampleHealthEvent);
        sink.HealthEvents.Should().HaveCount(2);
    }

    /// <summary>
    /// [EDGE][AC-51] Rate limiter window does NOT reset 1 tick before the boundary.
    /// </summary>
    [Fact]
    public async Task RateLimiter_one_tick_before_boundary_still_limited()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 1);
        var dispatcher = CreateDispatcher([sink], options);

        await dispatcher.DispatchAsync(SampleHealthEvent);

        // Advance by 1 second minus 1 tick — just under the window
        _timeProvider.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1));

        await dispatcher.DispatchAsync(SampleHealthEvent);

        // Still limited — only 1 event passed
        sink.HealthEvents.Should().HaveCount(1);
    }

    /// <summary>
    /// [EDGE][AC-51] Rate limiter window reset works for tenant events too.
    /// Verifies symmetric behavior between DispatchAsync and DispatchTenantEventAsync
    /// sharing the same per-sink rate limiter.
    /// </summary>
    [Fact]
    public async Task RateLimiter_tenant_events_also_reset_after_window()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 2);
        var dispatcher = CreateDispatcher([sink], options);

        // Use up rate limit with tenant events
        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);
        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);
        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent); // Dropped

        // Advance past window
        _timeProvider.Advance(TimeSpan.FromSeconds(1.1));

        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);

        // 2 from first window + 1 from second = 3
        sink.Events.Should().HaveCount(3);
    }

    /// <summary>
    /// [EDGE][AC-51] Mixed health and tenant events share the same rate limiter counter.
    /// A health event and a tenant event in the same window both consume from the same bucket.
    /// </summary>
    [Fact]
    public async Task RateLimiter_mixed_event_types_share_counter()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 2);
        var dispatcher = CreateDispatcher([sink], options);

        // One health event
        await dispatcher.DispatchAsync(SampleHealthEvent);
        // One tenant event — both consume from same rate limiter
        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);
        // Third event — should be dropped regardless of type
        await dispatcher.DispatchAsync(SampleHealthEvent);

        sink.HealthEvents.Should().HaveCount(1);
        sink.Events.Should().HaveCount(1);
    }

    // ═══════════════════════════════════════════════════════════════
    // [EDGE] Concurrent dispatch with rate limiting
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [EDGE][AC-51] Concurrent dispatch where rate limit is actively constraining.
    /// Multiple threads race to dispatch; only MaxEventsPerSecondPerSink should pass.
    /// </summary>
    [Fact]
    public async Task Concurrent_dispatch_with_tight_rate_limit_does_not_exceed_limit()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 10);
        var dispatcher = CreateDispatcher([sink], options);

        // Fire 50 concurrent dispatches with a rate limit of 10
        var tasks = Enumerable.Range(0, 50)
            .Select(_ => dispatcher.DispatchAsync(SampleHealthEvent));

        await Task.WhenAll(tasks);

        // At most 10 should pass the rate limiter (all in same clock tick)
        sink.HealthEvents.Should().HaveCountLessOrEqualTo(10);
        sink.HealthEvents.Should().HaveCountGreaterOrEqualTo(1);
    }

    // ═══════════════════════════════════════════════════════════════
    // [EDGE] All sinks failing — dispatcher survives
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [EDGE][AC-49] When ALL registered sinks throw, the dispatcher must not
    /// propagate any exception — it swallows all sink failures.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_all_sinks_failing_does_not_throw()
    {
        var failing1 = new ThrowingSink();
        var failing2 = new ThrowingSink();
        var failing3 = new ThrowingSink();
        var dispatcher = CreateDispatcher([failing1, failing2, failing3]);

        // Should complete without exception
        await dispatcher.DispatchAsync(SampleHealthEvent);
    }

    /// <summary>
    /// [EDGE][AC-49] When ALL sinks throw on tenant event, dispatcher doesn't throw.
    /// </summary>
    [Fact]
    public async Task DispatchTenantEventAsync_all_sinks_failing_does_not_throw()
    {
        var failing1 = new ThrowingSink();
        var failing2 = new ThrowingSink();
        var dispatcher = CreateDispatcher([failing1, failing2]);

        await dispatcher.DispatchTenantEventAsync(SampleTenantEvent);
    }

    /// <summary>
    /// [EDGE][AC-49] All sinks failing logs a warning for each failure.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_all_sinks_failing_logs_warning_per_sink()
    {
        var logger = new RecordingLogger<EventSinkDispatcher>();
        var dispatcher = CreateDispatcher(
            [new ThrowingSink(), new ThrowingSink(), new ThrowingSink()],
            logger: logger);

        await dispatcher.DispatchAsync(SampleHealthEvent);

        logger.Entries.Where(e => e.Level == LogLevel.Warning).Should().HaveCount(3);
    }

    /// <summary>
    /// [EDGE][AC-49] Sink that returns a faulted Task (async exception path).
    /// Verifies the dispatcher handles both sync throws and async faults.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_async_faulting_sink_is_isolated()
    {
        var goodSink = new FakeHealthEventSink();
        var asyncFault = new AsyncFaultingSink();
        var dispatcher = CreateDispatcher([asyncFault, goodSink]);

        await dispatcher.DispatchAsync(SampleHealthEvent);

        goodSink.HealthEvents.Should().ContainSingle();
    }

    // ═══════════════════════════════════════════════════════════════
    // [COVERAGE] StructuredLogEventSink — all state transition pairs
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [COVERAGE] All valid HealthState transitions log at Information level.
    /// The developer tests cover Healthy→Degraded and Degraded→CircuitOpen.
    /// This covers the remaining transitions: CircuitOpen→Degraded (recovery started),
    /// CircuitOpen→Healthy (direct recovery), Degraded→Healthy (recovery).
    /// </summary>
    [Theory]
    [InlineData(HealthState.CircuitOpen, HealthState.Degraded)]
    [InlineData(HealthState.CircuitOpen, HealthState.Healthy)]
    [InlineData(HealthState.Degraded, HealthState.Healthy)]
    [InlineData(HealthState.Healthy, HealthState.CircuitOpen)]
    [InlineData(HealthState.Degraded, HealthState.CircuitOpen)]
    public async Task StructuredLogSink_all_state_transitions_log_at_Information(
        HealthState previous, HealthState newState)
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = new HealthEvent(TestDep, previous, newState, TestFixtures.BaseTime);

        await sink.OnHealthStateChanged(evt);

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information);
    }

    /// <summary>
    /// [COVERAGE] StructuredLogEventSink correctly logs dependency ID and both states.
    /// </summary>
    [Theory]
    [InlineData(HealthState.CircuitOpen, HealthState.Healthy)]
    [InlineData(HealthState.Degraded, HealthState.CircuitOpen)]
    public async Task StructuredLogSink_transition_message_contains_both_states(
        HealthState previous, HealthState newState)
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = new HealthEvent(TestDep, previous, newState, TestFixtures.BaseTime);

        await sink.OnHealthStateChanged(evt);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Message.Should().Contain(previous.ToString());
        entry.Message.Should().Contain(newState.ToString());
    }

    /// <summary>
    /// [COVERAGE] All TenantHealthStatus transitions use correct log level.
    /// Covers transitions not in developer tests: Unavailable→Healthy, Unavailable→Degraded.
    /// </summary>
    [Theory]
    [InlineData(TenantHealthStatus.Unavailable, TenantHealthStatus.Healthy, LogLevel.Information)]
    [InlineData(TenantHealthStatus.Unavailable, TenantHealthStatus.Degraded, LogLevel.Warning)]
    [InlineData(TenantHealthStatus.Healthy, TenantHealthStatus.Unavailable, LogLevel.Error)]
    [InlineData(TenantHealthStatus.Degraded, TenantHealthStatus.Healthy, LogLevel.Information)]
    public async Task StructuredLogSink_tenant_transition_uses_correct_log_level(
        TenantHealthStatus previous, TenantHealthStatus newStatus, LogLevel expectedLevel)
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);
        var evt = new TenantHealthEvent(
            TestDep, TestTenant, previous, newStatus,
            SuccessRate: 0.50, OccurredAt: TestFixtures.BaseTime);

        await sink.OnTenantHealthChanged(evt);

        logger.Entries.Should().ContainSingle(e => e.Level == expectedLevel);
    }

    /// <summary>
    /// [EDGE] StructuredLogEventSink null health event throws ArgumentNullException.
    /// Developer tests cover constructor null; this covers method-level null.
    /// </summary>
    [Fact]
    public async Task StructuredLogSink_null_health_event_throws()
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);

        Func<Task> act = () => sink.OnHealthStateChanged(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// [EDGE] StructuredLogEventSink null tenant event throws ArgumentNullException.
    /// </summary>
    [Fact]
    public async Task StructuredLogSink_null_tenant_event_throws()
    {
        var logger = new RecordingLogger<StructuredLogEventSink>();
        var sink = new StructuredLogEventSink(logger);

        Func<Task> act = () => sink.OnTenantHealthChanged(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ═══════════════════════════════════════════════════════════════
    // [EDGE] Dispatcher: negative rate limit
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [EDGE] Negative MaxEventsPerSecondPerSink also rejected (not just zero).
    /// </summary>
    [Fact]
    public void Constructor_negative_rate_limit_throws()
    {
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: -5);

        Action act = () => new EventSinkDispatcher([], options, _clock);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// [EDGE] MaxEventsPerSecondPerSink = 1 is the minimum valid value.
    /// </summary>
    [Fact]
    public async Task Dispatcher_rate_limit_one_allows_single_event_per_window()
    {
        var sink = new FakeHealthEventSink();
        var options = new EventSinkDispatcherOptions(MaxEventsPerSecondPerSink: 1);
        var dispatcher = CreateDispatcher([sink], options);

        await dispatcher.DispatchAsync(SampleHealthEvent);
        await dispatcher.DispatchAsync(SampleHealthEvent); // Dropped

        _timeProvider.Advance(TimeSpan.FromSeconds(2));

        await dispatcher.DispatchAsync(SampleHealthEvent);
        await dispatcher.DispatchAsync(SampleHealthEvent); // Dropped

        sink.HealthEvents.Should().HaveCount(2);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private static List<InstanceHealthResult> CreateInstanceResults(
        int healthyCount, int unhealthyCount)
    {
        var results = new List<InstanceHealthResult>();
        for (int i = 0; i < healthyCount; i++)
            results.Add(new InstanceHealthResult($"instance-{i + 1}", IsHealthy: true));
        for (int i = 0; i < unhealthyCount; i++)
            results.Add(new InstanceHealthResult($"instance-{healthyCount + i + 1}", IsHealthy: false));
        return results;
    }

    /// <summary>Sink that always throws synchronously to test error isolation.</summary>
    private sealed class ThrowingSink : IHealthEventSink
    {
        public Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default)
            => throw new InvalidOperationException("Sync sink failure");

        public Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default)
            => throw new InvalidOperationException("Sync sink failure");
    }

    /// <summary>Sink that returns a faulted Task (async exception path).</summary>
    private sealed class AsyncFaultingSink : IHealthEventSink
    {
        public Task OnHealthStateChanged(HealthEvent healthEvent, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("Async sink failure"));

        public Task OnTenantHealthChanged(TenantHealthEvent tenantEvent, CancellationToken ct = default)
            => Task.FromException(new InvalidOperationException("Async sink failure"));
    }
}
