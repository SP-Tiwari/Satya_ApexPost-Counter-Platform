namespace ApexPost.Domain.Rules;

// Created → PickedUp → InTransit → OutForDelivery → Delivered or Failed.
// Each status moves only to the next one, and Delivered and Failed are final,
// so a repeated or backward change is refused as well as a skipped step.
public sealed class ShipmentStatusTransitionRule
{
	public bool IsAllowed(ShipmentStatus current, ShipmentStatus next) =>
		(current, next) switch
		{
			(ShipmentStatus.Created, ShipmentStatus.PickedUp) => true,
			(ShipmentStatus.PickedUp, ShipmentStatus.InTransit) => true,
			(ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery) => true,
			(ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered or ShipmentStatus.Failed) => true,
			_ => false
		};

	public void EnsureAllowed(ShipmentStatus current, ShipmentStatus next)
	{
		if (!IsAllowed(current, next))
		{
			throw new InvalidOperationException(
				$"A shipment cannot move from {current} to {next}.");
		}
	}
}
