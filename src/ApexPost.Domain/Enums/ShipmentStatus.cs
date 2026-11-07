namespace ApexPost.Domain.Enums;

// The lifecycle of a tracked shipment. ShipmentStatusTransitionRule decides
// which status may follow which.
public enum ShipmentStatus
{
	Created,
	PickedUp,
	InTransit,
	OutForDelivery,
	Delivered,
	Failed
}
