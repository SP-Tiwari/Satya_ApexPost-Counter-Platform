
namespace ApexPost.Domain.Tests.Services;

public class ShippingPricingServiceTests
{
	[Fact]
	public void CalculatePrice_WithConfiguredRate_CalculatesBaseAndWeightCharges()
	{
		var service = new ShippingPricingService(
		[
			new ShippingRate(
				"Zone A",
				ShippingServiceLevel.Standard,
				new Money(10m, "INR"),
				new Money(2m, "INR"))
		]);

		var price = service.CalculatePrice(CreateShippingItem(
			ShippingItemType.Parcel,
			2.5m,
			"Zone A",
			ShippingServiceLevel.Standard));

		Assert.Equal(new Money(15m, "INR"), price);
	}

	[Fact]
	public void CalculatePrice_WithDestinationZoneMismatch_ThrowsInvalidOperationException()
	{
		var service = new ShippingPricingService(
		[
			CreateRate("Zone A", ShippingServiceLevel.Standard)
		]);

		var exception = Assert.Throws<InvalidOperationException>(() =>
			service.CalculatePrice(CreateShippingItem(
				ShippingItemType.Parcel,
				1m,
				"Zone B",
				ShippingServiceLevel.Standard)));

		Assert.Contains("destination zone 'Zone B'", exception.Message);
	}

	[Fact]
	public void CalculatePrice_WithServiceLevelMismatch_ThrowsInvalidOperationException()
	{
		var service = new ShippingPricingService(
		[
			CreateRate("Zone A", ShippingServiceLevel.Standard)
		]);

		var exception = Assert.Throws<InvalidOperationException>(() =>
			service.CalculatePrice(CreateShippingItem(
				ShippingItemType.Parcel,
				1m,
				"Zone A",
				ShippingServiceLevel.Express)));

		Assert.Contains("service level 'Express'", exception.Message);
	}

	[Fact]
	public void CalculatePrice_WithNoRates_ThrowsInvalidOperationException()
	{
		var service = new ShippingPricingService([]);

		Assert.Throws<InvalidOperationException>(() =>
			service.CalculatePrice(CreateShippingItem(
				ShippingItemType.Parcel,
				1m,
				"Zone A",
				ShippingServiceLevel.Standard)));
	}

	[Fact]
	public void Constructor_WithDuplicateRateConfiguration_ThrowsArgumentException()
	{
		var rate = CreateRate("Zone A", ShippingServiceLevel.Standard);

		Assert.Throws<ArgumentException>(() =>
			new ShippingPricingService([rate, rate]));
	}

	[Fact]
	public void Constructor_WithNullRates_ThrowsArgumentNullException()
	{
		Assert.Throws<ArgumentNullException>(() =>
			new ShippingPricingService(null!));
	}

	[Fact]
	public void CalculatePrice_WithNullShippingItem_ThrowsArgumentNullException()
	{
		var service = new ShippingPricingService([]);

		Assert.Throws<ArgumentNullException>(() =>
			service.CalculatePrice(null!));
	}

	// Week 5 TDD: a parcel strictly over 30 kg pays a heavy-parcel surcharge.
	// The amount is policy (ADR 0005), so it is supplied to the service; these
	// tests use a fictional 50.00 INR. Letters are exempt, as they are from the
	// parcel weight limit. The rate is 10 INR + 2 INR per kg.
	private static readonly Money HeavyParcelSurcharge = new(50m, "INR");

	[Fact]
	public void CalculatePrice_ParcelOfExactly30Kg_AddsNoSurcharge()
	{
		// Arrange
		var service = ServiceWithSurcharge(HeavyParcelSurcharge);
		var parcel = CreateShippingItem(ShippingItemType.Parcel, 30m, "Zone A", ShippingServiceLevel.Standard);

		// Act
		var price = service.CalculatePrice(parcel);

		// Assert
		Assert.Equal(new Money(70m, "INR"), price);
	}

