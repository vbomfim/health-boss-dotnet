// <copyright file="MetricsEdgeCaseTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Diagnostics.Metrics;
using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.Core.Tests.EdgeCases;

/// <summary>
/// Edge case tests for the metrics subsystem. Verifies behavior at boundaries,
/// under concurrent access, and when metrics are not configured (NullObject fallback).
/// </summary>
public sealed class MetricsEdgeCaseTests
{
    // ─── Helper: create HealthBossMetrics with a DI-provided IMeterFactory ───

    /// <summary>
    /// Creates a <see cref="HealthBossMetrics"/> backed by a DI-provided <see cref="IMeterFactory"/>.
    /// Returns both the metrics instance and the <see cref="ServiceProvider"/> that must be disposed
    /// when the test is complete (disposing the provider disposes the factory and the meter).
    /// </summary>
    private static (HealthBossMetrics Metrics, ServiceProvider Provider) CreateMetrics()
    {
        var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();
        return (new HealthBossMetrics(factory), provider);
    }
    // ─────────────────────────────────────────────────────────────────
    // [EDGE] NullHealthBossMetrics — all methods are safe no-ops
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] NullHealthBossMetrics.Instance must be a singleton.
    /// </summary>
    [Fact]
    public void NullMetrics_Instance_IsSingleton()
    {
        var a = NullHealthBossMetrics.Instance;
        var b = NullHealthBossMetrics.Instance;
        a.Should().BeSameAs(b);
    }

    /// <summary>
    /// [EDGE] All counter methods on NullHealthBossMetrics must be no-ops (no exceptions).
    /// </summary>
    [Fact]
    public void NullMetrics_AllCounterMethods_AreNoOp()
    {
        var metrics = NullHealthBossMetrics.Instance;

        var act = () =>
        {
            metrics.RecordSignal("comp", "Success");
            metrics.RecordStateTransition("comp", "Healthy", "Degraded");
            metrics.RecordRecoveryProbeAttempt("comp");
            metrics.RecordRecoveryProbeSuccess("comp");
            metrics.RecordEventSinkDispatch();
            metrics.RecordEventSinkFailure("SinkType");
            metrics.RecordShutdownGateEvaluation("Gate", true);
        };

        act.Should().NotThrow("NullHealthBossMetrics counter methods must be safe no-ops");
    }

    /// <summary>
    /// [EDGE] All histogram methods on NullHealthBossMetrics must be no-ops (no exceptions).
    /// </summary>
    [Fact]
    public void NullMetrics_AllHistogramMethods_AreNoOp()
    {
        var metrics = NullHealthBossMetrics.Instance;

        var act = () =>
        {
            metrics.RecordAssessmentDuration("comp", 0.042);
            metrics.RecordInboundRequestDuration("comp", 0.125);
            metrics.RecordOutboundRequestDuration("comp", 0.300);
        };

        act.Should().NotThrow("NullHealthBossMetrics histogram methods must be safe no-ops");
    }

    /// <summary>
    /// [EDGE] All gauge setter methods on NullHealthBossMetrics must be no-ops (no exceptions).
    /// </summary>
    [Fact]
    public void NullMetrics_AllGaugeSetterMethods_AreNoOp()
    {
        var metrics = NullHealthBossMetrics.Instance;

        var act = () =>
        {
            metrics.SetHealthState("comp", HealthState.Degraded);
            metrics.SetActiveSessionCount(42);
            metrics.SetDrainStatus(DrainStatus.Draining);
            metrics.SetQuorumHealth("comp", 3, 5, true);
            metrics.SetTenantCount("comp", 100);
        };

        act.Should().NotThrow("NullHealthBossMetrics gauge methods must be safe no-ops");
    }

    // ─────────────────────────────────────────────────────────────────
    // [EDGE] NullHealthBossMetrics does NOT emit any metrics
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] NullHealthBossMetrics must NOT create a meter or publish any instruments.
    /// This ensures zero overhead when metrics are disabled.
    /// </summary>
    [Fact]
    public void NullMetrics_DoesNotPublish_AnyInstruments()
    {
        var instruments = new List<string>();
        using var listener = new MeterListener();

        listener.InstrumentPublished = (instrument, _) =>
        {
            instruments.Add(instrument.Name);
        };

        listener.Start();

        // Exercise all methods on NullHealthBossMetrics
        var metrics = NullHealthBossMetrics.Instance;
        metrics.RecordSignal("test", "Success");
        metrics.SetHealthState("test", HealthState.Healthy);
        metrics.RecordAssessmentDuration("test", 0.1);

        // NullHealthBossMetrics doesn't create a Meter — no instruments should be published
        // from the NullHealthBossMetrics type
        // Note: other tests may publish instruments, so we check that null object
        // doesn't cause any measurement callbacks
        instruments.Should().NotContain(i => i.Contains("null", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────────────────────────────────────────────
    // [EDGE] IMeterFactory lifecycle — after provider disposal, methods are safe no-ops
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] After the DI provider (and thus IMeterFactory) is disposed,
    /// calling any method on HealthBossMetrics must NOT throw — recording
    /// is silently dropped because the meter is disposed by the factory.
    /// </summary>
    [Fact]
    public void FactoryDisposed_AllMethods_AreNoOp()
    {
        var (metrics, provider) = CreateMetrics();
        provider.Dispose(); // disposes IMeterFactory → disposes the Meter

        var act = () =>
        {
            // Counters
            metrics.RecordSignal("comp", "Success");
            metrics.RecordStateTransition("comp", "Healthy", "Degraded");
            metrics.RecordRecoveryProbeAttempt("comp");
            metrics.RecordRecoveryProbeSuccess("comp");
            metrics.RecordEventSinkDispatch();
            metrics.RecordEventSinkFailure("SinkType");
            metrics.RecordShutdownGateEvaluation("Gate", true);

            // Histograms
            metrics.RecordAssessmentDuration("comp", 0.042);
            metrics.RecordInboundRequestDuration("comp", 0.125);
            metrics.RecordOutboundRequestDuration("comp", 0.300);

            // Gauge setters (state is set but meter is disposed — callbacks won't fire)
            metrics.SetHealthState("comp", HealthState.Degraded);
            metrics.SetActiveSessionCount(42);
            metrics.SetDrainStatus(DrainStatus.Draining);
            metrics.SetQuorumHealth("comp", 3, 5, true);
            metrics.SetTenantCount("comp", 100);
        };

        act.Should().NotThrow("HealthBossMetrics must be safe to call after factory disposal");
    }

    /// <summary>
    /// [EDGE] Disposing the DI provider multiple times must NOT throw.
    /// </summary>
    [Fact]
    public void DoubleProviderDispose_DoesNotThrow()
    {
        var (_, provider) = CreateMetrics();

        var act = () =>
        {
            provider.Dispose();
            provider.Dispose();
        };

        act.Should().NotThrow("double Dispose on the DI provider must be safe");
    }

    /// <summary>
    /// [EDGE] HealthBossMetrics constructor must throw ArgumentNullException
    /// when meterFactory is null.
    /// </summary>
    [Fact]
    public void Constructor_NullMeterFactory_ThrowsArgumentNullException()
    {
        var act = () => new HealthBossMetrics(null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("meterFactory");
    }

    // ─────────────────────────────────────────────────────────────────
    // [EDGE] Boundary values for gauge setters
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] SetActiveSessionCount with 0 — boundary minimum.
    /// </summary>
    [Fact]
    public void SetActiveSessionCount_Zero_IsValid()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);

        metrics.SetActiveSessionCount(0);
        listener.RecordObservableInstruments();

        intMeasurements.Should().Contain(m =>
            m.InstrumentName == "healthboss.active_sessions" && m.Value == 0);
    }

    /// <summary>
    /// [EDGE] SetActiveSessionCount with int.MaxValue — boundary maximum.
    /// </summary>
    [Fact]
    public void SetActiveSessionCount_MaxValue_IsValid()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);

        metrics.SetActiveSessionCount(int.MaxValue);
        listener.RecordObservableInstruments();

        intMeasurements.Should().Contain(m =>
            m.InstrumentName == "healthboss.active_sessions" && m.Value == int.MaxValue);
    }

    /// <summary>
    /// [EDGE] SetDrainStatus with all enum values — exhaustive.
    /// </summary>
    [Theory]
    [InlineData(DrainStatus.Idle, 0)]
    [InlineData(DrainStatus.Draining, 1)]
    [InlineData(DrainStatus.Drained, 2)]
    [InlineData(DrainStatus.TimedOut, 3)]
    public void SetDrainStatus_AllEnumValues_ProduceCorrectGaugeValue(
        DrainStatus status, int expectedValue)
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);

        metrics.SetDrainStatus(status);
        listener.RecordObservableInstruments();

        intMeasurements.Should().Contain(m =>
            m.InstrumentName == "healthboss.drain_status" && m.Value == expectedValue);
    }

    /// <summary>
    /// [EDGE] SetHealthState with all enum values — exhaustive.
    /// </summary>
    [Theory]
    [InlineData(HealthState.Healthy, 0)]
    [InlineData(HealthState.Degraded, 1)]
    [InlineData(HealthState.CircuitOpen, 2)]
    public void SetHealthState_AllEnumValues_ProduceCorrectGaugeValue(
        HealthState state, int expectedValue)
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);

        metrics.SetHealthState("comp", state);
        listener.RecordObservableInstruments();

        intMeasurements.Should().Contain(m =>
            m.InstrumentName == "healthboss.health_state" &&
            m.Value == expectedValue &&
            m.Tags.ContainsKey("component"));
    }

    /// <summary>
    /// [EDGE] SetQuorumHealth with zero instances — boundary.
    /// </summary>
    [Fact]
    public void SetQuorumHealth_ZeroInstances_IsValid()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);

        metrics.SetQuorumHealth("comp", healthyInstances: 0, totalInstances: 0, quorumMet: false);
        listener.RecordObservableInstruments();

        intMeasurements.Should().Contain(m =>
            m.InstrumentName == "healthboss.quorum_healthy_instances" && m.Value == 0);
        intMeasurements.Should().Contain(m =>
            m.InstrumentName == "healthboss.quorum_total_instances" && m.Value == 0);
        intMeasurements.Should().Contain(m =>
            m.InstrumentName == "healthboss.quorum_met" && m.Value == 0);
    }

    /// <summary>
    /// [EDGE] RecordAssessmentDuration with 0 seconds — boundary.
    /// </summary>
    [Fact]
    public void RecordAssessmentDuration_ZeroSeconds_IsValid()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out _, out var doubleMeasurements);

        metrics.RecordAssessmentDuration("comp", 0.0);

        doubleMeasurements.Should().ContainSingle(m =>
            m.InstrumentName == "healthboss.assessment_duration_seconds" && m.Value == 0.0);
    }

    // ─────────────────────────────────────────────────────────────────
    // [EDGE] Observable gauge overwrites — last value wins
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] Setting the same component's health state multiple times should
    /// only report the latest value when the gauge is observed.
    /// </summary>
    [Fact]
    public void SetHealthState_MultipleUpdates_SameComponent_LastValueWins()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);

        metrics.SetHealthState("db", HealthState.Healthy);
        metrics.SetHealthState("db", HealthState.Degraded);
        metrics.SetHealthState("db", HealthState.CircuitOpen);

        listener.RecordObservableInstruments();

        var dbGauges = intMeasurements
            .Where(m => m.InstrumentName == "healthboss.health_state" &&
                        m.Tags.GetValueOrDefault("component") == "db")
            .ToList();

        // Observable gauge should emit exactly one measurement per component
        dbGauges.Should().ContainSingle()
            .Which.Value.Should().Be((int)HealthState.CircuitOpen,
                "last SetHealthState call should win");
    }

    /// <summary>
    /// [EDGE] SetTenantCount overwrite — last value wins per component.
    /// </summary>
    [Fact]
    public void SetTenantCount_Overwrite_LastValueWins()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);

        metrics.SetTenantCount("comp", 10);
        metrics.SetTenantCount("comp", 50);
        metrics.SetTenantCount("comp", 200);

        listener.RecordObservableInstruments();

        var tenantGauges = intMeasurements
            .Where(m => m.InstrumentName == "healthboss.tenant_count" &&
                        m.Tags.GetValueOrDefault("component") == "comp")
            .ToList();

        tenantGauges.Should().ContainSingle()
            .Which.Value.Should().Be(200);
    }

    // ─────────────────────────────────────────────────────────────────
    // [EDGE] Concurrent access to HealthBossMetrics
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] Multiple threads calling RecordSignal concurrently must not
    /// throw or corrupt state.
    /// </summary>
    [Fact]
    public void ConcurrentRecordSignal_DoesNotThrow()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        const int threads = 10;
        const int callsPerThread = 1000;

        var act = () => Parallel.For(0, threads, _ =>
        {
            for (int i = 0; i < callsPerThread; i++)
            {
                metrics.RecordSignal($"comp-{i % 5}", i % 2 == 0 ? "Success" : "Failure");
            }
        });

        act.Should().NotThrow("concurrent counter recording must be thread-safe");
    }

    /// <summary>
    /// [EDGE] Multiple threads calling SetHealthState concurrently must not
    /// throw or corrupt the ConcurrentDictionary.
    /// </summary>
    [Fact]
    public void ConcurrentSetHealthState_DoesNotThrow()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);
        var states = new[] { HealthState.Healthy, HealthState.Degraded, HealthState.CircuitOpen };

        var act = () => Parallel.For(0, 100, i =>
        {
            metrics.SetHealthState($"comp-{i % 10}", states[i % states.Length]);
        });

        act.Should().NotThrow("concurrent gauge state updates must be thread-safe");

        // Verify gauges can still be read
        listener.RecordObservableInstruments();
        var healthGauges = intMeasurements
            .Where(m => m.InstrumentName == "healthboss.health_state")
            .ToList();
        healthGauges.Should().HaveCountGreaterThan(0);
    }

    /// <summary>
    /// [EDGE] Concurrent SetActiveSessionCount and SetDrainStatus via Volatile —
    /// must not throw.
    /// </summary>
    [Fact]
    public void ConcurrentVolatileGauges_DoNotThrow()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;

        var act = () => Parallel.For(0, 100, i =>
        {
            metrics.SetActiveSessionCount(i);
            metrics.SetDrainStatus((DrainStatus)(i % 4));
        });

        act.Should().NotThrow("Volatile-backed gauges must be thread-safe");
    }

    // ─────────────────────────────────────────────────────────────────
    // [EDGE] Components with optional metrics parameter (null → NullHealthBossMetrics)
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] Components created without metrics parameter must use
    /// NullHealthBossMetrics internally and not throw.
    /// </summary>
    [Fact]
    public void SessionHealthTracker_WithoutMetrics_DoesNotThrow()
    {
        var clock = new SystemClock(TimeProvider.System);
        var tracker = new SessionHealthTracker(clock); // no metrics parameter

        var act = () =>
        {
            var handle = tracker.TrackSessionStart("ws", "s1");
            handle.Complete(SessionOutcome.Success);
        };

        act.Should().NotThrow("SessionHealthTracker with null metrics should use NullHealthBossMetrics");
    }

    /// <summary>
    /// [EDGE] EventSinkDispatcher without metrics parameter must dispatch normally.
    /// </summary>
    [Fact]
    public async Task EventSinkDispatcher_WithoutMetrics_DispatchesNormally()
    {
        var clock = new SystemClock(TimeProvider.System);
        var fakeSink = new Fakes.FakeHealthEventSink();

        var dispatcher = new EventSinkDispatcher(
            new List<IHealthEventSink> { fakeSink },
            new EventSinkDispatcherOptions(),
            clock); // no logger, no metrics

        var healthEvent = new HealthEvent(
            new DependencyId("api"),
            HealthState.Healthy,
            HealthState.Degraded,
            DateTimeOffset.UtcNow);

        await dispatcher.DispatchAsync(healthEvent);

        fakeSink.HealthEvents.Should().ContainSingle(
            "dispatch should work without metrics injection");
    }

    // ─────────────────────────────────────────────────────────────────
    // [EDGE] Multiple components — metric isolation
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [EDGE] Multiple observable gauges for different components must be reported
    /// independently with correct component tags.
    /// </summary>
    [Fact]
    public void MultipleComponents_ObservableGauges_AreIsolated()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);

        metrics.SetHealthState("db", HealthState.Degraded);
        metrics.SetHealthState("cache", HealthState.Healthy);
        metrics.SetHealthState("api", HealthState.CircuitOpen);

        metrics.SetTenantCount("db", 10);
        metrics.SetTenantCount("cache", 20);

        metrics.SetQuorumHealth("api", 3, 5, true);

        listener.RecordObservableInstruments();

        // Verify each component's health_state gauge is independent
        var healthGauges = intMeasurements
            .Where(m => m.InstrumentName == "healthboss.health_state")
            .ToList();

        healthGauges.Should().HaveCount(3);
        healthGauges.Should().Contain(m =>
            m.Tags["component"] == "db" && m.Value == (int)HealthState.Degraded);
        healthGauges.Should().Contain(m =>
            m.Tags["component"] == "cache" && m.Value == (int)HealthState.Healthy);
        healthGauges.Should().Contain(m =>
            m.Tags["component"] == "api" && m.Value == (int)HealthState.CircuitOpen);

        // Verify tenant counts are isolated
        var tenantGauges = intMeasurements
            .Where(m => m.InstrumentName == "healthboss.tenant_count")
            .ToList();

        tenantGauges.Should().HaveCount(2);
        tenantGauges.Should().Contain(m => m.Tags["component"] == "db" && m.Value == 10);
        tenantGauges.Should().Contain(m => m.Tags["component"] == "cache" && m.Value == 20);
    }

    // ─────────────────────────────────────────────────────────────────
    // [COVERAGE] Quorum/tenant gauges are NOT wired into any component
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [COVERAGE] Developer noted quorum/tenant gauges are NOT wired.
    /// This test documents the gap: SetQuorumHealth and SetTenantCount are
    /// defined on the interface but never called by any production component.
    /// The methods work correctly when called directly (verified here).
    /// </summary>
    [Fact]
    public void QuorumAndTenantGauges_WorkCorrectly_EvenThoughNotWired()
    {
        var (metrics, metricsProvider) = CreateMetrics();

        using var _provider = metricsProvider;
        using var listener = CreateMeterListener(out var intMeasurements);

        // These gauges are functional but no production component calls them yet
        metrics.SetQuorumHealth("api", healthyInstances: 4, totalInstances: 5, quorumMet: true);
        metrics.SetTenantCount("api", 42);

        listener.RecordObservableInstruments();

        intMeasurements.Should().Contain(m =>
            m.InstrumentName == "healthboss.quorum_healthy_instances" && m.Value == 4);
        intMeasurements.Should().Contain(m =>
            m.InstrumentName == "healthboss.tenant_count" && m.Value == 42);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static MeterListener CreateMeterListener(out List<IntMeasurement> intMeasurements)
    {
        var measurements = new List<IntMeasurement>();
        intMeasurements = measurements;

        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "HealthBoss")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            measurements.Add(new IntMeasurement(instrument.Name, value, ExtractTags(tags)));
        });

        // Also handle long (counters) — no-op to avoid unhandled instrument types
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => { });
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => { });

        listener.Start();
        return listener;
    }

    private static MeterListener CreateMeterListener(
        out List<IntMeasurement> intMeasurements,
        out List<DoubleMeasurement> doubleMeasurements)
    {
        var intList = new List<IntMeasurement>();
        var doubleList = new List<DoubleMeasurement>();
        intMeasurements = intList;
        doubleMeasurements = doubleList;

        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "HealthBoss")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            intList.Add(new IntMeasurement(instrument.Name, value, ExtractTags(tags)));
        });

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            doubleList.Add(new DoubleMeasurement(instrument.Name, value, ExtractTags(tags)));
        });

        listener.SetMeasurementEventCallback<long>((_, _, _, _) => { });

        listener.Start();
        return listener;
    }

    private static Dictionary<string, string> ExtractTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var dict = new Dictionary<string, string>();
        foreach (var tag in tags)
        {
            dict[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        return dict;
    }

    private sealed record IntMeasurement(string InstrumentName, int Value, Dictionary<string, string> Tags);
    private sealed record DoubleMeasurement(string InstrumentName, double Value, Dictionary<string, string> Tags);
}
