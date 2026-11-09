using System.Net;
using System.Text.Json;
using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;
using Polly.CircuitBreaker;

namespace ApexPost.Courier.Sync.Tests;

// D9: the real uploader through the production client pipeline (AddCourierSync,
// with retries, circuit breaker and the auth handler), against a stub API.
public sealed class ApiShipmentUploaderTests
{
	private const string A = "APX-00000101";
	private const string B = "APX-00000102";
	private const string C = "APX-00000103";

	[Fact]
	public async Task Batch_IsGroupedByTrackingNumber_InFirstAppearanceOrder_OneRequestPerShipment()
	{
		using var host = new ClientHost(Accept);
		var a1 = TestScans.For(A, 1);
		var b1 = TestScans.For(B, 2);
		var a2 = TestScans.For(A, 3);
		var c1 = TestScans.For(C, 4);

		var results = await host.Uploader().UploadAsync([a1, b1, a2, c1]);

		Assert.Equal(
			[$"/api/v2/shipments/{A}/scans", $"/api/v2/shipments/{B}/scans", $"/api/v2/shipments/{C}/scans"],
			host.Network.Requests.Select(request => request.Uri.AbsolutePath));
		Assert.Equal([a1.Id, a2.Id], ScanIds(host.Network.Requests[0]));
		Assert.All(results, result => Assert.Equal(ShipmentUploadOutcome.Synced, result.Outcome));
		Assert.Equal([a1.Id, a2.Id, b1.Id, c1.Id], results.Select(result => result.ScanId));
	}

	[Fact]
	public async Task EachScan_IsSentWithAllItsFields_AndTheScanTypeName()
	{
		using var host = new ClientHost(Accept);
		var scan = ShipmentScan.Record(A, ScanType.Delivered, TestValues.CourierId, "Pune hub", TestValues.Now.AddHours(-1), "Left at reception");

		await host.Uploader().UploadAsync([scan]);

		using var body = JsonDocument.Parse(host.Network.Requests[0].Body!);
		var item = body.RootElement.GetProperty("scans")[0];
		Assert.Equal(scan.Id, item.GetProperty("scanId").GetString());
		Assert.Equal(A, item.GetProperty("trackingNumber").GetString());
		Assert.Equal("Delivered", item.GetProperty("scanType").GetString());
		Assert.Equal(TestValues.CourierId, item.GetProperty("courierId").GetString());
		Assert.Equal("Pune hub", item.GetProperty("location").GetString());
		Assert.Equal(scan.ScannedAtUtc, item.GetProperty("scannedAtUtc").GetDateTimeOffset());
		Assert.Equal("Left at reception", item.GetProperty("notes").GetString());
	}

	[Fact]
	public async Task AcceptedAndDuplicateScans_AreSynced_AndRejectedOnesFailWithTheirReason()
	{
		var scans = new[] { TestScans.For(A, 1), TestScans.For(A, 2), TestScans.For(A, 3) };
		using var host = new ClientHost(_ => Responses.Json(
			HttpStatusCode.OK,
			$$"""{"accepted":1,"duplicatesSkipped":1,"rejected":1,"rejections":[{"scanId":"{{scans[2].Id}}","reason":"The scan ID is already recorded for another shipment."}]}"""));

		var results = await host.Uploader().UploadAsync(scans);

		Assert.Equal(ShipmentUploadOutcome.Synced, results[0].Outcome);
		Assert.Equal(ShipmentUploadOutcome.Synced, results[1].Outcome);
		Assert.Equal(
			ShipmentUploadResult.Failure(scans[2].Id, "The scan ID is already recorded for another shipment."),
			results[2]);
	}

	[Theory]
	[InlineData(HttpStatusCode.BadRequest, """{"title":"One or more validation errors occurred.","status":400,"errors":{"Scans[0].ScannedAtUtc":["The scan time cannot be later than the server's current time."]}}""", "One or more validation errors occurred. The scan time cannot be later than the server's current time.")]
	[InlineData(HttpStatusCode.Forbidden, """{"title":"Forbidden","status":403,"detail":"Every scan must be recorded under your own courierId, COURIER-DEMO-07. No scan in the batch was stored."}""", "Every scan must be recorded under your own courierId, COURIER-DEMO-07. No scan in the batch was stored.")]
	[InlineData(HttpStatusCode.NotFound, """{"title":"Not Found","status":404,"detail":"No shipment APX-00000101."}""", "No shipment APX-00000101.")]
	[InlineData(HttpStatusCode.Conflict, "", "The server refused the request (HTTP 409).")]
	[InlineData(HttpStatusCode.UnsupportedMediaType, "", "The server refused the request (HTTP 415).")]
	public async Task AFinal4xx_FailsThatShipmentsScans_AndTheNextShipmentIsStillSent(
		HttpStatusCode status,
		string problem,
		string reason)
	{
		using var host = new ClientHost(request => request.RequestUri!.AbsolutePath.Contains(A)
			? Responses.Problem(status, problem)
			: Accept(request));
		var a1 = TestScans.For(A, 1);
		var b1 = TestScans.For(B, 2);

		var results = await host.Uploader().UploadAsync([a1, b1]);

		Assert.Equal(ShipmentUploadResult.Failure(a1.Id, reason), results[0]);
		Assert.Equal(ShipmentUploadOutcome.Synced, results[1].Outcome);
		Assert.Equal(2, host.Network.Requests.Count);
	}

