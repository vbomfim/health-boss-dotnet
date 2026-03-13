// <copyright file="IGrpcHealthSource.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Grpc;

/// <summary>
/// Abstraction over gRPC client subchannel connectivity state.
/// Since <c>Grpc.Net.Client</c> does not expose subchannel state directly
/// via public API (OQ-3), consumers implement this interface to provide
/// subchannel health data from their specific infrastructure.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe. Properties may be called concurrently
/// from multiple evaluation threads.
/// </remarks>
public interface IGrpcHealthSource
{
    /// <summary>
    /// Gets the number of subchannels currently in the <c>Ready</c> state.
    /// </summary>
    int ReadySubchannelCount { get; }

    /// <summary>
    /// Gets the total number of subchannels known to the gRPC client.
    /// </summary>
    int TotalSubchannelCount { get; }
}
