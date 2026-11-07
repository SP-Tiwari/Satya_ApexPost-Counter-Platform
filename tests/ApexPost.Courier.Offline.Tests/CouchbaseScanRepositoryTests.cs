using System.Text.Json;
using ApexPost.Courier.Offline.Domain;
using Couchbase.Lite;

namespace ApexPost.Courier.Offline.Tests;

// Criteria: document model, and repository with key-value CRUD.
public sealed class CouchbaseScanRepositoryTests
{
	[Fact]
	public void Create_StoresTheSevenFieldsAsAJsonDocument()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New(notes: "Fragile") with
		{
			ScannedAtUtc = new DateTimeOffset(2026, 11, 2, 9, 30, 15, 123, TimeSpan.Zero)
		};

		context.Scans.Create(scan);

		using var document = context.Database.Collection.GetDocument(scan.Id)!;
		using var json = JsonDocument.Parse(document.ToJSON());
		var root = json.RootElement;
		Assert.Equal(
			["courierId", "location", "notes", "scanType", "scannedAtUtc", "syncStatus", "trackingNumber"],
			root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
		Assert.Equal("APX-TEST-0001", root.GetProperty("trackingNumber").GetString());
		Assert.Equal("Pickup", root.GetProperty("scanType").GetString());
		Assert.Equal("COURIER-TEST-01", root.GetProperty("courierId").GetString());
		Assert.Equal("Test Depot", root.GetProperty("location").GetString());
		Assert.Equal("2026-11-02T09:30:15.1230000Z", root.GetProperty("scannedAtUtc").GetString());
		Assert.Equal("Fragile", root.GetProperty("notes").GetString());
		Assert.Equal("Pending", root.GetProperty("syncStatus").GetString());
	}

	[Fact]
	public void GetById_ReturnsTheSameScanByKeyValueLookup()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New(notes: "Ring twice");
		context.Scans.Create(scan);

		Assert.Equal(scan, context.Scans.GetById(scan.Id));
	}

	[Fact]
	public void GetById_ReturnsNullForAnUnknownId()
	{
		using var context = new CourierTestContext();

		Assert.Null(context.Scans.GetById(ShipmentScanId.New()));
	}

	[Fact]
	public void GetById_RejectsAMalformedId()
	{
		using var context = new CourierTestContext();

		Assert.Throws<ArgumentException>(() => context.Scans.GetById("abc"));
	}

	[Fact]
	public void Create_RefusesAnExistingId()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New();
		context.Scans.Create(scan);

		Assert.Throws<InvalidOperationException>(
			() => context.Scans.Create(scan with { Location = "Elsewhere" }));
		Assert.Equal("Test Depot", context.Scans.GetById(scan.Id)!.Location);
	}

	[Theory]
	[InlineData(SyncStatus.Synced)]
	[InlineData(SyncStatus.Failed)]
	public void Create_RefusesAScanThatIsNotPending(SyncStatus status)
	{
		using var context = new CourierTestContext();
		var scan = Scans.New() with { SyncStatus = status };

		Assert.Throws<InvalidOperationException>(() => context.Scans.Create(scan));
		Assert.Null(context.Scans.GetById(scan.Id));
	}

	[Fact]
	public void Update_ReplacesTheStoredScan()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New();
		context.Scans.Create(scan);
		var changed = scan.MarkSynced() with { Location = "Test Hub", Notes = null };

		context.Scans.Update(changed);

		Assert.Equal(changed, context.Scans.GetById(scan.Id));
	}

	[Fact]
	public void Update_ThrowsForAMissingScan()
	{
		using var context = new CourierTestContext();

		Assert.Throws<KeyNotFoundException>(() => context.Scans.Update(Scans.New()));
	}

	[Fact]
	public void Delete_RemovesTheScan()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New();
		context.Scans.Create(scan);

		Assert.True(context.Scans.Delete(scan.Id));

		Assert.Null(context.Scans.GetById(scan.Id));
		Assert.Empty(context.Scans.ListAll());
		Assert.False(context.Scans.Delete(scan.Id));
	}

	[Fact]
	public void ListAll_ReturnsEveryScanOldestFirst()
	{
		using var context = new CourierTestContext();
		var later = Scans.New(minutesAfterBase: 10);
		var earlier = Scans.New(minutesAfterBase: 1);
		context.Scans.Create(later);
		context.Scans.Create(earlier);

		Assert.Equal([earlier, later], context.Scans.ListAll());
	}

	[Fact]
	public void ListAll_IgnoresDocumentsThatAreNotScans()
	{
		using var context = new CourierTestContext();
		context.Scans.Create(Scans.New());
		using var other = new MutableDocument("settings::device");
		other.SetString("trackingNumber", "not a scan");
		context.Database.Collection.Save(other);

		Assert.Single(context.Scans.ListAll());
	}

	[Fact]
	public void Read_FailsLoudlyForACorruptDocument()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New();
		context.Scans.Create(scan);
		using (var stored = context.Database.Collection.GetDocument(scan.Id)!)
		using (var corrupt = stored.ToMutable())
		{
			corrupt.SetString("scanType", "1");
			context.Database.Collection.Save(corrupt);
		}

		var exception = Assert.Throws<InvalidDataException>(() => context.Scans.GetById(scan.Id));
		Assert.Contains(scan.Id, exception.Message);
	}
}
