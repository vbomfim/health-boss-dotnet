// <copyright file="HealthSignal.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

namespace HealthBoss.Core.Contracts;

/// <summary>
/// An immutable health signal recorded from real traffic.
/// String fields (<see cref="GrpcStatus"/> and <see cref="Metadata"/>) are automatically
/// sanitized on construction to strip control characters and enforce length limits.
/// </summary>
public sealed record HealthSignal
{
    /// <summary>Gets when the signal was recorded.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>Gets the dependency that produced the signal.</summary>
    public DependencyId DependencyId { get; }

    /// <summary>Gets the outcome of the operation.</summary>
    public SignalOutcome Outcome { get; }

    /// <summary>Gets the optional latency of the operation. Null when duration was not measured.</summary>
    public TimeSpan? Latency { get; }

    /// <summary>Gets the optional HTTP status code (0 if not applicable).</summary>
    public int HttpStatusCode { get; }

    /// <summary>Gets the optional gRPC status string (sanitized, max 128 chars).</summary>
    public string? GrpcStatus { get; }

    /// <summary>Gets the optional metadata for extensibility (sanitized, max 1024 chars).</summary>
    public string? Metadata { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="HealthSignal"/> record.
    /// String fields are automatically sanitized via <see cref="HealthBossValidator.SanitizeString"/>.
    /// </summary>
    /// <param name="timestamp">When the signal was recorded.</param>
    /// <param name="dependencyId">The dependency that produced the signal.</param>
    /// <param name="outcome">The outcome of the operation.</param>
    /// <param name="latency">The optional latency of the operation. Null when not measured.</param>
    /// <param name="httpStatusCode">Optional HTTP status code (0 if not applicable).</param>
    /// <param name="grpcStatus">Optional gRPC status string (sanitized to max 128 chars).</param>
    /// <param name="metadata">Optional metadata for extensibility (sanitized to max 1024 chars).</param>
    public HealthSignal(
        DateTimeOffset timestamp,
        DependencyId dependencyId,
        SignalOutcome outcome,
        TimeSpan? latency = null,
        int httpStatusCode = 0,
        string? grpcStatus = null,
        string? metadata = null)
    {
        Timestamp = timestamp;
        DependencyId = dependencyId;
        Outcome = outcome;
        Latency = latency;
        HttpStatusCode = httpStatusCode;
        GrpcStatus = HealthBossValidator.SanitizeString(grpcStatus, 128);
        Metadata = HealthBossValidator.SanitizeString(metadata, 1024);
    }
}
