namespace Prague.Baseline.Bdn;

using BenchmarkDotNet.Running;

internal static class Program {
	private static void Main(string[] args) {
		var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
		// Config name and path come from the environment so a run can be split across processes:
		// BDN's in-process toolchain shares one PragueArrayPool, and a benchmark that leaves the pool
		// warm changes the allocation another class measures. Configs are the harness's isolation unit.
		var config = Environment.GetEnvironmentVariable("PRAGUE_PERF_CONFIG") ?? "core-only";
		BdnResultExport.Emit(summaries, $"perf/out/{config}.json");
	}
}
