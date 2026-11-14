using ApexPost.Api.IntegrationTests;

namespace ApexPost.Specs.Support;

// State shared by the steps of one Shipment API scenario. Reqnroll creates one
// instance per scenario and injects it into every binding class that asks for
// it, so steps never share state through static fields. Hooks create and
// dispose the API host.
public sealed class ShipmentApiContext
{
	internal ApiFactory? Factory { get; set; }

	internal HttpClient Client =>
		ClientOrNull ?? throw new InvalidOperationException("The Shipment API was not started for this scenario.");

	internal HttpClient? ClientOrNull { get; set; }

	internal HttpResponseMessage Response =>
		ResponseOrNull ?? throw new InvalidOperationException("No request was sent in this scenario.");

	internal HttpResponseMessage? ResponseOrNull { get; set; }

	internal string? LastTrackingNumber { get; set; }
}
