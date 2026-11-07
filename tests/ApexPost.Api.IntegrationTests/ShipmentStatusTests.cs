using System.Net;
using System.Net.Http.Json;
using System.Text;
using ApexPost.Api.Models;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.IntegrationTests;

// PATCH /api/shipments/{trackingNumber}/status. The full 36-pair matrix is in
// ShipmentStatusTransitionRuleTests; these cases go through HTTP and SQL.
public sealed class ShipmentStatusTests : IDisposable
{
	private const string TrackingNumber = "APX-20000001";

	private readonly ApiFactory _factory = new();
	private readonly HttpClient _client;

	public ShipmentStatusTests() => _client = _factory.CreateClient();

	public void Dispose() => _factory.Dispose();

	[Theory]
	[InlineData(ShipmentStatus.Delivered)]
	[InlineData(ShipmentStatus.Failed)]
	public async Task ChangeStatus_EachStepOfTheLifecycle_Returns200WithTheNewStatus(ShipmentStatus last)
	{
		await _client.CreateAsync(TrackingNumber);

		foreach (var status in new[] { ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, last })
		{
			var response = await _client.PatchAsJsonAsync(Api.Status(TrackingNumber), new { status });

			Assert.Equal(HttpStatusCode.OK, response.StatusCode);
			Assert.Equal(status, (await response.Content.ReadFromJsonAsync<ShipmentResponse>())!.Status);
		}

		Assert.Equal(last, (await _client.GetFromJsonAsync<ShipmentResponse>(Api.Shipment(TrackingNumber)))!.Status);
	}

	public static TheoryData<ShipmentStatus[], ShipmentStatus> RefusedChanges() => new()
	{
		{ [], ShipmentStatus.InTransit },
		{ [], ShipmentStatus.Delivered },
		{ [], ShipmentStatus.Created },
		{ [ShipmentStatus.PickedUp], ShipmentStatus.Created },
		{ [ShipmentStatus.PickedUp, ShipmentStatus.InTransit], ShipmentStatus.Failed },
		{ [ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered], ShipmentStatus.Failed },
		{ [ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.Failed], ShipmentStatus.OutForDelivery }
	};

	[Theory]
	[MemberData(nameof(RefusedChanges))]
	public async Task ChangeStatus_RefusedTransition_Returns409_AndKeepsTheStatus(
		ShipmentStatus[] path,
		ShipmentStatus refused)
	{
		await _client.CreateAsync(TrackingNumber);
		await _client.AdvanceAsync(TrackingNumber, path);
		var current = path.Length == 0 ? ShipmentStatus.Created : path[^1];

		var response = await _client.PatchAsJsonAsync(Api.Status(TrackingNumber), new { status = refused });

		var problem = await response.AssertProblemAsync(HttpStatusCode.Conflict);
		Assert.Equal(
			$"A shipment cannot move from {current} to {refused}.",
			problem.GetProperty("detail").GetString());
		Assert.Equal(current, (await _client.GetFromJsonAsync<ShipmentResponse>(Api.Shipment(TrackingNumber)))!.Status);
	}

	[Theory]
	[InlineData("{\"status\":42}")]
	[InlineData("{}")]
	[InlineData("{\"status\":\"Delivered\"}")]
	public async Task ChangeStatus_MissingOrInvalidStatus_Returns400(string body)
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.PatchAsync(
			Api.Status(TrackingNumber), new StringContent(body, Encoding.UTF8, "application/json"));

		await response.AssertProblemAsync(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task ChangeStatus_UnknownTrackingNumber_Returns404()
	{
		var response = await _client.PatchAsJsonAsync(
			Api.Status(TrackingNumber), new { status = ShipmentStatus.PickedUp });

		await response.AssertProblemAsync(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task ChangeStatus_WrongContentType_Returns415()
	{
		await _client.CreateAsync(TrackingNumber);

		var response = await _client.PatchAsync(
			Api.Status(TrackingNumber), new StringContent("status=1", Encoding.UTF8, "application/x-www-form-urlencoded"));

		await response.AssertProblemAsync(HttpStatusCode.UnsupportedMediaType);
	}
}
