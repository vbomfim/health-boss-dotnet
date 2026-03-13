// <copyright file="MetricsIspContractTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Reflection;
using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Contract tests for the Interface Segregation Principle (ISP) split of
/// <see cref="IHealthBossMetrics"/> into 5 domain-specific interfaces.
/// Verifies interface composition, method placement, implementation compliance,
/// DI registration, and consumer dependency narrowing.
/// See GitHub Issue #61.
/// </summary>
public sealed class MetricsIspContractTests
{
    // ─────────────────────────────────────────────────────────────────
    // [ISP] Interface Composition
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [ISP] IHealthBossMetrics MUST compose exactly 5 sub-interfaces.
    /// </summary>
    [Fact]
    public void IHealthBossMetrics_Composes_Exactly5_SubInterfaces()
    {
        var interfaces = typeof(IHealthBossMetrics).GetInterfaces();
        interfaces.Should().HaveCount(5,
            "IHealthBossMetrics should inherit from IComponentMetrics, ISessionMetrics, " +
            "IStateMachineMetrics, ITenantMetrics, IQuorumMetrics");
    }

    /// <summary>
    /// [ISP] IHealthBossMetrics MUST inherit from all 5 domain-specific interfaces.
    /// </summary>
    [Theory]
    [InlineData(typeof(IComponentMetrics))]
    [InlineData(typeof(ISessionMetrics))]
    [InlineData(typeof(IStateMachineMetrics))]
    [InlineData(typeof(ITenantMetrics))]
    [InlineData(typeof(IQuorumMetrics))]
    public void IHealthBossMetrics_Inherits_From_SubInterface(Type subInterface)
    {
        typeof(IHealthBossMetrics).Should().Implement(subInterface,
            $"IHealthBossMetrics must compose {subInterface.Name}");
    }

    /// <summary>
    /// [ISP] IHealthBossMetrics MUST NOT declare any methods of its own —
    /// all methods come from the 5 composed interfaces.
    /// </summary>
    [Fact]
    public void IHealthBossMetrics_HasNoOwnMethods()
    {
        // GetMethods with DeclaredOnly returns only methods declared on this interface, not inherited.
        var ownMethods = typeof(IHealthBossMetrics).GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        ownMethods.Should().BeEmpty(
            "IHealthBossMetrics should be a pure composition of sub-interfaces with no own methods");
    }

