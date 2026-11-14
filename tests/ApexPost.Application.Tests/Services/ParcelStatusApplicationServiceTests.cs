using ApexPost.Application.Courier;
using Moq;

namespace ApexPost.Application.Tests.Services;

// Copilot-assisted: drafted with GitHub Copilot (Prompt A) and then reviewed and
// corrected; COPILOT-LOG.md records what was kept, changed and rejected.
//
// Interaction tests: the transaction repository, the screen query service and the
// courier tracking service are Moq mocks, so these tests prove which source the
// service reads, how it maps the stored facts and when it asks the courier. They
// do not prove the SQL behind FindByShipmentIdAsync or FindParcelAsync; the partner
// parcel-status endpoint is covered end to end against SQL Server by
// CounterScreensIntegrationTests.
public sealed class ParcelStatusApplicationServiceTests
{
	private const string ShipmentId = "APX-SHIP-1001";
	private const string DestinationZone = "ZONE-NORTH";
	private static readonly DateTimeOffset AcceptedAtUtc = new(2026, 2, 10, 11, 0, 0, TimeSpan.Zero);

	private readonly Mock<ICounterTransactionRepository> _transactions = new();
	private readonly Mock<ICourierTrackingService> _courier = new();
	private readonly Mock<IScreenQueryService> _queries = new();

