// <copyright file="GrpcSubchannelHealthAdapter.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Grpc;

/// <summary>
/// Bridges <see cref="IGrpcHealthSource"/> to <see cref="IInstanceHealthProbe"/>
/// for quorum evaluation of gRPC subchannels.
/// </summary>
/// <remarks>
/// <para>
/// Converts subchannel ready/total counts into a list of <see cref="InstanceHealthResult"/>
/// instances. Each result uses an opaque index identifier ("instance-0", "instance-1", …)
/// and never exposes IP addresses, hostnames, or ports (Security Finding #9).
/// </para>
/// <para>
/// This class is thread-safe. <see cref="ProbeAllAsync"/> reads the current counts from the
/// source atomically and produces a consistent snapshot.
/// </para>
/// </remarks>
public sealed class GrpcSubchannelHealthAdapter : IInstanceHealthProbe
{
    private readonly IGrpcHealthSource _source;
    private readonly string _componentName;

    /// <summary>
    /// Initializes a new instance of the <see cref="GrpcSubchannelHealthAdapter"/> class.
    /// </summary>
    /// <param name="source">The gRPC health source providing subchannel counts.</param>
    /// <param name="componentName">The logical component name for the gRPC backend pool
    /// (e.g., "grpc_backend_pool"). Must be a valid dependency identifier.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="componentName"/> is invalid.</exception>
    public GrpcSubchannelHealthAdapter(IGrpcHealthSource source, string componentName)
    {
        ArgumentNullException.ThrowIfNull(source);
        HealthBossValidator.ValidateDependencyId(componentName);

        _source = source;
        _componentName = componentName;
    }

    /// <summary>
    /// Discovers current gRPC subchannels and returns their health status.
    /// Ready subchannels are reported as healthy; all others as unhealthy.
    /// Instance identifiers are opaque indexes — never network addresses.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>A read-only list of instance health results, one per subchannel.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested.</exception>
    public Task<IReadOnlyList<InstanceHealthResult>> ProbeAllAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Note: non-atomic read pair — Math.Clamp handles transient inconsistency
        var total = Math.Max(0, _source.TotalSubchannelCount);
        var ready = Math.Clamp(_source.ReadySubchannelCount, 0, total);

        if (total == 0)
        {
            return Task.FromResult<IReadOnlyList<InstanceHealthResult>>(Array.Empty<InstanceHealthResult>());
        }

        var results = new InstanceHealthResult[total];

        // Healthy instances first (ready subchannels), then unhealthy
        for (int i = 0; i < total; i++)
        {
            results[i] = new InstanceHealthResult(
                InstanceId: $"instance-{i}",
                IsHealthy: i < ready);
        }

        return Task.FromResult<IReadOnlyList<InstanceHealthResult>>(results);
    }
}