    // ─────────────────────────────────────────────────────────────────
    // [ISP] Sub-Interface Method Placement
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [ISP] IComponentMetrics MUST have exactly 5 methods.
    /// </summary>
    [Fact]
    public void IComponentMetrics_HasExpected_MethodCount()
    {
        var methods = typeof(IComponentMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        methods.Should().HaveCount(5,
            "IComponentMetrics: RecordSignal, RecordAssessmentDuration, RecordInboundRequestDuration, " +
            "RecordOutboundRequestDuration, SetHealthState");
    }

    /// <summary>
    /// [ISP] IComponentMetrics MUST define expected methods.
    /// </summary>
    [Theory]
    [InlineData("RecordSignal")]
    [InlineData("RecordAssessmentDuration")]
    [InlineData("RecordInboundRequestDuration")]
    [InlineData("RecordOutboundRequestDuration")]
    [InlineData("SetHealthState")]
    public void IComponentMetrics_HasMethod(string methodName)
    {
        typeof(IComponentMetrics).GetMethod(methodName).Should().NotBeNull(
            $"IComponentMetrics must define {methodName}");
    }

    /// <summary>
    /// [ISP] ISessionMetrics MUST have exactly 2 methods.
    /// </summary>
    [Fact]
    public void ISessionMetrics_HasExpected_MethodCount()
    {
        var methods = typeof(ISessionMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        methods.Should().HaveCount(2,
            "ISessionMetrics: SetActiveSessionCount, SetDrainStatus");
    }

    /// <summary>
    /// [ISP] ISessionMetrics MUST define expected methods.
    /// </summary>
    [Theory]
    [InlineData("SetActiveSessionCount")]
    [InlineData("SetDrainStatus")]
    public void ISessionMetrics_HasMethod(string methodName)
    {
        typeof(ISessionMetrics).GetMethod(methodName).Should().NotBeNull(
            $"ISessionMetrics must define {methodName}");
    }

    /// <summary>
    /// [ISP] IStateMachineMetrics MUST have exactly 6 methods.
    /// </summary>
    [Fact]
    public void IStateMachineMetrics_HasExpected_MethodCount()
    {
        var methods = typeof(IStateMachineMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        methods.Should().HaveCount(6,
            "IStateMachineMetrics: RecordStateTransition, RecordRecoveryProbeAttempt, " +
            "RecordRecoveryProbeSuccess, RecordEventSinkDispatch, RecordEventSinkFailure, " +
            "RecordShutdownGateEvaluation");
    }

    /// <summary>
    /// [ISP] IStateMachineMetrics MUST define expected methods.
    /// </summary>
    [Theory]
    [InlineData("RecordStateTransition")]
    [InlineData("RecordRecoveryProbeAttempt")]
    [InlineData("RecordRecoveryProbeSuccess")]
    [InlineData("RecordEventSinkDispatch")]
    [InlineData("RecordEventSinkFailure")]
    [InlineData("RecordShutdownGateEvaluation")]
    public void IStateMachineMetrics_HasMethod(string methodName)
    {
        typeof(IStateMachineMetrics).GetMethod(methodName).Should().NotBeNull(
            $"IStateMachineMetrics must define {methodName}");
    }

    /// <summary>
    /// [ISP] ITenantMetrics MUST have exactly 2 methods.
    /// </summary>
    [Fact]
    public void ITenantMetrics_HasExpected_MethodCount()
    {
        var methods = typeof(ITenantMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        methods.Should().HaveCount(2, "ITenantMetrics: RecordTenantStatusChange + SetTenantCount");
    }

    /// <summary>
    /// [ISP] IQuorumMetrics MUST have exactly 1 method.
    /// </summary>
    [Fact]
    public void IQuorumMetrics_HasExpected_MethodCount()
    {
        var methods = typeof(IQuorumMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        methods.Should().HaveCount(1, "IQuorumMetrics: SetQuorumHealth");
    }

    /// <summary>
    /// [ISP] Total methods across all 5 sub-interfaces MUST equal 16
    /// (expanded from 15 after adding RecordTenantStatusChange per Issue #62).
    /// </summary>
    [Fact]
    public void SubInterfaces_TotalMethodCount_Equals16()
    {
        int total =
            typeof(IComponentMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance).Length +
            typeof(ISessionMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance).Length +
            typeof(IStateMachineMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance).Length +
            typeof(ITenantMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance).Length +
            typeof(IQuorumMetrics).GetMethods(BindingFlags.Public | BindingFlags.Instance).Length;

        total.Should().Be(16, "5 sub-interfaces must total 16 methods after Issue #62 consolidation");
    }

    // ─────────────────────────────────────────────────────────────────
    // [ISP] Implementation Compliance
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [ISP] HealthBossMetrics MUST implement all 5 sub-interfaces.
    /// </summary>
    [Theory]
    [InlineData(typeof(IComponentMetrics))]
    [InlineData(typeof(ISessionMetrics))]
    [InlineData(typeof(IStateMachineMetrics))]
    [InlineData(typeof(ITenantMetrics))]
    [InlineData(typeof(IQuorumMetrics))]
    [InlineData(typeof(IHealthBossMetrics))]
    public void HealthBossMetrics_Implements_SubInterface(Type subInterface)
    {
        typeof(HealthBossMetrics).Should().Implement(subInterface,
            $"HealthBossMetrics must implement {subInterface.Name}");
    }

    /// <summary>
    /// [ISP] NullHealthBossMetrics MUST implement all 5 sub-interfaces.
    /// </summary>
    [Theory]
    [InlineData(typeof(IComponentMetrics))]
    [InlineData(typeof(ISessionMetrics))]
    [InlineData(typeof(IStateMachineMetrics))]
    [InlineData(typeof(ITenantMetrics))]
    [InlineData(typeof(IQuorumMetrics))]
    [InlineData(typeof(IHealthBossMetrics))]
    public void NullHealthBossMetrics_Implements_SubInterface(Type subInterface)
    {
        typeof(NullHealthBossMetrics).Should().Implement(subInterface,
            $"NullHealthBossMetrics must implement {subInterface.Name}");
    }

    /// <summary>
    /// [ISP] NullHealthBossMetrics.Instance MUST be castable to every sub-interface.
    /// </summary>
    [Fact]
    public void NullHealthBossMetrics_Instance_IsCastable_ToAllSubInterfaces()
    {
        var instance = NullHealthBossMetrics.Instance;

        (instance as IComponentMetrics).Should().NotBeNull();
        (instance as ISessionMetrics).Should().NotBeNull();
        (instance as IStateMachineMetrics).Should().NotBeNull();
        (instance as ITenantMetrics).Should().NotBeNull();
        (instance as IQuorumMetrics).Should().NotBeNull();
        (instance as IHealthBossMetrics).Should().NotBeNull();
    }

    // ─────────────────────────────────────────────────────────────────
    // [ISP] Consumer Dependency Narrowing
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [ISP] HealthOrchestrator constructor MUST accept IComponentMetrics (not IHealthBossMetrics).
    /// </summary>
    [Fact]
    public void HealthOrchestrator_Depends_On_IComponentMetrics()
    {
        var ctor = typeof(HealthOrchestrator).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .First();
        var metricsParam = ctor.GetParameters().FirstOrDefault(p => p.Name == "metrics");

        metricsParam.Should().NotBeNull("constructor must have a 'metrics' parameter");
        metricsParam!.ParameterType.Should().Be(typeof(IComponentMetrics),
            "HealthOrchestrator should depend on IComponentMetrics, not the full IHealthBossMetrics");
    }

    /// <summary>
    /// [ISP] EventSinkDispatcher constructor MUST accept IStateMachineMetrics.
    /// </summary>
    [Fact]
    public void EventSinkDispatcher_Depends_On_IStateMachineMetrics()
    {
        var ctor = typeof(EventSinkDispatcher).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First();
        var metricsParam = ctor.GetParameters().FirstOrDefault(p => p.Name == "metrics");

        metricsParam.Should().NotBeNull();
        metricsParam!.ParameterType.Should().Be(typeof(IStateMachineMetrics),
            "EventSinkDispatcher should depend on IStateMachineMetrics");
    }

    /// <summary>
    /// [ISP] ShutdownOrchestrator constructor MUST accept IStateMachineMetrics.
    /// </summary>
    [Fact]
    public void ShutdownOrchestrator_Depends_On_IStateMachineMetrics()
    {
        var ctor = typeof(ShutdownOrchestrator).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .First();
        var metricsParam = ctor.GetParameters().FirstOrDefault(p => p.Name == "metrics");

        metricsParam.Should().NotBeNull();
        metricsParam!.ParameterType.Should().Be(typeof(IStateMachineMetrics),
            "ShutdownOrchestrator should depend on IStateMachineMetrics");
    }

    /// <summary>
    /// [ISP] RecoveryProber constructor MUST accept IStateMachineMetrics.
    /// </summary>
    [Fact]
    public void RecoveryProber_Depends_On_IStateMachineMetrics()
    {
        var ctor = typeof(RecoveryProber).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .First();
        var metricsParam = ctor.GetParameters().FirstOrDefault(p => p.Name == "metrics");

        metricsParam.Should().NotBeNull();
        metricsParam!.ParameterType.Should().Be(typeof(IStateMachineMetrics),
            "RecoveryProber should depend on IStateMachineMetrics");
    }

    /// <summary>
    /// [ISP] SessionHealthTracker constructor MUST accept ISessionMetrics.
    /// </summary>
    [Fact]
    public void SessionHealthTracker_Depends_On_ISessionMetrics()
    {
        var ctor = typeof(SessionHealthTracker).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .First();
        var metricsParam = ctor.GetParameters().FirstOrDefault(p => p.Name == "metrics");

        metricsParam.Should().NotBeNull();
        metricsParam!.ParameterType.Should().Be(typeof(ISessionMetrics),
            "SessionHealthTracker should depend on ISessionMetrics");
    }

    /// <summary>
    /// [ISP] DrainCoordinator constructor MUST accept ISessionMetrics.
    /// </summary>
    [Fact]
    public void DrainCoordinator_Depends_On_ISessionMetrics()
    {
        var ctor = typeof(DrainCoordinator).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .First();
        var metricsParam = ctor.GetParameters().FirstOrDefault(p => p.Name == "metrics");

        metricsParam.Should().NotBeNull();
        metricsParam!.ParameterType.Should().Be(typeof(ISessionMetrics),
            "DrainCoordinator should depend on ISessionMetrics");
    }

    // ─────────────────────────────────────────────────────────────────
    // [ISP] DI Registration — All sub-interfaces resolve to same singleton
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// [ISP][DI] All 5 sub-interfaces and IHealthBossMetrics MUST resolve from DI.
    /// </summary>
    [Fact]
    public void DI_AllSubInterfaces_Resolve_FromContainer()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("test-component"));

        using var provider = services.BuildServiceProvider();

        provider.GetService<IHealthBossMetrics>().Should().NotBeNull();
        provider.GetService<IComponentMetrics>().Should().NotBeNull();
        provider.GetService<ISessionMetrics>().Should().NotBeNull();
        provider.GetService<IStateMachineMetrics>().Should().NotBeNull();
        provider.GetService<ITenantMetrics>().Should().NotBeNull();
        provider.GetService<IQuorumMetrics>().Should().NotBeNull();
    }

    /// <summary>
    /// [ISP][DI] All 6 interface registrations MUST resolve to the same singleton instance.
    /// </summary>
    [Fact]
    public void DI_AllSubInterfaces_Resolve_ToSameSingleton()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("test-component"));

        using var provider = services.BuildServiceProvider();

        var full = provider.GetRequiredService<IHealthBossMetrics>();
        var component = provider.GetRequiredService<IComponentMetrics>();
        var session = provider.GetRequiredService<ISessionMetrics>();
        var stateMachine = provider.GetRequiredService<IStateMachineMetrics>();
        var tenant = provider.GetRequiredService<ITenantMetrics>();
        var quorum = provider.GetRequiredService<IQuorumMetrics>();

        // All should be the same object reference (singleton)
        component.Should().BeSameAs(full, "IComponentMetrics should resolve to same singleton");
        session.Should().BeSameAs(full, "ISessionMetrics should resolve to same singleton");
        stateMachine.Should().BeSameAs(full, "IStateMachineMetrics should resolve to same singleton");
        tenant.Should().BeSameAs(full, "ITenantMetrics should resolve to same singleton");
        quorum.Should().BeSameAs(full, "IQuorumMetrics should resolve to same singleton");
    }

    /// <summary>
    /// [ISP][DI] All resolved sub-interfaces MUST be of type HealthBossMetrics.
    /// </summary>
    [Fact]
    public void DI_AllSubInterfaces_ResolveAs_HealthBossMetrics()
    {
        var services = new ServiceCollection();
        services.AddHealthBoss(opts => opts.AddComponent("test-component"));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IComponentMetrics>().Should().BeOfType<HealthBossMetrics>();
        provider.GetRequiredService<ISessionMetrics>().Should().BeOfType<HealthBossMetrics>();
        provider.GetRequiredService<IStateMachineMetrics>().Should().BeOfType<HealthBossMetrics>();
        provider.GetRequiredService<ITenantMetrics>().Should().BeOfType<HealthBossMetrics>();
        provider.GetRequiredService<IQuorumMetrics>().Should().BeOfType<HealthBossMetrics>();
    }
}
