using ApexPost.Application.Persistence;
using ApexPost.Application.Services;
using ApexPost.Domain.Entities;

namespace ApexPost.Api.Models;

// Explicit mapping between the DTOs and the application layer. Validation has
// already run, so the required values are present.
internal static class ShipmentMapping
{
	public static ShipmentDetails ToDetails(this ShipmentDetailsRequest request) =>
		new(
			request.RecipientName!,
			request.RecipientPhone!,
			request.RecipientAddress!,
			request.RecipientCity!,
			request.WeightKg!.Value);

	public static CreateTrackedShipmentCommand ToCommand(this CreateShipmentRequest request) =>
		new(request.TrackingNumber!, request.ToDetails());

	public static IReadOnlyList<UploadedScan> ToUploadedScans(this ScanUploadRequest request) =>
		[.. request.Scans!.Select(scan => new UploadedScan(
			scan.ScanId!,
			scan.TrackingNumber!,
			scan.ScanType!.Value,
			scan.CourierId!,
			scan.Location!,
			scan.ScannedAtUtc!.Value,
			scan.Notes))];

	public static ShipmentResponse ToResponse(this TrackedShipment shipment) =>
		new(
			shipment.TrackingNumber,
			shipment.RecipientName,
			shipment.RecipientPhone,
			shipment.RecipientAddress,
			shipment.RecipientCity,
			shipment.Weight.Kilograms,
			shipment.Status);

	public static ShipmentPageResponse ToResponse(
		this TrackedShipmentPage page,
		ShipmentListQuery query) =>
		new([.. page.Items.Select(ToResponse)], query.Page, query.PageSize, page.TotalCount);

	public static ScanResponse ToResponse(this TrackedShipmentScan scan) =>
		new(scan.ScanId, scan.ScanType, scan.CourierId, scan.Location, scan.ScannedAtUtc, scan.Notes);

	public static ScanUploadResponse ToResponse(this ScanUploadResult result) =>
		new(
			result.Accepted,
			result.DuplicatesSkipped,
			result.Rejected.Count,
			[.. result.Rejected.Select(scan => new RejectedScanResponse(scan.ScanId, scan.Reason))]);
}
