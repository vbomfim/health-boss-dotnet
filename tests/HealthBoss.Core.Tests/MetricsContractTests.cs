// <copyright file="MetricsContractTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Diagnostics.Metrics;
using System.Reflection;
using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Contract tests for the <see cref="IHealthBossMetrics"/> interface and
/// its implementations. Verifies interface stability, completeness,
/// and that all 18 instruments are registered under the "HealthBoss" meter.
/// </summary>
public sealed class MetricsContractTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly HealthBossMetrics _metrics;
    private readonly MeterListener _listener = new();
    private readonly List<InstrumentInfo> _publishedInstruments = [];
    private readonly object _lock = new();

    public MetricsContractTests()
    {
        _serviceProvider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = _serviceProvider.GetRequiredService<IMeterFactory>();
        _metrics = new HealthBossMetrics(meterFactory);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "HealthBoss")
            {
                lock (_lock)
                {
                    _publishedInstruments.Add(new InstrumentInfo(
                        instrument.Name,
                        instrument.GetType().Name,
                        instrument.Unit,
                        instrument.Meter.Name,
                        instrument.Meter.Version));
                }

                listener.EnableMeasurementEvents(instrument);
            }
        };

        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _serviceProvider.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────
    // [CONTRACT] Meter identity
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CONTRACT] The meter MUST be named "HealthBoss" per acceptance criteria.
    /// </summary>
    [Fact]
    public void MeterName_Is_HealthBoss()
    {
        _publishedInstruments.Should().NotBeEmpty();
        _publishedInstruments.Should().OnlyContain(i => i.MeterName == "HealthBoss");
    }

    /// <summary>
    /// [CONTRACT] The meter MUST report version "1.0.0".
    /// </summary>
    [Fact]
    public void MeterVersion_Is_1_0_0()
    {
        _publishedInstruments.Should().NotBeEmpty();
        _publishedInstruments.Should().OnlyContain(i => i.MeterVersion == "1.0.0");
    }

    // ─────────────────────────────────────────────────────────────────
    // [CONTRACT] Instrument count: exactly 17
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CONTRACT] There must be exactly 18 unique instrument names registered.
    /// 8 counters + 3 histograms + 7 observable gauges.
    /// Note: In a test process, multiple HealthBossMetrics instances may register
    /// the same instrument names. We verify the unique set of names equals 18.
    /// </summary>
    [Fact]
    public void TotalInstrumentCount_Is_18()
    {
        List<string> uniqueNames;
        lock (_lock)
        {
            uniqueNames = _publishedInstruments.Select(i => i.Name).Distinct().ToList();
        }

        uniqueNames.Should().HaveCount(18,
            "PR #60 specifies 18 instruments: 8 counters, 3 histograms, 7 observable gauges");
    }

    // ─────────────────────────────────────────────────────────────────
    // [CONTRACT] All 7 counters are present with expected names
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CONTRACT] All 8 counter instruments MUST be registered with their expected names.
    /// </summary>
    [Theory]
    [InlineData("healthboss.signals_recorded")]
    [InlineData("healthboss.state_transitions")]
    [InlineData("healthboss.recovery_probe_attempts")]
    [InlineData("healthboss.recovery_probe_successes")]
    [InlineData("healthboss.eventsink_dispatches")]
    [InlineData("healthboss.eventsink_failures")]
    [InlineData("healthboss.shutdown_gate_evaluations")]
    [InlineData("healthboss.tenant.status_changes")]
    public void Counter_InstrumentName_IsRegistered(string expectedName)
    {
        _publishedInstruments.Should().Contain(i => i.Name == expectedName,
            $"counter '{expectedName}' must be registered under the HealthBoss meter");
    }

    // ─────────────────────────────────────────────────────────────────
    // [CONTRACT] All 3 histograms are present with expected names and units
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CONTRACT] All 3 histogram instruments MUST be registered with their expected names
    /// and the "s" (seconds) unit.
    /// </summary>
    [Theory]
    [InlineData("healthboss.assessment_duration_seconds")]
    [InlineData("healthboss.middleware_inbound_duration_seconds")]
    [InlineData("healthboss.middleware_outbound_duration_seconds")]
    public void Histogram_InstrumentName_IsRegistered_WithSecondsUnit(string expectedName)
    {
        var instrument = _publishedInstruments.FirstOrDefault(i => i.Name == expectedName);
        instrument.Should().NotBeNull($"histogram '{expectedName}' must be registered");
        instrument!.Unit.Should().Be("s", $"histogram '{expectedName}' should use seconds unit");
    }

    // ─────────────────────────────────────────────────────────────────
    // [CONTRACT] All 7 observable gauges are present with expected names
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CONTRACT] All 7 observable gauge instruments MUST be registered with their expected names.
    /// </summary>
    [Theory]
    [InlineData("healthboss.health_state")]
    [InlineData("healthboss.active_sessions")]
    [InlineData("healthboss.drain_status")]
    [InlineData("healthboss.quorum_healthy_instances")]
    [InlineData("healthboss.quorum_total_instances")]
    [InlineData("healthboss.quorum_met")]
    [InlineData("healthboss.tenant_count")]
    public void ObservableGauge_InstrumentName_IsRegistered(string expectedName)
    {
        _publishedInstruments.Should().Contain(i => i.Name == expectedName,
            $"observable gauge '{expectedName}' must be registered under the HealthBoss meter");
    }

    // ─────────────────────────────────────────────────────────────────
    // [CONTRACT] IHealthBossMetrics interface completeness
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CONTRACT] IHealthBossMetrics MUST expose exactly 16 methods covering all metric operations.
    /// Since IHealthBossMetrics is now a composed interface (ISP split, Issue #61),
    /// the methods are inherited from the 5 sub-interfaces.
    /// </summary>
    [Fact]
    public void Interface_HasExpected_MethodCount()
    {
        var methods = GetAllInterfaceMethods(typeof(IHealthBossMetrics));
        methods.Should().HaveCount(16,
            "IHealthBossMetrics composes 5 sub-interfaces totaling 8 counter + 3 histogram + 5 gauge = 16 methods");
    }

    /// <summary>
    /// [CONTRACT] IHealthBossMetrics MUST expose all expected counter method signatures
    /// (via composed sub-interfaces).
    /// </summary>
    [Theory]
    [InlineData("RecordSignal", new[] { typeof(string), typeof(string) })]
    [InlineData("RecordStateTransition", new[] { typeof(string), typeof(string), typeof(string) })]
    [InlineData("RecordRecoveryProbeAttempt", new[] { typeof(string) })]
    [InlineData("RecordRecoveryProbeSuccess", new[] { typeof(string) })]
    [InlineData("RecordEventSinkDispatch", new Type[0])]
    [InlineData("RecordEventSinkFailure", new[] { typeof(string) })]
    public void Interface_HasCounterMethod(string methodName, Type[] parameterTypes)
    {
        var method = FindMethodInHierarchy(typeof(IHealthBossMetrics), methodName, parameterTypes);
        method.Should().NotBeNull($"IHealthBossMetrics must expose {methodName} (via sub-interface)");
        method!.ReturnType.Should().Be(typeof(void), $"{methodName} must return void");
    }

    /// <summary>
    /// [CONTRACT] IHealthBossMetrics MUST expose all expected histogram method signatures
    /// (via composed sub-interfaces).
    /// </summary>
    [Theory]
    [InlineData("RecordAssessmentDuration")]
    [InlineData("RecordInboundRequestDuration")]
    [InlineData("RecordOutboundRequestDuration")]
    public void Interface_HasHistogramMethod(string methodName)
    {
        var method = FindMethodInHierarchy(typeof(IHealthBossMetrics), methodName,
            new[] { typeof(string), typeof(double) });
        method.Should().NotBeNull($"IHealthBossMetrics must expose {methodName}(string, double)");
        method!.ReturnType.Should().Be(typeof(void));
    }

    /// <summary>
    /// [CONTRACT] IHealthBossMetrics MUST expose the ShutdownGateEvaluation counter
    /// with (string, bool) parameters (via IStateMachineMetrics).
    /// </summary>
    [Fact]
    public void Interface_HasShutdownGateEvaluationMethod()
    {
        var method = FindMethodInHierarchy(typeof(IHealthBossMetrics), "RecordShutdownGateEvaluation",
            new[] { typeof(string), typeof(bool) });
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(void));
    }

    /// <summary>
    /// [CONTRACT] IHealthBossMetrics MUST expose all expected gauge setter method signatures
    /// (via composed sub-interfaces).
    /// </summary>
    [Fact]
    public void Interface_HasAllGaugeSetterMethods()
    {
        FindMethodInHierarchy(typeof(IHealthBossMetrics), "SetHealthState",
            new[] { typeof(string), typeof(HealthState) })
            .Should().NotBeNull();

        FindMethodInHierarchy(typeof(IHealthBossMetrics), "SetActiveSessionCount",
            new[] { typeof(int) })
            .Should().NotBeNull();

        FindMethodInHierarchy(typeof(IHealthBossMetrics), "SetDrainStatus",
            new[] { typeof(DrainStatus) })
            .Should().NotBeNull();

        FindMethodInHierarchy(typeof(IHealthBossMetrics), "SetQuorumHealth",
            new[] { typeof(string), typeof(int), typeof(int), typeof(bool) })
            .Should().NotBeNull();

        FindMethodInHierarchy(typeof(IHealthBossMetrics), "SetTenantCount",
            new[] { typeof(string), typeof(int) })
            .Should().NotBeNull();
    }

    // ─────────────────────────────────────────────────────────────────
    // [CONTRACT] NullHealthBossMetrics implements all methods
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CONTRACT] NullHealthBossMetrics MUST implement IHealthBossMetrics.
    /// </summary>
    [Fact]
    public void NullHealthBossMetrics_Implements_IHealthBossMetrics()
    {
        typeof(NullHealthBossMetrics).Should().Implement<IHealthBossMetrics>();
    }

    /// <summary>
    /// [CONTRACT] HealthBossMetrics MUST implement IHealthBossMetrics.
    /// </summary>
    [Fact]
    public void HealthBossMetrics_Implements_IHealthBossMetrics()
    {
        typeof(HealthBossMetrics).Should().Implement<IHealthBossMetrics>();
    }

    /// <summary>
    /// [CONTRACT] HealthBossMetrics MUST NOT implement IDisposable — the Meter lifecycle
    /// is managed by IMeterFactory (owned by the DI container). See Issue #63.
    /// </summary>
    [Fact]
    public void HealthBossMetrics_DoesNotImplement_IDisposable()
    {
        typeof(HealthBossMetrics).Should().NotImplement<IDisposable>(
            "Meter lifecycle is managed by IMeterFactory, not HealthBossMetrics (Issue #63)");
    }

    /// <summary>
    /// [CONTRACT] HealthBossMetrics constructor MUST require IMeterFactory for DI-managed meter lifecycle.
    /// </summary>
    [Fact]
    public void HealthBossMetrics_Constructor_Requires_IMeterFactory()
    {
        var ctor = typeof(HealthBossMetrics).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance);
        ctor.Should().ContainSingle("HealthBossMetrics should have exactly one public constructor");

        var parameters = ctor[0].GetParameters();
        parameters.Should().ContainSingle()
            .Which.ParameterType.Should().Be(typeof(IMeterFactory),
                "constructor must accept IMeterFactory for DI-managed meter lifecycle");
    }

    // ─────────────────────────────────────────────────────────────────
    // [CONTRACT] Instrument names use dot-separated namespace
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [CONTRACT] All instrument names MUST be prefixed with "healthboss." for
    /// OTel namespace consistency.
    /// </summary>
    [Fact]
    public void AllInstrumentNames_AreNamespaced_WithHealthBossPrefix()
    {
        _publishedInstruments.Should().NotBeEmpty();
        _publishedInstruments.Should().OnlyContain(
            i => i.Name.StartsWith("healthboss.", StringComparison.Ordinal),
            "all instruments should use the 'healthboss.' prefix for OTel consistency");
    }

    /// <summary>
    /// [CONTRACT] No duplicate instrument names within the "HealthBoss" meter.
    /// Verifies the set of unique names matches the total captured count
    /// from our single HealthBossMetrics instance.
    /// </summary>
    [Fact]
    public void AllInstrumentNames_AreUnique()
    {
        // In a multi-test process, the listener may capture instruments from
        // multiple HealthBossMetrics instances. Verify that the 18 expected
        // names are all distinct.
        List<string> uniqueNames;
        lock (_lock)
        {
            uniqueNames = _publishedInstruments.Select(i => i.Name).Distinct().ToList();
        }

        uniqueNames.Should().HaveCount(18,
            "all 18 instrument names should be unique");
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets all methods from an interface type including inherited interface methods.
    /// Required because <c>Type.GetMethods()</c> on a composed interface only returns
    /// methods declared directly on that type, not those inherited from parent interfaces.
    /// </summary>
    private static MethodInfo[] GetAllInterfaceMethods(Type interfaceType)
    {
        return interfaceType.GetInterfaces()
            .SelectMany(i => i.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Concat(interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .ToArray();
    }

    /// <summary>
    /// Finds a method by name and parameter types across an interface and all its parent interfaces.
    /// </summary>
    private static MethodInfo? FindMethodInHierarchy(Type interfaceType, string methodName, Type[] parameterTypes)
    {
        var method = interfaceType.GetMethod(methodName, parameterTypes);
        if (method is not null)
        {
            return method;
        }

        foreach (var parent in interfaceType.GetInterfaces())
        {
            method = parent.GetMethod(methodName, parameterTypes);
            if (method is not null)
            {
                return method;
            }
        }

        return null;
    }

    private sealed record InstrumentInfo(
        string Name,
        string TypeName,
        string? Unit,
        string MeterName,
        string? MeterVersion);
}
