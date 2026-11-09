using System.Net.Http.Json;
using System.Text.Json;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SyncContracts = ApexPost.Courier.Sync.Contracts;

namespace ApexPost.Api.IntegrationTests;

// D7: the courier client retries a scan upload, so the API must make that safe.
// Here the courier client (ApexPost.Courier.Sync, registered with
// AddCourierSync) talks to the real API in memory. The first attempt reaches
// the API, which stores the scans, but the response is lost on the way back.
// The client retries; the API recognises the scan IDs and answers with
// duplicates, and the shipment keeps exactly one copy of each scan.
public sealed class CourierClientRetryTests : IDisposable
{
	private const string TrackingNumber = "APX-61000001";

	private readonly ApiFactory _factory = new();

	public void Dispose() => _factory.Dispose();

	[Fact]
	public async Task UploadWhoseResponseWasLost_IsRetried_AndTheApiStoresEachScanOnce()
	{
		var admin = _factory.CreateClient();
		await admin.CreateAsync(TrackingNumber);
		var network = new LoseFirstResponseHandler { InnerHandler = _factory.Server.CreateHandler() };

		var services = new ServiceCollection().AddLogging();
		services.AddSingleton<ITokenStore>(
			new CourierToken(new AccessToken(TestTokens.Courier(), DateTimeOffset.UtcNow.AddHours(1))));
		services
			.AddCourierSync(new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>
				{
					[CourierSyncRegistration.BaseUrlKey] = "https://localhost/"
				})
				.Build())
			.ConfigurePrimaryHttpMessageHandler(() => network);
		await using var provider = services.BuildServiceProvider();
		var scannedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5);

		var result = await provider.GetRequiredService<ApexApiClient>().UploadScansAsync(
			TrackingNumber,
			new SyncContracts.ScanUploadRequest(
			[
				Scan(Api.NewScanId(), ScanType.Pickup, scannedAtUtc),
				Scan(Api.NewScanId(), ScanType.InTransit, scannedAtUtc.AddMinutes(1))
			]));

		Assert.Equal(2, network.Attempts);
		Assert.True(result.Succeeded);
		Assert.Equal(
			(0, 2, 0),
			(result.Value!.Accepted, result.Value.DuplicatesSkipped, result.Value.Rejected));
		var stored = await admin.GetFromJsonAsync<JsonElement>(Api.Scans(TrackingNumber));
		Assert.Equal(2, stored.GetArrayLength());
	}

	private static SyncContracts.ScanUploadItem Scan(string scanId, ScanType scanType, DateTimeOffset scannedAtUtc) =>
		new(scanId, TrackingNumber, scanType, TestTokens.CourierId, "Pune hub", scannedAtUtc, null);

	// Passes each request to the API; the first response is dropped and
	// replaced by a connection error, after the API has processed the request.
	private sealed class LoseFirstResponseHandler : DelegatingHandler
	{
		public int Attempts { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			var response = await base.SendAsync(request, cancellationToken);
			if (++Attempts > 1)
				return response;

			response.Dispose();
			throw new HttpRequestException("The connection was reset before the response arrived.");
		}
	}

	private sealed class CourierToken(AccessToken token) : ITokenStore
	{
		public AccessToken? GetToken() => token;

		public void SaveToken(AccessToken newToken) => throw new NotSupportedException();

		public void ClearToken() => throw new NotSupportedException();
	}
}
