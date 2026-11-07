using ApexPost.Courier.Offline.Application;
using ApexPost.Courier.Offline.Domain;
using ApexPost.Courier.Offline.Uploading;

namespace ApexPost.Courier.Offline.Tests;

// Criterion: outbox and offline persistence strategy.
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

		Assert.Equal(new OutboxRunResult(2, 1, 1), result);
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

		Assert.Equal([first.Id, second.Id], uploader.Uploaded);
		Assert.Equal(new OutboxRunResult(2, 2, 0), result);
		Assert.Equal(SyncStatus.Failed, context.Scans.GetById(alreadyFailed.Id)!.SyncStatus);
	}

	[Fact]
	public async Task SyncPending_MarksAScanFailedWhenTheUploaderThrows()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New();
		context.Scans.Create(scan);
		var uploader = new RecordingUploader { Throw = new HttpRequestException("offline") };

		var result = await Outbox(context, uploader).SyncPendingAsync();

		Assert.Equal(new OutboxRunResult(1, 0, 1), result);
		Assert.Equal(SyncStatus.Failed, context.Scans.GetById(scan.Id)!.SyncStatus);
	}

	[Fact]
	public async Task SyncPending_LeavesScansPendingWhenCancelled()
	{
		using var context = new CourierTestContext();
		var first = Scans.New(minutesAfterBase: 1);
		var second = Scans.New(minutesAfterBase: 2);
		context.Scans.Create(first);
		context.Scans.Create(second);
		using var cancellation = new CancellationTokenSource();
		var uploader = new RecordingUploader { OnUpload = cancellation.Cancel };

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			() => Outbox(context, uploader).SyncPendingAsync(cancellation.Token));

		Assert.Equal(SyncStatus.Synced, context.Scans.GetById(first.Id)!.SyncStatus);
		Assert.Equal(SyncStatus.Pending, context.Scans.GetById(second.Id)!.SyncStatus);
	}

	[Fact]
	public async Task SyncPending_DoesNothingWhenNothingIsPending()
	{
		using var context = new CourierTestContext();

		var result = await Outbox(context, new FakeShipmentUploader()).SyncPendingAsync();

		Assert.Equal(new OutboxRunResult(0, 0, 0), result);
	}

	[Fact]
	public async Task FakeShipmentUploader_AcceptsEveryScanByDefault()
	{
		var result = await new FakeShipmentUploader().UploadAsync(Scans.New());

		Assert.True(result.Succeeded);
	}

	private static OutboxService Outbox(CourierTestContext context, IShipmentUploader uploader) =>
		new(context.Scans, uploader, new RecordingLogger<OutboxService>());

	private sealed class RecordingUploader : IShipmentUploader
	{
		public List<string> Uploaded { get; } = [];
		public Exception? Throw { get; init; }
		public Action? OnUpload { get; init; }

		public Task<ShipmentUploadResult> UploadAsync(
			ShipmentScan scan,
			CancellationToken cancellationToken = default)
		{
			if (Throw is not null)
				throw Throw;

			Uploaded.Add(scan.Id);
			OnUpload?.Invoke();
			return Task.FromResult(ShipmentUploadResult.Success());
		}
	}
}
