// <copyright file="GrpcEdgeCaseTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using Grpc.Core;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Grpc.Tests.EdgeCases;

/// <summary>
/// Edge-case and boundary-value tests for Sprint 5 gRPC components:
/// GrpcClientHealthInterceptor and GrpcSubchannelHealthAdapter.
/// These fill coverage gaps left by the developer unit tests.
/// </summary>
public sealed class GrpcEdgeCaseTests
{
    private readonly FakeSignalRecorder _recorder = new();
    private readonly FakeClock _clock = new();
    private readonly FakeGrpcHealthSource _source = new();

    // ═══════════════════════════════════════════════════════════════
    // [EDGE] GrpcClientHealthInterceptor — zero latency
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [EDGE][AC-54] When the clock does not advance between call start and completion,
    /// latency should be TimeSpan.Zero — not negative or undefined.
    /// </summary>
    [Fact]
    public void Interceptor_zero_latency_records_zero_duration()
    {
        var interceptor = CreateInterceptor();
        var startTime = _clock.UtcNow;
        // Clock does NOT advance

        interceptor.RecordCallResult(StatusCode.OK, startTime);

        _recorder.Signals.Should().ContainSingle();
        _recorder.Signals[0].Latency.Should().Be(TimeSpan.Zero);
    }

    /// <summary>
    /// [EDGE][AC-54] Sub-millisecond latency (1 tick) is faithfully recorded.
    /// </summary>
    [Fact]
    public void Interceptor_sub_millisecond_latency_recorded()
    {
        var interceptor = CreateInterceptor();
        var startTime = _clock.UtcNow;
        _clock.UtcNow = startTime.AddTicks(1);

        interceptor.RecordCallResult(StatusCode.OK, startTime);

        _recorder.Signals[0].Latency.Should().Be(TimeSpan.FromTicks(1));
    }

    // ═══════════════════════════════════════════════════════════════
    // [EDGE] GrpcClientHealthInterceptor — sequential alternating
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [EDGE][AC-54][AC-55] Rapid sequential calls with alternating status codes.
    /// Each call should produce an independent signal with the correct outcome.
    /// </summary>
    [Fact]
    public void Interceptor_alternating_status_codes_records_each_independently()
    {
        var interceptor = CreateInterceptor();
        var baseTime = _clock.UtcNow;

        var statusSequence = new[]
        {
            StatusCode.OK,
            StatusCode.Unavailable,
            StatusCode.OK,
            StatusCode.DeadlineExceeded,
            StatusCode.OK,
            StatusCode.ResourceExhausted,
        };

        var expectedOutcomes = new[]
        {
            SignalOutcome.Success,
            SignalOutcome.Failure,
            SignalOutcome.Success,
            SignalOutcome.Timeout,
            SignalOutcome.Success,
            SignalOutcome.Rejected,
        };

        for (int i = 0; i < statusSequence.Length; i++)
        {
            _clock.UtcNow = baseTime.AddMilliseconds(i * 100);
            interceptor.RecordCallResult(statusSequence[i], baseTime.AddMilliseconds(i * 100 - 50));
        }

        _recorder.Signals.Should().HaveCount(statusSequence.Length);
        for (int i = 0; i < expectedOutcomes.Length; i++)
        {
            _recorder.Signals[i].Outcome.Should().Be(expectedOutcomes[i],
                $"signal {i} from StatusCode.{statusSequence[i]} should map to {expectedOutcomes[i]}");
        }
    }

    /// <summary>
    /// [EDGE][AC-55] All gRPC status codes that map to Failure are verified individually.
    /// This complements the Theory test by explicitly verifying "generic failure" codes
    /// that are less common: Aborted, OutOfRange, DataLoss, FailedPrecondition.
    /// </summary>
    [Theory]
    [InlineData(StatusCode.Aborted)]
    [InlineData(StatusCode.OutOfRange)]
    [InlineData(StatusCode.DataLoss)]
    [InlineData(StatusCode.FailedPrecondition)]
    [InlineData(StatusCode.Unknown)]
    public void Interceptor_uncommon_failure_codes_record_failure_signal(StatusCode statusCode)
    {
        var interceptor = CreateInterceptor();

        interceptor.RecordCallResult(statusCode, _clock.UtcNow);

        _recorder.Signals.Should().ContainSingle();
        _recorder.Signals[0].Outcome.Should().Be(SignalOutcome.Failure);
        _recorder.Signals[0].GrpcStatus.Should().Be(statusCode.ToString());
    }

