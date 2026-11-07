using System.Net;
using System.Net.Http.Json;
using System.Text;
using ApexPost.Api.Models;
using ApexPost.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ApexPost.Api.IntegrationTests;

public sealed class ShipmentEndpointsTests : IDisposable
{
	private const string TrackingNumber = "APX-10000001";

	private readonly ApiFactory _factory = new();
	private readonly HttpClient _client;

	public ShipmentEndpointsTests() => _client = _factory.CreateClient();

	public void Dispose() => _factory.Dispose();

	[Fact]
	public async Task Create_Returns201_WithALocationThatReturnsTheShipment()
	{
		var response = await _client.PostAsJsonAsync(Api.Shipments, Api.NewShipment(TrackingNumber));

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		Assert.Equal(new Uri(_client.BaseAddress!, Api.Shipment(TrackingNumber)), response.Headers.Location);
		var created = await response.Content.ReadFromJsonAsync<ShipmentResponse>();
		Assert.Equal(
			new ShipmentResponse(TrackingNumber, "DEMO Recipient", "+919876543210", "1 Demo Street", "Pune", 2.5m, ShipmentStatus.Created),
			created);

		var fetched = await _client.GetFromJsonAsync<ShipmentResponse>(response.Headers.Location);
		Assert.Equal(created, fetched);
	}

	[Fact]
	public async Task Create_DuplicateTrackingNumber_Returns409_AndKeepsTheFirst()
	{
		await _client.CreateAsync(TrackingNumber, city: "Pune");

		var response = await _client.PostAsJsonAsync(
			Api.Shipments, Api.NewShipment(TrackingNumber, city: "Mumbai"));

		var problem = await response.AssertProblemAsync(HttpStatusCode.Conflict);
		Assert.Equal($"Shipment {TrackingNumber} already exists.", problem.GetProperty("detail").GetString());
		var stored = await _client.GetFromJsonAsync<ShipmentResponse>(Api.Shipment(TrackingNumber));
		Assert.Equal("Pune", stored!.RecipientCity);
	}

	public static TheoryData<string, object?, string> InvalidFields() => new()
	{
		{ "trackingNumber", "APX-1234567", "trackingNumber" },
		{ "trackingNumber", "APX-123456789", "trackingNumber" },
		{ "trackingNumber", "apx-12345678", "trackingNumber" },
		{ "trackingNumber", "APX-١٢٣٤٥٦٧٨", "trackingNumber" },
		{ "trackingNumber", null, "trackingNumber" },
		{ "weightKg", 0.09m, "weightKg" },
		{ "weightKg", 70.001m, "weightKg" },
		{ "weightKg", null, "weightKg" },
		{ "recipientPhone", "9876543210", "recipientPhone" },
		{ "recipientPhone", "+91 98765 43210", "recipientPhone" },
		{ "recipientPhone", "+0123456789", "recipientPhone" },
		{ "recipientName", null, "recipientName" },
		{ "recipientName", "   ", "recipientName" },
		{ "recipientName", new string('n', 101), "recipientName" },
		{ "recipientAddress", null, "recipientAddress" },
		{ "recipientAddress", new string('a', 201), "recipientAddress" },
		{ "recipientCity", null, "recipientCity" },
		{ "recipientCity", new string('c', 101), "recipientCity" }
	};

	[Theory]
	[MemberData(nameof(InvalidFields))]
	public async Task Create_InvalidField_Returns400ValidationProblem_AndStoresNothing(
		string field,
		object? value,
		string errorKey)
	{
		var request = Api.NewShipment(TrackingNumber);
		request[field] = value;

		var response = await _client.PostAsJsonAsync(Api.Shipments, request);

		await response.AssertValidationErrorAsync(errorKey);
		await using var context = ApiTestDatabase.Database.CreateContext();
		Assert.False(await context.Set<Domain.Entities.TrackedShipment>().AnyAsync());
	}

	[Theory]
	[InlineData(0.1)]
	[InlineData(70)]
	public async Task Create_WeightAtEitherLimit_IsAccepted(decimal weightKg)
	{
		var request = Api.NewShipment(TrackingNumber);
		request["weightKg"] = weightKg;

		var response = await _client.PostAsJsonAsync(Api.Shipments, request);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
		Assert.Equal(weightKg, (await response.Content.ReadFromJsonAsync<ShipmentResponse>())!.WeightKg);
	}

