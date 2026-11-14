using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;
using ApexPost.Domain.Services;
using ApexPost.Domain.ValueObjects;
using ApexPost.Specs.Support;
using Reqnroll;

namespace ApexPost.Specs.StepDefinitions;

// Copilot-assisted: drafted with GitHub Copilot (Prompt B) and then reviewed and
// corrected; COPILOT-LOG.md records what was kept, changed and rejected.
//
// The heavy-parcel surcharge rule as business examples, run against
// ShippingPricingService itself. The overlap with ShippingPricingServiceTests is
// deliberate: that rule was built test first (TDD-LOG.md), and this feature states
// the same rule in business language. It does not cover counter-workflow pricing:
// the surcharge is not wired into MixedBasketApplicationService or the
// application configuration.
[Binding]
public sealed class HeavyParcelSurchargeSteps(HeavyParcelSurchargeContext context)
{
	[Given("the {word} rate to zone {string} is {decimal} INR plus {decimal} INR per kg")]
	public void GivenTheRate(
		string serviceLevel,
		string destinationZone,
		decimal basePrice,
		decimal perKilogramCharge)
	{
		context.Rate = new ShippingRate(
			destinationZone,
			Enum.Parse<ShippingServiceLevel>(serviceLevel),
			new Money(basePrice, "INR"),
			new Money(perKilogramCharge, "INR"));
	}

	[Given("the heavy-parcel surcharge is {decimal} INR")]
	public void GivenTheHeavyParcelSurchargeIs(decimal surcharge) =>
		context.HeavyParcelSurcharge = new Money(surcharge, "INR");

	[Given("no heavy-parcel surcharge is configured")]
	public void GivenNoHeavyParcelSurchargeIsConfigured() =>
		context.HeavyParcelSurcharge = null;

	// The shipment goes to the zone and service level of the Background's rate.
	// Only a refusal (InvalidOperationException) is kept for the Then steps; any
	// other exception fails this step.
	[When("I calculate the shipping price for a {string} weighing {decimal} kg")]
	public void WhenICalculateTheShippingPrice(string shipmentType, decimal weightKg)
	{
		var rate = context.Rate
			?? throw new InvalidOperationException("No shipping rate was given in this scenario.");
		var shipment = new ShippingItem(
			id: "APX-SHIP-3001",
			type: Enum.Parse<ShippingItemType>(shipmentType),
			weight: new Weight(weightKg),
			dimensions: new Dimensions(30m, 20m, 10m),
			destinationZone: rate.DestinationZone,
			serviceLevel: rate.ServiceLevel);
		var pricing = new ShippingPricingService([rate], context.HeavyParcelSurcharge);

		context.LastPrice = null;
		context.LastError = null;

		try
		{
			context.LastPrice = pricing.CalculatePrice(shipment);
		}
		catch (InvalidOperationException error)
		{
			context.LastError = error;
		}
	}

	[Then("the total shipping price is {decimal} INR")]
	public void ThenTheTotalShippingPriceIs(decimal expectedTotal)
	{
		Assert.Null(context.LastError);
		var price = Assert.IsType<Money>(context.LastPrice);
		Assert.Equal(expectedTotal, price.Amount);
		Assert.Equal("INR", price.Currency);
	}

	[Then("pricing is refused because no heavy-parcel surcharge is configured")]
	public void ThenPricingIsRefusedBecauseNoSurchargeIsConfigured()
	{
		var error = Assert.IsType<InvalidOperationException>(context.LastError);
		Assert.Equal(
			"No heavy-parcel surcharge is configured for parcels over 30 kg.",
			error.Message);
		Assert.Null(context.LastPrice);
	}
}