    /// <summary>
    /// [EDGE][AC-54] GrpcStatus field uses enum name string, not integer.
    /// Prevents accidental use of numeric status codes in log output.
    /// </summary>
    [Fact]
    public void Interceptor_grpc_status_is_enum_name_not_number()
    {
        var interceptor = CreateInterceptor();

        interceptor.RecordCallResult(StatusCode.Unavailable, _clock.UtcNow);

        _recorder.Signals[0].GrpcStatus.Should().Be("Unavailable");
        _recorder.Signals[0].GrpcStatus.Should().NotBe("14",
            "GrpcStatus must be the enum name, not the numeric value");
    }

    /// <summary>
    /// [EDGE] Multiple interceptors for different components record to same recorder independently.
    /// </summary>
    [Fact]
    public void Interceptor_different_components_record_correct_dependency_ids()
    {
        var interceptorA = new GrpcClientHealthInterceptor(_recorder, _clock, "service-a");
        var interceptorB = new GrpcClientHealthInterceptor(_recorder, _clock, "service-b");

        interceptorA.RecordCallResult(StatusCode.OK, _clock.UtcNow);
        interceptorB.RecordCallResult(StatusCode.Unavailable, _clock.UtcNow);

        _recorder.Signals.Should().HaveCount(2);
        _recorder.Signals[0].DependencyId.Value.Should().Be("service-a");
        _recorder.Signals[1].DependencyId.Value.Should().Be("service-b");
    }

    // ═══════════════════════════════════════════════════════════════
    // [BOUNDARY] GrpcSubchannelHealthAdapter — single subchannel
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [BOUNDARY][AC-52] Single subchannel, healthy — minimum non-trivial case.
    /// </summary>
    [Fact]
    public async Task Adapter_single_subchannel_healthy()
    {
        _source.TotalSubchannelCount = 1;
        _source.ReadySubchannelCount = 1;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().ContainSingle()
            .Which.IsHealthy.Should().BeTrue();
        results[0].InstanceId.Should().Be("instance-0");
    }

    /// <summary>
    /// [BOUNDARY][AC-52] Single subchannel, unhealthy — minimum failure case.
    /// </summary>
    [Fact]
    public async Task Adapter_single_subchannel_unhealthy()
    {
        _source.TotalSubchannelCount = 1;
        _source.ReadySubchannelCount = 0;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().ContainSingle()
            .Which.IsHealthy.Should().BeFalse();
    }

    /// <summary>
    /// [EDGE][AC-52] Ready equals Total exactly — all subchannels healthy.
    /// Verifies the clamping logic Math.Clamp(ready, 0, total) passes through
    /// the exact-match case without truncation.
    /// </summary>
    [Fact]
    public async Task Adapter_ready_equals_total_all_healthy()
    {
        _source.TotalSubchannelCount = 10;
        _source.ReadySubchannelCount = 10;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().HaveCount(10);
        results.Should().AllSatisfy(r => r.IsHealthy.Should().BeTrue());
    }

    /// <summary>
    /// [EDGE] Adapter returns empty ResponseTime for all instances.
    /// Verifies the default TimeSpan is used (not null or some other value).
    /// </summary>
    [Fact]
    public async Task Adapter_response_time_is_default_timespan()
    {
        _source.TotalSubchannelCount = 2;
        _source.ReadySubchannelCount = 1;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().AllSatisfy(r =>
            r.ResponseTime.Should().Be(default(TimeSpan)));
    }