	[Fact]
	public async Task GetStatusAsync_NullShipmentId_ThrowsArgumentNullException()
	{
		// Arrange
		var service = new ParcelStatusApplicationService(_transactions.Object);

		// Act
		Task Act() => service.GetStatusAsync(null!);

		// Assert
		var exception = await Assert.ThrowsAsync<ArgumentNullException>(Act);
		Assert.Equal("shipmentId", exception.ParamName);
		_transactions.Verify(repository => repository.FindByShipmentIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public async Task GetStatusAsync_EmptyOrWhitespaceShipmentId_ThrowsArgumentException(string shipmentId)
	{
		// Arrange
		var service = new ParcelStatusApplicationService(_transactions.Object);

		// Act
		Task Act() => service.GetStatusAsync(shipmentId);

		// Assert
		var exception = await Assert.ThrowsAsync<ArgumentException>(Act);
		Assert.Equal("shipmentId", exception.ParamName);
		_transactions.Verify(repository => repository.FindByShipmentIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task GetStatusAsync_ShipmentNotFoundInRepository_ReturnsNullAndDoesNotCallCourier()
	{
		// Arrange
		_transactions
			.Setup(repository => repository.FindByShipmentIdAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((TransactionBasket?)null);
		var service = new ParcelStatusApplicationService(_transactions.Object, _courier.Object);

		// Act
		var result = await service.GetStatusAsync(ShipmentId);

		// Assert
		Assert.Null(result);
		_transactions.Verify(repository => repository.FindByShipmentIdAsync(ShipmentId, It.IsAny<CancellationToken>()), Times.Once);
		_courier.Verify(tracking => tracking.GetTrackingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	// The repository finds the basket by shipment ID, but the service still
	// matches the basket's shipping items exactly, including letter case.
	[Theory]
	[InlineData("APX-SHIP-2002")]
	[InlineData("apx-ship-1001")]
	public async Task GetStatusAsync_RepositoryBasketHasNoMatchingShipment_ReturnsNullAndDoesNotCallCourier(string storedShipmentId)
	{
		// Arrange
		_transactions
			.Setup(repository => repository.FindByShipmentIdAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreatePaidBasketWithShipment(storedShipmentId));
		var service = new ParcelStatusApplicationService(_transactions.Object, _courier.Object);

		// Act
		var result = await service.GetStatusAsync(ShipmentId);

		// Assert
		Assert.Null(result);
		_courier.Verify(tracking => tracking.GetTrackingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task GetStatusAsync_ShipmentFoundInRepository_ReturnsEveryParcelStatusField()
	{
		// Arrange
		var basket = CreatePaidBasketWithShipment(ShipmentId);
		_transactions
			.Setup(repository => repository.FindByShipmentIdAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(basket);
		var service = new ParcelStatusApplicationService(_transactions.Object);

		// Act
		var result = await service.GetStatusAsync(ShipmentId);

		// Assert
		var status = Assert.IsType<ParcelStatus>(result);
		Assert.Equal(ShipmentId, status.ShipmentId);
		Assert.Equal(ShippingItemType.Parcel, status.Type);
		Assert.Equal(ShippingServiceLevel.Express, status.ServiceLevel);
		Assert.Equal(DestinationZone, status.DestinationZone);
		Assert.Equal(AcceptedAtUtc, status.AcceptedAtUtc);
		Assert.Equal(TransactionPaymentStatus.Paid, status.TransactionStatus);
		Assert.Null(status.Courier);
	}

	[Fact]
	public async Task GetStatusAsync_ScreenQueryServiceSupplied_UsesScreenQueryInsteadOfRepository()
	{
		// Arrange
		_queries
			.Setup(query => query.FindParcelAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateParcelDetail(ShipmentId));
		var service = new ParcelStatusApplicationService(_transactions.Object, courier: null, queries: _queries.Object);

		// Act
		var result = await service.GetStatusAsync(ShipmentId);

		// Assert
		var status = Assert.IsType<ParcelStatus>(result);
		Assert.Equal(ShipmentId, status.ShipmentId);
		Assert.Equal(ShippingItemType.Parcel, status.Type);
		Assert.Equal(ShippingServiceLevel.Express, status.ServiceLevel);
		Assert.Equal(DestinationZone, status.DestinationZone);
		Assert.Equal(AcceptedAtUtc, status.AcceptedAtUtc);
		Assert.Equal(TransactionPaymentStatus.Paid, status.TransactionStatus);
		Assert.Null(status.Courier);
		_queries.Verify(query => query.FindParcelAsync(ShipmentId, It.IsAny<CancellationToken>()), Times.Once);
		_transactions.Verify(repository => repository.FindByShipmentIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task GetStatusAsync_ShipmentNotFoundByScreenQuery_ReturnsNullAndDoesNotCallCourier()
	{
		// Arrange
		_queries
			.Setup(query => query.FindParcelAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync((ParcelDetail?)null);
		var service = new ParcelStatusApplicationService(_transactions.Object, _courier.Object, _queries.Object);

		// Act
		var result = await service.GetStatusAsync(ShipmentId);

		// Assert
		Assert.Null(result);
		_courier.Verify(tracking => tracking.GetTrackingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
		_transactions.Verify(repository => repository.FindByShipmentIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task GetStatusAsync_ScreenQueryShipmentIdCaseDiffers_ReturnsNull()
	{
		// Arrange
		var differentCaseShipmentId = ShipmentId.ToLowerInvariant();
		_queries
			.Setup(query => query.FindParcelAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateParcelDetail(differentCaseShipmentId));
		var service = new ParcelStatusApplicationService(_transactions.Object, _courier.Object, _queries.Object);

		// Act
		var result = await service.GetStatusAsync(ShipmentId);

		// Assert
		Assert.Null(result);
		_courier.Verify(tracking => tracking.GetTrackingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
	}

	[Fact]
	public async Task GetStatusAsync_CourierTrackingConfigured_IncludesCourierAndCallsTrackingOnce()
	{
		// Arrange
		var basket = CreatePaidBasketWithShipment(ShipmentId);
		var trackingResult = new CourierTrackingResult(
			ShipmentId,
			CourierLookupOutcome.Available,
			"In transit");
		_transactions
			.Setup(repository => repository.FindByShipmentIdAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(basket);
		_courier
			.Setup(tracking => tracking.GetTrackingAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(trackingResult);
		var service = new ParcelStatusApplicationService(_transactions.Object, _courier.Object);

		// Act
		var result = await service.GetStatusAsync(ShipmentId);

		// Assert
		var status = Assert.IsType<ParcelStatus>(result);
		Assert.Equal(trackingResult, status.Courier);
		_courier.Verify(tracking => tracking.GetTrackingAsync(ShipmentId, It.IsAny<CancellationToken>()), Times.Once);
	}

	[Fact]
	public async Task GetStatusAsync_ScreenQueryWithCourierTracking_IncludesCourierAndCallsTrackingOnce()
	{
		// Arrange
		var trackingResult = new CourierTrackingResult(
			ShipmentId,
			CourierLookupOutcome.Available,
			"Out for delivery");
		_queries
			.Setup(query => query.FindParcelAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(CreateParcelDetail(ShipmentId));
		_courier
			.Setup(tracking => tracking.GetTrackingAsync(ShipmentId, It.IsAny<CancellationToken>()))
			.ReturnsAsync(trackingResult);
		var service = new ParcelStatusApplicationService(_transactions.Object, _courier.Object, _queries.Object);

		// Act
		var result = await service.GetStatusAsync(ShipmentId);

		// Assert
		var status = Assert.IsType<ParcelStatus>(result);
		Assert.Equal(trackingResult, status.Courier);
		_courier.Verify(tracking => tracking.GetTrackingAsync(ShipmentId, It.IsAny<CancellationToken>()), Times.Once);
	}

	private static TransactionBasket CreatePaidBasketWithShipment(string shipmentId)
	{
		var basket = new TransactionBasket(
			"INR",
			new Dictionary<TaxCategory, decimal> { [TaxCategory.Standard] = 0m },
			branchCode: null,
			occurredAtUtc: AcceptedAtUtc);
		basket.AddLineItem(new TransactionLineItem(
			"Parcel shipment",
			TransactionItemType.Service,
			new Money(150m, "INR"),
			1,
			TaxCategory.Standard,
			productSku: null,
			shippingItem: new ShippingItem(
				shipmentId,
				ShippingItemType.Parcel,
				new Weight(2.5m),
				new Dimensions(30m, 20m, 10m),
				DestinationZone,
				ShippingServiceLevel.Express)));
		basket.Pay();
		return basket;
	}

	private static ParcelDetail CreateParcelDetail(string shipmentId) =>
		new(
			shipmentId,
			ShippingItemType.Parcel,
			ShippingServiceLevel.Express,
			DestinationZone,
			AcceptedAtUtc,
			TransactionPaymentStatus.Paid);
}
