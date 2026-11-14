
namespace ApexPost.Domain.Services;

public sealed class ShippingPricingService
{
	// A parcel strictly heavier than this pays the heavy-parcel surcharge.
	private static readonly Weight HeavyParcelThreshold = new(30m);

	private readonly IReadOnlyDictionary<
		(string DestinationZone, ShippingServiceLevel ServiceLevel),
		ShippingRate> _rates;
	private readonly Money? _heavyParcelSurcharge;

	public ShippingPricingService(
		IEnumerable<ShippingRate> rates,
		Money? heavyParcelSurcharge = null)
	{
		ArgumentNullException.ThrowIfNull(rates);

		var configuredRates = new Dictionary<
			(string DestinationZone, ShippingServiceLevel ServiceLevel),
			ShippingRate>();

		foreach (var rate in rates)
		{
			ArgumentNullException.ThrowIfNull(rate);

			var key = (rate.DestinationZone, rate.ServiceLevel);
			if (!configuredRates.TryAdd(key, rate))
			{
				throw new ArgumentException(
					$"A shipping rate is already configured for destination zone '{rate.DestinationZone}' and service level '{rate.ServiceLevel}'.",
					nameof(rates));
			}
		}

		_rates = configuredRates;
		_heavyParcelSurcharge = heavyParcelSurcharge;
	}

	public Money CalculatePrice(ShippingItem shippingItem)
	{
		ArgumentNullException.ThrowIfNull(shippingItem);

		var key = (shippingItem.DestinationZone, shippingItem.ServiceLevel);
		if (!_rates.TryGetValue(key, out var rate))
		{
			throw new InvalidOperationException(
				$"No shipping rate is configured for destination zone '{shippingItem.DestinationZone}' and service level '{shippingItem.ServiceLevel}'.");
		}

		var amount = rate.BasePrice.Amount
			+ shippingItem.Weight.Kilograms * rate.PerKilogramCharge.Amount
			+ HeavyParcelSurchargeFor(shippingItem, rate);

		return new Money(amount, rate.BasePrice.Currency);
	}

	// Zero for a letter or a parcel at or below the threshold. A heavier parcel
	// needs a configured surcharge in the rate's currency; pricing it without
	// one fails, as a missing rate does.
	private decimal HeavyParcelSurchargeFor(ShippingItem shippingItem, ShippingRate rate)
	{
		if (shippingItem.Type != ShippingItemType.Parcel ||
			!shippingItem.Weight.Exceeds(HeavyParcelThreshold))
		{
			return 0m;
		}

		if (_heavyParcelSurcharge is null)
		{
			throw new InvalidOperationException(
				$"No heavy-parcel surcharge is configured for parcels over {HeavyParcelThreshold.Kilograms} kg.");
		}

		if (!string.Equals(
			_heavyParcelSurcharge.Currency,
			rate.BasePrice.Currency,
			StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				$"The heavy-parcel surcharge currency ({_heavyParcelSurcharge.Currency}) must match the shipping rate currency ({rate.BasePrice.Currency}).");
		}

		return _heavyParcelSurcharge.Amount;
	}
}
