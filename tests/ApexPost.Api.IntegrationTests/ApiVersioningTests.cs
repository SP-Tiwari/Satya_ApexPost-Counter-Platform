using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.IntegrationTests;

// C4: URL-segment versioning. Version 1 keeps the Part B contract and is
// deprecated; version 2 changes the shipment representation. Both versions use
// the same service and the same database.
public sealed class ApiVersioningTests : IDisposable
{
	private const string V2Shipments = "/api/v2/shipments";
	private const string TrackingNumber = "APX-70000001";

	private readonly ApiFactory _factory = new();
	private readonly HttpClient _client;

	public ApiVersioningTests() => _client = _factory.CreateClient();

	public void Dispose() => _factory.Dispose();

	private static string V2Shipment(string trackingNumber) => $"{V2Shipments}/{trackingNumber}";

	[Fact]
	public async Task V1_Shipment_KeepsThePartBShape()
	{
		await _client.CreateAsync(TrackingNumber);

		var shipment = await _client.GetFromJsonAsync<JsonElement>(Api.Shipment(TrackingNumber));

		Assert.Equal(
			["trackingNumber", "recipientName", "recipientPhone", "recipientAddress", "recipientCity", "weightKg", "status"],
			shipment.EnumerateObject().Select(property => property.Name));
		Assert.Equal(JsonValueKind.Number, shipment.GetProperty("status").ValueKind);
	}

	[Fact]
	public async Task V2_Shipment_HasANestedRecipient_TheLatestScan_AndAStringStatus()
	{
		await _client.CreateAsync(TrackingNumber);
		var now = DateTimeOffset.UtcNow;
		var upload = await _client.UploadAsync(
			TrackingNumber,
			Api.Scan(Api.NewScanId(), TrackingNumber, now.AddMinutes(-30), ShipmentScanType.InTransit),
			Api.Scan(Api.NewScanId(), TrackingNumber, now.AddHours(-2), ShipmentScanType.Pickup));
		Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

		var shipment = await _client.GetFromJsonAsync<JsonElement>(V2Shipment(TrackingNumber));

		Assert.Equal(
			["trackingNumber", "recipient", "weightKg", "status", "latestScan"],
			shipment.EnumerateObject().Select(property => property.Name));
		Assert.Equal(TrackingNumber, shipment.GetProperty("trackingNumber").GetString());
		var recipient = shipment.GetProperty("recipient");
		Assert.Equal("DEMO Recipient", recipient.GetProperty("name").GetString());
		Assert.Equal("+919876543210", recipient.GetProperty("phone").GetString());
		Assert.Equal("1 Demo Street", recipient.GetProperty("address").GetString());
		Assert.Equal("Pune", recipient.GetProperty("city").GetString());
		Assert.Equal(2.5m, shipment.GetProperty("weightKg").GetDecimal());
		Assert.Equal("Created", shipment.GetProperty("status").GetString());
		// The most recent scan by scan time, not the first uploaded.
		var latestScan = shipment.GetProperty("latestScan");
		Assert.Equal(
			["scanType", "location", "scannedAtUtc"],
			latestScan.EnumerateObject().Select(property => property.Name));
		Assert.Equal((int)ShipmentScanType.InTransit, latestScan.GetProperty("scanType").GetInt32());
		Assert.Equal("Pune hub", latestScan.GetProperty("location").GetString());
		Assert.Equal(
			now.AddMinutes(-30).ToUnixTimeMilliseconds(),
			latestScan.GetProperty("scannedAtUtc").GetDateTimeOffset().ToUnixTimeMilliseconds());
	}

	[Fact]
	public async Task V2_ShipmentWithoutScans_HasANullLatestScan()
	{
		await _client.CreateAsync(TrackingNumber);

		var shipment = await _client.GetFromJsonAsync<JsonElement>(V2Shipment(TrackingNumber));

		Assert.Equal(JsonValueKind.Null, shipment.GetProperty("latestScan").ValueKind);
	}

	[Fact]
	public async Task V2_Create_Returns201_WithAV2Location_AndAV2Body()
	{
		var response = await _client.PostAsJsonAsync(V2Shipments, Api.NewShipment(TrackingNumber));

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		Assert.Equal(new Uri(_client.BaseAddress!, V2Shipment(TrackingNumber)), response.Headers.Location);
		var created = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Pune", created.GetProperty("recipient").GetProperty("city").GetString());
		Assert.Equal("Created", created.GetProperty("status").GetString());
		var fetched = await _client.GetFromJsonAsync<JsonElement>(response.Headers.Location);
		Assert.Equal(TrackingNumber, fetched.GetProperty("trackingNumber").GetString());
	}

	[Fact]
	public async Task V2_List_ReturnsV2Items()
	{
		await _client.CreateAsync(TrackingNumber);

		var page = await _client.GetFromJsonAsync<JsonElement>($"{V2Shipments}?status=Created&city=Pune");

		Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
		var item = Assert.Single(page.GetProperty("items").EnumerateArray());
		Assert.Equal("DEMO Recipient", item.GetProperty("recipient").GetProperty("name").GetString());
		Assert.Equal("Created", item.GetProperty("status").GetString());
	}

	[Fact]
	public async Task V2_Replace_ReturnsTheV2Shape()
	{
		await _client.CreateAsync(TrackingNumber);
		var details = Api.NewShipment(TrackingNumber, city: "Mumbai");
		details.Remove("trackingNumber");

		var response = await _client.PutAsJsonAsync(V2Shipment(TrackingNumber), details);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var shipment = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("Mumbai", shipment.GetProperty("recipient").GetProperty("city").GetString());
	}

