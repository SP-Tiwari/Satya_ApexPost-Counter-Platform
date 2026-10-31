

namespace ApexPost.Domain.Entities;

public sealed class ShippingItem
{
	public string Id { get; }
	public ShippingItemType Type { get; }
	public Weight Weight { get; }
	public Dimensions Dimensions { get; }
	public string DestinationZone { get; }
	public ShippingServiceLevel ServiceLevel { get; }

	// Used by EF Core when it materializes a stored row.
	private ShippingItem()
	{
		Id = null!;
		Weight = null!;
		Dimensions = null!;
		DestinationZone = null!;
	}

	public ShippingItem(
		string id,
		ShippingItemType type,
		Weight weight,
		Dimensions dimensions,
		string destinationZone,
		ShippingServiceLevel serviceLevel)
	{
		if (string.IsNullOrWhiteSpace(id))
			throw new ArgumentException("Shipping item ID cannot be empty.", nameof(id));

		ArgumentNullException.ThrowIfNull(weight);
		ArgumentNullException.ThrowIfNull(dimensions);

		if (string.IsNullOrWhiteSpace(destinationZone))
			throw new ArgumentException(
				"Destination zone cannot be empty.", nameof(destinationZone));

		if (!Enum.IsDefined(type))
			throw new ArgumentOutOfRangeException(nameof(type));

		if (!Enum.IsDefined(serviceLevel))
			throw new ArgumentOutOfRangeException(nameof(serviceLevel));

		Id = id.Trim();
		Type = type;
		Weight = weight;
		Dimensions = dimensions;
		DestinationZone = destinationZone.Trim();
		ServiceLevel = serviceLevel;
	}
}
