using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Offline.Persistence;
using Xunit.Abstractions;

namespace ApexPost.Courier.Offline.Tests;

// Criterion: SQL++ queries, parameters and indexes.
public sealed class ScanQueryTests(ITestOutputHelper output)
{
	[Fact]
	public void FindByTrackingNumber_ReturnsOnlyThatShipmentOrderedByScannedAtUtc()
	{
		using var context = new CourierTestContext();
		var delivered = Scans.New(scanType: ScanType.Delivered, minutesAfterBase: 30);
		var pickup = Scans.New(scanType: ScanType.Pickup, minutesAfterBase: 0);
		var inTransit = Scans.New(scanType: ScanType.InTransit, minutesAfterBase: 15);
		context.Scans.Create(delivered);
		context.Scans.Create(Scans.New(trackingNumber: "APX-TEST-0002", minutesAfterBase: 5));
		context.Scans.Create(pickup);
		context.Scans.Create(inTransit);

		var found = context.Scans.FindByTrackingNumber("APX-TEST-0001");

		Assert.Equal([pickup, inTransit, delivered], found);
	}

	[Fact]
	public void ListPending_ReturnsOnlyPendingScansOldestFirst()
	{
		using var context = new CourierTestContext();
		var secondPending = Scans.New(minutesAfterBase: 20);
		var firstPending = Scans.New(minutesAfterBase: 10);
		var synced = Scans.New(minutesAfterBase: 5);
		var failed = Scans.New(minutesAfterBase: 1);
		foreach (var scan in new[] { secondPending, firstPending, synced, failed })
			context.Scans.Create(scan);
		context.Scans.Update(synced.MarkSynced());
		context.Scans.Update(failed.MarkSyncFailed());

		Assert.Equal([firstPending, secondPending], context.Scans.ListPending());
		Assert.Equal(2, context.Database.CountPendingScans());
	}

	[Fact]
	public void CountByScanType_CountsOnlyThatCourier()
	{
		using var context = new CourierTestContext();
		context.Scans.Create(Scans.New(scanType: ScanType.Pickup));
		context.Scans.Create(Scans.New(scanType: ScanType.Pickup));
		context.Scans.Create(Scans.New(scanType: ScanType.Delivered));
		context.Scans.Create(Scans.New(scanType: ScanType.Failed));
		context.Scans.Create(Scans.New(scanType: ScanType.InTransit, courierId: "COURIER-TEST-02"));

		var counts = context.Scans.CountByScanType("COURIER-TEST-01");

		Assert.Equal(
			[
				new ScanTypeCount(ScanType.Delivered, 1),
				new ScanTypeCount(ScanType.Failed, 1),
				new ScanTypeCount(ScanType.Pickup, 2)
			],
			counts);
		Assert.Empty(context.Scans.CountByScanType("COURIER-UNKNOWN"));
	}

	// The values are bound as $parameters, so query syntax in them is only text.
	[Theory]
	[InlineData("' OR 1=1 --")]
	[InlineData("APX-TEST-0001' OR trackingNumber IS NOT MISSING OR '")]
	[InlineData("\" OR TRUE OR \"")]
	public void Parameters_TreatQuerySyntaxAsAPlainValue(string hostile)
	{
		using var context = new CourierTestContext();
		context.Scans.Create(Scans.New());
		var hostileScan = Scans.New(trackingNumber: hostile, courierId: hostile);
		context.Scans.Create(hostileScan);

		Assert.Equal([hostileScan], context.Scans.FindByTrackingNumber(hostile));
		Assert.Equal([new ScanTypeCount(ScanType.Pickup, 1)], context.Scans.CountByScanType(hostile));
	}

	[Fact]
	public void QueryText_UsesParametersAndNoFormattedValues()
	{
		string[] queries =
		[
			ScanQueries.All,
			ScanQueries.ByTrackingNumber,
			ScanQueries.BySyncStatus,
			ScanQueries.CountBySyncStatus,
			ScanQueries.CountByScanTypeForCourier
		];

		foreach (var sql in queries)
		{
			Assert.Contains("$", sql);
			Assert.DoesNotContain("'", sql);
			Assert.DoesNotContain("{", sql);
		}
	}

	[Fact]
	public void FindByTrackingNumber_UsesTheTrackingNumberIndex()
	{
		using var context = new CourierTestContext();
		context.Scans.Create(Scans.New());

		var plan = context.Scans.ExplainFindByTrackingNumber("APX-TEST-0001");
		output.WriteLine(plan);

		Assert.Contains($"USING INDEX {CourierDatabase.TrackingNumberIndexName}", plan);
	}

	[Fact]
	public void ListPending_UsesTheSyncStatusIndex()
	{
		using var context = new CourierTestContext();
		context.Scans.Create(Scans.New());

		var plan = context.Scans.ExplainListPending();
		output.WriteLine(plan);

		Assert.Contains($"USING INDEX {CourierDatabase.SyncStatusIndexName}", plan);
	}
}
