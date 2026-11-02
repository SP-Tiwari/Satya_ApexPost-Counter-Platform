using ApexPost.Courier.Offline.Persistence;

namespace ApexPost.Courier.Offline.Tests;

// Criterion: database lifecycle.
public sealed class CourierDatabaseTests
{
	[Fact]
	public void Open_CreatesApexpostCourierWithTheDefaultCollection()
	{
		using var context = new CourierTestContext();

		Assert.Equal("apexpost_courier", context.Database.Database.Name);
		Assert.Equal("_default", context.Database.Collection.Name);
		Assert.Equal("_default", context.Database.Collection.Scope.Name);
		Assert.True(Directory.Exists(Path.Combine(context.FolderPath, "apexpost_courier.cblite2")));
		Assert.StartsWith(context.FolderPath, context.Database.Path);
	}

	[Fact]
	public void Open_CreatesTheValueIndexes()
	{
		using var context = new CourierTestContext();

		var indexes = context.Database.Collection.GetIndexes();

		Assert.Contains(CourierDatabase.TrackingNumberIndexName, indexes);
		Assert.Contains(CourierDatabase.SyncStatusIndexName, indexes);
	}

	[Fact]
	public void Reopen_KeepsTheScansAndTheIndexes()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New();
		context.Scans.Create(scan);

		context.Reopen();

		Assert.Equal(scan, context.Scans.GetById(scan.Id));
		Assert.Equal(2, context.Database.Collection.GetIndexes().Count);
	}

	[Fact]
	public void Dispose_IsSafeToCallTwice()
	{
		using var folder = new CourierDatabaseFolder();
		var database = new CourierDatabase(folder.Path);

		database.Dispose();
		database.Dispose();
	}

	[Fact]
	public void Dispose_MakesTheDatabaseUnusable()
	{
		using var folder = new CourierDatabaseFolder();
		var database = new CourierDatabase(folder.Path);
		var scans = new CouchbaseScanRepository(database);

		database.Dispose();

		Assert.Throws<ObjectDisposedException>(() => database.CountPendingScans());
		Assert.Throws<ObjectDisposedException>(() => database.StartChangeListener());
		Assert.Throws<ObjectDisposedException>(() => scans.ListAll());
		Assert.Throws<ObjectDisposedException>(() => scans.Create(Scans.New()));
	}

	[Fact]
	public void Open_RefusesASecondOwnerOfTheSameDatabase()
	{
		using var context = new CourierTestContext();

		var exception = Assert.Throws<InvalidOperationException>(
			() => new CourierDatabase(context.FolderPath));
		Assert.Contains("already open", exception.Message);
	}

	[Fact]
	public void Dispose_ReleasesTheDatabaseForTheNextOwner()
	{
		using var folder = new CourierDatabaseFolder();
		new CourierDatabase(folder.Path).Dispose();

		using var reopened = new CourierDatabase(folder.Path);

		Assert.Equal(0, reopened.CountPendingScans());
	}

	[Theory]
	[InlineData("")]
	[InlineData("  ")]
	public void Open_RequiresADirectory(string directory) =>
		Assert.Throws<ArgumentException>(() => new CourierDatabase(directory));
}
