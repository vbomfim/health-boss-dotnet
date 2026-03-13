// <copyright file="TimerBudgetValidator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Validates that registered component timer budgets are consistent
/// with Kubernetes and ASP.NET hosting constraints.
/// Layer 1 (Leaf) — depends only on Contracts.
/// </summary>
internal sealed class TimerBudgetValidator : ITimerBudgetValidator
{
    /// <inheritdoc />
    public IReadOnlyList<TimerBudgetWarning> Validate(
        IReadOnlyDictionary<DependencyId, HealthPolicy> policies,
        TimerBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(options);

        if (policies.Count == 0)
        {
            return [];
        }

        var warnings = new List<TimerBudgetWarning>();

        foreach (var (dependencyId, policy) in policies)
        {
            string depName = dependencyId.ToString();
            ValidatePolicy(depName, policy, options, warnings);
        }

        return warnings;
    }

    private static void ValidatePolicy(
        string depName,
        HealthPolicy policy,
        TimerBudgetOptions options,
        List<TimerBudgetWarning> warnings)
    {
        CheckLivenessWindow(depName, policy, options, warnings);
        CheckTerminationGracePeriod(depName, options, warnings);
        CheckCooldownVsProbeInterval(depName, policy, warnings);
        CheckJitterDominatesCooldown(depName, policy, warnings);
    }

    /// <summary>
    /// Rule 1: RecoveryRetryCount × RecoveryProbeInterval + DrainTimeout > LivenessFailureWindow.
    /// Skipped when any required value is zero (unknown).
    /// </summary>
    private static void CheckLivenessWindow(
        string depName,
        HealthPolicy policy,
        TimerBudgetOptions options,
        List<TimerBudgetWarning> warnings)
    {
        if (options.LivenessFailureWindow <= TimeSpan.Zero ||
            options.RecoveryRetryCount <= 0 ||
            options.DrainTimeout <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan recoveryBudget =
            TimeSpan.FromTicks(options.RecoveryRetryCount * policy.RecoveryProbeInterval.Ticks)
            + options.DrainTimeout;

        if (recoveryBudget > options.LivenessFailureWindow)
        {
            warnings.Add(new TimerBudgetWarning(
                RuleName: "LivenessWindowExceeded",
                Message: $"Recovery budget ({recoveryBudget.TotalSeconds:F0}s = " +
                         $"{options.RecoveryRetryCount} × {policy.RecoveryProbeInterval.TotalSeconds:F0}s + " +
                         $"{options.DrainTimeout.TotalSeconds:F0}s drain) exceeds liveness failure window " +
                         $"({options.LivenessFailureWindow.TotalSeconds:F0}s). " +
                         "Kubernetes may kill the pod before drain completes.",
                ConfigPath: $"HealthBoss:Components:{depName}:RecoveryProbeInterval",
                IsCritical: false));
        }
    }

    /// <summary>
    /// Rule 2: DrainTimeout + ForceShutdownTimeout > TerminationGracePeriod.
    /// Skipped when any required value is zero (unknown).
    /// </summary>
    private static void CheckTerminationGracePeriod(
        string depName,
        TimerBudgetOptions options,
        List<TimerBudgetWarning> warnings)
    {
        if (options.TerminationGracePeriod <= TimeSpan.Zero ||
            options.DrainTimeout <= TimeSpan.Zero ||
            options.ForceShutdownTimeout <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan shutdownBudget = options.DrainTimeout + options.ForceShutdownTimeout;

        if (shutdownBudget > options.TerminationGracePeriod)
        {
            warnings.Add(new TimerBudgetWarning(
                RuleName: "TerminationGracePeriodExceeded",
                Message: $"Shutdown budget ({shutdownBudget.TotalSeconds:F0}s = " +
                         $"{options.DrainTimeout.TotalSeconds:F0}s drain + " +
                         $"{options.ForceShutdownTimeout.TotalSeconds:F0}s force) exceeds " +
                         $"terminationGracePeriod ({options.TerminationGracePeriod.TotalSeconds:F0}s). " +
                         "SIGKILL will arrive before graceful shutdown completes.",
                ConfigPath: $"HealthBoss:Components:{depName}:DrainTimeout",
                IsCritical: false));
        }
    }

    /// <summary>
    /// Rule 3: CooldownBeforeTransition &lt; RecoveryProbeInterval.
    /// Skipped when CooldownBeforeTransition is zero.
    /// </summary>
    private static void CheckCooldownVsProbeInterval(
        string depName,
        HealthPolicy policy,
        List<TimerBudgetWarning> warnings)
    {
        if (policy.CooldownBeforeTransition <= TimeSpan.Zero)
        {
            return;
        }

        if (policy.CooldownBeforeTransition < policy.RecoveryProbeInterval)
        {
            warnings.Add(new TimerBudgetWarning(
                RuleName: "CooldownBelowProbeInterval",
                Message: $"CooldownBeforeTransition ({policy.CooldownBeforeTransition.TotalSeconds:F0}s) " +
                         $"is less than RecoveryProbeInterval ({policy.RecoveryProbeInterval.TotalSeconds:F0}s). " +
                         "Transition may fire before the next recovery probe completes.",
                ConfigPath: $"HealthBoss:Components:{depName}:CooldownBeforeTransition",
                IsCritical: false));
        }
    }

    /// <summary>
    /// Rule 4: Jitter.MaxDelay > CooldownBeforeTransition / 2.
    /// Skipped when CooldownBeforeTransition is zero.
    /// </summary>
    private static void CheckJitterDominatesCooldown(
        string depName,
        HealthPolicy policy,
        List<TimerBudgetWarning> warnings)
    {
        if (policy.CooldownBeforeTransition <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan halfCooldown = policy.CooldownBeforeTransition / 2;

        if (policy.Jitter.MaxDelay > halfCooldown)
        {
            warnings.Add(new TimerBudgetWarning(
                RuleName: "JitterDominatesCooldown",
                Message: $"Jitter MaxDelay ({policy.Jitter.MaxDelay.TotalSeconds:F1}s) exceeds half " +
                         $"of CooldownBeforeTransition ({policy.CooldownBeforeTransition.TotalSeconds:F0}s / 2 = " +
                         $"{halfCooldown.TotalSeconds:F1}s). " +
                         "Jitter may dominate the cooldown window.",
                ConfigPath: $"HealthBoss:Components:{depName}:Jitter",
                IsCritical: false));
        }
    }
}
