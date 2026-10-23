// docs: every code block of www/docs/articles/explanation/concurrency.md. One writer thread against
// lock-free readers; each #region is one block the article pulls in verbatim. The tests assert only
// what the contract guarantees (no exception, a self-consistent result, visibility after a hand-off),
// never a timing, so they are deterministic.
namespace Prague.Samples.Explanation;

using System.Collections.Concurrent;
using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

[TestFixture]
public class Concurrency {
	private const int Orders = 1_000;
	private const int Customers = 10;
	private const int Writes = 20_000;
	private const int ReaderThreads = 4;
	private const int QueriesPerReader = 2_000;

	[Test]
	public void WriterAndReaders() {
		var cache = Seed();
		var failures = new ConcurrentQueue<Exception>();

		#region writer-and-readers
		// One writer — in production the Kafka consumer thread — mutates while readers query. Nobody
		// takes a lock: every query completes, and every result is a set of rows that were live at
		// some instant during its walk.
		var writer = new Thread(() => {
			for (var i = 0; i < Writes; i++) {
				var id = i % Orders;
				if (i % 7 == 0)
					cache.Remove(OrderId(id));                                 // delete …
				else
					cache.AddOrUpdate(NewOrder(id, customerId: i % Customers)); // … or move to another customer
			}
		});

		var readers = new Thread[ReaderThreads];
		for (var t = 0; t < readers.Length; t++)
			readers[t] = new Thread(() => {
				try {
					var seen = new HashSet<string>();
					for (var q = 0; q < QueriesPerReader; q++) {
						using var rows = cache.Query().WithCustomerId(q % Customers).ExecutePooled();   // pooled: disposed here

						seen.Clear();
						foreach (var order in rows)
							if (!seen.Add(order.OrderId))
								throw new InvalidOperationException("a key came back twice");
						if (seen.Count != rows.Count)
							throw new InvalidOperationException("Count and the enumeration disagree");
					}
				} catch (Exception e) {
					failures.Enqueue(e);
				}
			});

		writer.Start();
		foreach (var reader in readers)
			reader.Start();
		writer.Join();
		foreach (var reader in readers)
			reader.Join();
		#endregion

		Assert.That(failures, Is.Empty);
	}

	[Test]
	public void NextQuerySeesTheWrite() {
		var cache = Seed();
		Order? seen = null;

		#region next-query-sees-the-write
		// A write is published before AddOrUpdate returns. Anything that orders the reader after the
		// writer — a signal, a message, a Task continuation — orders the query after the write.
		using var written = new ManualResetEventSlim();

		var writer = new Thread(() => {
			cache.AddOrUpdate(NewOrder(1, customerId: 42, total: 250m));
			written.Set();
		});
		var reader = new Thread(() => {
			written.Wait();
			if (cache.TryGet(OrderId(1), out var order))
				seen = order;
		});

		writer.Start();
		reader.Start();
		writer.Join();
		reader.Join();
		#endregion

		Assert.That(seen, Is.Not.Null);
		Assert.That(seen!.CustomerId, Is.EqualTo(42));
		Assert.That(seen.Total, Is.EqualTo(250m));
	}

	[Test]
	public void SharedFrozenQuery() {
		var cache = Seed();
		var failures = new ConcurrentQueue<Exception>();

		#region shared-frozen-query
		// Build once, store in a field, execute from every thread. The frozen query holds no
		// per-execution state; each ExecutePooled rents its own buffer.
		var byCustomer = cache.Prepare<int>()
			.WithCustomerId(static a => a)
			.BuildFrozen();

		var writer = new Thread(() => {
			for (var i = 0; i < Writes; i++)
				cache.AddOrUpdate(NewOrder(i % Orders, customerId: i % Customers));
		});
		var readers = new Thread[ReaderThreads];
		for (var t = 0; t < readers.Length; t++)
			readers[t] = new Thread(() => {
				try {
					for (var q = 0; q < QueriesPerReader; q++) {
						using var rows = byCustomer.ExecutePooled(q % Customers);
						if (rows.Count > Orders)
							throw new InvalidOperationException("more rows than keys");
					}
				} catch (Exception e) {
					failures.Enqueue(e);
				}
			});

		writer.Start();
		foreach (var reader in readers)
			reader.Start();
		writer.Join();
		foreach (var reader in readers)
			reader.Join();
		#endregion

		Assert.That(failures, Is.Empty);
	}