	[Theory]
	[InlineData("\"PickedUp\"")]
	[InlineData("\"pickedUp\"")]
	[InlineData("1")]
	public async Task V2_ChangeStatus_AcceptsTheNameOrTheV1Number_AndReturnsTheName(string status)
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.PatchAsync(
			$"{V2Shipment(TrackingNumber)}/status",
			new StringContent($$"""{ "status": {{status}} }""", System.Text.Encoding.UTF8, "application/json"));

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var shipment = await response.Content.ReadFromJsonAsync<JsonElement>();
		Assert.Equal("PickedUp", shipment.GetProperty("status").GetString());
	}

	[Theory]
	[InlineData("\"Teleported\"")]
	[InlineData("42")]
	[InlineData("null")]
	public async Task V2_ChangeStatus_UnknownStatus_Returns400(string status)
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.PatchAsync(
			$"{V2Shipment(TrackingNumber)}/status",
			new StringContent($$"""{ "status": {{status}} }""", System.Text.Encoding.UTF8, "application/json"));

		await response.AssertProblemAsync(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task V2_RefusedTransition_Returns409_AndAV1ClientSeesTheSameShipment()
	{
		await _client.CreateAsync(TrackingNumber);

		var refused = await _client.PatchAsJsonAsync($"{V2Shipment(TrackingNumber)}/status", new { status = "Delivered" });

		await refused.AssertProblemAsync(HttpStatusCode.Conflict);
		var v1 = await _client.GetFromJsonAsync<JsonElement>(Api.Shipment(TrackingNumber));
		Assert.Equal((int)ShipmentStatus.Created, v1.GetProperty("status").GetInt32());
	}

	[Fact]
	public async Task V2_ScansAndDelete_WorkOnTheSameShipment()
	{
		await _client.CreateAsync(TrackingNumber);
		var scanId = Api.NewScanId();

		var upload = await _client.PostAsJsonAsync(
			$"{V2Shipment(TrackingNumber)}/scans",
			new { scans = new[] { Api.Scan(scanId, TrackingNumber, DateTimeOffset.UtcNow.AddMinutes(-5)) } });
		var history = await _client.GetFromJsonAsync<JsonElement>($"{V2Shipment(TrackingNumber)}/scans");
		var deleted = await _client.DeleteAsync(V2Shipment(TrackingNumber));
		var missing = await _client.GetAsync(V2Shipment(TrackingNumber));

		Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
		Assert.Equal(1, (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32());
		Assert.Equal(scanId, Assert.Single(history.EnumerateArray()).GetProperty("scanId").GetString());
		Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
		await missing.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task V2_Errors_AreStillProblemDetails()
	{
		var invalid = await _client.PostAsJsonAsync(V2Shipments, Api.NewShipment("APX-1"));
		var unknown = await _client.GetAsync(V2Shipment("APX-99999999"));

		await invalid.AssertValidationErrorAsync("trackingNumber");
		await unknown.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	// ReportApiVersions: every response from a versioned action, success or
	// error, lists the supported and the deprecated versions. The automatic 400
	// is answered before action filters run; ReportApiVersionsOnEveryResponse
	// covers it.
	[Theory]
	[InlineData("/api/v1/shipments", HttpStatusCode.OK)]
	[InlineData("/api/v2/shipments", HttpStatusCode.OK)]
	[InlineData("/api/v1/shipments/APX-99999999", HttpStatusCode.NotFound)]
	[InlineData("/api/v2/shipments/APX-99999999/scans", HttpStatusCode.NotFound)]
	[InlineData("/api/v1/shipments?pageSize=51", HttpStatusCode.BadRequest)]
	[InlineData("/api/v2/shipments?pageSize=51", HttpStatusCode.BadRequest)]
	public async Task Responses_ReportTheSupportedAndDeprecatedVersions(string path, HttpStatusCode status)
	{
		var response = await _client.GetAsync(path);

		Assert.Equal(status, response.StatusCode);
		AssertVersionHeaders(response);
	}

	[Theory]
	[InlineData("/api/v1/shipments")]
	[InlineData("/api/v2/shipments")]
	public async Task InvalidBody400_AlsoReportsTheVersions(string path)
	{
		var invalid = await _client.PostAsJsonAsync(path, Api.NewShipment("APX-1"));

		Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
		AssertVersionHeaders(invalid);
	}

	private static void AssertVersionHeaders(HttpResponseMessage response)
	{
		Assert.Equal("2.0", Assert.Single(response.Headers.GetValues("api-supported-versions")));
		Assert.Equal("1.0", Assert.Single(response.Headers.GetValues("api-deprecated-versions")));
	}

	[Fact]
	public async Task V1_0_IsTheSameVersionAsV1()
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.GetAsync($"/api/v1.0/shipments/{TrackingNumber}");

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	// With URL-segment versioning, an unknown version is a URL that names no
	// resource.
	[Theory]
	[InlineData("/api/v3/shipments")]
	[InlineData("/api/v3/shipments/APX-70000001/scans")]
	public async Task UnsupportedVersion_Returns404ProblemDetails(string path)
	{
		var response = await _client.GetAsync(path);

		await response.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	// The version is part of every URL; the Part B URLs without one are no
	// longer routes.
	[Theory]
	[InlineData("/api/shipments")]
	[InlineData("/api/shipments/APX-70000001")]
	public async Task UnversionedUrl_Returns404ProblemDetails(string path)
	{
		var response = await _client.GetAsync(path);

		await response.AssertProblemAsync(HttpStatusCode.NotFound);
	}
}
