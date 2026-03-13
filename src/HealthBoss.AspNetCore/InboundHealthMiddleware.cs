using System.Diagnostics;
using HealthBoss.Core;
using HealthBoss.Core.Contracts;
using Microsoft.AspNetCore.Http;

namespace HealthBoss.AspNetCore;

/// <summary>
/// ASP.NET Core middleware that records inbound HTTP request success/failure
/// as <see cref="HealthSignal"/> instances to an <see cref="ISignalBuffer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each request is classified as success or failure based on a configurable predicate
/// (default: status code ≥ 500 is failure). The middleware records the timestamp,
/// outcome, latency, and HTTP status code for each request.
/// </para>
/// <para>
/// Route-to-component mapping uses safe aliases (validated <see cref="DependencyId"/> values)
/// rather than raw route templates, preventing URL path information from leaking
/// into health signals (Security Finding #10).
/// </para>
/// <para>
/// If no <see cref="ISignalBuffer"/> is registered in DI, the middleware is a no-op
/// and passes requests through without recording.
/// </para>
/// </remarks>
public sealed class InboundHealthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly InboundTrackingOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboundHealthMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in the pipeline.</param>
    /// <param name="options">The tracking configuration options.</param>
    public InboundHealthMiddleware(RequestDelegate next, InboundTrackingOptions options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Processes the HTTP request, invokes the next middleware, and records
    /// a health signal based on the response outcome.
    /// </summary>
    /// <param name="context">The HTTP context for the current request.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? "/";

        // Skip excluded paths (e.g., /healthz) — no signal recorded.
        if (_options.IsExcluded(path))
        {
            await _next(context);
            return;
        }

        // Resolve ISignalBuffer from DI — if not registered, pass through silently.
        var signalBuffer = context.RequestServices.GetService(typeof(ISignalBuffer)) as ISignalBuffer;
        if (signalBuffer is null)
        {
            await _next(context);
            return;
        }

        var timeProvider = context.RequestServices.GetService(typeof(TimeProvider)) as TimeProvider
            ?? TimeProvider.System;

        long startTimestamp = timeProvider.GetTimestamp();
        DateTimeOffset requestTime = timeProvider.GetUtcNow().UtcDateTime;
        bool exceptionThrown = false;

        try
        {
            await _next(context);
        }
        catch
        {
            exceptionThrown = true;
            throw;
        }
        finally
        {
            TimeSpan elapsed = timeProvider.GetElapsedTime(startTimestamp);
            int statusCode = exceptionThrown ? 500 : context.Response.StatusCode;

            bool isFailure = exceptionThrown || _options.IsFailure(statusCode);
            string componentName = _options.ResolveComponent(path);

            var signal = new HealthSignal(
                timestamp: requestTime,
                dependencyId: new DependencyId(componentName),
                outcome: isFailure ? SignalOutcome.Failure : SignalOutcome.Success,
                latency: elapsed,
                httpStatusCode: statusCode);

            signalBuffer.Record(signal);

            var metrics = context.RequestServices.GetService(typeof(IComponentMetrics)) as IComponentMetrics;
            metrics?.RecordInboundRequestDuration(componentName, elapsed.TotalSeconds);
        }
    }
}
