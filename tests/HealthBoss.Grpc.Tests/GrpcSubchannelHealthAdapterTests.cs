// <copyright file="GrpcSubchannelHealthAdapterTests.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using FluentAssertions;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Grpc.Tests;

public sealed class GrpcSubchannelHealthAdapterTests
{
    private readonly FakeGrpcHealthSource _source = new();

    [Fact]
    public void Constructor_throws_when_source_is_null()
    {
        var act = () => new GrpcSubchannelHealthAdapter(null!, "grpc_backend_pool");

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("source");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_throws_when_componentName_is_invalid(string? name)
    {
        var act = () => new GrpcSubchannelHealthAdapter(_source, name!);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ProbeAllAsync_returns_empty_when_total_is_zero()
    {
        _source.TotalSubchannelCount = 0;
        _source.ReadySubchannelCount = 0;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeAllAsync_returns_all_healthy_when_all_ready()
    {
        _source.TotalSubchannelCount = 3;
        _source.ReadySubchannelCount = 3;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().HaveCount(3);
        results.Should().AllSatisfy(r => r.IsHealthy.Should().BeTrue());
    }

    [Fact]
    public async Task ProbeAllAsync_returns_unhealthy_for_non_ready_subchannels()
    {
        _source.TotalSubchannelCount = 5;
        _source.ReadySubchannelCount = 2;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().HaveCount(5);
        results.Count(r => r.IsHealthy).Should().Be(2);
        results.Count(r => !r.IsHealthy).Should().Be(3);
    }

    [Fact]
    public async Task ProbeAllAsync_returns_all_unhealthy_when_none_ready()
    {
        _source.TotalSubchannelCount = 4;
        _source.ReadySubchannelCount = 0;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().HaveCount(4);
        results.Should().AllSatisfy(r => r.IsHealthy.Should().BeFalse());
    }

    [Fact]
    public async Task InstanceIds_are_opaque_indexes_not_hostnames()
    {
        _source.TotalSubchannelCount = 3;
        _source.ReadySubchannelCount = 3;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results[0].InstanceId.Should().Be("instance-0");
        results[1].InstanceId.Should().Be("instance-1");
        results[2].InstanceId.Should().Be("instance-2");
    }

    [Fact]
    public async Task InstanceIds_never_contain_ip_address_patterns()
    {
        _source.TotalSubchannelCount = 5;
        _source.ReadySubchannelCount = 3;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        foreach (var result in results)
        {
            result.InstanceId.Should().NotMatchRegex(@"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}",
                "InstanceId must never contain IP addresses (Security Finding #9)");
            result.InstanceId.Should().NotContain(":",
                "InstanceId must never contain port separators (Security Finding #9)");
            result.InstanceId.Should().MatchRegex(@"^instance-\d+$",
                "InstanceId must be an opaque index");
        }
    }

    [Fact]
    public async Task Metadata_is_null_by_default()
    {
        _source.TotalSubchannelCount = 1;
        _source.ReadySubchannelCount = 1;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results[0].Metadata.Should().BeNull();
    }

    [Fact]
    public async Task ProbeAllAsync_clamps_ready_to_total_when_exceeds()
    {
        // Edge: source reports more ready than total (inconsistent state)
        _source.TotalSubchannelCount = 3;
        _source.ReadySubchannelCount = 5;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().HaveCount(3);
        results.Should().AllSatisfy(r => r.IsHealthy.Should().BeTrue());
    }

    [Fact]
    public async Task ProbeAllAsync_clamps_negative_ready_to_zero()
    {
        // Edge: source reports negative ready count
        _source.TotalSubchannelCount = 3;
        _source.ReadySubchannelCount = -1;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().HaveCount(3);
        results.Should().AllSatisfy(r => r.IsHealthy.Should().BeFalse());
    }

    [Fact]
    public async Task ProbeAllAsync_clamps_negative_total_to_zero()
    {
        // Edge: source reports negative total
        _source.TotalSubchannelCount = -2;
        _source.ReadySubchannelCount = 0;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeAllAsync_supports_cancellation()
    {
        _source.TotalSubchannelCount = 3;
        _source.ReadySubchannelCount = 3;
        var adapter = CreateAdapter();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await adapter.ProbeAllAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ProbeAllAsync_healthy_instances_come_first_then_unhealthy()
    {
        _source.TotalSubchannelCount = 4;
        _source.ReadySubchannelCount = 2;
        var adapter = CreateAdapter();

        var results = await adapter.ProbeAllAsync();

        // First N are healthy, rest are unhealthy
        results[0].IsHealthy.Should().BeTrue();
        results[1].IsHealthy.Should().BeTrue();
        results[2].IsHealthy.Should().BeFalse();
        results[3].IsHealthy.Should().BeFalse();
    }

    [Fact]
    public async Task ProbeAllAsync_is_thread_safe_under_concurrent_calls()
    {
        _source.TotalSubchannelCount = 10;
        _source.ReadySubchannelCount = 5;
        var adapter = CreateAdapter();

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => adapter.ProbeAllAsync())
            .ToArray();

        var allResults = await Task.WhenAll(tasks);

        allResults.Should().AllSatisfy(results =>
        {
            results.Should().HaveCount(10);
            results.Count(r => r.IsHealthy).Should().Be(5);
        });
    }

    private GrpcSubchannelHealthAdapter CreateAdapter(string componentName = "grpc_backend_pool") =>
        new(_source, componentName);
}
