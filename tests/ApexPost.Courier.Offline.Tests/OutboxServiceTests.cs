using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Domain;
using ApexPost.Courier.Offline.Uploading;

namespace ApexPost.Courier.Offline.Tests;

// Criterion: outbox and offline persistence strategy (Part A), with the Part D
// batch upload (D9): batches of at most 20, oldest first, and a scan stays
// Pending until the server gives a final answer.
public sealed class OutboxServiceTests
{
	[Fact]
	public async Task SyncPending_MarksAcceptedScansSyncedAndRejectedScansFailed()
	{
		using var context = new CourierTestContext();
		var accepted = Scans.New(trackingNumber: "APX-TEST-OK", minutesAfterBase: 1);
		var rejected = Scans.New(trackingNumber: "APX-TEST-REJECT", minutesAfterBase: 2);
		context.Scans.Create(accepted);
		context.Scans.Create(rejected);
		var outbox = Outbox(context, new FakeShipmentUploader(
			scan => scan.TrackingNumber == "APX-TEST-REJECT"));

		var result = await outbox.SyncPendingAsync();

		Assert.Equal((2, 1, 1, 0), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Null(result.StoppedBecause);
		var failure = Assert.Single(result.Failures);
		Assert.Equal((rejected.Id, "APX-TEST-REJECT", "Rejected by the fake uploader."), (failure.ScanId, failure.TrackingNumber, failure.Reason));
		Assert.Equal(SyncStatus.Synced, context.Scans.GetById(accepted.Id)!.SyncStatus);
		Assert.Equal(SyncStatus.Failed, context.Scans.GetById(rejected.Id)!.SyncStatus);
		Assert.Empty(context.Scans.ListPending());
	}

	[Fact]
	public async Task SyncPending_UploadsOnlyPendingScansOldestFirst()
	{
		using var context = new CourierTestContext();
		var alreadySynced = Scans.New(minutesAfterBase: 0);
		var alreadyFailed = Scans.New(minutesAfterBase: 1);
		var second = Scans.New(minutesAfterBase: 9);
		var first = Scans.New(minutesAfterBase: 3);
		foreach (var scan in new[] { alreadySynced, alreadyFailed, second, first })
			context.Scans.Create(scan);
		context.Scans.Update(alreadySynced.MarkSynced());
		context.Scans.Update(alreadyFailed.MarkSyncFailed());
		var uploader = new RecordingUploader();

		var result = await Outbox(context, uploader).SyncPendingAsync();

		Assert.Equal([first.Id, second.Id], uploader.Batches.Single());
		Assert.Equal((2, 2, 0, 0), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Equal(SyncStatus.Failed, context.Scans.GetById(alreadyFailed.Id)!.SyncStatus);
	}

	[Fact]
	public async Task SyncPending_Uploads45Scans_InBatchesOf20_20And5_OldestFirst()
	{
		using var context = new CourierTestContext();
		var created = Enumerable.Range(0, 45)
			.Select(minute => Scans.New(minutesAfterBase: minute))
			.ToList();
		foreach (var scan in created.AsEnumerable().Reverse())
			context.Scans.Create(scan);
		var uploader = new RecordingUploader();

		var result = await Outbox(context, uploader).SyncPendingAsync();

		Assert.Equal([20, 20, 5], uploader.Batches.Select(batch => batch.Count));
		Assert.Equal(created.Select(scan => scan.Id), uploader.Batches.SelectMany(batch => batch));
		Assert.Equal((45, 45, 0, 0), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Empty(context.Scans.ListPending());
	}

	// Part D (D9) replaces the Part A rule "an uploader exception counts as
	// Failed": an exception means the server never answered, so marking the
	// scan Failed would wrongly stop it from ever being sent again. The batch
	// stays Pending and the run stops; nothing later is attempted.
	[Fact]
	public async Task SyncPending_WhenTheUploaderThrows_LeavesTheScansPendingAndStops()
	{
		using var context = new CourierTestContext();
		foreach (var minute in Enumerable.Range(0, 25))
			context.Scans.Create(Scans.New(minutesAfterBase: minute));
		var uploader = new RecordingUploader { Throw = new HttpRequestException("offline") };

		var result = await Outbox(context, uploader).SyncPendingAsync();

		Assert.Equal((20, 0, 0, 25), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Equal("The upload failed before the server answered.", result.StoppedBecause);
		Assert.Single(uploader.Batches);
		Assert.Equal(25, context.Scans.ListPending().Count);
	}

	[Fact]
	public async Task SyncPending_RetryLater_KeepsThoseScansPending_AppliesTheRest_AndStops()
	{
		using var context = new CourierTestContext();
		var created = Enumerable.Range(0, 25)
			.Select(minute => Scans.New(
				trackingNumber: minute is 3 or 4 ? "APX-TEST-LATER" : minute == 5 ? "APX-TEST-REJECT" : "APX-TEST-OK",
				minutesAfterBase: minute))
			.ToList();
		foreach (var scan in created)
			context.Scans.Create(scan);
		var uploader = new RecordingUploader
		{
			Decide = scan => scan.TrackingNumber switch
			{
				"APX-TEST-LATER" => ShipmentUploadResult.RetryLater(scan.Id, "Server unavailable."),
				"APX-TEST-REJECT" => ShipmentUploadResult.Failure(scan.Id, "Unknown shipment."),
				_ => ShipmentUploadResult.Success(scan.Id)
			}
		};

		var result = await Outbox(context, uploader).SyncPendingAsync();

		Assert.Single(uploader.Batches);
		Assert.Equal((20, 17, 1, 7), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Equal("Server unavailable.", result.StoppedBecause);
		Assert.Equal(SyncStatus.Pending, context.Scans.GetById(created[3].Id)!.SyncStatus);
		Assert.Equal(SyncStatus.Failed, context.Scans.GetById(created[5].Id)!.SyncStatus);
		Assert.Equal(SyncStatus.Synced, context.Scans.GetById(created[19].Id)!.SyncStatus);
		Assert.All(created.Skip(20), scan => Assert.Equal(SyncStatus.Pending, context.Scans.GetById(scan.Id)!.SyncStatus));
	}

	[Fact]
	public async Task SyncPending_AScanWithoutAResult_StaysPending()
	{
		using var context = new CourierTestContext();
		var answered = Scans.New(minutesAfterBase: 1);
		var unanswered = Scans.New(minutesAfterBase: 2);
		context.Scans.Create(answered);
		context.Scans.Create(unanswered);
		var uploader = new RecordingUploader { Omit = unanswered.Id };

		var result = await Outbox(context, uploader).SyncPendingAsync();

		Assert.Equal((2, 1, 0, 1), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Equal("No result was returned for this scan.", result.StoppedBecause);
		Assert.Equal(SyncStatus.Pending, context.Scans.GetById(unanswered.Id)!.SyncStatus);
	}

	[Fact]
	public async Task SyncPending_LeavesLaterBatchesPendingWhenCancelled()
	{
		using var context = new CourierTestContext();
		var created = Enumerable.Range(0, 21)
			.Select(minute => Scans.New(minutesAfterBase: minute))
			.ToList();
		foreach (var scan in created)
			context.Scans.Create(scan);
		using var cancellation = new CancellationTokenSource();
		var uploader = new RecordingUploader { OnUpload = cancellation.Cancel };

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => Outbox(context, uploader).SyncPendingAsync(cancellation.Token));

		// The first batch was answered before the cancellation took effect.
		Assert.All(created.Take(20), scan => Assert.Equal(SyncStatus.Synced, context.Scans.GetById(scan.Id)!.SyncStatus));
		Assert.Equal(SyncStatus.Pending, context.Scans.GetById(created[20].Id)!.SyncStatus);
	}

	[Fact]
	public async Task SyncPending_DoesNothingWhenNothingIsPending()
	{
		using var context = new CourierTestContext();

		var result = await Outbox(context, new FakeShipmentUploader()).SyncPendingAsync();

		Assert.Equal((0, 0, 0, 0), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Null(result.StoppedBecause);
		Assert.Empty(result.Failures);
	}

	[Fact]
	public async Task FakeShipmentUploader_AcceptsEveryScanByDefault()
	{
		var scan = Scans.New();

		var result = Assert.Single(await new FakeShipmentUploader().UploadAsync([scan]));

		Assert.Equal(ShipmentUploadResult.Success(scan.Id), result);
	}

	private static OutboxService Outbox(CourierTestContext context, IShipmentUploader uploader) =>
		new(context.Scans, uploader, new RecordingLogger<OutboxService>());

	private sealed class RecordingUploader : IShipmentUploader
	{
		public List<List<string>> Batches { get; } = [];
		public Exception? Throw { get; init; }
		public Action? OnUpload { get; init; }
		public string? Omit { get; init; }
		public Func<ShipmentScan, ShipmentUploadResult> Decide { get; init; } =
			scan => ShipmentUploadResult.Success(scan.Id);

		public Task<IReadOnlyList<ShipmentUploadResult>> UploadAsync(
			IReadOnlyList<ShipmentScan> scans,
			CancellationToken cancellationToken = default)
		{
			Batches.Add([.. scans.Select(scan => scan.Id)]);
			if (Throw is not null)
				throw Throw;

			OnUpload?.Invoke();
			IReadOnlyList<ShipmentUploadResult> results =
				[.. scans.Where(scan => scan.Id != Omit).Select(Decide)];
			return Task.FromResult(results);
		}
	}
}
