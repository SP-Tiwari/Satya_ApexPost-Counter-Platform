using ApexPost.Application.Courier;

namespace ApexPost.Application.Services;

// What a courier partner may see about one shipment: facts already stored by
// the counter, the existing transaction status, and the courier's own lookup.
// No prices, staff, sender, recipient, or identity data is included, and no
// new parcel-status vocabulary is introduced.
public sealed record ParcelStatus(
	string ShipmentId,
	ShippingItemType Type,
	ShippingServiceLevel ServiceLevel,
	string DestinationZone,
	DateTimeOffset? AcceptedAtUtc,
	TransactionPaymentStatus TransactionStatus,
	CourierTrackingResult? Courier);

public sealed class ParcelStatusApplicationService(
	ICounterTransactionRepository transactions,
	ICourierTrackingService? courier = null,
	IScreenQueryService? queries = null)
{
	private readonly ICounterTransactionRepository _transactions = transactions
		?? throw new ArgumentNullException(nameof(transactions));

	// Returns null when no transaction contains the shipment.
	public async Task<ParcelStatus?> GetStatusAsync(
		string shipmentId,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(shipmentId);

		if (queries is not null)
			return await GetProjectedStatusAsync(queries, shipmentId, cancellationToken)
				.ConfigureAwait(false);

		var basket = await _transactions
			.FindByShipmentIdAsync(shipmentId, cancellationToken)
			.ConfigureAwait(false);
		var shipment = basket?.LineItems
			.Select(lineItem => lineItem.ShippingItem)
			.OfType<ShippingItem>()
			.FirstOrDefault(item => string.Equals(
				item.Id, shipmentId, StringComparison.Ordinal));
		if (basket is null || shipment is null)
			return null;

		var courierResult = courier is null
			? null
			: await courier.GetTrackingAsync(shipment.Id, cancellationToken)
				.ConfigureAwait(false);

		return new ParcelStatus(
			shipment.Id,
			shipment.Type,
			shipment.ServiceLevel,
			shipment.DestinationZone,
			basket.OccurredAtUtc,
			basket.PaymentStatus,
			courierResult);
	}

	// The ID column ignores case and shipment IDs do not, so it is compared
	// again after the query.
	private async Task<ParcelStatus?> GetProjectedStatusAsync(
		IScreenQueryService queries,
		string shipmentId,
		CancellationToken cancellationToken)
	{
		var parcel = await queries.FindParcelAsync(shipmentId, cancellationToken)
			.ConfigureAwait(false);
		if (parcel is null ||
			!string.Equals(parcel.ShipmentId, shipmentId, StringComparison.Ordinal))
		{
			return null;
		}

		var courierResult = courier is null
			? null
			: await courier.GetTrackingAsync(parcel.ShipmentId, cancellationToken)
				.ConfigureAwait(false);

		return new ParcelStatus(
			parcel.ShipmentId,
			parcel.Type,
			parcel.ServiceLevel,
			parcel.DestinationZone,
			parcel.AcceptedAtUtc,
			parcel.TransactionStatus,
			courierResult);
	}
}
