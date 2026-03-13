using HealthBoss.AspNetCore;
using FluentAssertions;

namespace HealthBoss.AspNetCore.Tests;

/// <summary>
/// TDD tests for InboundTrackingOptions: route mapping, exclusions, defaults.
/// </summary>
public sealed class InboundTrackingOptionsTests
{
    // ─── Default Values ──────────────────────────────────────────────────

    [Fact]
    public void Default_component_is_api()
    {
        var options = new InboundTrackingOptions();

        options.DefaultComponent.Should().Be("api");
    }

    [Fact]
    public void Default_exclude_paths_contains_healthz()
    {
        var options = new InboundTrackingOptions();

        options.ExcludePaths.Should().Contain("/healthz");
        options.ExcludePaths.Should().Contain("/healthz/ready");
    }

    [Fact]
    public void Default_IsFailure_returns_true_for_500()
    {
        var options = new InboundTrackingOptions();

        options.IsFailure(500).Should().BeTrue();
    }

    [Fact]
    public void Default_IsFailure_returns_false_for_499()
    {
        var options = new InboundTrackingOptions();

        options.IsFailure(499).Should().BeFalse();
    }

    [Fact]
    public void Default_IsFailure_returns_true_for_503()
    {
        var options = new InboundTrackingOptions();

        options.IsFailure(503).Should().BeTrue();
    }

    // ─── Route Mapping ───────────────────────────────────────────────────

    [Fact]
    public void Map_returns_options_for_fluent_chaining()
    {
        var options = new InboundTrackingOptions();

        var result = options.Map("/api/payments", "payments-handler");

        result.Should().BeSameAs(options);
    }

    [Fact]
    public void ResolveComponent_returns_mapped_component_for_prefix_match()
    {
        var options = new InboundTrackingOptions();
        options.Map("/api/payments", "payments-handler");

        var result = options.ResolveComponent("/api/payments/charge");

        result.Should().Be("payments-handler");
    }

    [Fact]
    public void ResolveComponent_returns_mapped_component_for_exact_match()
    {
        var options = new InboundTrackingOptions();
        options.Map("/api/payments", "payments-handler");

        var result = options.ResolveComponent("/api/payments");

        result.Should().Be("payments-handler");
    }

    [Fact]
    public void ResolveComponent_returns_default_for_unmapped_path()
    {
        var options = new InboundTrackingOptions();
        options.Map("/api/payments", "payments-handler");
        options.DefaultComponent = "fallback";

        var result = options.ResolveComponent("/api/unknown");

        result.Should().Be("fallback");
    }

    [Fact]
    public void ResolveComponent_matches_first_registered_mapping()
    {
        var options = new InboundTrackingOptions();
        options.Map("/api", "api-general");
        options.Map("/api/payments", "payments-handler");

        var result = options.ResolveComponent("/api/payments/charge");

        result.Should().Be("api-general");
    }

    [Fact]
    public void ResolveComponent_is_case_insensitive()
    {
        var options = new InboundTrackingOptions();
        options.Map("/api/Payments", "payments-handler");

        var result = options.ResolveComponent("/API/PAYMENTS/charge");

        result.Should().Be("payments-handler");
    }

    // ─── Excluded Paths ──────────────────────────────────────────────────

    [Theory]
    [InlineData("/healthz", true)]
    [InlineData("/healthz/ready", true)]
    [InlineData("/api/data", false)]
    [InlineData("/healthz/extra/deep", false)]
    public void IsExcluded_matches_exact_paths(string path, bool expected)
    {
        var options = new InboundTrackingOptions();

        options.IsExcluded(path).Should().Be(expected);
    }

    [Fact]
    public void IsExcluded_is_case_insensitive()
    {
        var options = new InboundTrackingOptions();

        options.IsExcluded("/HEALTHZ").Should().BeTrue();
    }

    // ─── Validation ──────────────────────────────────────────────────────

    [Fact]
    public void Map_throws_for_null_pattern()
    {
        var options = new InboundTrackingOptions();

        var act = () => options.Map(null!, "component");

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Map_throws_for_null_component()
    {
        var options = new InboundTrackingOptions();

        var act = () => options.Map("/api", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Map_throws_for_empty_component()
    {
        var options = new InboundTrackingOptions();

        var act = () => options.Map("/api", "");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Map_throws_for_invalid_component_name_characters()
    {
        var options = new InboundTrackingOptions();

        var act = () => options.Map("/api", "invalid component!");

        act.Should().Throw<ArgumentException>();
    }
}
