using FluentAssertions;
using HealthBoss.Core.Components;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Tests;

/// <summary>
/// Tests for <see cref="PercentileCalculator"/> using the nearest-rank method.
/// Covers: single element, even distribution, all same values, p50, p95, p99.
/// </summary>
public sealed class PercentileCalculatorTests
{
    // ─── Compute (Span<TimeSpan>, double) ─────────────────────────────────

    [Fact]
    public void Compute_single_element_returns_that_element()
    {
        // Arrange
        TimeSpan[] values = [TimeSpan.FromMilliseconds(100)];

        // Act
        var p50 = PercentileCalculator.Compute(values.AsSpan(), 0.50);
        var p95 = PercentileCalculator.Compute(values.AsSpan(), 0.95);
        var p99 = PercentileCalculator.Compute(values.AsSpan(), 0.99);

        // Assert
        p50.Should().Be(TimeSpan.FromMilliseconds(100));
        p95.Should().Be(TimeSpan.FromMilliseconds(100));
        p99.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Compute_even_distribution_1_to_100_p95_is_95()
    {
        // Arrange: sorted 1ms, 2ms, ..., 100ms
        var values = Enumerable.Range(1, 100)
            .Select(i => TimeSpan.FromMilliseconds(i))
            .ToArray();

        // Act — nearest-rank: rank = ⌈0.95 × 100⌉ = 95 → index 94 → 95ms
        var p95 = PercentileCalculator.Compute(values.AsSpan(), 0.95);

        // Assert
        p95.Should().Be(TimeSpan.FromMilliseconds(95));
    }

    [Fact]
    public void Compute_even_distribution_1_to_100_p50_is_50()
    {
        var values = Enumerable.Range(1, 100)
            .Select(i => TimeSpan.FromMilliseconds(i))
            .ToArray();

        var p50 = PercentileCalculator.Compute(values.AsSpan(), 0.50);

        p50.Should().Be(TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public void Compute_even_distribution_1_to_100_p99_is_99()
    {
        var values = Enumerable.Range(1, 100)
            .Select(i => TimeSpan.FromMilliseconds(i))
            .ToArray();

        var p99 = PercentileCalculator.Compute(values.AsSpan(), 0.99);

        p99.Should().Be(TimeSpan.FromMilliseconds(99));
    }

    [Fact]
    public void Compute_all_same_values_returns_that_value()
    {
        // Arrange: 20 identical values of 42ms
        var values = Enumerable.Repeat(TimeSpan.FromMilliseconds(42), 20).ToArray();

        // Act
        var p50 = PercentileCalculator.Compute(values.AsSpan(), 0.50);
        var p95 = PercentileCalculator.Compute(values.AsSpan(), 0.95);
        var p99 = PercentileCalculator.Compute(values.AsSpan(), 0.99);

        // Assert
        p50.Should().Be(TimeSpan.FromMilliseconds(42));
        p95.Should().Be(TimeSpan.FromMilliseconds(42));
        p99.Should().Be(TimeSpan.FromMilliseconds(42));
    }

    [Fact]
    public void Compute_two_elements_p95_returns_second()
    {
        // Arrange
        TimeSpan[] values = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(200)];

        // rank = ⌈0.95 × 2⌉ = 2 → index 1 → 200ms
        var p95 = PercentileCalculator.Compute(values.AsSpan(), 0.95);

        p95.Should().Be(TimeSpan.FromMilliseconds(200));
    }

    [Theory]
    [InlineData(0.50, 50)]
    [InlineData(0.75, 75)]
    [InlineData(0.90, 90)]
    [InlineData(0.95, 95)]
    [InlineData(0.99, 99)]
    public void Compute_nearest_rank_matches_expected(double percentile, int expectedMs)
    {
        // Arrange: 100 sorted values from 1ms to 100ms
        var values = Enumerable.Range(1, 100)
            .Select(i => TimeSpan.FromMilliseconds(i))
            .ToArray();

        // Act
        var result = PercentileCalculator.Compute(values.AsSpan(), percentile);

        // Assert — nearest rank: ⌈percentile × N⌉
        result.Should().Be(TimeSpan.FromMilliseconds(expectedMs));
    }

    // ─── ComputeStandard (signals → p50/p95/p99) ─────────────────────────

    [Fact]
    public void ComputeStandard_no_signals_returns_all_null()
    {
        var signals = new List<HealthSignal>();

        var (p50, p95, p99) = PercentileCalculator.ComputeStandard(signals);

        p50.Should().BeNull();
        p95.Should().BeNull();
        p99.Should().BeNull();
    }

    [Fact]
    public void ComputeStandard_signals_without_latency_returns_all_null()
    {
        // Arrange — 5 signals with null Latency
        var signals = Enumerable.Range(0, 5)
            .Select(_ => TestFixtures.CreateSignalWithoutLatency())
            .ToList();

        // Act
        var (p50, p95, p99) = PercentileCalculator.ComputeStandard(signals);

        // Assert
        p50.Should().BeNull();
        p95.Should().BeNull();
        p99.Should().BeNull();
    }

    [Fact]
    public void ComputeStandard_mixed_null_and_non_null_excludes_null_latencies()
    {
        // Arrange: 3 signals with latency, 2 without
        var signals = new List<HealthSignal>
        {
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(10)),
            TestFixtures.CreateSignalWithoutLatency(),
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(20)),
            TestFixtures.CreateSignalWithoutLatency(),
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(30)),
        };

        // Act
        var (p50, p95, p99) = PercentileCalculator.ComputeStandard(signals);

        // Assert — only 3 values: [10, 20, 30]
        p50.Should().NotBeNull();
        p95.Should().NotBeNull();
        p99.Should().NotBeNull();

        // p50 of [10, 20, 30]: rank = ⌈0.5 × 3⌉ = 2 → index 1 → 20ms
        p50!.Value.Should().Be(TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public void ComputeStandard_known_distribution_computes_correct_percentiles()
    {
        // Arrange: 100 signals with latency 1ms to 100ms
        var latencies = Enumerable.Range(1, 100)
            .Select(i => TimeSpan.FromMilliseconds(i));
        var signals = TestFixtures.CreateSignalsWithLatencies(latencies);

        // Act
        var (p50, p95, p99) = PercentileCalculator.ComputeStandard(signals);

        // Assert
        p50.Should().Be(TimeSpan.FromMilliseconds(50));
        p95.Should().Be(TimeSpan.FromMilliseconds(95));
        p99.Should().Be(TimeSpan.FromMilliseconds(99));
    }

    // ─── ExtractAndSortLatencies ──────────────────────────────────────────

    [Fact]
    public void ExtractAndSortLatencies_unsorted_input_returns_sorted()
    {
        // Arrange — signals with latencies in random order
        var signals = new List<HealthSignal>
        {
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(300)),
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(100)),
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(200)),
        };

        // Act
        var result = PercentileCalculator.ExtractAndSortLatencies(signals);

        // Assert
        result.Should().BeInAscendingOrder();
        result.Should().HaveCount(3);
    }

    [Fact]
    public void ExtractAndSortLatencies_filters_null_latencies()
    {
        var signals = new List<HealthSignal>
        {
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(100)),
            TestFixtures.CreateSignalWithoutLatency(),
            TestFixtures.CreateSignal(latency: TimeSpan.FromMilliseconds(200)),
        };

        var result = PercentileCalculator.ExtractAndSortLatencies(signals);

        result.Should().HaveCount(2);
    }
}
