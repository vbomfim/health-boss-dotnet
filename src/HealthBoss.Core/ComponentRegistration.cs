// <copyright file="ComponentRegistration.cs" company="HealthBoss">
// Copyright (c) HealthBoss. All rights reserved.
// </copyright>

using HealthBoss.Core.Contracts;

namespace HealthBoss.Core;

/// <summary>
/// Internal record that captures a validated component registration
/// produced by the fluent <see cref="ComponentBuilder"/> API.
/// </summary>
/// <param name="DependencyId">The strongly-typed dependency identifier.</param>
/// <param name="Policy">The validated health policy for this component.</param>
internal sealed record ComponentRegistration(
    DependencyId DependencyId,
    HealthPolicy Policy);
