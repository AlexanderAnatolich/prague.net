---
title: Health and Metrics
description: Wire the liveness and readiness checks, read their failure reasons, tune the thresholds, and export the Prague.Kafka meter through OpenTelemetry.
---

# Health and Metrics

**Goal:** an orchestrator that restarts a dead consumer and stops routing to a replica that is still loading, and a dashboard that shows cache sizes and message rates.

**Prerequisite:** a host with `AddKafkaCaches`. The code is in [`HealthAndMetrics.cs`](https://github.com/nanov/prague.net/blob/main/samples/Prague.Samples/HowTo/HealthAndMetrics.cs). The ASP.NET mapping is compiled-only; the evaluator, the health-check service and the meter registration run as tests without a broker.

## 1. Register the checks

Two checks, two tags, two endpoints. Map `Degraded` to 503 on the readiness endpoint; the default maps it to 200.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/HealthAndMetrics.cs#health-register)]

| Check | Fails as | Reasons |
|---|---|---|
| `prague-kafka-live` | Unhealthy | `FatalLatched`, `PollLoopStalled`, `HandlerLoopFaulted`, `HandlerProcessingTimeout` |
| `prague-kafka-ready` | Degraded (Unhealthy if liveness also fails) | `InitialLoadIncomplete`, `NoPartitionAssigned`, `BrokersDown`, `PartitionsLost` |

The thresholds live in `KafkaCachesHealthOptions`: `PollLoopHeartbeatTimeout` (3 s), `HandlerProcessingTimeout` (5 s), `MinBrokersUp` (1). `BrokersDown` needs librdkafka statistics, which are on by default; set `MinBrokersUp = 0` if you turn them off. The full matrix is in [Kafka health checks](https://github.com/nanov/prague.net/blob/main/context/kafka-health.md).

## 2. Read a verdict

The checks are thin wrappers over `KafkaCachesHealthEvaluator.Evaluate`, which you can call yourself on the statistics object, for a test or a custom check.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/HealthAndMetrics.cs#health-verdict)]

Through `HealthCheckService` the same verdict shows up as entry status plus a `Data` dictionary with the failure lists per consumer:

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/HealthAndMetrics.cs#health-report)]

## 3. Export metrics

`AddPragueKafkaInstrumentation` registers the `Prague.Kafka` meter with any OpenTelemetry metrics pipeline; an optional prefix namespaces the instruments.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/HealthAndMetrics.cs#metrics-register)]

The meter publishes eight observable instruments; the test lists them with a `MeterListener`:

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/HealthAndMetrics.cs#metrics-instruments)]

| Instrument | Tags |
|---|---|
| `prague.kafka.consumer.partitions.assigned`, `consumer.broker.rtt.p99`, `consumer.health` | `consumer` |
| `prague.kafka.cache.size`, `cache.messages.received`, `cache.health` | `consumer`, `cache` |
| `prague.kafka.cache.index.size` | `consumer`, `cache`, `index`, `kind` |
| `prague.kafka.cache.index.capacity` | `consumer`, `cache`, `index` |

`consumer.health` and `cache.health` are 1 or 0 and use the same predicates as the checks (consumer-level failures on the first, per-cache ones on the second), so an alert on them and a 503 from `/health/ready` agree.

## Pitfalls

- **One readiness endpoint for everything.** Readiness fails during the initial load by design; if the same endpoint gates your database check, a long replay looks like a database outage. Tag and split.
- **`Degraded` returning 200.** Kubernetes keeps routing. Set `ResultStatusCodes` as in step 1.
- **A handler that awaits I/O.** `HandlerProcessingTimeout` fires after 5 s and liveness restarts the pod; the fix is in the handler ([React to changes](react-to-changes.md)), not in the timeout.
- **No consumers yet.** Before `AddKafkaCaches` has created a consumer, both checks report Healthy: there is nothing to judge. Readiness becomes meaningful once the host starts.
