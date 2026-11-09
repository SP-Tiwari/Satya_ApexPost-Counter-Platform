using System.Net.Mime;
using ApexPost.Api.Models.V2;
using ApexPost.Api.Security;
using ApexPost.Application.Services;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApexPost.Api.Controllers.V2;

// D3: the read-only partner route, in version 2 only. A partner system
// authenticates with its API key (X-Api-Key), checked by
// PartnerApiKeyMiddleware before this controller runs; it has no bearer token.
// [AllowAnonymous] exempts it from the fallback policy that requires a bearer
// token everywhere else. It does not make the route open: without a valid key
// the middleware answers 401 before authorization runs.

/// <summary>
/// Read-only shipment tracking for approved partner systems, authenticated by
/// a partner API key in the <c>X-Api-Key</c> header.
/// </summary>
/// <param name="shipments">The application service that reads shipments.</param>
[ApiController]
[Route("api/v{version:apiVersion}/partner/shipments")]
[Tags("Partner")]
[ApiVersion("2.0")]
[RequirePartnerApiKey]
[AllowAnonymous]
[ProducesResponseType<ProblemDetails>(
	StatusCodes.Status401Unauthorized, MediaTypeNames.Application.ProblemJson)]
[ProducesResponseType<ProblemDetails>(
	StatusCodes.Status500InternalServerError, MediaTypeNames.Application.ProblemJson)]
public sealed class PartnerShipmentsController(TrackedShipmentApplicationService shipments)
	: ControllerBase
{
	/// <summary>Track a shipment as a partner</summary>
	/// <remarks>
	/// Returns a shipment's status, destination city and latest scan for a
	/// partner system. The recipient's name, phone number and address are not
	/// included. Requires a valid partner API key in the <c>X-Api-Key</c>
	/// header.
	/// </remarks>
	/// <param name="trackingNumber">The tracking number: APX- followed by eight digits, such as APX-00000001.</param>
	/// <param name="cancellationToken">Cancels the request.</param>
	/// <returns>The shipment's tracking status.</returns>
	/// <response code="200">The shipment's tracking status.</response>
	/// <response code="401">The X-Api-Key header is missing, or its key is not a partner key.</response>
	/// <response code="404">No shipment has the tracking number, or it is not APX- followed by eight digits.</response>
	/// <response code="500">An unexpected error occurred, or no partner key is configured.</response>
	[HttpGet(ShipmentRoutes.TrackingNumber, Name = "GetPartnerShipment")]
	[EndpointSummary("Track a shipment as a partner")]
	[ProducesResponseType<PartnerShipmentResponse>(StatusCodes.Status200OK, MediaTypeNames.Application.Json)]
	[ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, MediaTypeNames.Application.ProblemJson)]
	public async Task<ActionResult<PartnerShipmentResponse>> Get(
		string trackingNumber,
		CancellationToken cancellationToken)
	{
		var shipment = await shipments.FindAsync(trackingNumber, cancellationToken);
		return shipment is null
			? this.ShipmentNotFound(trackingNumber)
			: Ok(shipment.ToPartnerResponse());
	}
}