	[Test]
	public void DisposeOnAnotherThread() {
		var cache = Seed();

		#region dispose-on-another-thread
		// The pool is process-wide: a pooled result may be handed to another thread and disposed
		// there. What matters is exactly one Dispose, by whoever ends up owning the struct.
		var rows = cache.Query().WithCustomerId(3).ExecutePooled();
		var count = rows.Count;

		var consumer = Task.Run(() => {
			using (rows)
				return rows.Count;
		});
		#endregion

		Assert.That(consumer.Result, Is.EqualTo(count));
		Assert.That(count, Is.EqualTo(Orders / Customers));
	}

	[Test]
	public void SerializeMultipleWriters() {
		var cache = new OrderCache();

		#region serialize-multiple-writers
		// Index maintenance is written for one writer. If several threads must write, serialize them
		// yourself — a lock, a channel drained by one thread, an actor. Readers are unaffected either way.
		var gate = new object();
		var writers = new Thread[2];
		for (var w = 0; w < writers.Length; w++) {
			var customer = w + 1;
			writers[w] = new Thread(() => {
				for (var i = 0; i < Orders; i++)
					lock (gate)
						cache.AddOrUpdate(NewOrder(i, customerId: customer));   // the last writer of each key wins
			});
		}

		foreach (var writer in writers)
			writer.Start();
		foreach (var writer in writers)
			writer.Join();
		#endregion

		// Every key is held by exactly one customer, and the indexes agree with the store.
		var c1 = cache.Query().WithCustomerId(1).Count();
		var c2 = cache.Query().WithCustomerId(2).Count();
		Assert.That(c1 + c2, Is.EqualTo(Orders));
		Assert.That(cache.CustomerIdIndex.GetValues(1).Count + cache.CustomerIdIndex.GetValues(2).Count, Is.EqualTo(Orders));
	}

	[Test]
	public void JoinSlotsUnderAWriter() {
		var registry = new DataCacheRegistryBuilder().Register<OrderCache>().Build();
		var orders = registry.GetCache<OrderCache>();
		var lines = registry.GetCache<OrderLineCache>();
		for (var i = 0; i < 100; i++) {
			orders.AddOrUpdate(NewOrder(i, customerId: 1));
			for (var l = 0; l < 5; l++)
				lines.AddOrUpdate(NewLine(i, l));
		}

		var failures = new ConcurrentQueue<Exception>();
		var dropped = QueryResultsDiagnostics.DroppedRows;

		#region join-slots-under-a-writer
		// A JoinMany slot is sized from the pairs the resolver recorded, so a right added while the
		// query runs is simply not in this result. Truncated stays false; the next query includes it.
		var writer = new Thread(() => {
			for (var i = 0; i < Writes; i++) {
				var line = NewLine(i % 100, 5 + i % 3);
				if (i % 2 == 0)
					lines.AddOrUpdate(line);
				else
					lines.Remove(line.LineId);
			}
		});
		var reader = new Thread(() => {
			try {
				for (var q = 0; q < QueriesPerReader; q++) {
					using var rows = orders.Query().WithCustomerId(1).JoinWithOrderLine().ExecutePooled();
					if (rows.Truncated)
						throw new InvalidOperationException("a slot dropped a row");
					foreach (var row in rows)
						if (row.Right.Count < 5)
							throw new InvalidOperationException("the five stable lines are always there");
				}
			} catch (Exception e) {
				failures.Enqueue(e);
			}
		});

		writer.Start();
		reader.Start();
		writer.Join();
		reader.Join();
		#endregion

		Assert.That(failures, Is.Empty);
		Assert.That(QueryResultsDiagnostics.DroppedRows, Is.EqualTo(dropped));
	}

	private static OrderCache Seed() {
		var cache = new OrderCache();
		for (var i = 0; i < Orders; i++)
			cache.AddOrUpdate(NewOrder(i, customerId: i % Customers));
		return cache;
	}

	private static string OrderId(int id) => $"ORD-{id:D4}";

	private static Order NewOrder(int id, int customerId, decimal total = 99.99m) => new() {
		OrderId = OrderId(id),
		CustomerId = customerId,
		CreatedAtUnixMs = 1_700_000_000_000L + id,
		Status = OrderStatus.Pending,
		Total = total,
	};

	private static OrderLine NewLine(int orderId, int line) => new() {
		LineId = $"{OrderId(orderId)}/{line}",
		OrderId = OrderId(orderId),
		ProductId = line,
		Quantity = 1,
		UnitPrice = 10m,
	};
}
