// <copyright file="IHealthBossMetrics.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core;

/// <summary>
/// Composed interface that unifies all domain-specific metric contracts.
/// <para>
/// Individual components should depend only on the narrow sub-interface they need
/// (Interface Segregation Principle). This composed interface exists for:
/// <list type="bullet">
///   <item>DI registration — the singleton <c>HealthBossMetrics</c> implements this.</item>
///   <item>Backward compatibility — existing code that depends on the full surface area.</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Refactored from a monolithic 15-method interface into 5 domain-specific interfaces
/// per Code Review Guardian finding (God Interface, HIGH). See GitHub Issue #61.
/// </remarks>
/// <seealso cref="IComponentMetrics"/>
/// <seealso cref="ISessionMetrics"/>
/// <seealso cref="IStateMachineMetrics"/>
/// <seealso cref="ITenantMetrics"/>
/// <seealso cref="IQuorumMetrics"/>
public interface IHealthBossMetrics
    : IComponentMetrics,
      ISessionMetrics,
      IStateMachineMetrics,
      ITenantMetrics,
      IQuorumMetrics
{
}