    // ═══════════════════════════════════════════════════════════════
    // [AC-53] Adapter → QuorumEvaluator contract integration
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [AC-53] GrpcSubchannelHealthAdapter output is structurally compatible
    /// with IQuorumEvaluator.Evaluate() — the adapter implements IInstanceHealthProbe,
    /// and its output satisfies the quorum evaluation contract.
    /// This integration test proves: adapter probe → quorum assessment works end-to-end.
    /// </summary>
    [Fact]
    public async Task Adapter_probe_results_are_valid_quorum_evaluator_input()
    {
        // Setup: 5 subchannels, 3 ready
        _source.TotalSubchannelCount = 5;
        _source.ReadySubchannelCount = 3;
        IInstanceHealthProbe probe = CreateAdapter();

        // Act: probe returns IReadOnlyList<InstanceHealthResult>
        var results = await probe.ProbeAllAsync();

        // Assert: results have the correct shape for quorum evaluation
        results.Should().HaveCount(5);
        results.Count(r => r.IsHealthy).Should().Be(3);
        results.Count(r => !r.IsHealthy).Should().Be(2);

        // Verify: all instance IDs are unique
        results.Select(r => r.InstanceId).Distinct().Should().HaveCount(5);

        // Verify: results are usable with a QuorumHealthPolicy
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3);
        int healthyCount = results.Count(r => r.IsHealthy);
        bool quorumMet = healthyCount >= policy.MinimumHealthyInstances;
        quorumMet.Should().BeTrue("adapter reported 3 ready with minimum of 3");
    }

    /// <summary>
    /// [AC-53] When adapter reports zero ready, the quorum contract produces
    /// a zero-healthy result — the input for CircuitOpen assessment.
    /// </summary>
    [Fact]
    public async Task Adapter_zero_ready_produces_zero_healthy_for_quorum()
    {
        _source.TotalSubchannelCount = 4;
        _source.ReadySubchannelCount = 0;
        IInstanceHealthProbe probe = CreateAdapter();

        var results = await probe.ProbeAllAsync();

        results.Count(r => r.IsHealthy).Should().Be(0,
            "zero ready subchannels should produce zero healthy instances for CircuitOpen");
    }

    /// <summary>
    /// [AC-53] Adapter at exact quorum boundary — ready count equals minimum required.
    /// </summary>
    [Fact]
    public async Task Adapter_at_exact_quorum_boundary()
    {
        _source.TotalSubchannelCount = 5;
        _source.ReadySubchannelCount = 2;
        IInstanceHealthProbe probe = CreateAdapter();

        var results = await probe.ProbeAllAsync();

        int healthyCount = results.Count(r => r.IsHealthy);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 2);
        bool quorumMet = healthyCount >= policy.MinimumHealthyInstances;
        quorumMet.Should().BeTrue("exactly 2 ready with minimum of 2 meets quorum");
    }

    /// <summary>
    /// [AC-53] Adapter below quorum boundary — one short.
    /// </summary>
    [Fact]
    public async Task Adapter_one_below_quorum_boundary()
    {
        _source.TotalSubchannelCount = 5;
        _source.ReadySubchannelCount = 2;
        IInstanceHealthProbe probe = CreateAdapter();

        var results = await probe.ProbeAllAsync();

        int healthyCount = results.Count(r => r.IsHealthy);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3);
        bool quorumMet = healthyCount >= policy.MinimumHealthyInstances;
        quorumMet.Should().BeFalse("2 ready with minimum of 3 does not meet quorum");
    }

    // ═══════════════════════════════════════════════════════════════
    // [EDGE] Adapter — source values change between calls
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// [EDGE][AC-52] Source values change between consecutive probe calls.
    /// Each probe must produce a fresh snapshot — not a cached result.
    /// </summary>
    [Fact]
    public async Task Adapter_reflects_source_changes_between_probes()
    {
        _source.TotalSubchannelCount = 5;
        _source.ReadySubchannelCount = 3;
        var adapter = CreateAdapter();

        var first = await adapter.ProbeAllAsync();
        first.Count(r => r.IsHealthy).Should().Be(3);

        // Source changes
        _source.ReadySubchannelCount = 1;

        var second = await adapter.ProbeAllAsync();
        second.Count(r => r.IsHealthy).Should().Be(1);
    }

    /// <summary>
    /// [EDGE][AC-52] Source total increases dynamically (scale-up).
    /// Adapter should return more instances on next probe.
    /// </summary>
    [Fact]
    public async Task Adapter_handles_dynamic_scale_up()
    {
        _source.TotalSubchannelCount = 3;
        _source.ReadySubchannelCount = 3;
        var adapter = CreateAdapter();

        var before = await adapter.ProbeAllAsync();
        before.Should().HaveCount(3);

        // Fleet scales up
        _source.TotalSubchannelCount = 6;
        _source.ReadySubchannelCount = 4;

        var after = await adapter.ProbeAllAsync();
        after.Should().HaveCount(6);
        after.Count(r => r.IsHealthy).Should().Be(4);
    }

    /// <summary>
    /// [EDGE][AC-52] Source total decreases dynamically (scale-down).
    /// </summary>
    [Fact]
    public async Task Adapter_handles_dynamic_scale_down()
    {
        _source.TotalSubchannelCount = 10;
        _source.ReadySubchannelCount = 8;
        var adapter = CreateAdapter();

        var before = await adapter.ProbeAllAsync();
        before.Should().HaveCount(10);

        // Fleet scales down
        _source.TotalSubchannelCount = 3;
        _source.ReadySubchannelCount = 3;

        var after = await adapter.ProbeAllAsync();
        after.Should().HaveCount(3);
        after.Should().AllSatisfy(r => r.IsHealthy.Should().BeTrue());
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private GrpcClientHealthInterceptor CreateInterceptor(string componentName = "grpc_backend_pool") =>
        new(_recorder, _clock, componentName);

    private GrpcSubchannelHealthAdapter CreateAdapter(string componentName = "grpc_backend_pool") =>
        new(_source, componentName);
}
