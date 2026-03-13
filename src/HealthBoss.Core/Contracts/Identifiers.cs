// <copyright file="Identifiers.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// Strongly-typed identifier for a monitored dependency.
/// Validates that the value conforms to naming requirements on construction.
/// </summary>
/// <remarks>
/// Because <c>readonly record struct</c> always allows parameterless construction,
/// <c>default(DependencyId)</c> produces an instance with <see cref="Value"/> equal to <c>null</c>.
/// This is the empty sentinel used when no dependency is available (e.g., zero-signal evaluation).
/// Use <see cref="IsDefault"/> to check for this case.
/// </remarks>
public readonly record struct DependencyId
{
    /// <summary>
    /// Gets the string identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets a value indicating whether this instance was created via <c>default</c>
    /// (parameterless construction) and therefore has no validated value.
    /// </summary>
    public bool IsDefault => Value is null;

    /// <summary>
    /// Initializes a new instance of the <see cref="DependencyId"/> struct.
    /// </summary>
    /// <param name="value">The dependency identifier string. Must be non-null, non-empty,
    /// max 200 chars, alphanumeric with hyphens and underscores.</param>
    /// <exception cref="ArgumentException">Thrown when the value fails validation.</exception>
    public DependencyId(string value)
    {
        HealthBossValidator.ValidateDependencyId(value);
        Value = value;
    }

    /// <summary>
    /// Creates a validated <see cref="DependencyId"/> from the given string.
    /// </summary>
    /// <param name="value">The dependency identifier string to validate and wrap.</param>
    /// <returns>A validated <see cref="DependencyId"/>.</returns>
    public static DependencyId Create(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// Strongly-typed identifier for a tenant in multi-tenant scenarios.
/// Validates that the value conforms to naming requirements on construction.
/// </summary>
/// <remarks>
/// Because <c>readonly record struct</c> always allows parameterless construction,
/// <c>default(TenantId)</c> produces an instance with <see cref="Value"/> equal to <c>null</c>.
/// This is the empty sentinel used when no tenant is available.
/// Use <see cref="IsDefault"/> to check for this case.
/// </remarks>
public readonly record struct TenantId
{
    /// <summary>
    /// Gets the string identifier value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets a value indicating whether this instance was created via <c>default</c>
    /// (parameterless construction) and therefore has no validated value.
    /// </summary>
    public bool IsDefault => Value is null;

    /// <summary>
    /// Initializes a new instance of the <see cref="TenantId"/> struct.
    /// </summary>
    /// <param name="value">The tenant identifier string. Must be non-null, non-empty,
    /// max 128 chars, alphanumeric with hyphens and underscores.</param>
    /// <exception cref="ArgumentException">Thrown when the value fails validation.</exception>
    public TenantId(string value)
    {
        HealthBossValidator.ValidateTenantId(value);
        Value = value;
    }

    /// <summary>
    /// Creates a validated <see cref="TenantId"/> from the given string.
    /// </summary>
    /// <param name="value">The tenant identifier string to validate and wrap.</param>
    /// <returns>A validated <see cref="TenantId"/>.</returns>
    public static TenantId Create(string value) => new(value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
