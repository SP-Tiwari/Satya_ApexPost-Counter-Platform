using System.Net;
using System.Text.Json;
using ApexPost.Courier.Domain;
using Polly.CircuitBreaker;

namespace ApexPost.Courier.Sync.Tests;

// D9 (with D7 and D10): the Part A outbox driving the real ApiShipmentUploader
// through the production client pipeline, against a stub API.
public sealed class OfflineSyncTests
{
	private const string A = "APX-00000201";
	private const string B = "APX-00000202";

	[Fact]
	public async Task FortyFiveScans_GoInBatchesOf20_20And5_EachSplitByShipment_OldestFirst()
	{
		using var host = new ClientHost(Accept);
		var repository = new InMemoryScanRepository();
		var scans = TestScans.Store(
			repository,
			[.. Enumerable.Range(0, 45).Select(minute => TestScans.For(minute % 2 == 0 ? A : B, minute))]);

		var result = await host.Outbox(repository).SyncPendingAsync();

		// Batch 1: scans 0-19, batch 2: 20-39, batch 3: 40-44; each batch is one
		// request per shipment, in the order its first scan appears.
		var requests = host.Network.Requests;
		Assert.Equal([10, 10, 10, 10, 3, 2], requests.Select(request => ScanIds(request).Count));
		Assert.Equal([A, B, A, B, A, B], requests.Select(request => request.Uri.AbsolutePath.Split('/')[4]));
		Assert.Equal(
			scans.Where(scan => scan.TrackingNumber == A).Select(scan => scan.Id),
			requests.Where(request => request.Uri.AbsolutePath.Contains(A)).SelectMany(ScanIds));
		Assert.Equal((45, 45, 0, 0), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Empty(repository.ListPending());
	}

	[Fact]
	public async Task Unauthorized_LeavesEveryScanPending_ClearsTheToken_AndStopsTheRun()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.Unauthorized));
		host.Tokens.Token = TestValues.ValidToken();
		var repository = new InMemoryScanRepository();
		var scans = TestScans.Store(
			repository,
			[.. Enumerable.Range(0, 25).Select(minute => TestScans.For(A, minute))]);

		var result = await host.Outbox(repository).SyncPendingAsync();

		Assert.Equal((20, 0, 0, 25), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Equal("Your sign-in is missing, invalid or expired. Sign in again.", result.StoppedBecause);
		Assert.All(scans, scan => Assert.Equal(SyncStatus.Pending, repository.StatusOf(scan)));
		Assert.Single(host.Network.Requests);
		Assert.Null(host.Tokens.Token);
		Assert.True(host.Session.IsSignInRequired);
	}

	[Fact]
	public async Task WhileTheCircuitIsOpen_ScansAreStillSavedLocally_AndStayPending()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.ServiceUnavailable));
		var repository = new InMemoryScanRepository();
		TestScans.Store(repository, TestScans.For(A, 1));
		var outbox = host.Outbox(repository);

		// Two runs: four failed attempts, then the fifth failure opens the circuit.
		await outbox.SyncPendingAsync();
		var opening = await outbox.SyncPendingAsync();
		Assert.Equal(CircuitState.Open, host.State.Snapshot().CircuitState);
		Assert.Equal("Server unavailable – working offline.", opening.StoppedBecause);
		var requests = host.Network.Requests.Count;

		// Saving needs no network: a new scan is stored as Pending as usual.
		var savedOffline = TestScans.For(B, 2);
		repository.Create(savedOffline);
		var whileOpen = await outbox.SyncPendingAsync();

		Assert.Equal((2, 0, 0, 2), (whileOpen.Attempted, whileOpen.Synced, whileOpen.Failed, whileOpen.Pending));
		Assert.Equal(requests, host.Network.Requests.Count);
		Assert.Equal(2, repository.ListPending().Count);
		Assert.Equal(SyncStatus.Pending, repository.StatusOf(savedOffline));
	}

	[Fact]
	public async Task CircuitStateChanges_ArePublishedToTheSyncState()
	{
		var serverUp = false;
		using var host = new ClientHost(request => serverUp ? Accept(request) : Responses.Empty(HttpStatusCode.ServiceUnavailable));
		var repository = new InMemoryScanRepository();
		TestScans.Store(repository, TestScans.For(A, 1));
		var outbox = host.Outbox(repository);
		var states = new List<CircuitState>();
		host.State.Changed += (_, _) => states.Add(host.State.Snapshot().CircuitState);
		Assert.Equal(CircuitState.Closed, host.State.Snapshot().CircuitState);

		await outbox.SyncPendingAsync();
		await outbox.SyncPendingAsync();
		serverUp = true;
		host.Clock.Advance(CourierResilience.BreakDuration);
		var recovered = await outbox.SyncPendingAsync();

		Assert.Equal([CircuitState.Open, CircuitState.HalfOpen, CircuitState.Closed], states);
		Assert.Equal(1, recovered.Synced);
		Assert.Equal(CircuitState.Closed, host.State.Snapshot().CircuitState);
	}

	private static HttpResponseMessage Accept(HttpRequestMessage request)
	{
		var count = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result)
			.RootElement.GetProperty("scans").GetArrayLength();
		return Responses.Json(
			HttpStatusCode.OK,
			$$"""{"accepted":{{count}},"duplicatesSkipped":0,"rejected":0,"rejections":[]}""");
	}

	private static List<string> ScanIds(RecordedRequest request)
	{
		using var body = JsonDocument.Parse(request.Body!);
		return [.. body.RootElement.GetProperty("scans").EnumerateArray().Select(item => item.GetProperty("scanId").GetString()!)];
	}
}
