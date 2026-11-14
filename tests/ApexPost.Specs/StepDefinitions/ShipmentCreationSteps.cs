using System.Net;
using System.Net.Http.Json;
using ApexPost.Api.IntegrationTests;
using ApexPost.Api.Models;
using ApexPost.Domain.Enums;
using ApexPost.Specs.Support;
using Reqnroll;
using ShipmentApi = ApexPost.Api.IntegrationTests.Api;

namespace ApexPost.Specs.StepDefinitions;

// Shipment creation through version 1 of the Shipment API, end to end: HTTP,
// authorization, validation, the application service and SQL Server.
[Binding]
public sealed class ShipmentCreationSteps(ShipmentApiContext api)
{
	[Given("I am signed in to the Shipment API with permission to create shipments")]
	public void GivenSignedInAsAnAdministrator() =>
		api.Client.WithToken(TestTokens.Admin());

	[Given("shipment {string} already exists, weighing {decimal} kg")]
	public async Task GivenTheShipmentAlreadyExists(string trackingNumber, decimal weightKg)
	{
		using var response = await CreateAsync(trackingNumber, weightKg);
		Assert.Equal(HttpStatusCode.Created, response.StatusCode);
	}

	[When("I create shipment {string} weighing {decimal} kg")]
	public async Task WhenICreateTheShipment(string trackingNumber, decimal weightKg)
	{
		api.LastTrackingNumber = trackingNumber;
		api.ResponseOrNull = await CreateAsync(trackingNumber, weightKg);
	}

	[Then("the shipment is created")]
	public void ThenTheShipmentIsCreated()
	{
		Assert.Equal(HttpStatusCode.Created, api.Response.StatusCode);
		Assert.Equal(
			new Uri(api.Client.BaseAddress!, ShipmentApi.Shipment(api.LastTrackingNumber!)),
			api.Response.Headers.Location);
	}

	[Then("the shipment is refused because it already exists")]
	public Task ThenTheShipmentIsRefusedAsADuplicate() =>
		api.Response.AssertProblemAsync(HttpStatusCode.Conflict);

	[Then("the shipment is refused for its weight")]
	public Task ThenTheShipmentIsRefusedForItsWeight() =>
		api.Response.AssertValidationErrorAsync("weightKg");

	[Then("shipment {string} can be found with status {string} and weight {decimal} kg")]
	public async Task ThenTheShipmentCanBeFound(string trackingNumber, string status, decimal weightKg)
	{
		var shipment = await api.Client.GetFromJsonAsync<ShipmentResponse>(ShipmentApi.Shipment(trackingNumber));

		Assert.NotNull(shipment);
		Assert.Equal(Enum.Parse<ShipmentStatus>(status), shipment.Status);
		Assert.Equal(weightKg, shipment.WeightKg);
	}

	// The fictional details every valid request uses, with the given weight.
	private Task<HttpResponseMessage> CreateAsync(string trackingNumber, decimal weightKg)
	{
		var request = ShipmentApi.NewShipment(trackingNumber);
		request["weightKg"] = weightKg;
		return api.Client.PostAsJsonAsync(ShipmentApi.Shipments, request);
	}
}
