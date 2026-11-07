using ApexPost.Courier.Harness;
using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Offline.Uploading;

namespace ApexPost.Courier.Offline.Tests;

// The console harness, driven by a script instead of a keyboard.
public sealed class CourierHarnessTests
{
	[Fact]
	public async Task Menu_RunsEveryOptionAgainstTheLocalDatabase()
	{
		using var context = new CourierTestContext();
		var transcript = await RunAsync(
			context,
			"1", "APX-TEST-0100", "1", "COURIER-TEST-01", "Test Depot", "Front desk",
			"1", "APX-TEST-0100", "InTransit", "COURIER-TEST-01", "Test Hub", "",
			"1", "APX-TEST-0200", "3", "COURIER-TEST-01", "Test Street", "",
			"2", "APX-TEST-0100",
			"3",
			"4", "APX-TEST-0200",
			"3",
			"6");

		Assert.Contains("Scans per type for courier COURIER-TEST-01: InTransit 1, Pickup 1", transcript);
		Assert.Contains("Scans for APX-TEST-0100, oldest first: 2", transcript);
		Assert.Contains("Pending scans, oldest first: 3", transcript);
		Assert.Contains("Sync finished: 3 attempted, 2 synced, 1 failed.", transcript);
		Assert.Contains("Pending scans, oldest first: 0", transcript);
		Assert.Contains("Goodbye.", transcript);

		var stored = context.Scans.ListAll();
		Assert.Equal(3, stored.Count);
		Assert.Equal(SyncStatus.Failed, stored.Single(scan => scan.TrackingNumber == "APX-TEST-0200").SyncStatus);
	}

	[Fact]
	public async Task DeleteScan_RemovesTheScanAndReportsAnUnknownId()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New();
		context.Scans.Create(scan);

		var transcript = await RunAsync(context, "5", scan.Id, "5", scan.Id, "6");

		Assert.Contains($"Deleted {scan.Id}.", transcript);
		Assert.Contains($"No scan {scan.Id}.", transcript);
		Assert.Empty(context.Scans.ListAll());
	}

	[Fact]
	public async Task InvalidInput_IsReportedAndTheMenuContinues()
	{
		using var context = new CourierTestContext();

		var transcript = await RunAsync(
			context,
			"9",
			"5", "not-an-id",
			"1", "APX-TEST-0300", "Teleported",
			"1", "",
			"6");

		Assert.Contains("Please choose 1 to 6.", transcript);
		Assert.Contains("Error: A scan ID must have the format scan::{Guid}.", transcript);
		Assert.Contains("Error: Unknown scan type.", transcript);
		Assert.Contains("Error: A value is required.", transcript);
		Assert.Contains("Goodbye.", transcript);
		Assert.Empty(context.Scans.ListAll());
	}

	[Fact]
	public async Task EndOfInput_EndsTheHarness()
	{
		using var context = new CourierTestContext();

		var transcript = await RunAsync(context, "3");

		Assert.Contains("Pending scans, oldest first: 0", transcript);
	}

	[Fact]
	public async Task MenuHeader_ShowsTheLivePendingCountFromTheListener()
	{
		using var context = new CourierTestContext();
		var output = new StringWriter();
		var harness = Harness(context, new StringReader(string.Join(Environment.NewLine, "6")), output);
		var recorder = new NotificationRecorder();
		context.Database.StartChangeListener(change =>
		{
			harness.OnScansChanged(change);
			recorder.Record(change);
		});
		var scan = Scans.New();
		context.Scans.Create(scan);
		await recorder.WaitForDocumentAsync(scan.Id);

		await harness.RunAsync();

		Assert.Contains("pending scans (live): 1", output.ToString());
	}

	private static async Task<string> RunAsync(CourierTestContext context, params string[] lines)
	{
		var output = new StringWriter();
		await Harness(context, new StringReader(string.Join(Environment.NewLine, lines)), output)
			.RunAsync();
		return output.ToString();
	}

	private static CourierHarness Harness(
		CourierTestContext context,
		TextReader input,
		TextWriter output) =>
		new(
			input,
			output,
			new Lock(),
			context.Scans,
			rejects => new OutboxService(
				context.Scans,
				new FakeShipmentUploader(rejects),
				new RecordingLogger<OutboxService>()),
			TimeProvider.System,
			context.Database.CountPendingScans());
}
