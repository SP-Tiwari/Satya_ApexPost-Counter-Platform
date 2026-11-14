using ApexPost.Courier.Domain;
using ApexPost.Courier.Offline.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ApexPost.Courier.Offline.Tests;

// Interaction tests: the local scan store and the uploader are Moq mocks, so
// these tests prove what the outbox sends and which status it writes back for
// each answer. They do not prove that Couchbase Lite stores or orders the
// scans; OutboxServiceTests covers the same rules against a real store.
public sealed class OutboxServiceInteractionTests
{
	private readonly Mock<IScanRepository> _scans = new();
	private readonly Mock<IShipmentUploader> _uploader = new();
	private readonly OutboxService _outbox;

	public OutboxServiceInteractionTests() =>
		_outbox = new OutboxService(_scans.Object, _uploader.Object, NullLogger<OutboxService>.Instance);

	// R6: each scan gets the status of its own final answer.
	[Fact]
	public async Task SyncPendingAsync_AcceptedAndRejectedScans_WritesBackSyncedAndFailed()
	{
		// Arrange
		var accepted = Scans.New(trackingNumber: "APX-TEST-OK", minutesAfterBase: 1);
		var rejected = Scans.New(trackingNumber: "APX-TEST-REJECT", minutesAfterBase: 2);
		PendingAre(accepted, rejected);
		_uploader
			.Setup(uploader => uploader.UploadAsync(
				It.Is<IReadOnlyList<ShipmentScan>>(batch =>
					batch.Count == 2 && batch[0].Id == accepted.Id && batch[1].Id == rejected.Id),
				It.IsAny<CancellationToken>()))
			.ReturnsAsync(
			[
				ShipmentUploadResult.Success(accepted.Id),
				ShipmentUploadResult.Failure(rejected.Id, "Unknown tracking number.")
			]);

		// Act
		var result = await _outbox.SyncPendingAsync();

		// Assert
		Assert.Equal((2, 1, 1, 0), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Equal("Unknown tracking number.", Assert.Single(result.Failures).Reason);
		_scans.Verify(repository => repository.Update(It.Is<ShipmentScan>(scan =>
			scan.Id == accepted.Id && scan.SyncStatus == SyncStatus.Synced)), Times.Once);
		_scans.Verify(repository => repository.Update(It.Is<ShipmentScan>(scan =>
			scan.Id == rejected.Id && scan.SyncStatus == SyncStatus.Failed)), Times.Once);
		_uploader.Verify(uploader => uploader.UploadAsync(
			It.IsAny<IReadOnlyList<ShipmentScan>>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task SyncPendingAsync_25PendingScans_SendsABatchOf20ThenABatchOf5()
	{
		// Arrange
		PendingAre(Many(25));
		UploaderAcceptsEveryScan();

		// Act
		var result = await _outbox.SyncPendingAsync();

		// Assert
		Assert.Equal((25, 25, 0), (result.Attempted, result.Synced, result.Pending));
		_uploader.Verify(uploader => uploader.UploadAsync(
			It.Is<IReadOnlyList<ShipmentScan>>(batch => batch.Count == OutboxService.BatchSize),
			It.IsAny<CancellationToken>()), Times.Once);
		_uploader.Verify(uploader => uploader.UploadAsync(
			It.Is<IReadOnlyList<ShipmentScan>>(batch => batch.Count == 5),
			It.IsAny<CancellationToken>()), Times.Once);
		_scans.Verify(repository => repository.Update(It.IsAny<ShipmentScan>()), Times.Exactly(25));
	}

	// R6: no answer means no status change; the next batch is not even sent.
	[Fact]
	public async Task SyncPendingAsync_UploaderThrows_StopsAfterOneBatchAndUpdatesNothing()
	{
		// Arrange
		PendingAre(Many(21));
		_uploader
			.Setup(uploader => uploader.UploadAsync(
				It.IsAny<IReadOnlyList<ShipmentScan>>(), It.IsAny<CancellationToken>()))
			.ThrowsAsync(new HttpRequestException("No network."));

		// Act
		var result = await _outbox.SyncPendingAsync();

		// Assert
		Assert.Equal((20, 0, 0, 21), (result.Attempted, result.Synced, result.Failed, result.Pending));
		Assert.Equal("The upload failed before the server answered.", result.StoppedBecause);
		_uploader.Verify(uploader => uploader.UploadAsync(
			It.IsAny<IReadOnlyList<ShipmentScan>>(), It.IsAny<CancellationToken>()), Times.Once);
		_scans.Verify(repository => repository.Update(It.IsAny<ShipmentScan>()), Times.Never);
	}

	[Fact]
	public async Task SyncPendingAsync_RetryLaterForOneScan_LeavesItPendingAndSendsNoLaterBatch()
	{
		// Arrange
		var pending = Many(21);
		var retry = pending[0];
		PendingAre(pending);
		_uploader
			.Setup(uploader => uploader.UploadAsync(
				It.IsAny<IReadOnlyList<ShipmentScan>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<ShipmentScan> batch, CancellationToken _) =>
				batch.Select(scan => scan.Id == retry.Id
					? ShipmentUploadResult.RetryLater(scan.Id, "Server unavailable.")
					: ShipmentUploadResult.Success(scan.Id)).ToList());

		// Act
		var result = await _outbox.SyncPendingAsync();

		// Assert
		Assert.Equal((20, 19, 2), (result.Attempted, result.Synced, result.Pending));
		Assert.Equal("Server unavailable.", result.StoppedBecause);
		_scans.Verify(repository => repository.Update(It.Is<ShipmentScan>(scan => scan.Id == retry.Id)), Times.Never);
		_scans.Verify(repository => repository.Update(It.Is<ShipmentScan>(scan =>
			scan.SyncStatus == SyncStatus.Synced)), Times.Exactly(19));
		_uploader.Verify(uploader => uploader.UploadAsync(
			It.IsAny<IReadOnlyList<ShipmentScan>>(), It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task SyncPendingAsync_NothingPending_NeverCallsTheUploader()
	{
		// Arrange
		PendingAre();

		// Act
		var result = await _outbox.SyncPendingAsync();

		// Assert
		Assert.Equal((0, 0, 0, 0), (result.Attempted, result.Synced, result.Failed, result.Pending));
		_uploader.Verify(uploader => uploader.UploadAsync(
			It.IsAny<IReadOnlyList<ShipmentScan>>(), It.IsAny<CancellationToken>()), Times.Never);
		_scans.Verify(repository => repository.Update(It.IsAny<ShipmentScan>()), Times.Never);
	}

	[Fact]
	public async Task SyncPendingAsync_AlreadyCancelled_ThrowsAndSendsNothing()
	{
		// Arrange
		PendingAre(Many(3));
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();

		// Act
		var exception = await Record.ExceptionAsync(() => _outbox.SyncPendingAsync(cancellation.Token));

		// Assert
		Assert.IsAssignableFrom<OperationCanceledException>(exception);
		_uploader.Verify(uploader => uploader.UploadAsync(
			It.IsAny<IReadOnlyList<ShipmentScan>>(), It.IsAny<CancellationToken>()), Times.Never);
		_scans.Verify(repository => repository.Update(It.IsAny<ShipmentScan>()), Times.Never);
	}

	private void PendingAre(params ShipmentScan[] pending) =>
		_scans.Setup(repository => repository.ListPending()).Returns(pending);

	private void UploaderAcceptsEveryScan() =>
		_uploader
			.Setup(uploader => uploader.UploadAsync(
				It.IsAny<IReadOnlyList<ShipmentScan>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync((IReadOnlyList<ShipmentScan> batch, CancellationToken _) =>
				batch.Select(scan => ShipmentUploadResult.Success(scan.Id)).ToList());

	// Oldest first, one minute apart, as ListPending returns them.
	private static ShipmentScan[] Many(int count) =>
		[.. Enumerable.Range(0, count).Select(minute => Scans.New(minutesAfterBase: minute))];
}
