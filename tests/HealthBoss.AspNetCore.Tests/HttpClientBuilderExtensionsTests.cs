using FluentAssertions;
using HealthBoss.AspNetCore.Tests.Fakes;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HealthBoss.AspNetCore.Tests;

public sealed class HttpClientBuilderExtensionsTests
{
    [Fact]
    public async Task AddHealthBossOutboundTracking_registers_handler_that_records_signals()
    {
        // Arrange
        var buffer = new FakeSignalBuffer();
        var clock = new FakeSystemClock();

        var services = new ServiceCollection();
        services.AddSingleton<ISignalBuffer>(buffer);
        services.AddSingleton<ISystemClock>(clock);

        services.AddHttpClient("test-api")
            .AddHealthBossOutboundTracking(o => o.ComponentName = "test-api")
            .ConfigurePrimaryHttpMessageHandler(() =>
                new FakeHttpMessageHandler(new HttpResponseMessage(System.Net.HttpStatusCode.OK)));

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("test-api");

        // Act
        await client.GetAsync("https://example.com");

        // Assert
        buffer.Signals.Should().ContainSingle();
        buffer.Signals[0].DependencyId.Value.Should().Be("test-api");
        buffer.Signals[0].Outcome.Should().Be(SignalOutcome.Success);
    }

    [Fact]
    public void AddHealthBossOutboundTracking_null_builder_throws()
    {
        IHttpClientBuilder builder = null!;

        var act = () => builder.AddHealthBossOutboundTracking(o => o.ComponentName = "test");

        act.Should().Throw<ArgumentNullException>().WithParameterName("builder");
    }

    [Fact]
    public void AddHealthBossOutboundTracking_null_configure_throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        var act = () => builder.AddHealthBossOutboundTracking(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("configure");
    }

    [Fact]
    public void AddHealthBossOutboundTracking_empty_component_name_throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        var act = () => builder.AddHealthBossOutboundTracking(o => { });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddHealthBossOutboundTracking_invalid_component_name_throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddHttpClient("test");

        var act = () => builder.AddHealthBossOutboundTracking(
            o => o.ComponentName = "invalid name with spaces!");

        act.Should().Throw<ArgumentException>();
    }
}
