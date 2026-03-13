// <copyright file="NullHealthBossMetrics.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Runtime.CompilerServices;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Null Object implementation of <see cref="IHealthBossMetrics"/>.
/// All methods are no-ops marked with <see cref="MethodImplOptions.AggressiveInlining"/>
/// so the JIT can eliminate the call entirely at the call site.
/// Used as a default when metrics are not configured.
/// </summary>
internal sealed class NullHealthBossMetrics : IHealthBossMetrics
{
    /// <summary>
    /// Singleton instance of <see cref="NullHealthBossMetrics"/>.
    /// </summary>
    public static readonly NullHealthBossMetrics Instance = new();

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordSignal(string component, string outcome)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordStateTransition(string component, string fromState, string toState)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordRecoveryProbeAttempt(string component)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordRecoveryProbeSuccess(string component)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordEventSinkDispatch()
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordEventSinkFailure(string sinkType)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordShutdownGateEvaluation(string gate, bool approved)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordAssessmentDuration(string component, double durationSeconds)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordInboundRequestDuration(string component, double durationSeconds)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordOutboundRequestDuration(string component, double durationSeconds)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetHealthState(string component, HealthState state)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetActiveSessionCount(int count)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDrainStatus(DrainStatus status)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetQuorumHealth(string component, int healthyInstances, int totalInstances, bool quorumMet)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordTenantStatusChange(string component, string tenantId, string fromStatus, string toStatus)
    {
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetTenantCount(string component, int count)
    {
    }
}
