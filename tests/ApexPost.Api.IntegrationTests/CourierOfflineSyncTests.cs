using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Offline.Persistence;
using ApexPost.Courier.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CourierDatabaseFolder = ApexPost.Courier.Offline.Tests.CourierDatabaseFolder;

namespace ApexPost.Api.IntegrationTests;

// D9 end to end: scans in the courier's Couchbase Lite store are synced by
// the production composition (AddCourierSync: SyncCoordinator, OutboxService,
// ApiShipmentUploader, ApexApiClient with its handlers) to the real API in
// memory, on a disposable SQL Server database.
public sealed class CourierOfflineSyncTests : IDisposable
{
	private const string A = "APX-62000001";
	private const string B = "APX-62000002";
	private const string C = "APX-62000003";

	private readonly ApiFactory _factory = new();
	private readonly CourierDatabaseFolder _folder = new();

	public void Dispose()
	{
		_factory.Dispose();
		_folder.Dispose();
	}

	[Fact]
	public async Task PendingScans_AreSyncedFailedOrSkippedAsDuplicates_ByTheApisAnswer()
	{
		var admin = _factory.CreateClient();
		await admin.CreateAsync(A);
		await admin.CreateAsync(C);
		var startUtc = DateTimeOffset.UtcNow.AddHours(-1);

		// Two scans the server already holds for A, as if sent from another run.
		var alreadyStored = Api.NewScanId();
		var storedForA = Api.NewScanId();
		var preload = await admin.UploadAsync(A, Api.Scan(alreadyStored, A, startUtc), Api.Scan(storedForA, A, startUtc.AddMinutes(1)));
		Assert.Equal(HttpStatusCode.OK, preload.StatusCode);

		using var database = new CourierDatabase(_folder.Path);
		var repository = new CouchbaseScanRepository(database);
		var a1 = Scan(A, startUtc.AddMinutes(2));
		var a2 = Scan(A, startUtc.AddMinutes(3));
		var duplicate = Scan(A, startUtc.AddMinutes(4)) with { Id = alreadyStored };
		var otherShipment = Scan(C, startUtc.AddMinutes(5)) with { Id = storedForA };
		var unknownShipment = Scan(B, startUtc.AddMinutes(6));
		var c1 = Scan(C, startUtc.AddMinutes(7));
		foreach (var scan in new[] { a1, a2, duplicate, otherShipment, unknownShipment, c1 })
			repository.Create(scan);

		await using var provider = CourierServices(repository);
		var coordinator = provider.GetRequiredService<SyncCoordinator>();

		var result = await coordinator.SyncNowAsync();

		Assert.NotNull(result);
		Assert.Equal((6, 4, 2, 0), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Null(result.StoppedBecause);
		foreach (var scan in new[] { a1, a2, duplicate, c1 })
			Assert.Equal(SyncStatus.Synced, repository.GetById(scan.Id)!.SyncStatus);
		Assert.Equal(SyncStatus.Failed, repository.GetById(otherShipment.Id)!.SyncStatus);
		Assert.Equal(SyncStatus.Failed, repository.GetById(unknownShipment.Id)!.SyncStatus);
		Assert.Equal(
			"The scan ID is already recorded for another shipment.",
			result.Failures.Single(failure => failure.ScanId == otherShipment.Id).Reason);
		Assert.Equal(B, result.Failures.Single(failure => failure.ScanId == unknownShipment.Id).TrackingNumber);
		Assert.Same(result, provider.GetRequiredService<SyncState>().Snapshot().LastResult);
		Assert.Equal(4, await ServerScanCountAsync(admin, A));
		Assert.Equal(1, await ServerScanCountAsync(admin, C));

		// Sending an accepted scan again (as after a crash between upload and
		// marking it Synced) is counted as a duplicate and stores nothing new.
		repository.Update(repository.GetById(a1.Id)! with { SyncStatus = SyncStatus.Pending });

		var again = await coordinator.SyncNowAsync();

		Assert.Equal((1, 1, 0, 0), (again!.Attempted, again.Synced, again.Failed, again.Pending));
		Assert.Equal(SyncStatus.Synced, repository.GetById(a1.Id)!.SyncStatus);
		Assert.Equal(4, await ServerScanCountAsync(admin, A));
	}

	private ServiceProvider CourierServices(IScanRepository repository)
	{
		var services = new ServiceCollection().AddLogging();
		services.AddSingleton(repository);
		services.AddSingleton<ITokenStore>(
			TestTokens.CourierTokenStore());
		services
			.AddCourierSync(new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>
				{
					[CourierSyncRegistration.BaseUrlKey] = "https://localhost/"
				})
				.Build())
			.ConfigurePrimaryHttpMessageHandler(() => _factory.Server.CreateHandler());
		return services.BuildServiceProvider();
	}

	private static ShipmentScan Scan(string trackingNumber, DateTimeOffset scannedAtUtc) =>
		ShipmentScan.Record(trackingNumber, ScanType.InTransit, TestTokens.CourierId, "Pune hub", scannedAtUtc.ToUniversalTime());

	private static async Task<int> ServerScanCountAsync(HttpClient client, string trackingNumber) =>
		(await client.GetFromJsonAsync<JsonElement>(Api.Scans(trackingNumber))).GetArrayLength();
}
