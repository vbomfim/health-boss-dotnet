// <copyright file="NullHealthBossMetricsTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Reflection;
using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Comprehensive tests for the <see cref="NullHealthBossMetrics"/> Null Object implementation.
/// Verifies singleton identity, interface compliance, no-op safety, and usability as a
/// default parameter value in constructors.
/// Issue #66 — Missing integration/NullObject/compat tests.
/// </summary>
public sealed class NullHealthBossMetricsTests
{
    // ─────────────────────────────────────────────────────────────────
    // [SINGLETON] Instance identity and uniqueness
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// NullHealthBossMetrics.Instance must always return the same reference (true singleton).
    /// </summary>
    [Fact]
    public void Instance_IsReferenceEqual_AcrossMultipleAccesses()
    {
        var a = NullHealthBossMetrics.Instance;
        var b = NullHealthBossMetrics.Instance;
        var c = NullHealthBossMetrics.Instance;

        a.Should().BeSameAs(b);
        b.Should().BeSameAs(c);
        ReferenceEquals(a, c).Should().BeTrue("Instance must be a true singleton");
    }

    /// <summary>
    /// Instance must not be null — it's a static readonly field initialized at class load.
    /// </summary>
    [Fact]
    public void Instance_IsNotNull()
    {
        NullHealthBossMetrics.Instance.Should().NotBeNull();
    }

