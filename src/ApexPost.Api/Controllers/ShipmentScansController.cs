using System.Net.Mime;
using ApexPost.Api.Models;
using ApexPost.Api.Security;
using ApexPost.Application.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApexPost.Api.Controllers;

// Each route name is the operation's OpenAPI operationId, so it is part of the
// contract and must not change.
// The OpenAPI generator copies each XML <summary> over [EndpointSummary], so
// the two are kept identical. It also writes the description of any <param>
// that is not an operation parameter, such as cancellationToken, onto the
// request body, in tag order, so the request tag comes last.

/// <summary>
/// A shipment's scans: the bulk upload from courier devices and the scan
/// history. The scan contract is the same in both versions.
/// </summary>
/// <param name="shipments">The application service that holds the scan rules.</param>
[ApiController]
[Route(ShipmentRoutes.Shipments + "/" + ShipmentRoutes.TrackingNumber + "/scans")]
[Tags("Scans")]
[ApiVersion("1.0", Deprecated = true)]
[ApiVersion("2.0")]
[ProducesResponseType<ProblemDetails>(
	StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)]
[ProducesResponseType<ProblemDetails>(
	StatusCodes.Status500InternalServerError, MediaTypeNames.Application.ProblemJson)]
public sealed class ShipmentScansController(TrackedShipmentApplicationService shipments)
	: ControllerBase
{
	/// <summary>Upload offline courier scans</summary>
	/// <remarks>
	/// Requires a bearer token with the Courier role, and every scan must carry
	/// the token's own courierId; otherwise the whole batch is refused with 403.
	/// Uploads a batch of scans recorded offline on a courier device. Idempotent by scan ID: a scan already recorded for the shipment, or
	/// repeated in the batch, is counted in <c>duplicatesSkipped</c> and not
	/// stored again. A scan with a malformed ID, another shipment's tracking
	/// number or an ID recorded for another shipment is counted in
	/// <c>rejected</c>, with its reason, and the rest of the batch is still
	/// processed. Scans never change the shipment's status.
	/// </remarks>
	/// <param name="trackingNumber">The tracking number: APX- followed by eight digits, such as APX-00000001.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <param name="request">One to 100 scans.</param>
	/// <returns>The accepted, skipped and rejected counts, and the reason for each rejection.</returns>
	/// <response code="200">The batch was processed; the counts say what happened to each scan.</response>
	/// <response code="400">The batch failed validation, such as a scan time in the future, or its JSON is malformed.</response>
	/// <response code="401">The bearer token is missing, invalid or expired.</response>
	/// <response code="403">The token does not have the Courier role, or a scan's courierId is not the token's courierId; no scan is stored.</response>
	/// <response code="404">No shipment has the tracking number, or it is not APX- followed by eight digits.</response>
	/// <response code="409">A simultaneous upload stored the same scans and the retry could not resolve it.</response>
	/// <response code="415">The body is not application/json.</response>
	/// <response code="500">An unexpected error occurred.</response>
	[HttpPost(Name = "UploadShipmentScans")]
	[EndpointSummary("Upload offline courier scans")]
	[Authorize(Roles = ApexPostClaims.CourierRole)]
	[RequireOwnScans]
	[Consumes(MediaTypeNames.Application.Json)]
	[ProducesResponseType<ScanUploadResponse>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
	[ProducesResponseType<ValidationProblemDetails>(
		StatusCodes.Status400BadRequest, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(
		StatusCodes.Status401Unauthorized, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(
		StatusCodes.Status403Forbidden, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(
		StatusCodes.Status409Conflict, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<ProblemDetails>(
		StatusCodes.Status415UnsupportedMediaType, MediaTypeNames.Application.ProblemJson)]
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

	/// <summary>Get a shipment's scan history</summary>
	/// <remarks>Returns every scan recorded for the shipment, oldest first.</remarks>
	/// <param name="trackingNumber">The tracking number: APX- followed by eight digits, such as APX-00000001.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <returns>The shipment's scans, oldest first; empty when it has none.</returns>
	/// <response code="200">The scan history.</response>
	/// <response code="401">The bearer token is missing, invalid or expired.</response>
	/// <response code="404">No shipment has the tracking number, or it is not APX- followed by eight digits.</response>
	/// <response code="500">An unexpected error occurred.</response>
	[HttpGet(Name = "GetShipmentScans")]
	[EndpointSummary("Get a shipment's scan history")]
	[Authorize]
	[ProducesResponseType<ProblemDetails>(
		StatusCodes.Status401Unauthorized, MediaTypeNames.Application.ProblemJson)]
	[ProducesResponseType<IReadOnlyList<ScanResponse>>(
		StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
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
