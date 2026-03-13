// <copyright file="FakeGrpcHealthSource.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Grpc.Tests;

/// <summary>
/// Configurable test double for <see cref="IGrpcHealthSource"/>.
/// </summary>
internal sealed class FakeGrpcHealthSource : IGrpcHealthSource
{
    public int ReadySubchannelCount { get; set; }
    public int TotalSubchannelCount { get; set; }
}
