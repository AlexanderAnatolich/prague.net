---
title: API Reference
description: Generated reference for the public types in Prague, Prague.Core, Prague.Kafka, Prague.Attributes and Prague.Codegen.
---

# API Reference

This section is generated from the XML documentation comments in the source. Use the table of contents to browse by namespace, or the search box for a type or member name.

| Namespace | What lives there |
|---|---|
| `Prague.Attributes` | The attributes you put on your POCOs: `[DataCache]`, `[DataCacheKey]`, `[DataCacheIndex]`, `[DataCacheForeignKey<T>]`, `[DataCacheSort]` and friends. |
| `Prague.Core` | The runtime: `InMemoryDataCache`, index storage, query builders, prepared and frozen queries, join resolvers, pooled `QueryResults<T>`. |
| `Prague.Kafka` | Consumer and producer wiring, message filters, health checks, OpenTelemetry instrumentation and the hosted background worker. |
| `Prague.Codegen` | The Roslyn source generator and its analyzers. You reference it as an analyzer; you rarely call into it. |
| `Prague` | The facade package that references Core, Attributes and Codegen. |

Most application code touches only the attributes and the generated `XxxCache` classes. Start with the [Quick Start](../articles/getting-started/quick-start.md) if you have not used Prague before.
