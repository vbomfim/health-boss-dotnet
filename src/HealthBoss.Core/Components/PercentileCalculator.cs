// <copyright file="PercentileCalculator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core.Components;

/// <summary>
/// Computes latency percentiles using the nearest-rank method.
/// All methods are pure functions with no side effects.
/// </summary>
internal static class PercentileCalculator
{
    /// <summary>
    /// Computes a single percentile value from a pre-sorted span of durations
    /// using the nearest-rank method: rank = ⌈percentile × N⌉.
    /// </summary>
    /// <param name="sorted">A span of <see cref="TimeSpan"/> values that <b>must</b> already be sorted ascending.</param>
    /// <param name="percentile">The percentile to compute (0.0–1.0 exclusive).</param>
    /// <returns>The <see cref="TimeSpan"/> at the given percentile rank.</returns>
    public static TimeSpan Compute(Span<TimeSpan> sorted, double percentile)
    {
        int rank = (int)Math.Ceiling(percentile * sorted.Length);
        return sorted[Math.Min(rank, sorted.Length) - 1];
    }

    /// <summary>
    /// Computes P50, P95, and P99 percentiles from health signals in a single pass (after sorting).
    /// Only signals with non-null <see cref="HealthSignal.Latency"/> are included.
    /// </summary>
    /// <param name="signals">The health signals to extract latencies from.</param>
    /// <returns>A tuple of nullable percentile values; all null when no signals have latency data.</returns>
    public static (TimeSpan? P50, TimeSpan? P95, TimeSpan? P99) ComputeStandard(
        IReadOnlyList<HealthSignal> signals)
    {
        var latencies = ExtractAndSortLatencies(signals);

        if (latencies.Length == 0)
        {
            return (null, null, null);
        }

        Span<TimeSpan> sorted = latencies.AsSpan();

        return (
            Compute(sorted, 0.50),
            Compute(sorted, 0.95),
            Compute(sorted, 0.99));
    }

    /// <summary>
    /// Extracts non-null latency values from signals and returns them sorted ascending.
    /// </summary>
    /// <param name="signals">The health signals to extract latencies from.</param>
    /// <returns>A sorted array of latency values.</returns>
    internal static TimeSpan[] ExtractAndSortLatencies(IReadOnlyList<HealthSignal> signals)
    {
        var latencies = new List<TimeSpan>(signals.Count);
        for (int i = 0; i < signals.Count; i++)
        {
            var latency = signals[i].Latency;
            if (latency.HasValue)
            {
                latencies.Add(latency.Value);
            }
        }

        var array = latencies.ToArray();
        Array.Sort(array);
        return array;
    }
}
