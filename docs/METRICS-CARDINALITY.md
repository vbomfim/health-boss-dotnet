# Metrics Cardinality Best Practices

> **Applies to:** All HealthBoss OpenTelemetry metrics (18 instruments under the `HealthBoss` meter).
> **See also:** [SPECIFICATION.md](SPECIFICATION.md) · [COMPONENT-DESIGN.md](architecture/COMPONENT-DESIGN.md)

## What is cardinality?

**Cardinality** is the number of unique time series a metric produces. Each unique
combination of tag (label) values creates a separate time series in your
TSDB (Prometheus, Azure Monitor, Datadog, etc.).

```
healthboss.signals_recorded { component="redis", outcome="Success" }   → 1 series
healthboss.signals_recorded { component="redis", outcome="Failure" }   → 1 series
healthboss.signals_recorded { component="sql-db", outcome="Success" }  → 1 series
                                                            Total:       3 series
```

**Cardinality explosion** occurs when a tag has unbounded unique values (e.g.,
user IDs, request IDs, or tenant IDs that grow without limit). A single metric
with 10,000 unique tag combinations creates 10,000 time series — consuming
memory, storage, and query time proportionally.

## HealthBoss cardinality profile

### Tag dimensions in use

| Tag | Used by | Typical cardinality | Risk |
|-----|---------|---------------------|------|
| `component` | Most metrics (signals, state transitions, probes, assessments, quorum, tenant count) | Number of registered dependencies (≤ ~20 in typical deployments) | **Low** — bounded by configuration |
| `outcome` | `signals_recorded` | 2 (`Success`, `Failure`) | **Negligible** |
| `from_state` / `to_state` | `state_transitions` | 3 × 3 = 9 combinations (`Healthy`, `Degraded`, `CircuitOpen`) | **Negligible** |
| `gate` | `shutdown_gate_evaluations` | ~5 gate names | **Negligible** |
| `result` | `shutdown_gate_evaluations` | 2 (`approved`, `denied`) | **Negligible** |
| `sink_type` | `eventsink_failures` | Number of registered event sinks (typically 2–3) | **Negligible** |
| `tenant_id` | `tenant.status_changes` | Number of unique tenants **per component** | ⚠️ **Medium–High** |
| `from_status` / `to_status` | `tenant.status_changes` | 3 × 3 = 9 combinations | **Negligible** (but multiplicative with `tenant_id`) |

### Worst-case analysis

The highest-risk metric is **`healthboss.tenant.status_changes`**:

```
cardinality = components × tenants × from_status × to_status
            = 10        × 5,000   × 3           × 3
            = 450,000 time series  ← DANGER
```

Even with just 100 tenants and 10 components, that's 100 × 10 × 9 = **9,000 series**
for a single metric — which is tolerable but not trivial.

## Recommended limits

| Dimension | Recommended max | Rationale |
|-----------|-----------------|-----------|
| `component` (dependency IDs) | ≤ 100 | Bounded by `AddHealthBoss()` registration |
| `tenant_id` (per component) | ≤ 500 | Above 500, aggregate or sample |
| **Total series per metric** | ≤ 10,000 | Prometheus best practice |
| **Total series across all metrics** | ≤ 100,000 | Typical TSDB budget for one service |

### `MaxCardinality` guidance constant

The `HealthBossMetrics` class is designed to record whatever the caller emits.
If you need to enforce cardinality limits at runtime, consider one of these
strategies:

1. **Allow-list approach** — Only record metrics for a configured set of
   tenant IDs. Ignore tenants not on the list.

2. **Bucketing approach** — Group tenants into buckets (e.g., by plan tier:
   `free`, `standard`, `premium`) and use the bucket name as the tag instead
   of the raw tenant ID.

3. **Sampling approach** — Record metrics for a random sample of tenants
   (e.g., 10%) and extrapolate in dashboards.

4. **Configuration cap** — Add a `MaxTrackedTenants` option to
   `HealthBossOptions`. When the cap is reached, aggregate new tenants under
   an `_overflow` bucket.

```csharp
// Example: hypothetical MaxTrackedTenants configuration
services.AddHealthBoss(options =>
{
    // Cap tenant-level metrics to prevent cardinality explosion
    options.MaxTrackedTenants = 500;
});
```

> **Note:** Runtime cardinality enforcement is not implemented in v1.0.
> The recommended approach for v1.0 is to keep the number of registered
> components small (≤ 20) and monitor cardinality via your TSDB's built-in
> tools (e.g., `prometheus_tsdb_head_series` gauge).

## Safe tag values

✅ **Low cardinality (safe as tags):**
- Dependency/component names (bounded by registration)
- Health states (`Healthy`, `Degraded`, `CircuitOpen`)
- Signal outcomes (`Success`, `Failure`)
- Drain statuses (`Idle`, `Draining`, `Drained`, `TimedOut`)
- Gate names (`MinSignals`, `Cooldown`, etc.)

❌ **High cardinality (avoid as tags):**
- Request IDs, correlation IDs, trace IDs
- User IDs, session IDs
- Timestamps, URLs, IP addresses
- Unbounded tenant IDs (thousands+)

## Monitoring cardinality in production

### Prometheus

```promql
# Total active time series for HealthBoss metrics
count({__name__=~"healthboss_.*"})

# Cardinality breakdown by metric name
count by (__name__)({__name__=~"healthboss_.*"})

# Top cardinality offenders
topk(5, count by (__name__)({__name__=~"healthboss_.*"}))
```

### Azure Monitor / Application Insights

Use the **Metrics** blade → filter by `HealthBoss` custom metrics namespace.
Check "Split by" dimensions to see unique value counts.

### Grafana

Use the **Cardinality Management** dashboard (available in Grafana Cloud)
to identify high-cardinality labels.

## References

- [Prometheus Best Practices — Instrumentation](https://prometheus.io/docs/practices/instrumentation/)
- [OpenTelemetry Metrics — Cardinality](https://opentelemetry.io/docs/specs/otel/metrics/supplementary-guidelines/#cardinality)
- [Grafana — Understanding Cardinality](https://grafana.com/docs/grafana-cloud/cost-management-and-billing/reduce-costs/metrics-costs/understand-high-cardinality/)
