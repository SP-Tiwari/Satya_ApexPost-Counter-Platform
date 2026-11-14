using Moq;

namespace ApexPost.Application.Tests.Services;

// Interaction tests: the repository and the unit of work are Moq mocks, so
// these tests prove which calls the service makes and when it saves. They do
// not prove the SQL behind those calls; the primary key, unique scan IDs and
// the transaction are covered against SQL Server by SqlPersistenceTests and
// the API's ShipmentEndpointsTests, ShipmentStatusTests and ScanUploadTests.
public sealed class TrackedShipmentApplicationServiceTests
{
	private const string TrackingNumber = "APX-00000001";
	private const string FirstScanId = "scan::11111111-1111-1111-1111-111111111111";
	private const string SecondScanId = "scan::22222222-2222-2222-2222-222222222222";
	private static readonly DateTimeOffset ScannedAtUtc = new(2026, 1, 15, 9, 30, 0, TimeSpan.Zero);

	private readonly Mock<ITrackedShipmentRepository> _shipments = new();
	private readonly Mock<IUnitOfWork> _unitOfWork = new();
	private readonly TrackedShipmentApplicationService _service;

	public TrackedShipmentApplicationServiceTests()
	{
		// The real unit of work runs the use case inside its transaction, so
		// the mock runs the callback too; otherwise no repository call happens.
		RunsWorkInTransaction<bool>();
		RunsWorkInTransaction<ShipmentChangeResult>();
		RunsWorkInTransaction<ScanUploadResult?>();
		_service = new TrackedShipmentApplicationService(_shipments.Object, _unitOfWork.Object);
	}

