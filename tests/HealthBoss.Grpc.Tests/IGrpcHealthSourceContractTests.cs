// <copyright file="IGrpcHealthSourceContractTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;

namespace HealthBoss.Grpc.Tests;

/// <summary>
/// Contract tests verifying the IGrpcHealthSource abstraction invariants.
/// </summary>
public sealed class IGrpcHealthSourceContractTests
{
    [Fact]
    public void FakeSource_ready_count_defaults_to_zero()
    {
        var source = new FakeGrpcHealthSource();

        source.ReadySubchannelCount.Should().Be(0);
    }

    [Fact]
    public void FakeSource_total_count_defaults_to_zero()
    {
        var source = new FakeGrpcHealthSource();

        source.TotalSubchannelCount.Should().Be(0);
    }

    [Fact]
    public void FakeSource_reflects_configured_values()
    {
        var source = new FakeGrpcHealthSource
        {
            ReadySubchannelCount = 7,
            TotalSubchannelCount = 10,
        };

        source.ReadySubchannelCount.Should().Be(7);
        source.TotalSubchannelCount.Should().Be(10);
    }
}
