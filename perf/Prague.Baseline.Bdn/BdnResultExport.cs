namespace Prague.Baseline.Bdn;

using BenchmarkDotNet.Reports;
using Prague.Baseline.Scenario;
using Metric = Prague.Baseline.Scenario.Metric;

internal static class BdnResultExport {
	// BDN 0.15.8: Descriptor.Id for the MemoryDiagnoser allocated-bytes metric.
	private const string AllocatedMemoryId = "Allocated Memory";

	public static void Emit(IEnumerable<Summary> summaries, string outPath) {
		var metrics = new List<Metric>();
		foreach (var summary in summaries) {
			foreach (var report in summary.Reports) {
				if (!report.Success || report.ResultStatistics is null) continue;
				var name = report.BenchmarkCase.Descriptor.WorkloadMethod.Name;
				var meanNs = report.ResultStatistics.Mean; // nanoseconds
				var allocBytes = report.Metrics.TryGetValue(AllocatedMemoryId, out var m) ? m.Value : 0;

				if (name == "IngestAll") {
					var meanSeconds = meanNs / 1_000_000_000.0;
					metrics.Add(new Metric("ingest.throughput", "ent/s",
						ScenarioSpec.TotalEntities / meanSeconds, true));
					metrics.Add(new Metric("ingest.alloc", "bytes",
						allocBytes / ScenarioSpec.TotalEntities, false));
				} else {
					var type = MapQueryType(name);
					// core-only (BDN) has no percentile: `.p50` carries BDN's Mean as a
					// stable per-op proxy; true percentiles come from the harness config.
					metrics.Add(new Metric($"query.{type}.p50", "ns", meanNs, false));
					// A joined shape's allocation is not reproducible across runs in this harness —
					// measured 64 B and 268 B for the same code on consecutive runs — so gating it
					// would fail the tripwire at random. p50 for that shape is stable to ~1%.
					// query.joinMany.alloc and query.multiJoin.alloc predate this and have the same
					// problem; they are left as they are rather than silently dropped.
					if (!SkipAllocMetric(name))
						metrics.Add(new Metric($"query.{type}.alloc", "bytes", allocBytes, false));
				}
			}
		}

		var result = new BaselineResult(
			EnvCapture.MachineClass(),
			Environment.GetEnvironmentVariable("PRAGUE_PERF_CONFIG") ?? "core-only",
			Environment.GetEnvironmentVariable("PRAGUE_PERF_COMMIT") ?? "local",
			DateTime.UtcNow.ToString("O"), EnvCapture.Current(), metrics);
		ResultWriter.Write(outPath, result);
		Console.WriteLine($"[baseline] wrote {outPath} ({metrics.Count} metrics)");
	}

	// Joined shapes measure 224 B and 24 B on consecutive identical runs, the way
	// query.joinMany.alloc flips between 64 B and ~268 B. SortBoundedFullPage allocates a fraction of
	// a byte per op, which BDN rounds to 0 or 1 from run to run — gated against a zero baseline, that
	// reads as an infinite regression. Neither is a number worth failing a build over; their p50s are
	// stable to ~1%.
	private static bool SkipAllocMetric(string method)
		=> method is "SortTiedJoined" or "SortTiedJoinedBoundedPage"
			or "SortTiedLeftThenJoinClassic" or "SortTiedLeftThenJoinBounded"
			or "SortBoundedFullPage";

	private static string MapQueryType(string method) => method switch {
		"UniqueLookup" => "uniqueLookup",
		"RangeScan" => "rangeScan",
		"JoinOne" => "joinOne",
		"JoinMany" => "joinMany",
		"MultiJoin" => "multiJoin",
		"SortDistinct" => "sortDistinct",
		"SortTied" => "sortTied",
		"SortTiedJoined" => "sortTiedJoined",
		"SortBoundedPage" => "sortBoundedPage",
		"SortBoundedFullPage" => "sortBoundedFullPage",
		"SortTiedJoinedBoundedPage" => "sortTiedJoinedBoundedPage",
		"SortTiedLeftThenJoinClassic" => "sortTiedLeftThenJoinClassic",
		"SortTiedLeftThenJoinBounded" => "sortTiedLeftThenJoinBounded",
		_ => method,
	};
}
