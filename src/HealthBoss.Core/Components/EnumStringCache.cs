// <copyright file="EnumStringCache.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Collections.Frozen;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Pre-computed enum-to-string lookup tables using <see cref="FrozenDictionary{TKey, TValue}"/>.
/// Eliminates <c>Enum.ToString()</c> allocations on hot metric-recording paths.
/// <para>
/// <c>FrozenDictionary</c> is optimized for read-heavy scenarios with zero-allocation lookups.
/// Each dictionary is built once at type initialization and shared across all threads.
/// </para>
/// </summary>
/// <remarks>
/// Issue #64: Fix hot-path ToString() allocations identified by Code Review Guardian.
/// </remarks>
internal static class EnumStringCache
{
    /// <summary>
    /// Cached string representations for <see cref="SignalOutcome"/> values.
    /// Used by <see cref="HealthOrchestrator"/> on every signal recording call.
    /// </summary>
    public static readonly FrozenDictionary<SignalOutcome, string> SignalOutcomeNames =
        CreateEnumCache<SignalOutcome>();

    /// <summary>
    /// Cached string representations for <see cref="HealthState"/> values.
    /// Used by <see cref="OpenTelemetryMetricEventSink"/> for state transition tags.
    /// </summary>
    public static readonly FrozenDictionary<HealthState, string> HealthStateNames =
        CreateEnumCache<HealthState>();

    /// <summary>
    /// Cached string representations for <see cref="TenantHealthStatus"/> values.
    /// Used by <see cref="OpenTelemetryMetricEventSink"/> for tenant status change tags.
    /// </summary>
    public static readonly FrozenDictionary<TenantHealthStatus, string> TenantHealthStatusNames =
        CreateEnumCache<TenantHealthStatus>();

    /// <summary>
    /// Creates a <see cref="FrozenDictionary{TKey, TValue}"/> mapping every value
    /// of <typeparamref name="TEnum"/> to its <c>ToString()</c> representation.
    /// Called once per enum type at static initialization.
    /// </summary>
    private static FrozenDictionary<TEnum, string> CreateEnumCache<TEnum>()
        where TEnum : struct, Enum
    {
        return Enum.GetValues<TEnum>()
            .ToFrozenDictionary(v => v, v => v.ToString());
    }
}
