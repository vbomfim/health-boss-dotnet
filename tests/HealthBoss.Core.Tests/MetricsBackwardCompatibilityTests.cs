// <copyright file="MetricsBackwardCompatibilityTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Backward compatibility tests ensuring that existing code paths continue to work
/// when metrics are not explicitly configured. Validates the NullObject fallback pattern
/// used by components accepting <c>IXxxMetrics? metrics = null</c>.
/// Issue #66 — Missing integration/NullObject/compat tests.
/// </summary>
public sealed class MetricsBackwardCompatibilityTests
{
    // ─────────────────────────────────────────────────────────────────
    // [COMPAT] AddHealthBoss() without explicit OpenTelemetry — no throw
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// AddHealthBoss() must work even when no explicit OpenTelemetry exporter is configured.
    /// The <c>AddMetrics()</c> call inside AddHealthBoss provides the default IMeterFactory.
    /// </summary>
    [Fact]
    public void AddHealthBoss_WithoutOpenTelemetryConfigured_DoesNotThrow()
    {
        var services = new ServiceCollection();

        var act = () => services.AddHealthBoss(opts =>
        {
            opts.AddComponent("redis");
        });

        act.Should().NotThrow(
            "AddHealthBoss() should work without explicit OpenTelemetry exporter — " +
            "AddMetrics() provides a default IMeterFactory");
    }

    /// <summary>
    /// Building and using a ServiceProvider after AddHealthBoss() without OTel must not throw.
    /// </summary>
    [Fact]
    public void BuildServiceProvider_WithoutOpenTelemetry_ResolvesAllServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddHealthBoss(opts => opts.AddComponent("test-dep"));

        var act = () =>
        {
            using var provider = services.BuildServiceProvider();

            // Resolve every key service — none should throw
            _ = provider.GetRequiredService<IHealthOrchestrator>();
            _ = provider.GetRequiredService<IHealthBossMetrics>();
            _ = provider.GetRequiredService<IComponentMetrics>();
            _ = provider.GetRequiredService<ISessionMetrics>();
            _ = provider.GetRequiredService<IStateMachineMetrics>();
            _ = provider.GetRequiredService<ITenantMetrics>();
            _ = provider.GetRequiredService<IQuorumMetrics>();
            _ = provider.GetRequiredService<IEventSinkDispatcher>();
        };

