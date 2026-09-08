namespace Prague.Core;

// ── Bounded sorted paging: when does a page still pay for the top-K plan? ──
//
// Execute(skip, take) with a finite take runs a top-K plan instead of the classic full sort: a heap of
// skip + take rows while that is small, otherwise collect every matched row as a (value, ordinal) tuple
// and introselect the page bounds, sorting only the page. Both beat the full sort as long as the page
// is a fraction of the result; once the page IS the result — or most of it — the collect plan
// degenerates into a full sort of tuples through a two-level comparison (value, then ordinal), which
// the classic path does cheaper with StableSort over the plain values and the same stable order. The
// routing below sends such pages to the classic core, so a finite take never costs more than
// Execute() does.
//
// What matters is the page LENGTH, not skip + take: the collect plan's cost is one pass over every row
// plus a sort of the page, so a deep short page (skip 99 000, take 1 000 of 100 000) stays cheap and
// must keep the top-K plan, while the classic fallback always sorts everything.

/// <summary>Plan routing shared by the simple and joined bounded sorted terminals.</summary>
internal static class TopKPlan {
	// Share of the base row bound a page must hold before the classic full sort beats the collect
	// plan. Measured on 100k rows with distinct keys: the collect plan costs 0.84× the classic sort at
	// a half page and 1.03× at 5/8, crossing at ≈0.60; the exact full page costs 1.4× (distinct keys)
	// to 3.0× (tie-heavy keys) the classic sort. Tie-heavy keys would prefer an even lower share, but
	// the router cannot see the key distribution, so the distinct-key crossing wins.
	internal const int ClassicShareNumerator = 3;
	internal const int ClassicShareDenominator = 5;

	/// <summary>Rows the [skip, skip + take) page can actually hold out of <paramref name="maxCount"/>.</summary>
	internal static int PageLength(int skip, int take, int maxCount) {
		var remaining = Math.Max((long)maxCount - skip, 0);
		return (int)Math.Min(take, remaining);
	}

	/// <summary>
	/// True when the page holds at least <see cref="ClassicShareNumerator"/>/<see cref="ClassicShareDenominator"/>
	/// of <paramref name="maxCount"/>, the base walk's row bound (narrowed candidates, or the cache
	/// size): the collect plan would sort nearly everything anyway, through the slower tuple comparison.
	/// </summary>
	internal static bool PageIsMostOfResult(int skip, int take, int maxCount) =>
		(long)PageLength(skip, take, maxCount) * ClassicShareDenominator >= (long)maxCount * ClassicShareNumerator;

	/// <summary>
	/// True when the page holds every row the base walk can emit — no skip, take at least the bound. A
	/// joined page with a skip keeps the top-K plan whatever its length: it saves the join fill of the
	/// rows it skips, which the classic path cannot.
	/// </summary>
	internal static bool PageCoversResult(int skip, int take, int maxCount) =>
		skip == 0 && take >= maxCount;
}
