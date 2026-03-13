// <copyright file="QuorumTypes.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Policy configuration for instance-quorum health evaluation.
/// Determines whether enough instances are healthy to consider the service operational.
/// </summary>
/// <param name="MinimumHealthyInstances">
/// Minimum number of healthy instances required to meet quorum. Must be ≥ 1.
/// </param>
/// <param name="TotalExpectedInstances">
/// Total number of expected instances. Use 0 when the fleet size is unknown or dynamic.
/// Must be ≥ 0.
/// </param>
/// <param name="ProbeInterval">Interval between health probe cycles.</param>
/// <param name="ProbeTimeout">Timeout for each individual probe request.</param>
public sealed record QuorumHealthPolicy(
    int MinimumHealthyInstances,
    int TotalExpectedInstances = 0,
    TimeSpan ProbeInterval = default,
    TimeSpan ProbeTimeout = default);

/// <summary>
/// Result of probing a single instance's health.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InstanceId"/> must be an opaque identifier (e.g., "instance-0")
/// and must never contain IP addresses, hostnames, or ports to prevent
/// information disclosure (Security Finding #9).
/// </para>
/// </remarks>
/// <param name="InstanceId">An opaque identifier for the instance. Must not contain network addresses.</param>
/// <param name="IsHealthy">Whether the instance responded as healthy.</param>
/// <param name="ResponseTime">How long the probe took to complete (default for adapters that don't measure latency).</param>
/// <param name="Metadata">Optional metadata dictionary for extensibility.</param>
public sealed record InstanceHealthResult(
    string InstanceId,
    bool IsHealthy,
    TimeSpan ResponseTime = default,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// The result of evaluating instance health results against a <see cref="QuorumHealthPolicy"/>.
/// Immutable snapshot of quorum health at a point in time.
/// </summary>
/// <param name="HealthyInstances">Number of instances that reported healthy.</param>
/// <param name="TotalInstances">Total number of instances probed.</param>
/// <param name="MinimumRequired">The quorum threshold from the policy.</param>
/// <param name="QuorumMet">Whether the healthy count meets or exceeds <paramref name="MinimumRequired"/>.</param>
/// <param name="Status">The recommended <see cref="HealthState"/> based on quorum evaluation.</param>
/// <param name="InstanceResults">Per-instance probe results included for diagnostics.</param>
public sealed record QuorumAssessment(
    int HealthyInstances,
    int TotalInstances,
    int MinimumRequired,
    bool QuorumMet,
    HealthState Status,
    IReadOnlyList<InstanceHealthResult> InstanceResults);
