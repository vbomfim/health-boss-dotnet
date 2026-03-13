// <copyright file="TimerBudgetTypes.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Warning produced by timer budget validation when configured timings
/// may conflict with Kubernetes or ASP.NET hosting constraints.
/// </summary>
/// <param name="RuleName">Short identifier for the validation rule that fired (e.g. "LivenessWindowExceeded").</param>
/// <param name="Message">Human-readable description of the timing conflict and its potential impact.</param>
/// <param name="ConfigPath">Configuration path that triggered the warning (e.g. "HealthBoss:Components:SqlDb:RecoveryProbeInterval").</param>
/// <param name="IsCritical">When <c>true</c>, indicates the service should not start; when <c>false</c>, the warning is advisory.</param>
public sealed record TimerBudgetWarning(
    string RuleName,
    string Message,
    string ConfigPath,
    bool IsCritical);

/// <summary>
/// Options describing Kubernetes and ASP.NET hosting timing constraints
/// used by <see cref="HealthBoss.Core.ITimerBudgetValidator"/> to detect budget conflicts at startup.
/// A value of <see cref="TimeSpan.Zero"/> (or <c>0</c> for integers) means the constraint is unknown and the corresponding check is skipped.
/// </summary>
/// <param name="TerminationGracePeriod">Kubernetes <c>terminationGracePeriodSeconds</c>. Zero means unknown.</param>
/// <param name="LivenessFailureWindow">Kubernetes liveness probe <c>failureThreshold × periodSeconds</c>. Zero means unknown.</param>
/// <param name="ShutdownTimeout">ASP.NET <c>ShutdownTimeout</c> (typically 30 s). Zero means unknown.</param>
/// <param name="DrainTimeout">Graceful drain timeout before connections are forcibly closed. Zero means unknown.</param>
/// <param name="RecoveryRetryCount">Maximum number of recovery probe retries before giving up. Zero means unknown.</param>
/// <param name="ForceShutdownTimeout">Time allowed for forced shutdown after drain completes. Zero means unknown.</param>
public sealed record TimerBudgetOptions(
    TimeSpan TerminationGracePeriod = default,
    TimeSpan LivenessFailureWindow = default,
    TimeSpan ShutdownTimeout = default,
    TimeSpan DrainTimeout = default,
    int RecoveryRetryCount = 0,
    TimeSpan ForceShutdownTimeout = default);
