using ApexPost.Api.Models;
using ApexPost.Application.Services;
using Microsoft.AspNetCore.Mvc;

namespace ApexPost.Api.Controllers;

// A shipment's scans: the bulk upload from courier devices and the history.
[ApiController]
[Route(ShipmentRoutes.Shipments + "/" + ShipmentRoutes.TrackingNumber + "/scans")]
public sealed class ShipmentScansController(TrackedShipmentApplicationService shipments)
	: ControllerBase
{
	// Idempotent: a scan ID already recorded is counted, not stored again.
	[HttpPost]
	[Consumes("application/json")]
	public async Task<ActionResult<ScanUploadResponse>> Upload(
		string trackingNumber,
		ScanUploadRequest request,
		CancellationToken cancellationToken)
	{
		var result = await shipments.UploadScansAsync(
			trackingNumber, request.ToUploadedScans(), cancellationToken);
		return result is null
			? this.ShipmentNotFound(trackingNumber)
			: Ok(result.ToResponse());
	}

	[HttpGet]
	public async Task<ActionResult<IReadOnlyList<ScanResponse>>> History(
		string trackingNumber,
		CancellationToken cancellationToken)
	{
		var scans = await shipments.GetScansAsync(trackingNumber, cancellationToken);
		return scans is null
			? this.ShipmentNotFound(trackingNumber)
			: Ok(scans.Select(scan => scan.ToResponse()).ToArray());
	}
}
