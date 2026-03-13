// <copyright file="InstanceHealthResultTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Grpc.Tests;

public sealed class InstanceHealthResultTests
{
    [Fact]
    public void Can_create_healthy_instance()
    {
        var result = new InstanceHealthResult("instance-0", IsHealthy: true);

        result.InstanceId.Should().Be("instance-0");
        result.IsHealthy.Should().BeTrue();
        result.Metadata.Should().BeNull();
    }

    [Fact]
    public void Can_create_unhealthy_instance()
    {
        var result = new InstanceHealthResult("instance-1", IsHealthy: false);

        result.InstanceId.Should().Be("instance-1");
        result.IsHealthy.Should().BeFalse();
    }

    [Fact]
    public void Supports_optional_metadata()
    {
        var metadata = new Dictionary<string, string> { ["state"] = "TransientFailure" };
        var result = new InstanceHealthResult("instance-0", IsHealthy: false, Metadata: metadata);

        result.Metadata.Should().ContainKey("state")
            .WhoseValue.Should().Be("TransientFailure");
    }

    [Fact]
    public void Equality_is_structural()
    {
        var a = new InstanceHealthResult("instance-0", true);
        var b = new InstanceHealthResult("instance-0", true);

        a.Should().Be(b);
    }

    [Fact]
    public void Different_ids_are_not_equal()
    {
        var a = new InstanceHealthResult("instance-0", true);
        var b = new InstanceHealthResult("instance-1", true);

        a.Should().NotBe(b);
    }

    [Fact]
    public void Different_health_states_are_not_equal()
    {
        var a = new InstanceHealthResult("instance-0", true);
        var b = new InstanceHealthResult("instance-0", false);

        a.Should().NotBe(b);
    }
}
