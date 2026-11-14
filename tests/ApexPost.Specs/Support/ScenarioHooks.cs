using ApexPost.Api.IntegrationTests;
using ApexPost.Courier.Offline.Tests;
using Reqnroll;

namespace ApexPost.Specs.Support;

// Creates each scenario's test resources before its first step and removes
// them afterwards. AfterScenario hooks run even when a step fails, so no API
// host or courier database folder outlives its scenario. The hooks are scoped
// by the feature tags, so a scenario creates only what it uses.
[Binding]
public sealed class ScenarioHooks(ShipmentApiContext api, CourierScanContext courier)
{
	// A new API host on the disposable ApexPost_Test_* SQL Server database.
	// ApiFactory empties the database when the host starts, so every scenario
	// begins with no shipments.
	[BeforeScenario("shipment-api")]
	public void StartShipmentApi()
	{
		api.Factory = new ApiFactory();
		api.ClientOrNull = api.Factory.CreateClient();
	}

	[AfterScenario("shipment-api")]
	public async Task StopShipmentApi()
	{
		api.ResponseOrNull?.Dispose();
		api.ClientOrNull?.Dispose();
		if (api.Factory is not null)
			await api.Factory.DisposeAsync();
	}

	// A Couchbase Lite database in a new folder of its own.
	[BeforeScenario("courier-offline")]
	public void OpenCourierStore() => courier.DeviceOrNull = new CourierTestContext();

	// Closes the database and deletes its folder.
	[AfterScenario("courier-offline")]
	public void DeleteCourierStore() => courier.DeviceOrNull?.Dispose();
}