    // ─────────────────────────────────────────────────────────────────
    // [ISP] Implements all 5 sub-interfaces + composed interface
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(IComponentMetrics))]
    [InlineData(typeof(ISessionMetrics))]
    [InlineData(typeof(IStateMachineMetrics))]
    [InlineData(typeof(ITenantMetrics))]
    [InlineData(typeof(IQuorumMetrics))]
    [InlineData(typeof(IHealthBossMetrics))]
    public void Instance_Implements_SubInterface(Type interfaceType)
    {
        NullHealthBossMetrics.Instance.Should().BeAssignableTo(interfaceType,
            $"NullHealthBossMetrics must implement {interfaceType.Name} " +
            "to be a valid substitute for the real implementation");
    }

    /// <summary>
    /// Casting Instance to each sub-interface must succeed and yield the same reference.
    /// </summary>
    [Fact]
    public void Instance_CastToSubInterfaces_RetainsSameReference()
    {
        var instance = NullHealthBossMetrics.Instance;

        IComponentMetrics component = instance;
        ISessionMetrics session = instance;
        IStateMachineMetrics stateMachine = instance;
        ITenantMetrics tenant = instance;
        IQuorumMetrics quorum = instance;
        IHealthBossMetrics composed = instance;

        component.Should().BeSameAs(instance);
        session.Should().BeSameAs(instance);
        stateMachine.Should().BeSameAs(instance);
        tenant.Should().BeSameAs(instance);
        quorum.Should().BeSameAs(instance);
        composed.Should().BeSameAs(instance);
    }

    // ─────────────────────────────────────────────────────────────────
    // [NO-OP] Every method is a safe no-op — doesn't throw
    // ─────────────────────────────────────────────────────────────────

    // IComponentMetrics methods (5)

    [Fact]
    public void RecordSignal_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordSignal("comp", "Success");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordAssessmentDuration_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordAssessmentDuration("comp", 0.5);
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordInboundRequestDuration_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordInboundRequestDuration("comp", 1.23);
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordOutboundRequestDuration_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordOutboundRequestDuration("comp", 0.001);
        act.Should().NotThrow();
    }

    [Fact]
    public void SetHealthState_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.SetHealthState("comp", HealthState.Degraded);
        act.Should().NotThrow();
    }

    // ISessionMetrics methods (2)

    [Fact]
    public void SetActiveSessionCount_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.SetActiveSessionCount(42);
        act.Should().NotThrow();
    }

    [Fact]
    public void SetDrainStatus_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.SetDrainStatus(DrainStatus.Draining);
        act.Should().NotThrow();
    }

    // IStateMachineMetrics methods (6)

    [Fact]
    public void RecordStateTransition_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordStateTransition(
            "comp", "Healthy", "Degraded");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordRecoveryProbeAttempt_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordRecoveryProbeAttempt("comp");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordRecoveryProbeSuccess_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordRecoveryProbeSuccess("comp");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordEventSinkDispatch_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordEventSinkDispatch();
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordEventSinkFailure_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordEventSinkFailure("OTelSink");
        act.Should().NotThrow();
    }

    [Fact]
    public void RecordShutdownGateEvaluation_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordShutdownGateEvaluation("drain", true);
        act.Should().NotThrow();
    }

    // ITenantMetrics methods (2)

    [Fact]
    public void RecordTenantStatusChange_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.RecordTenantStatusChange(
            "comp", "tenant-1", "Healthy", "Degraded");
        act.Should().NotThrow();
    }

    [Fact]
    public void SetTenantCount_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.SetTenantCount("comp", 100);
        act.Should().NotThrow();
    }

    // IQuorumMetrics methods (1)

    [Fact]
    public void SetQuorumHealth_IsNoOp()
    {
        var act = () => NullHealthBossMetrics.Instance.SetQuorumHealth("comp", 3, 5, true);
        act.Should().NotThrow();
    }

    // ─────────────────────────────────────────────────────────────────
    // [NO-OP] All 16 methods in a single rapid-fire call — no throw
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AllMethods_CalledRapidly_NeverThrow()
    {
        var m = NullHealthBossMetrics.Instance;

        var act = () =>
        {
            // IComponentMetrics
            m.RecordSignal("c", "Success");
            m.RecordAssessmentDuration("c", 0.042);
            m.RecordInboundRequestDuration("c", 0.125);
            m.RecordOutboundRequestDuration("c", 0.300);
            m.SetHealthState("c", HealthState.Healthy);

            // ISessionMetrics
            m.SetActiveSessionCount(0);
            m.SetDrainStatus(DrainStatus.Idle);

            // IStateMachineMetrics
            m.RecordStateTransition("c", "Healthy", "Degraded");
            m.RecordRecoveryProbeAttempt("c");
            m.RecordRecoveryProbeSuccess("c");
            m.RecordEventSinkDispatch();
            m.RecordEventSinkFailure("sink");
            m.RecordShutdownGateEvaluation("gate", false);

            // ITenantMetrics
            m.RecordTenantStatusChange("c", "t", "Healthy", "Degraded");
            m.SetTenantCount("c", 50);

            // IQuorumMetrics
            m.SetQuorumHealth("c", 2, 3, true);
        };

        act.Should().NotThrow("all 16 NullObject methods must be safe no-ops");
    }

    // ─────────────────────────────────────────────────────────────────
    // [NO-OP] Boundary values — no exceptions even for extreme inputs
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NoOp_Methods_AcceptBoundaryValues()
    {
        var m = NullHealthBossMetrics.Instance;

        var act = () =>
        {
            m.RecordSignal("", "");
            m.RecordAssessmentDuration("", double.MaxValue);
            m.RecordAssessmentDuration("", double.MinValue);
            m.RecordAssessmentDuration("", 0.0);
            m.RecordAssessmentDuration("", double.NaN);
            m.RecordAssessmentDuration("", double.PositiveInfinity);
            m.RecordInboundRequestDuration("", double.NegativeInfinity);
            m.RecordOutboundRequestDuration("", -1.0);
            m.SetHealthState("", HealthState.Healthy);
            m.SetActiveSessionCount(int.MaxValue);
            m.SetActiveSessionCount(int.MinValue);
            m.SetActiveSessionCount(0);
            m.SetDrainStatus((DrainStatus)999);
            m.SetQuorumHealth("", -1, 0, false);
            m.SetTenantCount("", int.MinValue);
        };

        act.Should().NotThrow(
            "NullObject must accept any input without throwing — " +
            "it's a no-op by contract");
    }

    // ─────────────────────────────────────────────────────────────────
    // [SEALED] Class is sealed — prevents inheritance
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NullHealthBossMetrics_IsSealed()
    {
        typeof(NullHealthBossMetrics).IsSealed.Should().BeTrue(
            "NullObject must be sealed to prevent subclassing that could break the singleton contract");
    }

    // ─────────────────────────────────────────────────────────────────
    // [METHOD-COUNT] All 16 methods from 5 interfaces are present
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void NullHealthBossMetrics_Implements_All16Methods()
    {
        // Get all public instance methods declared or inherited from the 5 sub-interfaces
        var interfaceMethods = typeof(IHealthBossMetrics)
            .GetInterfaces()
            .SelectMany(i => i.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        // Each interface method must have an implementation in NullHealthBossMetrics
        foreach (var methodName in interfaceMethods)
        {
            typeof(NullHealthBossMetrics)
                .GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
                .Should().NotBeNull(
                    $"NullHealthBossMetrics must implement {methodName}");
        }

        interfaceMethods.Should().HaveCount(16,
            "IHealthBossMetrics composes 16 methods across 5 sub-interfaces");
    }

    // ─────────────────────────────────────────────────────────────────
    // [DEFAULT-PARAM] Works as default constructor parameter value
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Instance_WorksAs_DefaultParameter_ForIComponentMetrics()
    {
        // Simulates: void Ctor(IComponentMetrics? metrics = null) => _metrics = metrics ?? NullHealthBossMetrics.Instance
        IComponentMetrics? injected = null;
        IComponentMetrics effective = injected ?? NullHealthBossMetrics.Instance;

        effective.Should().BeSameAs(NullHealthBossMetrics.Instance);
        var act = () => effective.RecordSignal("test", "Success");
        act.Should().NotThrow();
    }

    [Fact]
    public void Instance_WorksAs_DefaultParameter_ForISessionMetrics()
    {
        ISessionMetrics? injected = null;
        ISessionMetrics effective = injected ?? NullHealthBossMetrics.Instance;

        effective.Should().BeSameAs(NullHealthBossMetrics.Instance);
        var act = () => effective.SetActiveSessionCount(10);
        act.Should().NotThrow();
    }

    [Fact]
    public void Instance_WorksAs_DefaultParameter_ForIStateMachineMetrics()
    {
        IStateMachineMetrics? injected = null;
        IStateMachineMetrics effective = injected ?? NullHealthBossMetrics.Instance;

        effective.Should().BeSameAs(NullHealthBossMetrics.Instance);
        var act = () => effective.RecordStateTransition("c", "A", "B");
        act.Should().NotThrow();
    }

    [Fact]
    public void Instance_WorksAs_DefaultParameter_ForITenantMetrics()
    {
        ITenantMetrics? injected = null;
        ITenantMetrics effective = injected ?? NullHealthBossMetrics.Instance;

        effective.Should().BeSameAs(NullHealthBossMetrics.Instance);
        var act = () => effective.RecordTenantStatusChange("c", "t1", "A", "B");
        act.Should().NotThrow();
    }

    [Fact]
    public void Instance_WorksAs_DefaultParameter_ForIQuorumMetrics()
    {
        IQuorumMetrics? injected = null;
        IQuorumMetrics effective = injected ?? NullHealthBossMetrics.Instance;

        effective.Should().BeSameAs(NullHealthBossMetrics.Instance);
        var act = () => effective.SetQuorumHealth("c", 1, 3, false);
        act.Should().NotThrow();
    }

    [Fact]
    public void Instance_WorksAs_DefaultParameter_ForIHealthBossMetrics()
    {
        IHealthBossMetrics? injected = null;
        IHealthBossMetrics effective = injected ?? NullHealthBossMetrics.Instance;

        effective.Should().BeSameAs(NullHealthBossMetrics.Instance);
        var act = () =>
        {
            effective.RecordSignal("c", "OK");
            effective.SetActiveSessionCount(1);
            effective.RecordStateTransition("c", "A", "B");
        };
        act.Should().NotThrow();
    }

    // ─────────────────────────────────────────────────────────────────
    // [INLINE] All methods have AggressiveInlining attribute
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AllPublicMethods_HaveAggressiveInlining()
    {
        var methods = typeof(NullHealthBossMetrics)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        foreach (var method in methods)
        {
            var implAttr = method.GetMethodImplementationFlags();
            (implAttr & MethodImplAttributes.AggressiveInlining).Should().Be(
                MethodImplAttributes.AggressiveInlining,
                $"{method.Name} must be marked [MethodImpl(AggressiveInlining)] for zero-overhead no-ops");
        }

        methods.Should().HaveCountGreaterThan(0,
            "NullHealthBossMetrics must declare public methods");
    }
}
