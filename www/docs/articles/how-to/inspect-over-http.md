---
title: Inspect over HTTP
description: Mount Prague.Api to read consumer statistics and query any cache from a browser or curl, know the query-string syntax, and keep the endpoints off the public surface.
---

# Inspect over HTTP

**Goal:** look inside a running service's caches without attaching a debugger: sizes, load times, and ad-hoc queries by index.

**Prerequisite:** the `Prague.Api` package and an ASP.NET Core host with `AddKafkaCaches`. The code is in [`InspectOverHttp.cs`](https://github.com/nanov/prague.net/blob/main/samples/Prague.Samples/HowTo/InspectOverHttp.cs); the mounting blocks are compiled-only, the requests run against the real endpoints in-process over a hand-built registry.

## 1. Mount

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/InspectOverHttp.cs#inspect-mount)]

`MapPragueEndpoints` resolves `IDataCacheRegistry`, `KafkaCachesStatistics` and `IOptions<KafkaCachesGlobalOptions>`; `AddKafkaCaches` registers all three. For `Prague.Api.UI`, add `builder.AddPragueUI()` and `app.MapPragueUI()` next to it.

## 2. Statistics

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/InspectOverHttp.cs#inspect-statistics)]

The JSON is `Consumers` → one entry per `AddKafkaCaches` section → `Caches` → per-cache size, load time, messages received and per-index sizes. Property names are PascalCase (the serializer is source-generated without a naming policy).

## 3. Query a cache

`GET /prague/{CacheTypeName}` runs the generated query-string parser against the cache: indexed fields become lanes, the rest become `Where` filters, and the result is the first 100 rows unless you page.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/InspectOverHttp.cs#inspect-query-string)]

| Syntax | Meaning |
|---|---|
| `field=value` | equality (`field=1,2,3` or `field=[1,2,3]` is an IN) |
| `field>=10`, `field.gte=10` | also `>`, `<`, `<=` and `.gt`, `.lt`, `.lte` |
| `field=10..100`, `field=[10..100)` | range; `[` `]` inclusive, `(` `)` exclusive |
| `from=20&size=10` | offset and page size |

Field names are matched case-insensitively. An unknown field is ignored, not rejected. `sort=` is parsed but does not reorder the result.

## 4. By id

`GET /prague/{CacheTypeName}/{id}` is mapped only for caches whose generated `IsKeyParsable` is `true`. For the sample caches, `OrderCache` (string key) and `CustomerCache` (int key) both report `false`, so the route is a 404; query the key field instead: `/prague/OrderCache?orderId=ORD-001`.

## 5. Keep it internal

`MapPragueEndpoints` maps onto any `IEndpointRouteBuilder`, so a route group gives it a prefix, an authorization policy, or a host restriction.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/InspectOverHttp.cs#inspect-secure)]

## Pitfalls

- **Encoded operators.** `HttpClient` percent-encodes `>` and `[` in a URI string and the parser does not decode, so `total>=100` becomes an unknown field and is ignored. From code use the suffix form `total.gte=100` and the bare range `1000..2000`; typed into a browser both forms work.
- **Treating the collection endpoint as an API.** It allocates and serializes the cache's own instances; it is for inspection. Put real traffic behind your own endpoints ([Page an API](page-an-api.md)).
- **Mounting it on the public port.** It reads every cache. Use step 5, or bind it to a management port.