	[Fact]
	public async Task Create_FieldsAtTheirMaximumLength_AreStored()
	{
		var request = Api.NewShipment(TrackingNumber);
		request["recipientName"] = new string('n', 100);
		request["recipientAddress"] = new string('a', 200);
		request["recipientCity"] = new string('c', 100);
		request["recipientPhone"] = "+" + new string('9', 15);

		var response = await _client.PostAsJsonAsync(Api.Shipments, request);

		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	[Fact]
	public async Task Create_WrongContentType_Returns415ProblemDetails()
	{
		var response = await _client.PostAsync(
			Api.Shipments,
			new StringContent($"trackingNumber={TrackingNumber}", Encoding.UTF8, "text/plain"));

		await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
	}

	[Fact]
	public async Task Create_MalformedJson_Returns400ValidationProblem()
	{
		var response = await _client.PostAsync(
			Api.Shipments,
			new StringContent("{\"trackingNumber\":", Encoding.UTF8, "application/json"));

		await response.AssertProblemAsync(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task Get_UnknownTrackingNumber_Returns404ProblemDetails()
	{
		var response = await _client.GetAsync(Api.Shipment(TrackingNumber));

		var problem = await response.AssertProblemAsync(HttpStatusCode.NotFound);
		Assert.Equal($"No shipment has tracking number {TrackingNumber}.", problem.GetProperty("detail").GetString());
	}

	[Theory]
	[InlineData("NOT-A-NUMBER")]
	[InlineData("APX-1234567")]
	public async Task Get_ValueOutsideTheRouteConstraint_Returns404ProblemDetails(string trackingNumber)
	{
		var response = await _client.GetAsync(Api.Shipment(trackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Replace_Returns200_ChangesTheDetailsOnly()
	{
		await _client.CreateAsync(TrackingNumber);
		await _client.AdvanceAsync(TrackingNumber, ShipmentStatus.PickedUp);
		var replacement = Api.NewShipment(TrackingNumber, city: "Mumbai");
		replacement.Remove("trackingNumber");
		replacement["recipientName"] = "DEMO Other Recipient";
		replacement["weightKg"] = 4m;

		var response = await _client.PutAsJsonAsync(Api.Shipment(TrackingNumber), replacement);

		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		var expected = new ShipmentResponse(
			TrackingNumber, "DEMO Other Recipient", "+919876543210", "1 Demo Street", "Mumbai", 4m, ShipmentStatus.PickedUp);
		Assert.Equal(expected, await response.Content.ReadFromJsonAsync<ShipmentResponse>());
		Assert.Equal(expected, await _client.GetFromJsonAsync<ShipmentResponse>(Api.Shipment(TrackingNumber)));
	}

	[Fact]
	public async Task Replace_UnknownTrackingNumber_Returns404()
	{
		var replacement = Api.NewShipment(TrackingNumber);
		replacement.Remove("trackingNumber");

		var response = await _client.PutAsJsonAsync(Api.Shipment(TrackingNumber), replacement);

		await response.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task Replace_InvalidWeight_Returns400_AndKeepsTheShipment()
	{
		await _client.CreateAsync(TrackingNumber);
		var replacement = Api.NewShipment(TrackingNumber);
		replacement.Remove("trackingNumber");
		replacement["weightKg"] = 80m;

		var response = await _client.PutAsJsonAsync(Api.Shipment(TrackingNumber), replacement);

		await response.AssertValidationErrorAsync("weightKg");
		Assert.Equal(2.5m, (await _client.GetFromJsonAsync<ShipmentResponse>(Api.Shipment(TrackingNumber)))!.WeightKg);
	}

	[Fact]
	public async Task Replace_WrongContentType_Returns415()
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.PutAsync(
			Api.Shipment(TrackingNumber), new StringContent("x", Encoding.UTF8, "text/xml"));

		await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
	}

	[Fact]
	public async Task Delete_CreatedShipment_Returns204_AndRemovesItWithItsScans()
	{
		await _client.CreateAsync(TrackingNumber);
		var upload = await _client.UploadAsync(TrackingNumber, Api.Scan(Api.NewScanId(), TrackingNumber, DateTimeOffset.UtcNow.AddMinutes(-5)));
		Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

		var response = await _client.DeleteAsync(Api.Shipment(TrackingNumber));

		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
		Assert.Equal(0, response.Content.Headers.ContentLength ?? 0);
		await (await _client.GetAsync(Api.Shipment(TrackingNumber))).AssertProblemAsync(HttpStatusCode.NotFound);
		await using var context = ApiTestDatabase.Database.CreateContext();
		Assert.False(await context.Set<Domain.Entities.TrackedShipmentScan>().AnyAsync());
	}

	[Theory]
	[InlineData(ShipmentStatus.PickedUp)]
	[InlineData(ShipmentStatus.Delivered)]
	public async Task Delete_AfterCreated_Returns409_AndKeepsTheShipment(ShipmentStatus status)
	{
		await _client.CreateAsync(TrackingNumber);
		await _client.AdvanceAsync(TrackingNumber, [.. Api.DeliveredPath.TakeWhile(step => step != status), status]);

		var response = await _client.DeleteAsync(Api.Shipment(TrackingNumber));

		var problem = await response.AssertProblemAsync(HttpStatusCode.Conflict);
		Assert.Equal(
			$"Shipment {TrackingNumber} is {status}; only a Created shipment can be deleted.",
			problem.GetProperty("detail").GetString());
		Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(Api.Shipment(TrackingNumber))).StatusCode);
	}

	[Fact]
	public async Task Delete_UnknownTrackingNumber_Returns404()
	{
		var response = await _client.DeleteAsync(Api.Shipment(TrackingNumber));

		await response.AssertProblemAsync(HttpStatusCode.NotFound);
	}
}
