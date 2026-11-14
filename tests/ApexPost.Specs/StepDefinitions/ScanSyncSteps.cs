using ApexPost.Courier.Domain;
using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Tests;
using ApexPost.Specs.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Reqnroll;

namespace ApexPost.Specs.StepDefinitions;

// The courier device's outbox (OutboxService) over its real Couchbase Lite
// store: what is sent, and which status each scan gets from the answer.
[Binding]
public sealed class ScanSyncSteps(CourierScanContext courier)
{
	[Given("the courier device has an empty local scan store")]
	public void GivenAnEmptyLocalScanStore() =>
		Assert.Empty(courier.Device.Scans.ListAll());

	// Each row is saved as a Pending scan, one minute after the previous one,
	// so the outbox sends them in table order.
	[Given("the courier scanned these parcels while offline:")]
	public void GivenTheCourierScannedTheseParcels(DataTable scans)
	{
		var minute = 0;
		foreach (var row in scans.Rows)
		{
			var scan = Scans.New(
				trackingNumber: row["tracking number"],
				scanType: Enum.Parse<ScanType>(row["scan type"]),
				minutesAfterBase: minute++);
			courier.Device.Scans.Create(scan);
			courier.ScansByName.Add(row["scan"], scan);
		}
	}

	[Given("the central system already holds scan {string}")]
	public void GivenTheCentralSystemAlreadyHoldsTheScan(string scanName) =>
		courier.CentralSystem.AlreadyHolds(courier.IdOf(scanName));

	[Given("the central system refuses scan {string} because {string}")]
	public void GivenTheCentralSystemRefusesTheScan(string scanName, string reason) =>
		courier.CentralSystem.Refuses(courier.IdOf(scanName), reason);

	[When("the device syncs with the central system")]
	public async Task WhenTheDeviceSyncs() =>
		courier.LastRun = await SyncAsync();

	[Then("the central system receives scans {string} in that order")]
	public void ThenTheCentralSystemReceivesTheScansInOrder(string scanNames)
	{
		var expected = scanNames
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
			.Select(courier.IdOf);

		Assert.Equal(expected, courier.CentralSystem.Requests.SelectMany(request => request));
	}

	[Then("scan {string} is marked {string}")]
	public void ThenTheScanIsMarked(string scanName, string status) =>
		Assert.Equal(
			Enum.Parse<SyncStatus>(status),
			courier.Device.Scans.GetById(courier.IdOf(scanName))!.SyncStatus);

	[Then("no scan is waiting to be sent")]
	public void ThenNoScanIsWaitingToBeSent()
	{
		Assert.Empty(courier.Device.Scans.ListPending());
		Assert.Equal(0, courier.LastRun!.Pending);
	}

	[Then("the sync reports scan {string} failed because {string}")]
	public void ThenTheSyncReportsTheFailure(string scanName, string reason)
	{
		var failure = Assert.Single(courier.LastRun!.Failures);
		Assert.Equal((courier.IdOf(scanName), reason), (failure.ScanId, failure.Reason));
	}

	// Synced and Failed scans are final, so a second run has nothing to send.
	[Then("the next sync sends nothing")]
	public async Task ThenTheNextSyncSendsNothing()
	{
		var requestsBefore = courier.CentralSystem.Requests.Count;

		var nextRun = await SyncAsync();

		Assert.Equal(0, nextRun.Attempted);
		Assert.Equal(requestsBefore, courier.CentralSystem.Requests.Count);
	}

	private Task<OutboxRunResult> SyncAsync() =>
		new OutboxService(courier.Device.Scans, courier.CentralSystem, NullLogger<OutboxService>.Instance)
			.SyncPendingAsync();
}
