// <copyright file="HealthBossValidator.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using System.Text.RegularExpressions;
using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Static validation and sanitization helpers for HealthBoss configuration and identifiers.
/// </summary>
public static partial class HealthBossValidator
{
    private static readonly Regex DependencyIdPattern = CreateDependencyIdRegex();
    private static readonly Regex TenantIdPattern = CreateTenantIdRegex();

    /// <summary>
    /// Validates that a dependency identifier meets naming requirements:
    /// non-null, non-empty, max 200 chars, alphanumeric with hyphens and underscores.
    /// </summary>
    /// <param name="value">The dependency identifier string to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the value is null, empty, whitespace, or contains invalid characters.</exception>
    public static void ValidateDependencyId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Dependency ID must not be null, empty, or whitespace.",
                nameof(value));
        }

        if (value.Length > 200)
        {
            throw new ArgumentException(
                $"Dependency ID must not exceed 200 characters. Got {value.Length}.",
                nameof(value));
        }

        if (!DependencyIdPattern.IsMatch(value))
        {
            throw new ArgumentException(
                "Dependency ID must contain only alphanumeric characters, hyphens, and underscores.",
                nameof(value));
        }
    }

    /// <summary>
    /// Validates that a tenant identifier meets naming requirements:
    /// non-null, non-empty, max 128 chars, alphanumeric with hyphens and underscores.
    /// </summary>
    /// <param name="value">The tenant identifier string to validate.</param>
    /// <exception cref="ArgumentException">Thrown when the value is null, empty, whitespace, or contains invalid characters.</exception>
    public static void ValidateTenantId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "Tenant ID must not be null, empty, or whitespace.",
                nameof(value));
        }

        if (value.Length > 128)
        {
            throw new ArgumentException(
                $"Tenant ID must not exceed 128 characters. Got {value.Length}.",
                nameof(value));
        }

        if (!TenantIdPattern.IsMatch(value))
        {
            throw new ArgumentException(
                "Tenant ID must contain only alphanumeric characters, hyphens, and underscores.",
                nameof(value));
        }
    }

    /// <summary>
    /// Sanitizes a string by removing control characters and truncating to max length.
    /// Uses a fast-path when no control characters are present (zero allocation).
    /// </summary>
    /// <param name="value">The string to sanitize. May be null.</param>
    /// <param name="maxLength">Maximum allowed length. Defaults to 1024.</param>
    /// <returns>The sanitized string, or null if the input was null.</returns>
    public static string? SanitizeString(string? value, int maxLength = 1024)
    {
        if (value is null) return null;
        if (value.Length > maxLength) value = value[..maxLength];

        // Fast path: most strings have no control characters — avoid allocation
        if (!ContainsControlCharacters(value)) return value;

        // Slow path: build sanitized string using stack-friendly string.Create
        return string.Create(value.Length, value, static (span, src) =>
        {
            int pos = 0;
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                if (!char.IsControl(c) || c is ' ' or '\t')
                {
                    span[pos++] = c;
                }
            }

            span[pos..].Fill('\0');
        }).TrimEnd('\0');
    }

    private static bool ContainsControlCharacters(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsControl(c) && c is not (' ' or '\t'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Validates that a <see cref="HealthPolicy"/> has consistent and valid configuration.
    /// </summary>
    /// <param name="policy">The policy to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when thresholds are out of range or window is invalid.</exception>
    /// <exception cref="ArgumentException">Thrown when threshold ordering is invalid.</exception>
    public static void ValidateHealthPolicy(HealthPolicy policy)
    {
        if (policy.DegradedThreshold < 0.0 || policy.DegradedThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.DegradedThreshold),
                policy.DegradedThreshold,
                "Degraded threshold must be between 0.0 and 1.0.");
        }

        if (policy.CircuitOpenThreshold < 0.0 || policy.CircuitOpenThreshold > 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.CircuitOpenThreshold),
                policy.CircuitOpenThreshold,
                "Circuit-open threshold must be between 0.0 and 1.0.");
        }

        if (policy.DegradedThreshold <= policy.CircuitOpenThreshold)
        {
            throw new ArgumentException(
                $"Degraded threshold ({policy.DegradedThreshold}) must be greater than " +
                $"circuit-open threshold ({policy.CircuitOpenThreshold}).");
        }

        if (policy.SlidingWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.SlidingWindow),
                policy.SlidingWindow,
                "Sliding window must be greater than zero.");
        }

        if (policy.MinSignalsForEvaluation < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.MinSignalsForEvaluation),
                policy.MinSignalsForEvaluation,
                "Minimum signals for evaluation must be non-negative.");
        }

        if (policy.CooldownBeforeTransition < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.CooldownBeforeTransition),
                policy.CooldownBeforeTransition,
                "CooldownBeforeTransition must be >= TimeSpan.Zero.");
        }

        if (policy.RecoveryProbeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.RecoveryProbeInterval),
                policy.RecoveryProbeInterval,
                "RecoveryProbeInterval must be > TimeSpan.Zero.");
        }

        ValidateJitterConfig(policy.Jitter);

        if (policy.CooldownBeforeTransition > TimeSpan.Zero &&
            policy.Jitter.MaxDelay > policy.CooldownBeforeTransition)
        {
            throw new ArgumentException(
                $"Maximum jitter delay ({policy.Jitter.MaxDelay}) must not exceed " +
                $"CooldownBeforeTransition ({policy.CooldownBeforeTransition}).");
        }

        if (policy.ResponseTime is not null)
        {
            ValidateResponseTimePolicy(policy.ResponseTime);
        }
    }

    /// <summary>
    /// Validates that a <see cref="ResponseTimePolicy"/> has consistent and valid configuration.
    /// </summary>
    /// <param name="policy">The response-time policy to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when values are out of valid range.</exception>
    /// <exception cref="ArgumentException">Thrown when threshold ordering is invalid.</exception>
    public static void ValidateResponseTimePolicy(ResponseTimePolicy policy)
    {
        if (policy.Percentile <= 0.0 || policy.Percentile >= 1.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.Percentile),
                policy.Percentile,
                "Percentile must be between 0.0 and 1.0 exclusive.");
        }

        if (policy.DegradedThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.DegradedThreshold),
                policy.DegradedThreshold,
                "Degraded threshold must be greater than zero.");
        }

        if (policy.UnhealthyThreshold.HasValue &&
            policy.UnhealthyThreshold.Value <= policy.DegradedThreshold)
        {
            throw new ArgumentException(
                $"Unhealthy threshold ({policy.UnhealthyThreshold.Value}) must be greater than " +
                $"degraded threshold ({policy.DegradedThreshold}).");
        }

        if (policy.MinimumSignals < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.MinimumSignals),
                policy.MinimumSignals,
                "Minimum signals must be at least 1.");
        }
    }

    /// <summary>
    /// Validates that a <see cref="JitterConfig"/> is consistent.
    /// </summary>
    /// <param name="config">The jitter configuration to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when MinDelay is negative.</exception>
    /// <exception cref="ArgumentException">Thrown when MaxDelay is less than MinDelay.</exception>
    public static void ValidateJitterConfig(JitterConfig config)
    {
        if (config.MinDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(config.MinDelay),
                config.MinDelay,
                "Minimum jitter delay must be non-negative.");
        }

        if (config.MaxDelay < config.MinDelay)
        {
            throw new ArgumentException(
                $"Maximum jitter delay ({config.MaxDelay}) must be greater than or equal to " +
                $"minimum jitter delay ({config.MinDelay}).");
        }
    }

    /// <summary>
    /// Validates that a <see cref="QuorumHealthPolicy"/> has consistent and valid configuration.
    /// </summary>
    /// <param name="policy">The quorum health policy to validate.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <see cref="QuorumHealthPolicy.MinimumHealthyInstances"/> is less than 1
    /// or <see cref="QuorumHealthPolicy.TotalExpectedInstances"/> is negative.
    /// </exception>
    public static void ValidateQuorumHealthPolicy(QuorumHealthPolicy policy)
    {
        if (policy.MinimumHealthyInstances < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.MinimumHealthyInstances),
                policy.MinimumHealthyInstances,
                "MinimumHealthyInstances must be at least 1.");
        }

        if (policy.TotalExpectedInstances < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(policy.TotalExpectedInstances),
                policy.TotalExpectedInstances,
                "TotalExpectedInstances must be non-negative.");
        }
    }

    [GeneratedRegex(@"^[a-zA-Z0-9\-_]+$")]
    private static partial Regex CreateDependencyIdRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9\-_]+$")]
    private static partial Regex CreateTenantIdRegex();
}
