using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.Models.V2;

// Explicit mapping from the application layer to the version 2 DTOs. The
// version 1 mapping is in ApexPost.Api.Models.ShipmentMapping; both map the
// same TrackedShipment from the same service.
internal static class ShipmentMapping
{
	public static ShipmentResponse ToV2Response(this TrackedShipment shipment) =>
		new(
			shipment.TrackingNumber,
			new RecipientResponse(
				shipment.RecipientName,
				shipment.RecipientPhone,
				shipment.RecipientAddress,
				shipment.RecipientCity),
			shipment.Weight.Kilograms,
			shipment.Status,
			shipment.ToLatestScanResponse());

	// The partner view leaves out the recipient's name, phone and address.
	public static PartnerShipmentResponse ToPartnerResponse(this TrackedShipment shipment) =>
		new(
			shipment.TrackingNumber,
			shipment.Status,
			shipment.RecipientCity,
			shipment.ToLatestScanResponse());

	private static LatestScanResponse? ToLatestScanResponse(this TrackedShipment shipment) =>
		shipment.LatestScan is { } scan
			? new LatestScanResponse(scan.ScanType.ToLatestScanType(), scan.Location, scan.ScannedAtUtc)
			: null;

	// By name, so a reordered or added member is a failure here, not a wrong
	// value on the wire.
	internal static LatestScanType ToLatestScanType(this ShipmentScanType scanType) => scanType switch
	{
		ShipmentScanType.Pickup => LatestScanType.Pickup,
		ShipmentScanType.InTransit => LatestScanType.InTransit,
		ShipmentScanType.Delivered => LatestScanType.Delivered,
		ShipmentScanType.Failed => LatestScanType.Failed,
		_ => throw new ArgumentOutOfRangeException(nameof(scanType), scanType, "Unknown scan type.")
	};

	public static ShipmentPageResponse ToV2Response(
		this TrackedShipmentPage page,
		ShipmentListQuery query) =>
		new([.. page.Items.Select(ToV2Response)], query.Page, query.PageSize, page.TotalCount);
}
