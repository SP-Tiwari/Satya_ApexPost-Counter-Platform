using ApexPost.Application.Persistence;
using ApexPost.Domain.Entities;

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
			? new LatestScanResponse(scan.ScanType, scan.Location, scan.ScannedAtUtc)
			: null;

	public static ShipmentPageResponse ToV2Response(
		this TrackedShipmentPage page,
		ShipmentListQuery query) =>
		new([.. page.Items.Select(ToV2Response)], query.Page, query.PageSize, page.TotalCount);
}
