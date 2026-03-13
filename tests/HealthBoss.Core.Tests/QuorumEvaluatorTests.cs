using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

public sealed class QuorumEvaluatorTests
{
    private readonly IQuorumEvaluator _evaluator = new QuorumEvaluator();

    // ── AC38: Quorum met (3 of 5 healthy → Healthy) ──────────────────────

    [Fact]
    public void Quorum_met_returns_Healthy()
    {
        var results = CreateInstanceResults(healthyCount: 3, unhealthyCount: 2);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Healthy);
        assessment.QuorumMet.Should().BeTrue();
        assessment.HealthyInstances.Should().Be(3);
        assessment.TotalInstances.Should().Be(5);
        assessment.MinimumRequired.Should().Be(3);
    }

    [Fact]
    public void Quorum_met_at_exact_boundary()
    {
        // Exactly MinimumHealthyInstances healthy → should still be Healthy
        var results = CreateInstanceResults(healthyCount: 2, unhealthyCount: 3);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 2);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Healthy);
        assessment.QuorumMet.Should().BeTrue();
        assessment.HealthyInstances.Should().Be(2);
    }

    [Fact]
    public void All_healthy_returns_Healthy()
    {
        var results = CreateInstanceResults(healthyCount: 5, unhealthyCount: 0);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Healthy);
        assessment.QuorumMet.Should().BeTrue();
        assessment.HealthyInstances.Should().Be(5);
        assessment.TotalInstances.Should().Be(5);
    }

    // ── AC39: Quorum not met (2 of 5 → Degraded) ────────────────────────

    [Fact]
    public void Quorum_not_met_returns_Degraded()
    {
        var results = CreateInstanceResults(healthyCount: 2, unhealthyCount: 3);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Degraded);
        assessment.QuorumMet.Should().BeFalse();
        assessment.HealthyInstances.Should().Be(2);
        assessment.MinimumRequired.Should().Be(3);
    }

    [Fact]
    public void One_below_minimum_returns_Degraded()
    {
        // 4 healthy but need 5 → Degraded (not CircuitOpen, because > 0)
        var results = CreateInstanceResults(healthyCount: 4, unhealthyCount: 1);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 5);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Degraded);
        assessment.QuorumMet.Should().BeFalse();
    }

    [Fact]
    public void Single_healthy_when_more_required_returns_Degraded()
    {
        var results = CreateInstanceResults(healthyCount: 1, unhealthyCount: 4);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.Degraded);
        assessment.QuorumMet.Should().BeFalse();
        assessment.HealthyInstances.Should().Be(1);
    }

    // ── AC40: Zero healthy → CircuitOpen ─────────────────────────────────

    [Fact]
    public void Zero_healthy_returns_CircuitOpen()
    {
        var results = CreateInstanceResults(healthyCount: 0, unhealthyCount: 5);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.CircuitOpen);
        assessment.QuorumMet.Should().BeFalse();
        assessment.HealthyInstances.Should().Be(0);
        assessment.TotalInstances.Should().Be(5);
    }

    [Fact]
    public void Empty_results_returns_CircuitOpen()
    {
        var results = Array.Empty<InstanceHealthResult>();
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 1);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.Status.Should().Be(HealthState.CircuitOpen);
        assessment.QuorumMet.Should().BeFalse();
        assessment.HealthyInstances.Should().Be(0);
        assessment.TotalInstances.Should().Be(0);
    }

    // ── AC41: Quorum metrics ─────────────────────────────────────────────

    [Fact]
    public void Assessment_contains_correct_metrics()
    {
        var results = CreateInstanceResults(healthyCount: 4, unhealthyCount: 6);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3, TotalExpectedInstances: 10);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.HealthyInstances.Should().Be(4);
        assessment.TotalInstances.Should().Be(10);
        assessment.MinimumRequired.Should().Be(3);
        assessment.InstanceResults.Should().HaveCount(10);
    }

    [Fact]
    public void Assessment_includes_all_instance_results()
    {
        var results = CreateInstanceResults(healthyCount: 2, unhealthyCount: 1);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 1);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.InstanceResults.Should().BeEquivalentTo(results);
    }

    [Fact]
    public void TotalInstances_uses_TotalExpected_when_greater_than_probed()
    {
        // 3 probed instances but 5 expected → TotalInstances should be 5
        var results = CreateInstanceResults(healthyCount: 3, unhealthyCount: 0);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3, TotalExpectedInstances: 5);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.TotalInstances.Should().Be(5);
        assessment.HealthyInstances.Should().Be(3);
    }

    [Fact]
    public void TotalInstances_uses_probed_count_when_TotalExpected_is_zero()
    {
        // Dynamic fleet: TotalExpectedInstances = 0 → use actual probed count
        var results = CreateInstanceResults(healthyCount: 3, unhealthyCount: 2);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 2, TotalExpectedInstances: 0);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.TotalInstances.Should().Be(5);
    }

    [Fact]
    public void TotalInstances_uses_probed_count_when_TotalExpected_less_than_probed()
    {
        // More instances responded than expected → use the actual count
        var results = CreateInstanceResults(healthyCount: 4, unhealthyCount: 2);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 3, TotalExpectedInstances: 4);

        var assessment = _evaluator.Evaluate(results, policy);

        assessment.TotalInstances.Should().Be(6);
    }

    // ── AC42: IInstanceHealthProbe pluggable ─────────────────────────────

    [Fact]
    public async Task Pluggable_probe_returns_results()
    {
        // Demonstrate that IInstanceHealthProbe is pluggable via a fake
        var fakeProbe = new FakeInstanceHealthProbe(
        [
            new InstanceHealthResult("i-1", true, TimeSpan.FromMilliseconds(10)),
            new InstanceHealthResult("i-2", false, TimeSpan.FromMilliseconds(500)),
            new InstanceHealthResult("i-3", true, TimeSpan.FromMilliseconds(20)),
        ]);

        var probeResults = await fakeProbe.ProbeAllAsync(CancellationToken.None);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 2);

        var assessment = _evaluator.Evaluate(probeResults, policy);

        assessment.Status.Should().Be(HealthState.Healthy);
        assessment.QuorumMet.Should().BeTrue();
        assessment.HealthyInstances.Should().Be(2);
        assessment.TotalInstances.Should().Be(3);
    }

    [Fact]
    public async Task Pluggable_probe_all_unhealthy()
    {
        var fakeProbe = new FakeInstanceHealthProbe(
        [
            new InstanceHealthResult("i-1", false, TimeSpan.FromMilliseconds(999)),
            new InstanceHealthResult("i-2", false, TimeSpan.FromMilliseconds(999)),
        ]);

        var probeResults = await fakeProbe.ProbeAllAsync(CancellationToken.None);
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 1);

        var assessment = _evaluator.Evaluate(probeResults, policy);

        assessment.Status.Should().Be(HealthState.CircuitOpen);
        assessment.QuorumMet.Should().BeFalse();
    }

    // ── Validation ───────────────────────────────────────────────────────

    [Fact]
    public void Validate_rejects_MinimumHealthyInstances_zero()
    {
        var act = () => HealthBossValidator.ValidateQuorumHealthPolicy(
            new QuorumHealthPolicy(MinimumHealthyInstances: 0));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("MinimumHealthyInstances");
    }

    [Fact]
    public void Validate_rejects_MinimumHealthyInstances_negative()
    {
        var act = () => HealthBossValidator.ValidateQuorumHealthPolicy(
            new QuorumHealthPolicy(MinimumHealthyInstances: -1));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("MinimumHealthyInstances");
    }

    [Fact]
    public void Validate_rejects_TotalExpectedInstances_negative()
    {
        var act = () => HealthBossValidator.ValidateQuorumHealthPolicy(
            new QuorumHealthPolicy(MinimumHealthyInstances: 1, TotalExpectedInstances: -1));

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("TotalExpectedInstances");
    }

    [Fact]
    public void Validate_accepts_valid_policy()
    {
        var act = () => HealthBossValidator.ValidateQuorumHealthPolicy(
            new QuorumHealthPolicy(MinimumHealthyInstances: 3, TotalExpectedInstances: 5));

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_accepts_zero_TotalExpectedInstances()
    {
        var act = () => HealthBossValidator.ValidateQuorumHealthPolicy(
            new QuorumHealthPolicy(MinimumHealthyInstances: 1, TotalExpectedInstances: 0));

        act.Should().NotThrow();
    }

    [Fact]
    public void Evaluate_throws_on_null_results()
    {
        var policy = new QuorumHealthPolicy(MinimumHealthyInstances: 1);

        var act = () => _evaluator.Evaluate(null!, policy);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Evaluate_throws_on_null_policy()
    {
        var results = Array.Empty<InstanceHealthResult>();

        var act = () => _evaluator.Evaluate(results, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static List<InstanceHealthResult> CreateInstanceResults(
        int healthyCount,
        int unhealthyCount)
    {
        var results = new List<InstanceHealthResult>();

        for (int i = 0; i < healthyCount; i++)
        {
            results.Add(new InstanceHealthResult(
                $"instance-{i + 1}",
                IsHealthy: true,
                ResponseTime: TimeSpan.FromMilliseconds(10 + i)));
        }

        for (int i = 0; i < unhealthyCount; i++)
        {
            results.Add(new InstanceHealthResult(
                $"instance-{healthyCount + i + 1}",
                IsHealthy: false,
                ResponseTime: TimeSpan.FromMilliseconds(500 + i)));
        }

        return results;
    }

    /// <summary>
    /// Fake implementation of <see cref="IInstanceHealthProbe"/> for testing pluggability.
    /// </summary>
    private sealed class FakeInstanceHealthProbe(
        IReadOnlyList<InstanceHealthResult> results) : IInstanceHealthProbe
    {
        public Task<IReadOnlyList<InstanceHealthResult>> ProbeAllAsync(CancellationToken ct) =>
            Task.FromResult(results);
    }
}