	[Fact]
	public async Task CreateAsync_NewTrackingNumber_AddsTheShipmentAndSavesOnce()
	{
		// Arrange
		var command = new CreateTrackedShipmentCommand(TrackingNumber, Details(2.5m));

		// Act
		var result = await _service.CreateAsync(command);

		// Assert
		Assert.Equal(ShipmentChangeOutcome.Succeeded, result.Outcome);
		_shipments.Verify(repository => repository.Add(It.Is<TrackedShipment>(shipment =>
			shipment.TrackingNumber == TrackingNumber &&
			shipment.Status == ShipmentStatus.Created &&
			shipment.Weight.Kilograms == 2.5m)), Times.Once);
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	// R2: the database's primary key reports the duplicate; the service turns
	// it into a conflict instead of letting the exception escape.
	[Fact]
	public async Task CreateAsync_DuplicateTrackingNumber_ReturnsConflict()
	{
		// Arrange
		_unitOfWork
			.Setup(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()))
			.ThrowsAsync(new DuplicateRecordException("Duplicate key.", new InvalidOperationException()));
		var command = new CreateTrackedShipmentCommand(TrackingNumber, Details(2.5m));

		// Act
		var result = await _service.CreateAsync(command);

		// Assert
		Assert.Equal(ShipmentChangeOutcome.Conflict, result.Outcome);
		Assert.Equal($"Shipment {TrackingNumber} already exists.", result.Problem);
		Assert.Null(result.Shipment);
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task ChangeStatusAsync_AllowedTransition_SavesTheNewStatus()
	{
		// Arrange
		var shipment = Shipment();
		FindReturns(shipment);

		// Act
		var result = await _service.ChangeStatusAsync(TrackingNumber, ShipmentStatus.PickedUp);

		// Assert
		Assert.Equal(ShipmentChangeOutcome.Succeeded, result.Outcome);
		Assert.Equal(ShipmentStatus.PickedUp, result.Shipment!.Status);
		_shipments.Verify(repository => repository.FindAsync(
			It.Is<string>(number => number == TrackingNumber), It.IsAny<CancellationToken>()), Times.Once);
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	// R1: a delivered shipment cannot go back to InTransit, and a refused
	// change saves nothing.
	[Fact]
	public async Task ChangeStatusAsync_DeliveredToInTransit_ReturnsConflictWithoutSaving()
	{
		// Arrange
		var shipment = Shipment(
			ShipmentStatus.PickedUp,
			ShipmentStatus.InTransit,
			ShipmentStatus.OutForDelivery,
			ShipmentStatus.Delivered);
		FindReturns(shipment);

		// Act
		var result = await _service.ChangeStatusAsync(TrackingNumber, ShipmentStatus.InTransit);

		// Assert
		Assert.Equal(ShipmentChangeOutcome.Conflict, result.Outcome);
		Assert.Equal("A shipment cannot move from Delivered to InTransit.", result.Problem);
		Assert.Equal(ShipmentStatus.Delivered, shipment.Status);
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task ChangeStatusAsync_UnknownTrackingNumber_ReturnsNotFoundWithoutSaving()
	{
		// Arrange
		FindReturns(null);

		// Act
		var result = await _service.ChangeStatusAsync(TrackingNumber, ShipmentStatus.PickedUp);

		// Assert
		Assert.Equal(ShipmentChangeOutcome.NotFound, result.Outcome);
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task DeleteAsync_CreatedShipment_RemovesItAndSavesOnce()
	{
		// Arrange
		var shipment = Shipment();
		FindReturns(shipment);

		// Act
		var result = await _service.DeleteAsync(TrackingNumber);

		// Assert
		Assert.Equal(ShipmentChangeOutcome.Succeeded, result.Outcome);
		_shipments.Verify(repository => repository.Remove(shipment), Times.Once);
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task DeleteAsync_PickedUpShipment_ReturnsConflictAndNeverRemovesIt()
	{
		// Arrange
		FindReturns(Shipment(ShipmentStatus.PickedUp));

		// Act
		var result = await _service.DeleteAsync(TrackingNumber);

		// Assert
		Assert.Equal(ShipmentChangeOutcome.Conflict, result.Outcome);
		Assert.Equal(
			$"Shipment {TrackingNumber} is PickedUp; only a Created shipment can be deleted.",
			result.Problem);
		_shipments.Verify(repository => repository.Remove(It.IsAny<TrackedShipment>()), Times.Never);
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
	}

	// R3: a scan repeated in the request is stored once; the stored-ID lookup
	// is asked about the uploaded IDs.
	[Fact]
	public async Task UploadScansAsync_RepeatedScan_StoresItOnceAndSavesOnce()
	{
		// Arrange
		var shipment = Shipment();
		FindReturns(shipment);
		StoredScanIdsAre();

		// Act
		var result = await _service.UploadScansAsync(
			TrackingNumber, [Scan(FirstScanId), Scan(FirstScanId)]);

		// Assert
		Assert.Equal((1, 1), (result!.Accepted, result.DuplicatesSkipped));
		Assert.Empty(result.Rejected);
		Assert.Equal(FirstScanId, Assert.Single(shipment.Scans).ScanId);
		_shipments.Verify(repository => repository.FindStoredScanIdsAsync(
			It.Is<IReadOnlyCollection<string>>(ids => ids.Contains(FirstScanId)),
			It.IsAny<CancellationToken>()), Times.Once);
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task UploadScansAsync_ScanStoredForAnotherShipment_RejectsItWithoutSaving()
	{
		// Arrange
		var shipment = Shipment();
		FindReturns(shipment);
		StoredScanIdsAre(FirstScanId);

		// Act
		var result = await _service.UploadScansAsync(TrackingNumber, [Scan(FirstScanId)]);

		// Assert
		var rejected = Assert.Single(result!.Rejected);
		Assert.Equal(
			(FirstScanId, "The scan ID is already recorded for another shipment."),
			(rejected.ScanId, rejected.Reason));
		Assert.Equal(0, result.Accepted);
		Assert.Empty(shipment.Scans);
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
	}

	// Another upload stored the same scan after this one read the stored IDs:
	// the save fails with a duplicate, and the service runs the whole upload
	// once more on fresh state, where the scan is now a duplicate.
	[Fact]
	public async Task UploadScansAsync_DuplicateOnSave_RunsTheUploadAgainOnFreshState()
	{
		// Arrange
		var storedByTheOtherUpload = Shipment();
		storedByTheOtherUpload.AddScan(new TrackedShipmentScan(
			SecondScanId, ShipmentScanType.Pickup, "COURIER-TEST-01", "Test Depot", ScannedAtUtc, null));
		_shipments
			.SetupSequence(repository => repository.FindAsync(TrackingNumber, It.IsAny<CancellationToken>()))
			.ReturnsAsync(Shipment())
			.ReturnsAsync(storedByTheOtherUpload);
		StoredScanIdsAre();
		_unitOfWork
			.Setup(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()))
			.ThrowsAsync(new DuplicateRecordException("Duplicate key.", new InvalidOperationException()));

		// Act
		var result = await _service.UploadScansAsync(TrackingNumber, [Scan(SecondScanId)]);

		// Assert
		Assert.Equal((0, 1), (result!.Accepted, result.DuplicatesSkipped));
		_unitOfWork.Verify(unitOfWork => unitOfWork.ExecuteInTransactionAsync(
			It.IsAny<Func<CancellationToken, Task<ScanUploadResult?>>>(),
			It.IsAny<CancellationToken>()), Times.Exactly(2));
		_unitOfWork.Verify(unitOfWork => unitOfWork.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
	}

	private void RunsWorkInTransaction<TResult>() =>
		_unitOfWork
			.Setup(unitOfWork => unitOfWork.ExecuteInTransactionAsync(
				It.IsAny<Func<CancellationToken, Task<TResult>>>(),
				It.IsAny<CancellationToken>()))
			.Returns((Func<CancellationToken, Task<TResult>> work, CancellationToken token) => work(token));

	private void FindReturns(TrackedShipment? shipment) =>
		_shipments
			.Setup(repository => repository.FindAsync(TrackingNumber, It.IsAny<CancellationToken>()))
			.ReturnsAsync(shipment);

	private void StoredScanIdsAre(params string[] scanIds) =>
		_shipments
			.Setup(repository => repository.FindStoredScanIdsAsync(
				It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
			.ReturnsAsync(scanIds);

	private static ShipmentDetails Details(decimal weightKg) =>
		new("Test Recipient", "+15550100", "1 Test Street", "Testville", weightKg);

	// A Created shipment, moved through the given statuses in order.
	private static TrackedShipment Shipment(params ShipmentStatus[] statuses)
	{
		var shipment = new TrackedShipment(
			TrackingNumber, "Test Recipient", "+15550100", "1 Test Street", "Testville", new Weight(2.5m));
		foreach (var status in statuses)
			shipment.ChangeStatus(status);
		return shipment;
	}

	private static UploadedScan Scan(string scanId) =>
		new(scanId, TrackingNumber, ShipmentScanType.Pickup, "COURIER-TEST-01", "Test Depot", ScannedAtUtc, null);
}
