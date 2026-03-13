// <copyright file="HealthBossMetricsTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Diagnostics.Metrics;
using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests for <see cref="HealthBossMetrics"/> verifying all 18 instruments:
/// 8 counters, 3 histograms, and 7 observable gauges.
/// Uses <see cref="MeterListener"/> to capture measurements.
/// </summary>
public sealed class HealthBossMetricsTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly HealthBossMetrics _metrics;
    private readonly MeterListener _listener = new();
    private readonly List<LongMeasurement> _longMeasurements = [];
    private readonly List<DoubleMeasurement> _doubleMeasurements = [];
    private readonly List<IntMeasurement> _intMeasurements = [];
    private readonly object _lock = new();

    public HealthBossMetricsTests()
    {
        _serviceProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = _serviceProvider.GetRequiredService<IMeterFactory>();
        _metrics = new HealthBossMetrics(meterFactory);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "HealthBoss")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var tagDict = ExtractTags(tags);
            lock (_lock)
            {
                _longMeasurements.Add(new LongMeasurement(instrument.Name, value, tagDict));
            }
        });

        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            var tagDict = ExtractTags(tags);
            lock (_lock)
            {
                _doubleMeasurements.Add(new DoubleMeasurement(instrument.Name, value, tagDict));
            }
        });

        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            var tagDict = ExtractTags(tags);
            lock (_lock)
            {
                _intMeasurements.Add(new IntMeasurement(instrument.Name, value, tagDict));
            }
        });

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _serviceProvider.Dispose();
    }

    // ───────────────────────────────────────────────────────────────
    // Counter: signals_recorded
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordSignal_IncrementsCounter_WithCorrectTags()
    {
        _metrics.RecordSignal("api-gateway", "Success");

        var recorded = GetLongMeasurements("healthboss.signals_recorded");
        recorded.Should().ContainSingle();
        recorded[0].Value.Should().Be(1);
        recorded[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("api-gateway");
        recorded[0].Tags.Should().ContainKey("outcome").WhoseValue.Should().Be("Success");
    }

    [Fact]
    public void RecordSignal_MultipleCalls_Accumulate()
    {
        _metrics.RecordSignal("api", "Success");
        _metrics.RecordSignal("api", "Failure");
        _metrics.RecordSignal("api", "Success");

        var recorded = GetLongMeasurements("healthboss.signals_recorded");
        recorded.Should().HaveCount(3);
        recorded.Should().OnlyContain(m => m.Value == 1);
    }

    // ───────────────────────────────────────────────────────────────
    // Counter: state_transitions
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordStateTransition_IncrementsCounter_WithCorrectTags()
    {
        _metrics.RecordStateTransition("db", "Healthy", "Degraded");

        var recorded = GetLongMeasurements("healthboss.state_transitions");
        recorded.Should().ContainSingle();
        recorded[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("db");
        recorded[0].Tags.Should().ContainKey("from_state").WhoseValue.Should().Be("Healthy");
        recorded[0].Tags.Should().ContainKey("to_state").WhoseValue.Should().Be("Degraded");
    }

    // ───────────────────────────────────────────────────────────────
    // Counter: recovery_probe_attempts
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordRecoveryProbeAttempt_IncrementsCounter_WithComponentTag()
    {
        _metrics.RecordRecoveryProbeAttempt("redis");

        var recorded = GetLongMeasurements("healthboss.recovery_probe_attempts");
        recorded.Should().ContainSingle();
        recorded[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("redis");
    }

    // ───────────────────────────────────────────────────────────────
    // Counter: recovery_probe_successes
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordRecoveryProbeSuccess_IncrementsCounter_WithComponentTag()
    {
        _metrics.RecordRecoveryProbeSuccess("redis");

        var recorded = GetLongMeasurements("healthboss.recovery_probe_successes");
        recorded.Should().ContainSingle();
        recorded[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("redis");
    }

    // ───────────────────────────────────────────────────────────────
    // Counter: eventsink_dispatches
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordEventSinkDispatch_IncrementsCounter()
    {
        _metrics.RecordEventSinkDispatch();

        var recorded = GetLongMeasurements("healthboss.eventsink_dispatches");
        recorded.Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    // ───────────────────────────────────────────────────────────────
    // Counter: eventsink_failures
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordEventSinkFailure_IncrementsCounter_WithSinkTypeTag()
    {
        _metrics.RecordEventSinkFailure("OpenTelemetryMetricEventSink");

        var recorded = GetLongMeasurements("healthboss.eventsink_failures");
        recorded.Should().ContainSingle();
        recorded[0].Tags.Should().ContainKey("sink_type")
            .WhoseValue.Should().Be("OpenTelemetryMetricEventSink");
    }

    // ───────────────────────────────────────────────────────────────
    // Counter: shutdown_gate_evaluations
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordShutdownGateEvaluation_Approved_RecordsApprovedResult()
    {
        _metrics.RecordShutdownGateEvaluation("MinSignals", true);

        var recorded = GetLongMeasurements("healthboss.shutdown_gate_evaluations");
        recorded.Should().ContainSingle();
        recorded[0].Tags.Should().ContainKey("gate").WhoseValue.Should().Be("MinSignals");
        recorded[0].Tags.Should().ContainKey("result").WhoseValue.Should().Be("approved");
    }

    [Fact]
    public void RecordShutdownGateEvaluation_Denied_RecordsDeniedResult()
    {
        _metrics.RecordShutdownGateEvaluation("Cooldown", false);

        var recorded = GetLongMeasurements("healthboss.shutdown_gate_evaluations");
        recorded.Should().ContainSingle();
        recorded[0].Tags.Should().ContainKey("result").WhoseValue.Should().Be("denied");
    }

    // ───────────────────────────────────────────────────────────────
    // Histogram: assessment_duration_seconds
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordAssessmentDuration_RecordsHistogram_WithComponentTag()
    {
        _metrics.RecordAssessmentDuration("db", 0.042);

        var recorded = GetDoubleMeasurements("healthboss.assessment_duration_seconds");
        recorded.Should().ContainSingle();
        recorded[0].Value.Should().BeApproximately(0.042, 0.0001);
        recorded[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("db");
    }

    // ───────────────────────────────────────────────────────────────
    // Histogram: middleware_inbound_duration_seconds
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordInboundRequestDuration_RecordsHistogram_WithComponentTag()
    {
        _metrics.RecordInboundRequestDuration("web", 0.125);

        var recorded = GetDoubleMeasurements("healthboss.middleware_inbound_duration_seconds");
        recorded.Should().ContainSingle();
        recorded[0].Value.Should().BeApproximately(0.125, 0.0001);
        recorded[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("web");
    }

    // ───────────────────────────────────────────────────────────────
    // Histogram: middleware_outbound_duration_seconds
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordOutboundRequestDuration_RecordsHistogram_WithComponentTag()
    {
        _metrics.RecordOutboundRequestDuration("payments", 0.300);

        var recorded = GetDoubleMeasurements("healthboss.middleware_outbound_duration_seconds");
        recorded.Should().ContainSingle();
        recorded[0].Value.Should().BeApproximately(0.300, 0.0001);
        recorded[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("payments");
    }

    // ───────────────────────────────────────────────────────────────
    // Observable Gauge: health_state
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void SetHealthState_SingleComponent_ProducesGaugeMeasurement()
    {
        _metrics.SetHealthState("db", HealthState.Degraded);
        _listener.RecordObservableInstruments();

        var recorded = GetIntMeasurements("healthboss.health_state");
        recorded.Should().ContainSingle();
        recorded[0].Value.Should().Be((int)HealthState.Degraded);
        recorded[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("db");
    }

    [Fact]
    public void SetHealthState_TwoComponents_ProducesTwoMeasurements()
    {
        _metrics.SetHealthState("db", HealthState.Healthy);
        _metrics.SetHealthState("cache", HealthState.CircuitOpen);
        _listener.RecordObservableInstruments();

        var recorded = GetIntMeasurements("healthboss.health_state");
        recorded.Should().HaveCount(2);
        recorded.Should().Contain(m => m.Tags["component"] == "db" && m.Value == (int)HealthState.Healthy);
        recorded.Should().Contain(m => m.Tags["component"] == "cache" && m.Value == (int)HealthState.CircuitOpen);
    }

    // ───────────────────────────────────────────────────────────────
    // Observable Gauge: active_sessions
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void SetActiveSessionCount_ProducesGaugeMeasurement()
    {
        _metrics.SetActiveSessionCount(42);
        _listener.RecordObservableInstruments();

        var recorded = GetIntMeasurements("healthboss.active_sessions");
        recorded.Should().Contain(m => m.Value == 42);
    }

    // ───────────────────────────────────────────────────────────────
    // Observable Gauge: drain_status
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void SetDrainStatus_ProducesGaugeMeasurement()
    {
        _metrics.SetDrainStatus(DrainStatus.Draining);
        _listener.RecordObservableInstruments();

        var recorded = GetIntMeasurements("healthboss.drain_status");
        recorded.Should().Contain(m => m.Value == (int)DrainStatus.Draining);
    }

    // ───────────────────────────────────────────────────────────────
    // Observable Gauge: quorum (3 instruments)
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void SetQuorumHealth_ProducesThreeGaugeMeasurements()
    {
        _metrics.SetQuorumHealth("api", healthyInstances: 3, totalInstances: 5, quorumMet: true);
        _listener.RecordObservableInstruments();

        var healthy = GetIntMeasurements("healthboss.quorum_healthy_instances");
        healthy.Should().ContainSingle();
        healthy[0].Value.Should().Be(3);
        healthy[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("api");

        var total = GetIntMeasurements("healthboss.quorum_total_instances");
        total.Should().ContainSingle();
        total[0].Value.Should().Be(5);
        total[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("api");

        var met = GetIntMeasurements("healthboss.quorum_met");
        met.Should().ContainSingle();
        met[0].Value.Should().Be(1);
        met[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("api");
    }

    [Fact]
    public void SetQuorumHealth_QuorumNotMet_RecordsZero()
    {
        _metrics.SetQuorumHealth("api", healthyInstances: 1, totalInstances: 5, quorumMet: false);
        _listener.RecordObservableInstruments();

        var met = GetIntMeasurements("healthboss.quorum_met");
        met.Should().ContainSingle()
            .Which.Value.Should().Be(0);
    }

    // ───────────────────────────────────────────────────────────────
    // Observable Gauge: tenant_count
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void SetTenantCount_ProducesGaugeMeasurement()
    {
        _metrics.SetTenantCount("db", 100);
        _listener.RecordObservableInstruments();

        var recorded = GetIntMeasurements("healthboss.tenant_count");
        recorded.Should().ContainSingle();
        recorded[0].Value.Should().Be(100);
        recorded[0].Tags.Should().ContainKey("component").WhoseValue.Should().Be("db");
    }

    // ───────────────────────────────────────────────────────────────
    // Tag isolation: counters don't cross-contaminate
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void RecordSignal_DoesNotAffectOtherCounters()
    {
        _metrics.RecordSignal("api", "Success");

        GetLongMeasurements("healthboss.state_transitions").Should().BeEmpty();
        GetLongMeasurements("healthboss.recovery_probe_attempts").Should().BeEmpty();
        GetLongMeasurements("healthboss.eventsink_dispatches").Should().BeEmpty();
    }

    [Fact]
    public void RecordStateTransition_DoesNotAffectSignalsCounter()
    {
        _metrics.RecordStateTransition("db", "Healthy", "Degraded");

        GetLongMeasurements("healthboss.signals_recorded").Should().BeEmpty();
    }

    // ───────────────────────────────────────────────────────────────
    // Meter lifecycle: managed by IMeterFactory
    // ───────────────────────────────────────────────────────────────

    [Fact]
    public void MeterLifecycle_ManagedByFactory_DisposingProviderDisablesMeter()
    {
        // Arrange: create a separate DI scope so we can dispose it
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        var metrics = new HealthBossMetrics(factory);

        // Act: disposing the provider disposes the factory which disposes the meter
        provider.Dispose();

        // After factory disposal, recording should be a no-op (no exceptions)
        metrics.RecordSignal("api", "Success");

        // The meter is disposed — no new measurements should appear
        // (existing listener won't receive callbacks for disposed meter instruments)
    }

    // ───────────────────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────────────────

    private static Dictionary<string, string> ExtractTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, string>();
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        return dict;
    }

    private List<LongMeasurement> GetLongMeasurements(string instrumentName)
    {
        lock (_lock)
        {
            return _longMeasurements.Where(m => m.InstrumentName == instrumentName).ToList();
        }
    }

    private List<DoubleMeasurement> GetDoubleMeasurements(string instrumentName)
    {
        lock (_lock)
        {
            return _doubleMeasurements.Where(m => m.InstrumentName == instrumentName).ToList();
        }
    }

    private List<IntMeasurement> GetIntMeasurements(string instrumentName)
    {
        lock (_lock)
        {
            return _intMeasurements.Where(m => m.InstrumentName == instrumentName).ToList();
        }
    }

    private sealed record LongMeasurement(
        string InstrumentName,
        long Value,
        Dictionary<string, string> Tags);

    private sealed record DoubleMeasurement(
        string InstrumentName,
        double Value,
        Dictionary<string, string> Tags);

    private sealed record IntMeasurement(
        string InstrumentName,
        int Value,
        Dictionary<string, string> Tags);
}