	[Fact]
	public async Task Unauthorized_LeavesTheScansForLater_ClearsTheToken_AndSendsNothingMore()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.Unauthorized));
		host.Tokens.Token = TestValues.ValidToken();
		var a1 = TestScans.For(A, 1);
		var b1 = TestScans.For(B, 2);

		var results = await host.Uploader().UploadAsync([a1, b1]);

		Assert.All(results, result => Assert.Equal(ShipmentUploadOutcome.RetryLater, result.Outcome));
		Assert.Equal("Your sign-in is missing, invalid or expired. Sign in again.", results[1].FailureReason);
		Assert.Single(host.Network.Requests);
		Assert.Null(host.Tokens.Token);
		Assert.True(host.Session.IsSignInRequired);
	}

	[Theory]
	[InlineData(HttpStatusCode.RequestTimeout, 4)]
	[InlineData(HttpStatusCode.TooManyRequests, 1)]
	[InlineData(HttpStatusCode.InternalServerError, 4)]
	[InlineData(HttpStatusCode.ServiceUnavailable, 4)]
	public async Task NoFinalAnswer_AfterTheRetries_LeavesTheScansForLater(HttpStatusCode status, int attempts)
	{
		using var host = new ClientHost(_ => Responses.Empty(status));
		var a1 = TestScans.For(A, 1);
		var b1 = TestScans.For(B, 2);

		var results = await host.Uploader().UploadAsync([a1, b1]);

		Assert.All(results, result => Assert.Equal(ShipmentUploadOutcome.RetryLater, result.Outcome));
		Assert.Equal(attempts, host.Network.Requests.Count);
		Assert.All(host.Network.Requests, request => Assert.Contains(A, request.Uri.AbsolutePath));
	}

	[Fact]
	public async Task NetworkFailure_LeavesTheScansForLater()
	{
		using var host = new ClientHost();
		host.Network.Throw = new HttpRequestException("No route to host.");

		var results = await host.Uploader().UploadAsync([TestScans.For(A, 1), TestScans.For(B, 2)]);

		Assert.All(results, result => Assert.Equal(
			("The API could not be reached.", ShipmentUploadOutcome.RetryLater),
			(result.FailureReason!, result.Outcome)));
		Assert.Equal(4, host.Network.Requests.Count);
	}

	[Fact]
	public async Task Timeout_LeavesTheScansForLater()
	{
		using var host = new ClientHost();
		host.Network.Hang = _ => true;

		var upload = host.Uploader().UploadAsync([TestScans.For(A, 1)]);
		for (var attempt = 1; attempt <= 3; attempt++)
		{
			Assert.True(await host.Network.AttemptStarted.WaitAsync(TimeSpan.FromSeconds(10)));
			host.Clock.Advance(CourierResilience.AttemptTimeout);
		}

		var result = Assert.Single(await upload);
		Assert.Equal(("The API did not answer in time.", ShipmentUploadOutcome.RetryLater), (result.FailureReason!, result.Outcome));
	}

	[Theory]
	[InlineData("""{"accepted":2,"duplicatesSkipped":0,"rejected":0,"rejections":[]}""")]
	[InlineData("{}")]
	[InlineData("""{"accepted":0,"duplicatesSkipped":0,"rejected":1,"rejections":[{"scanId":"scan::00000000-0000-0000-0000-000000000001","reason":"x"}]}""")]
	[InlineData("""{"accepted":0,"duplicatesSkipped":0,"rejected":1,"rejections":[{"reason":"x"}]}""")]
	public async Task AnUnreadableOrInconsistentAnswer_LeavesTheScansForLater(string body)
	{
		using var host = new ClientHost(_ => Responses.Json(HttpStatusCode.OK, body));

		var result = Assert.Single(await host.Uploader().UploadAsync([TestScans.For(A, 1)]));

		Assert.Equal(
			("The server sent a response this client could not read.", ShipmentUploadOutcome.RetryLater),
			(result.FailureReason!, result.Outcome));
	}

	[Fact]
	public async Task OpenCircuit_LeavesTheScansForLater_WithoutARequest()
	{
		using var host = new ClientHost(_ => Responses.Empty(HttpStatusCode.ServiceUnavailable));
		var uploader = host.Uploader();

		await uploader.UploadAsync([TestScans.For(A, 1)]);
		var opening = Assert.Single(await uploader.UploadAsync([TestScans.For(A, 2)]));
		var requestsWhenOpened = host.Network.Requests.Count;
		var whileOpen = await uploader.UploadAsync([TestScans.For(A, 3), TestScans.For(B, 4)]);

		Assert.Equal("Server unavailable – working offline.", opening.FailureReason);
		Assert.All(whileOpen, result => Assert.Equal(
			("Server unavailable – working offline.", ShipmentUploadOutcome.RetryLater),
			(result.FailureReason!, result.Outcome)));
		Assert.Equal(requestsWhenOpened, host.Network.Requests.Count);
		Assert.Equal(CircuitState.Open, host.State.CircuitState);
	}

	[Fact]
	public async Task CallerCancellation_IsThrown()
	{
		using var host = new ClientHost();
		host.Network.Hang = _ => true;
		using var cancellation = new CancellationTokenSource();

		var upload = host.Uploader().UploadAsync([TestScans.For(A, 1)], cancellation.Token);
		Assert.True(await host.Network.AttemptStarted.WaitAsync(TimeSpan.FromSeconds(10)));
		await cancellation.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
	}

	[Fact]
	public async Task MoreThan20Scans_AreRefused()
	{
		using var host = new ClientHost(Accept);
		var scans = Enumerable.Range(0, OutboxService.BatchSize + 1).Select(minute => TestScans.For(A, minute)).ToList();

		await Assert.ThrowsAsync<ArgumentException>(() => host.Uploader().UploadAsync(scans));
		Assert.Empty(host.Network.Requests);
	}

	// The API's answer when it accepts every scan of the request.
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
