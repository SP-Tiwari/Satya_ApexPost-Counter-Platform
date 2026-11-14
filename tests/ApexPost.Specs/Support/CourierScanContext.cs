using ApexPost.Courier.Domain;
using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Tests;

namespace ApexPost.Specs.Support;

// State shared by the steps of one scan-sync scenario, created by Reqnroll for
// each scenario and injected into the binding classes. The hooks open the
// device's Couchbase Lite store in a new temporary folder and delete it.
public sealed class CourierScanContext
{
	internal CourierTestContext Device =>
		DeviceOrNull ?? throw new InvalidOperationException("The courier device store was not opened for this scenario.");

	internal CourierTestContext? DeviceOrNull { get; set; }

	internal CentralSystemStub CentralSystem { get; } = new();

	// The scans of this scenario by the name the feature gives them.
	internal Dictionary<string, ShipmentScan> ScansByName { get; } = new(StringComparer.Ordinal);

	internal OutboxRunResult? LastRun { get; set; }

	internal string IdOf(string scanName) =>
		ScansByName.TryGetValue(scanName, out var scan)
			? scan.Id
			: throw new InvalidOperationException($"No scan named '{scanName}' was recorded in this scenario.");
}
