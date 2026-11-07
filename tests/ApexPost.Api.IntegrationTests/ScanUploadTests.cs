using System.Net;
using System.Net.Http.Json;
using System.Text;
using ApexPost.Api.Models;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Api.IntegrationTests;

// POST and GET /api/shipments/{trackingNumber}/scans, with the server clock
// fixed so the future-time boundary is exact.
public sealed class ScanUploadTests : IDisposable
{
	private const string TrackingNumber = "APX-50000001";
	private const string OtherTrackingNumber = "APX-50000002";

	private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

	private readonly ApiFactory _factory = ApiFactory.WithClock(Now);
	private readonly HttpClient _client;

	public ScanUploadTests() => _client = _factory.CreateClient();

	public void Dispose() => _factory.Dispose();

	[Fact]
	public async Task Upload_NewScans_AreAccepted_AndTheHistoryIsOldestFirst()
	{
		await _client.CreateAsync(TrackingNumber);
		var later = Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-1), ShipmentScanType.InTransit);
		var earlier = Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-3));
		earlier["notes"] = "Collected from DEMO sender";

		var response = await _client.UploadAsync(TrackingNumber, later, earlier);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Equal(new UploadCounts(2, 0, 0), await CountsAsync(response));
		var history = await _client.GetFromJsonAsync<ScanResponse[]>(Api.Scans(TrackingNumber));
		Assert.Equal(
			[
				new ScanResponse((string)earlier["scanId"]!, ShipmentScanType.Pickup, "COURIER-DEMO-07", "Pune hub", Now.AddHours(-3), "Collected from DEMO sender"),
				new ScanResponse((string)later["scanId"]!, ShipmentScanType.InTransit, "COURIER-DEMO-07", "Pune hub", Now.AddHours(-1), null)
			],
			history!);
	}

	[Fact]
	public async Task Upload_TheSameScansAgain_SkipsThemAsDuplicates_AndStoresOneCopy()
	{
		await _client.CreateAsync(TrackingNumber);
		var scans = new[]
		{
			Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-2)),
			Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-1), ShipmentScanType.InTransit)
		};
		await _client.UploadAsync(TrackingNumber, scans);

		var again = await _client.UploadAsync(TrackingNumber, scans);

		Assert.Equal(HttpStatusCode.OK, again.StatusCode);
		Assert.Equal(new UploadCounts(0, 2, 0), await CountsAsync(again));
		Assert.Equal(2, await StoredScanCountAsync());
	}

	[Fact]
	public async Task Upload_AnIdRepeatedInTheRequestOrInAnotherCase_IsADuplicate()
	{
		await _client.CreateAsync(TrackingNumber);
		var scanId = Api.NewScanId();

		var response = await _client.UploadAsync(
			TrackingNumber,
			Api.Scan(scanId, TrackingNumber, Now.AddHours(-2)),
			Api.Scan(scanId, TrackingNumber, Now.AddHours(-2)),
			Api.Scan(scanId.ToUpperInvariant().Replace("SCAN::", "scan::"), TrackingNumber, Now.AddHours(-2)));

		Assert.Equal(new UploadCounts(1, 2, 0), await CountsAsync(response));
		Assert.Equal(1, await StoredScanCountAsync());
	}

	[Theory]
	[InlineData("3f2b8c1e-9d4a-4b6f-a1c2-5e7d9f0b1a23")]
	[InlineData("scan::not-a-guid")]
	[InlineData("scan::00000000-0000-0000-0000-000000000000")]
	[InlineData("scan::3f2b8c1e9d4a4b6fa1c25e7d9f0b1a23")]
	[InlineData("SCAN::3f2b8c1e-9d4a-4b6f-a1c2-5e7d9f0b1a23")]
	public async Task Upload_MalformedScanId_IsRejectedWithAReason_AndTheRestAccepted(string scanId)
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.UploadAsync(
			TrackingNumber,
			Api.Scan(scanId, TrackingNumber, Now.AddHours(-2)),
			Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-1)));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.Content.ReadFromJsonAsync<ScanUploadResponse>();
		Assert.Equal(1, result!.Accepted);
		Assert.Equal(1, result.Rejected);
		Assert.Equal(
			new RejectedScanResponse(scanId, "The scan ID must have the format scan::{Guid}."),
			Assert.Single(result.Rejections));
		Assert.Equal(1, await StoredScanCountAsync());
	}

	[Fact]
	public async Task Upload_ScanForAnotherTrackingNumber_IsRejected()
	{
		await _client.CreateAsync(TrackingNumber);
		await _client.CreateAsync(OtherTrackingNumber);
		var scanId = Api.NewScanId();

		var response = await _client.UploadAsync(
			TrackingNumber, Api.Scan(scanId, OtherTrackingNumber, Now.AddHours(-1)));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = await response.Content.ReadFromJsonAsync<ScanUploadResponse>();
		Assert.Equal((0, 0, 1), (result!.Accepted, result.DuplicatesSkipped, result.Rejected));
		Assert.Equal(
			new RejectedScanResponse(scanId, $"The scan is for {OtherTrackingNumber}, not {TrackingNumber}."),
			Assert.Single(result!.Rejections));
		Assert.Equal(0, await StoredScanCountAsync());
	}

	[Fact]
	public async Task Upload_ScanIdStoredUnderAnotherShipment_IsRejected()
	{
		await _client.CreateAsync(TrackingNumber);
		await _client.CreateAsync(OtherTrackingNumber);
		var scanId = Api.NewScanId();
		await _client.UploadAsync(OtherTrackingNumber, Api.Scan(scanId, OtherTrackingNumber, Now.AddHours(-1)));

		var response = await _client.UploadAsync(
			TrackingNumber, Api.Scan(scanId, TrackingNumber, Now.AddHours(-1)));

		var result = await response.Content.ReadFromJsonAsync<ScanUploadResponse>();
		Assert.Equal(0, result!.Accepted);
		Assert.Equal(
			new RejectedScanResponse(scanId, "The scan ID is already recorded for another shipment."),
			Assert.Single(result.Rejections));
		Assert.Empty((await _client.GetFromJsonAsync<ScanResponse[]>(Api.Scans(TrackingNumber)))!);
	}

	[Fact]
	public async Task Upload_NeverChangesTheShipmentStatus()
	{
		await _client.CreateAsync(TrackingNumber);

		await _client.UploadAsync(
			TrackingNumber,
			Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-2)),
			Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-1), ShipmentScanType.Delivered));

		var shipment = await _client.GetFromJsonAsync<ShipmentResponse>(Api.Shipment(TrackingNumber));
		Assert.Equal(ShipmentStatus.Created, shipment!.Status);
	}

	[Theory]
	[InlineData(ShipmentStatus.Delivered)]
	[InlineData(ShipmentStatus.Failed)]
	public async Task Upload_HistoricalScanAfterAFinalStatus_IsAccepted(ShipmentStatus finalStatus)
	{
		await _client.CreateAsync(TrackingNumber);
		await _client.AdvanceAsync(
			TrackingNumber, ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, finalStatus);

		var response = await _client.UploadAsync(
			TrackingNumber, Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-1), ShipmentScanType.Delivered));

		Assert.Equal(new UploadCounts(1, 0, 0), await CountsAsync(response));
		var shipment = await _client.GetFromJsonAsync<ShipmentResponse>(Api.Shipment(TrackingNumber));
		Assert.Equal(finalStatus, shipment!.Status);
	}

	[Fact]
	public async Task Upload_ScanAtExactlyTheServerTime_IsAccepted()
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.UploadAsync(TrackingNumber, Api.Scan(Api.NewScanId(), TrackingNumber, Now));

		Assert.Equal(new UploadCounts(1, 0, 0), await CountsAsync(response));
	}

	[Theory]
	[InlineData(1)]
	[InlineData(10_000)]
	[InlineData(3_000_000_000)]
	public async Task Upload_ScanAfterTheServerTime_Returns400_AndStoresNothing(long ticksAhead)
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.UploadAsync(
			TrackingNumber,
			Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-1)),
			Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddTicks(ticksAhead)));

		await response.AssertValidationErrorAsync("Scans[1].ScannedAtUtc");
		Assert.Equal(0, await StoredScanCountAsync());
	}

	[Fact]
	public async Task Upload_FutureTimeInAnotherOffset_IsComparedInUtc()
	{
		await _client.CreateAsync(TrackingNumber);
		// 17:31 at +05:30 is 12:01 UTC, one minute after the server time.
		var ahead = new DateTimeOffset(2026, 11, 7, 17, 31, 0, TimeSpan.FromHours(5.5));
		var behind = new DateTimeOffset(2026, 11, 7, 17, 29, 0, TimeSpan.FromHours(5.5));

		var refused = await _client.UploadAsync(TrackingNumber, Api.Scan(Api.NewScanId(), TrackingNumber, ahead));
		var accepted = await _client.UploadAsync(TrackingNumber, Api.Scan(Api.NewScanId(), TrackingNumber, behind));

		await refused.AssertValidationErrorAsync("Scans[0].ScannedAtUtc");
		Assert.Equal(new UploadCounts(1, 0, 0), await CountsAsync(accepted));
		var stored = Assert.Single((await _client.GetFromJsonAsync<ScanResponse[]>(Api.Scans(TrackingNumber)))!);
		Assert.Equal(new DateTimeOffset(2026, 11, 7, 11, 59, 0, TimeSpan.Zero), stored.ScannedAtUtc);
		Assert.Equal(TimeSpan.Zero, stored.ScannedAtUtc.Offset);
	}

	public static TheoryData<string, object?, string> InvalidScanFields() => new()
	{
		{ "courierId", null, "Scans[0].CourierId" },
		{ "courierId", new string('c', 51), "Scans[0].CourierId" },
		{ "location", "  ", "Scans[0].Location" },
		{ "location", new string('l', 201), "Scans[0].Location" },
		{ "notes", new string('n', 501), "Scans[0].Notes" },
		{ "scanType", 99, "Scans[0].ScanType" },
		{ "scanType", null, "Scans[0].ScanType" },
		{ "scannedAtUtc", null, "Scans[0].ScannedAtUtc" },
		{ "scanId", null, "Scans[0].ScanId" },
		{ "trackingNumber", null, "Scans[0].TrackingNumber" }
	};

	[Theory]
	[MemberData(nameof(InvalidScanFields))]
	public async Task Upload_InvalidScanField_Returns400(string field, object? value, string errorKey)
	{
		await _client.CreateAsync(TrackingNumber);
		var scan = Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-1));
		scan[field] = value;

		var response = await _client.UploadAsync(TrackingNumber, scan);

		await response.AssertValidationErrorAsync(errorKey);
		Assert.Equal(0, await StoredScanCountAsync());
	}

	[Theory]
	[InlineData(0)]
	[InlineData(101)]
	public async Task Upload_EmptyOrOversizedBatch_Returns400(int count)
	{
		await _client.CreateAsync(TrackingNumber);
		var scans = Enumerable.Range(0, count)
			.Select(_ => Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-1)))
			.ToArray();

		var response = await _client.UploadAsync(TrackingNumber, scans);

		await response.AssertValidationErrorAsync("Scans");
	}

	[Fact]
	public async Task Upload_FullBatchOf100_IsAccepted()
	{
		await _client.CreateAsync(TrackingNumber);
		var scans = Enumerable.Range(0, ScanUploadRequest.MaximumScans)
			.Select(minute => Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddMinutes(-minute)))
			.ToArray();

		var response = await _client.UploadAsync(TrackingNumber, scans);

		Assert.Equal(new UploadCounts(100, 0, 0), await CountsAsync(response));
	}

	[Fact]
	public async Task Upload_UnknownShipment_Returns404()
	{
		var response = await _client.UploadAsync(
			TrackingNumber, Api.Scan(Api.NewScanId(), TrackingNumber, Now.AddHours(-1)));

		await response.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Upload_WrongContentType_Returns415()
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.PostAsync(
			Api.Scans(TrackingNumber), new StringContent("scans", Encoding.UTF8, "text/plain"));

		await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
	}

	[Fact]
	public async Task History_UnknownShipment_Returns404()
	{
		var response = await _client.GetAsync(Api.Scans(TrackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task History_NoScansYet_IsAnEmptyList()
	{
		await _client.CreateAsync(TrackingNumber);

		var history = await _client.GetFromJsonAsync<ScanResponse[]>(Api.Scans(TrackingNumber));

		Assert.Empty(history!);
	}

	private sealed record UploadCounts(int Accepted, int DuplicatesSkipped, int Rejected);

	private static async Task<UploadCounts> CountsAsync(HttpResponseMessage response)
	{
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var result = (await response.Content.ReadFromJsonAsync<ScanUploadResponse>())!;
		Assert.Equal(result.Rejected, result.Rejections.Count);
		return new UploadCounts(result.Accepted, result.DuplicatesSkipped, result.Rejected);
	}

	private static async Task<int> StoredScanCountAsync()
	{
		await using var context = ApiTestDatabase.Database.CreateContext();
		return await context.Set<TrackedShipmentScan>().CountAsync();
	}
}