	[Fact]
	public void CalculatePrice_ParcelBelow30Kg_AddsNoSurcharge()
	{
		// Arrange
		var service = ServiceWithSurcharge(HeavyParcelSurcharge);
		var parcel = CreateShippingItem(ShippingItemType.Parcel, 29.99m, "Zone A", ShippingServiceLevel.Standard);

		// Act
		var price = service.CalculatePrice(parcel);

		// Assert
		Assert.Equal(new Money(69.98m, "INR"), price);
	}

	// Weight in kg, expected price in INR: 10 + 2 × weight + 50.
	public static TheoryData<decimal, decimal> HeavyParcels => new()
	{
		{ 30.01m, 120.02m },
		{ 31m, 122m }
	};

	[Theory]
	[MemberData(nameof(HeavyParcels))]
	public void CalculatePrice_ParcelOver30Kg_AddsTheSurcharge(decimal kilograms, decimal expectedAmount)
	{
		// Arrange
		var service = ServiceWithSurcharge(HeavyParcelSurcharge);
		var parcel = CreateShippingItem(ShippingItemType.Parcel, kilograms, "Zone A", ShippingServiceLevel.Standard);

		// Act
		var price = service.CalculatePrice(parcel);

		// Assert
		Assert.Equal(new Money(expectedAmount, "INR"), price);
	}

	// Fails closed, as a missing rate does: a heavy parcel is never priced
	// without its surcharge.
	[Fact]
	public void CalculatePrice_ParcelOver30KgWithNoConfiguredSurcharge_ThrowsInvalidOperationException()
	{
		// Arrange
		var service = ServiceWithSurcharge(null);
		var parcel = CreateShippingItem(ShippingItemType.Parcel, 31m, "Zone A", ShippingServiceLevel.Standard);

		// Act
		var exception = Assert.Throws<InvalidOperationException>(() => service.CalculatePrice(parcel));

		// Assert
		Assert.Equal("No heavy-parcel surcharge is configured for parcels over 30 kg.", exception.Message);
	}

	[Fact]
	public void CalculatePrice_SurchargeInAnotherCurrency_ThrowsInvalidOperationException()
	{
		// Arrange
		var service = ServiceWithSurcharge(new Money(50m, "USD"));
		var parcel = CreateShippingItem(ShippingItemType.Parcel, 31m, "Zone A", ShippingServiceLevel.Standard);

		// Act
		var exception = Assert.Throws<InvalidOperationException>(() => service.CalculatePrice(parcel));

		// Assert
		Assert.Equal(
			"The heavy-parcel surcharge currency (USD) must match the shipping rate currency (INR).",
			exception.Message);
	}

	[Fact]
	public void CalculatePrice_LetterOver30Kg_AddsNoSurcharge()
	{
		// Arrange
		var service = ServiceWithSurcharge(HeavyParcelSurcharge);
		var letter = CreateShippingItem(ShippingItemType.Letter, 31m, "Zone A", ShippingServiceLevel.Standard);

		// Act
		var price = service.CalculatePrice(letter);

		// Assert
		Assert.Equal(new Money(72m, "INR"), price);
	}

	private static ShippingPricingService ServiceWithSurcharge(Money? surcharge) =>
		new(
			[CreateRate("Zone A", ShippingServiceLevel.Standard)],
			heavyParcelSurcharge: surcharge);

	private static ShippingRate CreateRate(
		string destinationZone,
		ShippingServiceLevel serviceLevel) =>
		new(
			destinationZone,
			serviceLevel,
			new Money(10m, "INR"),
			new Money(2m, "INR"));

	private static ShippingItem CreateShippingItem(
		ShippingItemType type,
		decimal kilograms,
		string destinationZone,
		ShippingServiceLevel serviceLevel) =>
		new(
			"shipping-item-1",
			type,
			new Weight(kilograms),
			new Dimensions(10m, 10m, 10m),
			destinationZone,
			serviceLevel);
}