        act.Should().NotThrow(
            "all registered services must resolve even without an OTel exporter");
    }

    // ─────────────────────────────────────────────────────────────────
    // [COMPAT] Components with null metrics default to NullObject
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// HealthOrchestrator constructed with <c>metrics: null</c> must default to
    /// NullHealthBossMetrics.Instance and operate without throwing.
    /// </summary>
    [Fact]
    public void HealthOrchestrator_WithNullMetrics_DefaultsToNullObject()
    {
        var clock = new SystemClock(TimeProvider.System);
        var monitors = new Dictionary<DependencyId, IDependencyMonitor>();
        var startupTracker = new StartupTracker();

        // Pass metrics: null explicitly
        var orchestrator = new HealthOrchestrator(
            monitors,
            healthResolver: null,
            readinessResolver: null,
            startupTracker,
            clock,
            logger: null,
            metrics: null);

        orchestrator.Should().NotBeNull(
            "HealthOrchestrator must accept null metrics and default to NullObject");
    }

    /// <summary>
    /// HealthOrchestrator with null metrics must still process signals without throwing.
    /// </summary>
    [Fact]
    public void HealthOrchestrator_WithNullMetrics_ProcessesSignals_WithoutThrowing()
    {
        var timeProvider = new FakeTimeProvider(TestFixtures.BaseTime);
        var clock = new SystemClock(timeProvider);
        var depId = new DependencyId("test-dep");
        var buffer = new SignalBuffer(clock);
        var evaluator = new PolicyEvaluator();
        var transitionEngine = new TransitionEngine(new DefaultStateGraph());
        var startupTracker = new StartupTracker();

        var monitor = new DependencyMonitor(
            depId, buffer, evaluator, transitionEngine,
            TestFixtures.DefaultPolicy, clock);

        var monitors = new Dictionary<DependencyId, IDependencyMonitor> { [depId] = monitor };

        var orchestrator = new HealthOrchestrator(
            monitors,
            healthResolver: null,
            readinessResolver: null,
            startupTracker,
            clock,
            logger: null,
            metrics: null);

        // Should not throw even with null metrics (NullObject handles it)
        var act = () => orchestrator.RecordSignal(depId, TestFixtures.CreateSignal());

        act.Should().NotThrow(
            "HealthOrchestrator with NullObject metrics must process signals silently");
    }

    /// <summary>
    /// SessionHealthTracker with <c>metrics: null</c> must default to NullObject
    /// and track sessions normally.
    /// </summary>
    [Fact]
    public void SessionHealthTracker_WithNullMetrics_ConstructsAndFunctions()
    {
        var clock = new SystemClock(TimeProvider.System);

        var tracker = new SessionHealthTracker(clock, metrics: null);

        tracker.Should().NotBeNull();
        tracker.ActiveSessionCount.Should().Be(0,
            "newly constructed tracker with null metrics should start at zero sessions");

        // Start a session — should not throw even with NullObject metrics
        using var handle = tracker.TrackSessionStart("websocket", "session-1");
        tracker.ActiveSessionCount.Should().Be(1);
    }

    /// <summary>
    /// EventSinkDispatcher with <c>metrics: null</c> must dispatch events without throwing.
    /// </summary>
    [Fact]
    public void EventSinkDispatcher_WithNullMetrics_ConstructsSuccessfully()
    {
        var clock = new SystemClock(TimeProvider.System);
        var sinks = new List<IHealthEventSink>();
        var options = new EventSinkDispatcherOptions();

        var act = () => new EventSinkDispatcher(
            sinks,
            options,
            clock,
            logger: null,
            metrics: null);

        act.Should().NotThrow(
            "EventSinkDispatcher must accept null metrics and default to NullObject");
    }

    /// <summary>
    /// ShutdownOrchestrator with <c>metrics: null</c> must construct without throwing.
    /// </summary>
    [Fact]
    public void ShutdownOrchestrator_WithNullMetrics_ConstructsSuccessfully()
    {
        var clock = new SystemClock(TimeProvider.System);
        var logger = NullLoggerFactory.Instance.CreateLogger<ShutdownOrchestrator>();
        var config = ShutdownConfig.Default;

        var act = () => new ShutdownOrchestrator(
            config,
            clock,
            logger,
            confirmDelegate: null,
            metrics: null);

        act.Should().NotThrow(
            "ShutdownOrchestrator must accept null metrics and default to NullObject");
    }

    /// <summary>
    /// DrainCoordinator with <c>metrics: null</c> must construct without throwing.
    /// </summary>
    [Fact]
    public void DrainCoordinator_WithNullMetrics_ConstructsSuccessfully()
    {
        var clock = new SystemClock(TimeProvider.System);
        var logger = NullLoggerFactory.Instance.CreateLogger<DrainCoordinator>();

        var act = () => new DrainCoordinator(
            clock,
            logger,
            timeProvider: null,
            metrics: null);

        act.Should().NotThrow(
            "DrainCoordinator must accept null metrics and default to NullObject");
    }

    // ─────────────────────────────────────────────────────────────────
    // [COMPAT] NullObject used in full DI orchestration path
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When AddHealthBoss() is used, the orchestrator receives the real HealthBossMetrics
    /// (not the NullObject). This confirms the DI wiring works end-to-end.
    /// </summary>
    [Fact]
    public void AddHealthBoss_Orchestrator_ReceivesRealMetrics_NotNullObject()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("redis"));

        using var provider = services.BuildServiceProvider();

        var metrics = provider.GetRequiredService<IComponentMetrics>();

        metrics.Should().NotBeNull();
        metrics.Should().BeOfType<HealthBossMetrics>(
            "DI should wire the real HealthBossMetrics, not NullHealthBossMetrics");
        metrics.Should().NotBeSameAs(NullHealthBossMetrics.Instance,
            "DI must provide the real implementation, not the NullObject fallback");
    }

    // ─────────────────────────────────────────────────────────────────
    // [COMPAT] Old code pattern — consuming IHealthBossMetrics directly
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Code that depends on the composed <see cref="IHealthBossMetrics"/> (pre-ISP split)
    /// must still work — the composed interface inherits all 5 sub-interfaces.
    /// </summary>
    [Fact]
    public void OldCode_DependingOn_IHealthBossMetrics_StillWorks()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("legacy-dep"));

        using var provider = services.BuildServiceProvider();

        var composed = provider.GetRequiredService<IHealthBossMetrics>();

        // Old code could call any method via the composed interface
        var act = () =>
        {
            composed.RecordSignal("comp", "Success");
            composed.SetActiveSessionCount(5);
            composed.RecordStateTransition("comp", "Healthy", "Degraded");
            composed.RecordTenantStatusChange("comp", "t1", "A", "B");
            composed.SetQuorumHealth("comp", 2, 3, true);
        };

        act.Should().NotThrow(
            "code using the composed IHealthBossMetrics must still work after ISP split");
    }

    /// <summary>
    /// NullHealthBossMetrics used as a fallback through the composed interface
    /// must also accept all method calls without throwing.
    /// </summary>
    [Fact]
    public void NullObject_ViaComposedInterface_AcceptsAllMethodCalls()
    {
        IHealthBossMetrics metrics = NullHealthBossMetrics.Instance;

        var act = () =>
        {
            metrics.RecordSignal("comp", "Fail");
            metrics.RecordAssessmentDuration("comp", 0.5);
            metrics.RecordInboundRequestDuration("comp", 1.0);
            metrics.RecordOutboundRequestDuration("comp", 2.0);
            metrics.SetHealthState("comp", HealthState.CircuitOpen);
            metrics.SetActiveSessionCount(0);
            metrics.SetDrainStatus(DrainStatus.Draining);
            metrics.RecordStateTransition("comp", "A", "B");
            metrics.RecordRecoveryProbeAttempt("comp");
            metrics.RecordRecoveryProbeSuccess("comp");
            metrics.RecordEventSinkDispatch();
            metrics.RecordEventSinkFailure("sink");
            metrics.RecordShutdownGateEvaluation("gate", true);
            metrics.RecordTenantStatusChange("comp", "t1", "A", "B");
            metrics.SetTenantCount("comp", 10);
            metrics.SetQuorumHealth("comp", 1, 1, true);
        };

        act.Should().NotThrow(
            "NullObject via composed interface must be a complete no-op");
    }

    // ─────────────────────────────────────────────────────────────────
    // [COMPAT] Narrow interface consumers are isolated from other interfaces
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A component depending on <see cref="IComponentMetrics"/> must only see
    /// the 5 methods from that interface — it doesn't need to know about
    /// session, state machine, tenant, or quorum metrics.
    /// </summary>
    [Fact]
    public void NarrowInterface_IComponentMetrics_ExposesOnly5Methods()
    {
        var methods = typeof(IComponentMetrics).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        methods.Should().HaveCount(5);
        methods.Select(m => m.Name).Should().BeEquivalentTo(
            "RecordSignal",
            "RecordAssessmentDuration",
            "RecordInboundRequestDuration",
            "RecordOutboundRequestDuration",
            "SetHealthState");
    }

    [Fact]
    public void NarrowInterface_ISessionMetrics_ExposesOnly2Methods()
    {
        var methods = typeof(ISessionMetrics).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        methods.Should().HaveCount(2);
        methods.Select(m => m.Name).Should().BeEquivalentTo(
            "SetActiveSessionCount",
            "SetDrainStatus");
    }

    [Fact]
    public void NarrowInterface_IStateMachineMetrics_ExposesOnly6Methods()
    {
        var methods = typeof(IStateMachineMetrics).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        methods.Should().HaveCount(6);
        methods.Select(m => m.Name).Should().BeEquivalentTo(
            "RecordStateTransition",
            "RecordRecoveryProbeAttempt",
            "RecordRecoveryProbeSuccess",
            "RecordEventSinkDispatch",
            "RecordEventSinkFailure",
            "RecordShutdownGateEvaluation");
    }

    [Fact]
    public void NarrowInterface_ITenantMetrics_ExposesOnly2Methods()
    {
        var methods = typeof(ITenantMetrics).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        methods.Should().HaveCount(2);
        methods.Select(m => m.Name).Should().BeEquivalentTo(
            "RecordTenantStatusChange",
            "SetTenantCount");
    }

    [Fact]
    public void NarrowInterface_IQuorumMetrics_ExposesOnly1Method()
    {
        var methods = typeof(IQuorumMetrics).GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        methods.Should().HaveCount(1);
        methods.Select(m => m.Name).Should().BeEquivalentTo(
            "SetQuorumHealth");
    }
}
